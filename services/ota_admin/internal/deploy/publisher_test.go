package deploy

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

const (
	deploySessionID   = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	deployCandidateID = "55555555-5555-4555-8555-555555555555"
	deployUploadID    = "66666666-6666-4666-8666-666666666666"
	deployPublishID   = "77777777-7777-4777-8777-777777777777"
)

type fixedValidator struct {
	report release.Report
	err    error
}

type markerValidator map[string]release.Report

func (validator markerValidator) Validate(_ context.Context, envelope *upload.Envelope, _ string) (release.Report, error) {
	for _, entry := range envelope.Entries {
		if entry.Name != "asset.nupkg" {
			continue
		}
		value, err := os.ReadFile(entry.Path)
		if err != nil {
			return release.Report{}, err
		}
		report, ok := validator[string(value)]
		if !ok {
			return release.Report{}, errors.New("unknown generation marker")
		}
		return report, nil
	}
	return release.Report{}, errors.New("generation marker is missing")
}

func (validator markerValidator) ValidateOpenedDirectory(ctx context.Context, envelope *upload.Envelope, _ *os.File, floor string) (release.Report, error) {
	return validator.Validate(ctx, envelope, floor)
}

func (validator fixedValidator) Validate(context.Context, *upload.Envelope, string) (release.Report, error) {
	if validator.err != nil {
		return release.Report{}, validator.err
	}
	return validator.report, nil
}

func (validator fixedValidator) ValidateOpenedDirectory(ctx context.Context, envelope *upload.Envelope, _ *os.File, floor string) (release.Report, error) {
	return validator.Validate(ctx, envelope, floor)
}

type fixedPreparer struct {
	mu       sync.Mutex
	calls    int
	prepared candidate.Prepared
}

func TestPublisherRefusesWritesUntilStartupRecoveryCompletes(t *testing.T) {
	publisher, _, _, _, _ := newUnrecoveredPublisherHarness(t, nil)
	if !errors.Is(publisher.ReadOnlyError(), ErrRecoveryRequired) {
		t.Fatalf("initial read-only state=%v", publisher.ReadOnlyError())
	}
	if _, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID); !errors.Is(err, ErrRecoveryRequired) {
		t.Fatalf("unrecovered publish err=%v", err)
	}
	if err := publisher.Recover(context.Background()); err != nil {
		t.Fatal(err)
	}
	if publisher.ReadOnlyError() != nil {
		t.Fatalf("recovery did not clear protection: %v", publisher.ReadOnlyError())
	}
}

func TestPublishRechecksSessionImmediatelyBeforeStableSwitch(t *testing.T) {
	var database *store.Store
	publisher, opened, _, root, now := newPublisherHarness(t, func(actual Boundary) error {
		if actual == BoundaryGenerationsSynced {
			if err := database.TransitionSession(context.Background(), deploySessionID, store.SessionRevoked); err != nil {
				return err
			}
		}
		return nil
	})
	database = opened
	_, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID)
	if !errors.Is(err, store.ErrInvalidTransition) {
		t.Fatalf("publish error=%v, want persisted failed result", err)
	}
	if _, err := os.Lstat(filepath.Join(root, "stable")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("stable became public after session revocation: %v", err)
	}
	op, found, lookupErr := database.PublishOperationForActiveSession(context.Background(), deploySessionID, deployPublishID, now)
	if !errors.Is(lookupErr, store.ErrSessionInactive) || found || op.OperationID != "" {
		t.Fatalf("revoked replay lookup=(%+v,%v,%v)", op, found, lookupErr)
	}
}

func (preparer *fixedPreparer) RevalidateForConfirm(context.Context, string, string) (candidate.Prepared, error) {
	preparer.mu.Lock()
	defer preparer.mu.Unlock()
	preparer.calls++
	return preparer.prepared, nil
}

func (preparer *fixedPreparer) callCount() int {
	preparer.mu.Lock()
	defer preparer.mu.Unlock()
	return preparer.calls
}

