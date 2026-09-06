//go:build linux

package deploy

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

const (
	rollbackReleaseOne = "10101010-1010-4010-8010-101010101010"
	rollbackReleaseTwo = "20202020-2020-4020-8020-202020202020"
	rollbackOperation  = "30303030-3030-4030-8030-303030303030"
	rollbackCandidate1 = "40404040-4040-4040-8040-404040404040"
	rollbackCandidate2 = "50505050-5050-4050-8050-505050505050"
)

type contentVersionValidator struct{ reports map[string]release.Report }

func (validator contentVersionValidator) Validate(_ context.Context, envelope *upload.Envelope, _ string) (release.Report, error) {
	for _, entry := range envelope.Entries {
		if entry.Name != "asset.nupkg" {
			continue
		}
		content, err := os.ReadFile(entry.Path)
		if err != nil {
			return release.Report{}, err
		}
		report, ok := validator.reports[string(content)]
		if !ok {
			return release.Report{}, errors.New("unknown fixture version")
		}
		return report, nil
	}
	return release.Report{}, errors.New("fixture marker missing")
}

func (validator contentVersionValidator) ValidateOpenedDirectory(ctx context.Context, envelope *upload.Envelope, _ *os.File, floor string) (release.Report, error) {
	return validator.Validate(ctx, envelope, floor)
}

func TestPublisherRollbackIsIdempotentPreservesFloorAndRecoversAfterStableSwitch(t *testing.T) {
	for _, crash := range []bool{false, true} {
		t.Run(map[bool]string{false: "normal", true: "crash recovery"}[crash], func(t *testing.T) {
			root := publicationRoot(t)
			var crashOnce bool
			disk, err := NewDisk(root, func(boundary Boundary) error {
				if crash && boundary == BoundaryRollbackStable && !crashOnce {
					crashOnce = true
					return ErrCrashSimulation
				}
				return nil
			})
			if err != nil {
				t.Fatal(err)
			}
			reportOne := rollbackReport("1.0.1")
			reportTwo := rollbackReport("1.0.2")
			generationOne, switched, err := disk.PublishGeneration(context.Background(), rollbackSourceEnvelope(t, "1.0.1"), rollbackReleaseOne, reportOne, fixedValidator{report: reportOne})
			if err != nil || !switched {
				t.Fatalf("publish one switched=%v err=%v", switched, err)
			}
			generationTwo, switched, err := disk.PublishGeneration(context.Background(), rollbackSourceEnvelope(t, "1.0.2"), rollbackReleaseTwo, reportTwo, fixedValidator{report: reportTwo})
			if err != nil || !switched {
				t.Fatalf("publish two switched=%v err=%v", switched, err)
			}

			database := rollbackStateStore(t)
			now := time.Unix(2_000_000_000, 0).UTC()
			if err := database.CreateSession(context.Background(), store.Session{IDHash: deploySessionID, State: store.SessionActive, CreatedAt: now.Add(-time.Minute), LastSeenAt: now, IdleExpiresAt: now.Add(time.Hour), AbsoluteExpiresAt: now.Add(2 * time.Hour)}); err != nil {
				t.Fatal(err)
			}
			commitRollbackRelease(t, database, deploySessionID, rollbackCandidate1, rollbackReleaseOne, generationOne, now)
			commitRollbackRelease(t, database, deploySessionID, rollbackCandidate2, rollbackReleaseTwo, generationTwo, now.Add(time.Minute))
			validator := contentVersionValidator{reports: map[string]release.Report{"1.0.1": reportOne, "1.0.2": reportTwo}}
			publisher, err := NewPublisher(PublisherOptions{Disk: disk, Store: database, Candidates: &fixedPreparer{}, Validator: validator, Now: func() time.Time { return now.Add(2 * time.Minute) }})
			if err != nil {
				t.Fatal(err)
			}
			if err := publisher.Recover(context.Background()); err != nil {
				t.Fatal(err)
			}
			result, rollbackErr := publisher.RollbackForActiveSession(context.Background(), deploySessionID, rollbackReleaseOne, rollbackOperation)
			if crash {
				if !errors.Is(rollbackErr, ErrCrashSimulation) || result.OperationID != "" {
					t.Fatalf("crash result=%+v err=%v", result, rollbackErr)
				}
				cleanDisk, err := NewDisk(root, nil)
				if err != nil {
					t.Fatal(err)
				}
				publisher, err = NewPublisher(PublisherOptions{Disk: cleanDisk, Store: database, Candidates: &fixedPreparer{}, Validator: validator, Now: func() time.Time { return now.Add(3 * time.Minute) }})
				if err != nil {
					t.Fatal(err)
				}
				if err := publisher.Recover(context.Background()); err != nil {
					t.Fatal(err)
				}
				result, rollbackErr = publisher.RollbackForActiveSession(context.Background(), deploySessionID, rollbackReleaseOne, rollbackOperation)
			}
			if rollbackErr != nil || result.OperationID != rollbackOperation || result.ReleaseID != rollbackReleaseOne || result.Version != "1.0.1" || result.ClientAutoDowngrade {
				t.Fatalf("result=%+v err=%v", result, rollbackErr)
			}
			replay, err := publisher.RollbackForActiveSession(context.Background(), deploySessionID, rollbackReleaseOne, rollbackOperation)
			if err != nil || replay != result {
				t.Fatalf("replay=%+v err=%v", replay, err)
			}
			if floor, found, err := database.HighestCommittedVersion(context.Background()); err != nil || !found || floor != "1.0.2" {
				t.Fatalf("floor=%q found=%v err=%v", floor, found, err)
			}
		})
	}
}