func TestPublishReplayReturnsCommittedResultWithoutRevalidatingConsumedCandidate(t *testing.T) {
	publisher, database, preparer, _, now := newPublisherHarness(t, nil)
	operation, created, err := database.BeginPublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID, now)
	if err != nil || !created || operation.State != store.OperationRunning {
		t.Fatalf("begin=%+v created=%v err=%v", operation, created, err)
	}
	report := preparer.prepared.Report
	assets := make([]store.ReleaseAsset, 0, len(report.Files))
	for _, file := range report.Files {
		assets = append(assets, store.ReleaseAsset{FileName: file.Name, SizeBytes: file.Size, SHA256: file.SHA256})
	}
	if err := database.CommitPublishedRelease(context.Background(), store.PublishCommit{Release: store.Release{ID: deployPublishID, Version: report.Version, GenerationName: "release-" + deployPublishID, CommittedAt: now}, OperationID: deployPublishID, CandidateID: deployCandidateID, Assets: assets}); err != nil {
		t.Fatal(err)
	}
	secondCandidate := "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
	secondPublish := "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
	if err := database.CreateCandidate(context.Background(), store.Candidate{ID: secondCandidate, SessionID: deploySessionID, State: store.CandidateUploading, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := database.ValidateCandidate(context.Background(), secondCandidate, strings.Repeat("d", 64), `{"valid":true}`); err != nil {
		t.Fatal(err)
	}
	if _, created, err := database.BeginPublishForActiveSession(context.Background(), deploySessionID, secondCandidate, secondPublish, now); err != nil || !created {
		t.Fatalf("second publish begin created=%v err=%v", created, err)
	}
	if err := database.CommitPublishedRelease(context.Background(), store.PublishCommit{Release: store.Release{ID: secondPublish, Version: "1.2.4", GenerationName: "release-" + secondPublish, CommittedAt: now.Add(time.Minute)}, OperationID: secondPublish, CandidateID: secondCandidate, Assets: assets}); err != nil {
		t.Fatal(err)
	}
	first := PublishResult{OperationID: deployPublishID, CandidateID: deployCandidateID, ReleaseID: deployPublishID, Version: report.Version, State: "succeeded"}
	second, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID)
	if err != nil {
		t.Fatal(err)
	}
	if err != nil || second != first {
		t.Fatalf("first=%+v second=%+v err=%v", first, second, err)
	}
	if preparer.callCount() != 0 {
		t.Fatalf("candidate revalidation calls=%d", preparer.callCount())
	}
	current, _, found, err := database.CurrentRelease(context.Background())
	if err != nil || !found || current.ID != secondPublish {
		t.Fatalf("current=%+v found=%v err=%v", current, found, err)
	}
}

func TestPreCommitValidationFailurePersistsOneStableReplayAndAuditCode(t *testing.T) {
	for _, test := range []struct {
		name, code string
		validation error
		persisted  error
	}{
		{name: "signature", code: "signature_mismatch", validation: release.ErrSignature, persisted: release.ErrSignature},
		{name: "manifest closure", code: "hash_mismatch", validation: release.ErrManifest, persisted: candidate.ErrDigestMismatch},
		{name: "version floor", code: "version_floor", validation: release.ErrVersionFloor, persisted: release.ErrVersionFloor},
	} {
		t.Run(test.name, func(t *testing.T) {
			publisher, database, _, _, now := newPublisherHarness(t, nil)
			publisher.validator = fixedValidator{err: test.validation}
			for attempt := range 2 {
				if _, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID); !errors.Is(err, test.persisted) {
					t.Fatalf("attempt %d err=%v", attempt+1, err)
				}
			}
			op, found, err := database.PublishOperationForActiveSession(context.Background(), deploySessionID, deployPublishID, now)
			if err != nil || !found || op.State != store.OperationFailed || op.ResultCode != test.code {
				t.Fatalf("operation=%+v found=%v err=%v", op, found, err)
			}
		})
	}
}

func TestPreCommitCleanupFailureStaysRunningAndRecoversBeforeWritesResume(t *testing.T) {
	publisher, database, preparer, root, now := newPublisherHarness(t, nil)
	publisher.validator = fixedValidator{err: release.ErrSignature}
	publisher.disk.remove = func(string, string) error { return errors.New("injected cleanup refusal") }
	if _, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID); !errors.Is(err, ErrCleanupIncomplete) {
		t.Fatalf("publish err=%v", err)
	}
	if publisher.ReadOnlyError() == nil {
		t.Fatal("cleanup uncertainty did not enter read-only protection")
	}
	op, found, err := database.PublishOperationForActiveSession(context.Background(), deploySessionID, deployPublishID, now)
	if err != nil || !found || op.State != store.OperationRunning {
		t.Fatalf("operation=%+v found=%v err=%v", op, found, err)
	}
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	restarted, err := NewPublisher(PublisherOptions{Disk: disk, Store: database, Candidates: preparer, Validator: fixedValidator{report: preparer.prepared.Report}, Now: func() time.Time { return now.Add(time.Minute) }})
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.Recover(context.Background()); err != nil {
		t.Fatal(err)
	}
	op, found, err = database.PublishOperationForActiveSession(context.Background(), deploySessionID, deployPublishID, now.Add(time.Minute))
	if err != nil || !found || op.State != store.OperationFailed {
		t.Fatalf("recovered operation=%+v found=%v err=%v", op, found, err)
	}
}

func TestPostCommitFaultRecoversSignedStableGenerationAndSQLiteIdempotently(t *testing.T) {
	for _, boundary := range []Boundary{BoundaryStableReplaced, BoundaryParentSynced} {
		t.Run(string(boundary), func(t *testing.T) {
			publisher, database, _, root, now := newPublisherHarness(t, func(actual Boundary) error {
				if actual == boundary {
					return errors.New("injected post-commit crash")
				}
				return nil
			})
			_, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID)
			if symlinkUnavailable(err) {
				t.Skipf("symbolic links unavailable: %v", err)
			}
			if err == nil || publisher.ReadOnlyError() == nil {
				t.Fatalf("post-commit fault err=%v readOnly=%v", err, publisher.ReadOnlyError())
			}
			cleanDisk, err := NewDisk(root, nil)
			if err != nil {
				t.Fatal(err)
			}
			restarted, err := NewPublisher(PublisherOptions{Disk: cleanDisk, Store: database, Candidates: publisher.candidates, Validator: publisher.validator, Now: func() time.Time { return now.Add(time.Minute) }})
			if err != nil {
				t.Fatal(err)
			}
			for range 2 {
				if err := restarted.Recover(context.Background()); err != nil {
					t.Fatal(err)
				}
			}
			current, _, found, err := database.CurrentRelease(context.Background())
			if err != nil || !found || current.ID != deployPublishID {
				t.Fatalf("current=%+v found=%v err=%v", current, found, err)
			}
		})
	}
}

func TestRecoveryAbortsAndCleansAnUnpublishedGenerationBeforeTheCommitPoint(t *testing.T) {
	publisher, database, preparer, _, now := newPublisherHarness(t, nil)
	operation, created, err := database.BeginPublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID, now)
	if err != nil || !created || operation.State != store.OperationRunning {
		t.Fatalf("begin=%+v created=%v err=%v", operation, created, err)
	}
	name, _, err := publisher.disk.stage(context.Background(), preparer.prepared.Envelope, deployPublishID)
	if err != nil {
		t.Fatal(err)
	}
	temporary := filepath.Join(publisher.disk.generations, "."+name+".tmp")
	final := filepath.Join(publisher.disk.generations, name)
	if err := os.Rename(temporary, final); err != nil {
		t.Fatal(err)
	}
	if err := publisher.Recover(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(final); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("unpublished generation remains: %v", err)
	}
	got, found, err := database.PublishOperationForActiveSession(context.Background(), deploySessionID, deployPublishID, now)
	if err != nil || !found || got.State != store.OperationFailed {
		t.Fatalf("operation=%+v found=%v err=%v", got, found, err)
	}
	if _, _, found, err := database.CurrentRelease(context.Background()); err != nil || found {
		t.Fatalf("current found=%v err=%v", found, err)
	}
}