func rollbackReport(version string) release.Report {
	return release.Report{SchemaVersion: 1, Verified: true, PackageID: "SuperPan.BaiduBatchTransfer", Channel: "win-x64-stable", Version: version, FileCount: 1, Files: []release.ReportFile{{Name: "asset.nupkg", Size: int64(len(version)), SHA256: strings.Repeat(version[len(version)-1:], 64)}}}
}

func rollbackSourceEnvelope(t *testing.T, version string) *upload.Envelope {
	t.Helper()
	root := t.TempDir()
	path := filepath.Join(root, "asset.nupkg")
	if err := os.WriteFile(path, []byte(version), 0o600); err != nil {
		t.Fatal(err)
	}
	return &upload.Envelope{ExtractionRoot: root, Entries: []upload.Entry{{Name: "asset.nupkg", Path: path, Size: int64(len(version))}}}
}

func rollbackStateStore(t *testing.T) *store.Store {
	t.Helper()
	stateRoot := t.TempDir()
	if err := os.Chmod(stateRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	database, err := store.Open(context.Background(), filepath.Join(stateRoot, "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	return database
}

func commitRollbackRelease(t *testing.T, database *store.Store, sessionID, candidateID, operationID string, generation Generation, at time.Time) {
	t.Helper()
	reportJSON, err := json.Marshal(generation.Report)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.CreateCandidate(context.Background(), store.Candidate{ID: candidateID, SessionID: sessionID, State: store.CandidateUploading, CreatedAt: at, ExpiresAt: at.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := database.ValidateCandidate(context.Background(), candidateID, strings.Repeat("d", 64), string(reportJSON)); err != nil {
		t.Fatal(err)
	}
	if err := database.TransitionCandidate(context.Background(), candidateID, store.CandidatePublishing); err != nil {
		t.Fatal(err)
	}
	if err := database.CreateOperation(context.Background(), store.Operation{ID: operationID, Kind: store.OperationPublish, State: store.OperationPending, CandidateID: candidateID, CreatedAt: at}); err != nil {
		t.Fatal(err)
	}
	if err := database.TransitionOperation(context.Background(), operationID, store.OperationRunning, ""); err != nil {
		t.Fatal(err)
	}
	if err := database.CommitPublishedRelease(context.Background(), store.PublishCommit{Release: store.Release{ID: operationID, Version: generation.Report.Version, GenerationName: generation.Name, CommittedAt: at}, OperationID: operationID, CandidateID: candidateID, Assets: generation.Assets}); err != nil {
		t.Fatal(err)
	}
}