func TestRecoveryCleansCrashArtifactsAtEveryPreCommitBoundary(t *testing.T) {
	boundaries := []Boundary{BoundaryFileSynced, BoundaryCandidateSynced, BoundaryGenerationNamed, BoundaryGenerationsSynced, BoundaryStableTemp}
	for _, boundary := range boundaries {
		t.Run(string(boundary), func(t *testing.T) {
			publisher, database, preparer, root, now := newPublisherHarness(t, func(actual Boundary) error {
				if actual == boundary {
					return ErrCrashSimulation
				}
				return nil
			})
			if _, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID); !errors.Is(err, ErrCrashSimulation) {
				if symlinkUnavailable(err) {
					t.Skipf("symbolic links unavailable: %v", err)
				}
				t.Fatalf("publish err=%v", err)
			}
			operation, found, err := database.PublishOperationForActiveSession(context.Background(), deploySessionID, deployPublishID, now)
			if err != nil || !found || operation.State != store.OperationRunning {
				t.Fatalf("pre-recovery operation=%+v found=%v err=%v", operation, found, err)
			}
			entries, err := os.ReadDir(filepath.Join(root, "generations"))
			if err != nil || len(entries) == 0 {
				t.Fatalf("crash artifacts=%v err=%v", entries, err)
			}
			cleanDisk, err := NewDisk(root, nil)
			if err != nil {
				t.Fatal(err)
			}
			restarted, err := NewPublisher(PublisherOptions{Disk: cleanDisk, Store: database, Candidates: preparer, Validator: publisher.validator, Now: func() time.Time { return now.Add(time.Minute) }})
			if err != nil {
				t.Fatal(err)
			}
			if err := restarted.Recover(context.Background()); err != nil {
				t.Fatal(err)
			}
			operation, found, err = database.PublishOperationForActiveSession(context.Background(), deploySessionID, deployPublishID, now.Add(time.Minute))
			if err != nil || !found || operation.State != store.OperationFailed {
				t.Fatalf("post-recovery operation=%+v found=%v err=%v", operation, found, err)
			}
			entries, err = os.ReadDir(filepath.Join(root, "generations"))
			if err != nil || len(entries) != 0 {
				t.Fatalf("post-recovery artifacts=%v err=%v", entries, err)
			}
		})
	}
}

func TestPreCommitFailureThatCannotBeRecordedEntersReadOnlyProtection(t *testing.T) {
	var database *store.Store
	publisher, opened, _, _, _ := newPublisherHarness(t, func(actual Boundary) error {
		if actual != BoundaryFileSynced {
			return nil
		}
		if err := database.Close(); err != nil {
			return err
		}
		return errors.New("injected filesystem failure")
	})
	database = opened
	if _, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID); err == nil {
		t.Fatal("publish unexpectedly succeeded")
	}
	if publisher.ReadOnlyError() == nil {
		t.Fatal("publisher remained writable after failure persistence was lost")
	}
}

func TestRecoveryRejectsAStableGenerationWhenFullValidationFails(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("real symlink recovery path is Linux-only")
	}
	publisher, database, _, root, now := newPublisherHarness(t, nil)
	if _, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID); err != nil {
		t.Fatal(err)
	}
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	restarted, err := NewPublisher(PublisherOptions{Disk: disk, Store: database, Candidates: publisher.candidates, Validator: fixedValidator{err: release.ErrSignature}, Now: func() time.Time { return now.Add(time.Minute) }})
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.Recover(context.Background()); !errors.Is(err, release.ErrSignature) {
		t.Fatalf("recovery err=%v", err)
	}
	if restarted.ReadOnlyError() == nil {
		t.Fatal("invalid stable generation did not enable read-only protection")
	}
}

func TestRecoveryRejectsWritableGenerationContent(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("generation mode enforcement is Linux-only")
	}
	publisher, database, _, root, now := newPublisherHarness(t, nil)
	if _, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID); err != nil {
		t.Fatal(err)
	}
	member := filepath.Join(root, "generations", "release-"+deployPublishID, "asset.nupkg")
	if err := os.Chmod(member, 0o644); err != nil {
		t.Fatal(err)
	}
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	restarted, err := NewPublisher(PublisherOptions{Disk: disk, Store: database, Candidates: publisher.candidates, Validator: publisher.validator, Now: func() time.Time { return now.Add(time.Minute) }})
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.Recover(context.Background()); err == nil || restarted.ReadOnlyError() == nil {
		t.Fatalf("recovery err=%v readOnly=%v", err, restarted.ReadOnlyError())
	}
}

func TestRecoveryRejectsMissingStableWhenSQLiteHasCurrentRelease(t *testing.T) {
	publisher, database, preparer, _, now := newPublisherHarness(t, nil)
	if _, created, err := database.BeginPublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID, now); err != nil || !created {
		t.Fatalf("created=%v err=%v", created, err)
	}
	report := preparer.prepared.Report
	assets := make([]store.ReleaseAsset, 0, len(report.Files))
	for _, file := range report.Files {
		assets = append(assets, store.ReleaseAsset{FileName: file.Name, SizeBytes: file.Size, SHA256: file.SHA256})
	}
	if err := database.CommitPublishedRelease(context.Background(), store.PublishCommit{Release: store.Release{ID: deployPublishID, Version: report.Version, GenerationName: "release-" + deployPublishID, CommittedAt: now}, OperationID: deployPublishID, CandidateID: deployCandidateID, Assets: assets}); err != nil {
		t.Fatal(err)
	}
	if err := publisher.Recover(context.Background()); err == nil {
		t.Fatal("missing stable accepted for current release")
	}
	if publisher.ReadOnlyError() == nil {
		t.Fatal("missing stable did not enable read-only protection")
	}
}

func TestRecoveryBackfillsLegacyCurrentAndRetainedNotesFromValidatedGenerations(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("real generation recovery path is Linux-only")
	}
	publisher, database, _, root, now := newPublisherHarness(t, nil)
	reports := markerValidator{}
	first := release.Report{SchemaVersion: 1, Verified: true, PackageID: "SuperPan.BaiduBatchTransfer", Channel: "win-x64-stable", Version: "1.2.3", NotesMarkdown: "第一版签名说明", FileCount: 2, Files: []release.ReportFile{{Name: "asset.nupkg", Size: 5, SHA256: strings.Repeat("a", 64)}, {Name: "releases.win-x64-stable.json", Size: 5, SHA256: strings.Repeat("b", 64)}}}
	second := first
	second.Version = "1.2.4"
	second.NotesMarkdown = "第二版签名说明"
	second.Files = append([]release.ReportFile(nil), first.Files...)
	second.Files[0].Size = 6
	reports["first"] = first
	reports["second"] = second

	installLegacyNotesRelease(t, publisher, database, deployCandidateID, deployPublishID, first, "", markedSourceEnvelope(t, "first"), now)
	secondCandidate := "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
	secondUpload := "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
	secondPublish := "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
	reserveValidatedCandidate(t, database, secondCandidate, secondUpload, second, now.Add(time.Minute))
	installLegacyNotesRelease(t, publisher, database, secondCandidate, secondPublish, second, "", markedSourceEnvelope(t, "second"), now.Add(time.Minute))

	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	restarted, err := NewPublisher(PublisherOptions{Disk: disk, Store: database, Candidates: publisher.candidates, Validator: reports, Now: func() time.Time { return now.Add(2 * time.Minute) }})
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.Recover(context.Background()); err != nil {
		t.Fatal(err)
	}
	for releaseID, want := range map[string]string{deployPublishID: first.NotesMarkdown, secondPublish: second.NotesMarkdown} {
		item, found, err := database.ReleaseByID(context.Background(), releaseID)
		if err != nil || !found || item.Notes != want {
			t.Fatalf("release=%+v found=%v err=%v want notes=%q", item, found, err, want)
		}
	}
}

func TestRecoveryRejectsPersistedNotesDriftFromValidatedGeneration(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("real generation recovery path is Linux-only")
	}
	publisher, database, _, root, now := newPublisherHarness(t, nil)
	report := release.Report{SchemaVersion: 1, Verified: true, PackageID: "SuperPan.BaiduBatchTransfer", Channel: "win-x64-stable", Version: "1.2.3", NotesMarkdown: "权威签名说明", FileCount: 2, Files: []release.ReportFile{{Name: "asset.nupkg", Size: 5, SHA256: strings.Repeat("a", 64)}, {Name: "releases.win-x64-stable.json", Size: 5, SHA256: strings.Repeat("b", 64)}}}
	installLegacyNotesRelease(t, publisher, database, deployCandidateID, deployPublishID, report, "数据库漂移说明", markedSourceEnvelope(t, "first"), now)
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	restarted, err := NewPublisher(PublisherOptions{Disk: disk, Store: database, Candidates: publisher.candidates, Validator: markerValidator{"first": report}, Now: func() time.Time { return now.Add(time.Minute) }})
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.Recover(context.Background()); !errors.Is(err, store.ErrInvalidTransition) {
		t.Fatalf("recovery err=%v", err)
	}
	if restarted.ReadOnlyError() == nil {
		t.Fatal("notes drift did not enable read-only protection")
	}
}

func installLegacyNotesRelease(t *testing.T, publisher *Publisher, database *store.Store, candidateID, publishID string, report release.Report, persistedNotes string, envelope *upload.Envelope, at time.Time) {
	t.Helper()
	if _, created, err := database.BeginPublishForActiveSession(context.Background(), deploySessionID, candidateID, publishID, at); err != nil || !created {
		t.Fatalf("begin publish created=%v err=%v", created, err)
	}
	generation, switched, err := publisher.disk.PublishGeneration(context.Background(), envelope, publishID, report, markerValidator{string(mustReadEntry(t, envelope, "asset.nupkg")): report})
	if err != nil || !switched {
		t.Fatalf("publish generation switched=%v err=%v", switched, err)
	}
	if err := database.CommitPublishedRelease(context.Background(), store.PublishCommit{Release: store.Release{ID: publishID, Version: report.Version, GenerationName: generation.Name, CommittedAt: at, Notes: persistedNotes}, OperationID: publishID, CandidateID: candidateID, Assets: generation.Assets}); err != nil {
		t.Fatal(err)
	}
}

func reserveValidatedCandidate(t *testing.T, database *store.Store, candidateID, uploadID string, report release.Report, at time.Time) {
	t.Helper()
	if _, acquired, err := database.ReserveCandidateForActiveSession(context.Background(), store.CandidateReservation{CandidateID: candidateID, SessionID: deploySessionID, OperationID: uploadID, LeaseOwner: candidateID, CreatedAt: at, ExpiresAt: at.Add(24 * time.Hour), LeaseExpiresAt: at.Add(5 * time.Minute)}); err != nil || !acquired {
		t.Fatalf("reserve candidate acquired=%v err=%v", acquired, err)
	}
	reportJSON, err := json.Marshal(report)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := database.AcceptCandidateForActiveSession(context.Background(), store.CandidateAcceptance{CandidateID: candidateID, SessionID: deploySessionID, OperationID: uploadID, LeaseOwner: candidateID, UploadDigest: strings.Repeat("d", 64), ValidationReport: string(reportJSON), Version: report.Version, CreatedAt: at, ExpiresAt: at.Add(24 * time.Hour), CommittedAt: at}); err != nil {
		t.Fatal(err)
	}
}

func markedSourceEnvelope(t *testing.T, marker string) *upload.Envelope {
	t.Helper()
	root := t.TempDir()
	entries := make([]upload.Entry, 0, 2)
	for _, item := range []struct{ name, content string }{{"asset.nupkg", marker}, {"releases.win-x64-stable.json", "index"}} {
		path := filepath.Join(root, item.name)
		if err := os.WriteFile(path, []byte(item.content), 0o600); err != nil {
			t.Fatal(err)
		}
		entries = append(entries, upload.Entry{Name: item.name, Path: path, Size: int64(len(item.content))})
	}
	return &upload.Envelope{ExtractionRoot: root, Entries: entries}
}

func mustReadEntry(t *testing.T, envelope *upload.Envelope, name string) []byte {
	t.Helper()
	for _, entry := range envelope.Entries {
		if entry.Name == name {
			value, err := os.ReadFile(entry.Path)
			if err != nil {
				t.Fatal(err)
			}
			return value
		}
	}
	t.Fatalf("entry %q missing", name)
	return nil
}

func TestPublishersForOneRootShareTheProcessLockAndReplayOneCommit(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("real symlink commit path is Linux-only")
	}
	entered := make(chan struct{})
	releaseBoundary := make(chan struct{})
	var once sync.Once
	publisher, database, preparer, root, now := newPublisherHarness(t, func(actual Boundary) error {
		if actual == BoundaryCandidateSynced {
			once.Do(func() { close(entered) })
			<-releaseBoundary
		}
		return nil
	})
	secondDisk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	second, err := NewPublisher(PublisherOptions{Disk: secondDisk, Store: database, Candidates: preparer, Validator: fixedValidator{report: preparer.prepared.Report}, Now: func() time.Time { return now }})
	if err != nil {
		t.Fatal(err)
	}
	if err := second.Recover(context.Background()); err != nil {
		t.Fatal(err)
	}
	results := make(chan error, 2)
	go func() {
		_, err := publisher.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID)
		results <- err
	}()
	select {
	case <-entered:
	case <-time.After(5 * time.Second):
		t.Fatal("first publisher did not reach durability boundary")
	}
	go func() {
		_, err := second.PublishForActiveSession(context.Background(), deploySessionID, deployCandidateID, deployPublishID)
		results <- err
	}()
	select {
	case err := <-results:
		t.Fatalf("publish escaped process lock: %v", err)
	case <-time.After(100 * time.Millisecond):
	}
	close(releaseBoundary)
	for range 2 {
		if err := <-results; err != nil {
			t.Fatal(err)
		}
	}
	if preparer.callCount() != 1 {
		t.Fatalf("candidate revalidations=%d", preparer.callCount())
	}
}

func TestFailedRollbackReplayReturnsItsPersistedTypedOutcome(t *testing.T) {
	publisher, _, _, _, _ := newPublisherHarness(t, nil)
	_, err := publisher.replayRollbackLocked(context.Background(), deploySessionID, store.ReleaseOperation{
		OperationID: deployPublishID,
		ReleaseID:   testReleaseID,
		State:       store.OperationFailed,
		ResultCode:  "signature_mismatch",
	})
	if !errors.Is(err, release.ErrSignature) {
		t.Fatalf("replay error=%v", err)
	}
}

func newPublisherHarness(t *testing.T, hook FaultHook) (*Publisher, *store.Store, *fixedPreparer, string, time.Time) {
	t.Helper()
	publisher, database, preparer, root, now := newUnrecoveredPublisherHarness(t, hook)
	if err := publisher.Recover(context.Background()); err != nil {
		t.Fatal(err)
	}
	return publisher, database, preparer, root, now
}

func newUnrecoveredPublisherHarness(t *testing.T, hook FaultHook) (*Publisher, *store.Store, *fixedPreparer, string, time.Time) {
	t.Helper()
	root := publicationRoot(t)
	disk, err := NewDisk(root, hook)
	if err != nil {
		t.Fatal(err)
	}
	stateRoot := t.TempDir()
	if err := os.Chmod(stateRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	database, err := store.Open(context.Background(), filepath.Join(stateRoot, "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	now := time.Unix(2_000_000_000, 0).UTC()
	if err := database.CreateSession(context.Background(), store.Session{IDHash: deploySessionID, State: store.SessionActive, CreatedAt: now.Add(-time.Minute), LastSeenAt: now, IdleExpiresAt: now.Add(time.Hour), AbsoluteExpiresAt: now.Add(2 * time.Hour)}); err != nil {
		t.Fatal(err)
	}
	report := release.Report{SchemaVersion: 1, Verified: true, PackageID: "SuperPan.BaiduBatchTransfer", Channel: "win-x64-stable", Version: "1.2.3", FileCount: 2, Files: []release.ReportFile{{Name: "asset.nupkg", Size: 12, SHA256: strings.Repeat("a", 64)}, {Name: "releases.win-x64-stable.json", Size: 31, SHA256: strings.Repeat("b", 64)}}}
	reportJSON, err := json.Marshal(report)
	if err != nil {
		t.Fatal(err)
	}
	if _, acquired, err := database.ReserveCandidateForActiveSession(context.Background(), store.CandidateReservation{
		CandidateID: deployCandidateID, SessionID: deploySessionID, OperationID: deployUploadID, LeaseOwner: deployCandidateID,
		CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), LeaseExpiresAt: now.Add(5 * time.Minute),
	}); err != nil || !acquired {
		t.Fatalf("candidate reservation acquired=%v err=%v", acquired, err)
	}
	_, _, err = database.AcceptCandidateForActiveSession(context.Background(), store.CandidateAcceptance{CandidateID: deployCandidateID, SessionID: deploySessionID, OperationID: deployUploadID, LeaseOwner: deployCandidateID, UploadDigest: strings.Repeat("c", 64), ValidationReport: string(reportJSON), Version: report.Version, CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), CommittedAt: now})
	if err != nil {
		t.Fatal(err)
	}
	preparer := &fixedPreparer{prepared: candidate.Prepared{Summary: candidate.Summary{CandidateID: deployCandidateID}, Report: report, Envelope: sourceEnvelope(t)}}
	publisher, err := NewPublisher(PublisherOptions{Disk: disk, Store: database, Candidates: preparer, Validator: fixedValidator{report: report}, Now: func() time.Time { return now }})
	if err != nil {
		t.Fatal(err)
	}
	return publisher, database, preparer, root, now
}

func symlinkUnavailable(err error) bool {
	return err != nil && (errors.Is(err, os.ErrPermission) || strings.Contains(strings.ToLower(err.Error()), "privilege"))
}
