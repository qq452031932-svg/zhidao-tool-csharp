package candidate

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/store"
)

func TestJanitorExpiresAndDeletesOnlyDerivedCandidatePaths(t *testing.T) {
	service, database, staging, now := newCandidateHarness(t)
	summary, err := service.AcceptNewEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	sentinel := filepath.Join(staging, "keep.txt")
	if err := os.WriteFile(sentinel, []byte("keep"), 0o600); err != nil {
		t.Fatal(err)
	}
	current := now.Add(24 * time.Hour)
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return current }})
	if err != nil {
		t.Fatal(err)
	}
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	paths := service.paths(summary.CandidateID)
	for _, path := range []string{paths.archive, paths.extraction} {
		if _, err := os.Lstat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("candidate path remains %s: %v", path, err)
		}
	}
	if content, err := os.ReadFile(sentinel); err != nil || string(content) != "keep" {
		t.Fatalf("sentinel changed content=%q err=%v", content, err)
	}
}

func TestJanitorAuditsFailureAndRetriesCleanup(t *testing.T) {
	service, database, staging, now := newCandidateHarness(t)
	summary, err := service.AcceptNewEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	current := now.Add(24 * time.Hour)
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return current }})
	if err != nil {
		t.Fatal(err)
	}
	realDelete := janitor.deleteCandidate
	janitor.deleteCandidate = func(string) error { return errors.New("injected cleanup failure") }
	if err := janitor.RunOnce(context.Background()); err == nil {
		t.Fatal("cleanup failure was not surfaced")
	}
	if _, err := os.Stat(service.paths(summary.CandidateID).archive); err != nil {
		t.Fatalf("failed cleanup removed retry evidence: %v", err)
	}
	current = current.Add(6 * time.Minute)
	janitor.deleteCandidate = realDelete
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatalf("retry: %v", err)
	}
	if _, err := os.Lstat(service.paths(summary.CandidateID).archive); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("archive remains after retry: %v", err)
	}
}

func TestJanitorCompletesLedgerWhenPathsAlreadyDeletedAfterAuditFailure(t *testing.T) {
	service, database, staging, base := newCandidateHarness(t)
	summary, err := service.AcceptNewEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	current := base.Add(24 * time.Hour)
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return current }})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	realDelete := janitor.deleteCandidate
	janitor.deleteCandidate = func(id string) error { err := realDelete(id); cancel(); return err }
	if err := janitor.RunOnce(ctx); err == nil {
		t.Fatal("canceled audit transaction succeeded")
	}
	if _, err := os.Lstat(service.paths(summary.CandidateID).archive); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("archive remains: %v", err)
	}
	current = current.Add(6 * time.Minute)
	var retried atomic.Int32
	janitor.deleteCandidate = func(id string) error { retried.Add(1); return realDelete(id) }
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if retried.Load() != 1 {
		t.Fatalf("retry attempts=%d", retried.Load())
	}
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if retried.Load() != 1 {
		t.Fatalf("completed cleanup was reclaimed: %d", retried.Load())
	}
}

func TestJanitorRefusesUnexpectedExtractionMembersAndStartDoesNotBlock(t *testing.T) {
	service, database, staging, now := newCandidateHarness(t)
	summary, err := service.AcceptNewEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	paths := service.paths(summary.CandidateID)
	if err := os.Mkdir(filepath.Join(paths.extraction, "unexpected-directory"), 0o700); err != nil {
		t.Fatal(err)
	}
	var errorsSeen atomic.Int32
	janitor, err := NewJanitor(JanitorOptions{
		Store: database, StagingRoot: staging, Interval: 10 * time.Millisecond,
		Now:     func() time.Time { return now.Add(24 * time.Hour) },
		OnError: func(error) { errorsSeen.Add(1) },
	})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	janitor.Start(ctx)
	deadline := time.Now().Add(3 * time.Second)
	for errorsSeen.Load() == 0 && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	cancel()
	if errorsSeen.Load() == 0 {
		t.Fatal("startup janitor did not report safe cleanup refusal")
	}
	if _, err := os.Stat(paths.archive); err != nil {
		t.Fatalf("unsafe cleanup removed archive: %v", err)
	}
}

func TestJanitorRemovesOnlyStaleBoundedRawUploadArtifacts(t *testing.T) {
	_, database, staging, base := newCandidateHarness(t)
	envelope := receiveGolden(t, staging)
	old := base.Add(-25 * time.Hour)
	for _, entry := range envelope.Entries {
		if err := os.Chtimes(entry.Path, old, old); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.Chtimes(envelope.ArchivePath, old, old); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(envelope.ExtractionRoot, old, old); err != nil {
		t.Fatal(err)
	}
	fresh := filepath.Join(staging, ".upload-fresh")
	if err := os.WriteFile(fresh, []byte("keep"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(fresh, base, base); err != nil {
		t.Fatal(err)
	}
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return base }})
	if err != nil {
		t.Fatal(err)
	}
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{envelope.ArchivePath, envelope.ExtractionRoot} {
		if _, err := os.Lstat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("stale path remains %s: %v", path, err)
		}
	}
	if _, err := os.Stat(fresh); err != nil {
		t.Fatalf("fresh upload removed: %v", err)
	}
}

func TestJanitorRawArtifactScanCannotStarveBehindMoreThanOnePage(t *testing.T) {
	_, database, staging, base := newCandidateHarness(t)
	for index := range 300 {
		name := filepath.Join(staging, fmt.Sprintf("000-junk-%03d", index))
		if err := os.WriteFile(name, []byte("keep"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	stale := filepath.Join(staging, ".upload-stale")
	if err := os.WriteFile(stale, []byte("old"), 0o600); err != nil {
		t.Fatal(err)
	}
	old := base.Add(-25 * time.Hour)
	if err := os.Chtimes(stale, old, old); err != nil {
		t.Fatal(err)
	}
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return base }})
	if err != nil {
		t.Fatal(err)
	}
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(stale); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("stale artifact starved behind first page: %v", err)
	}
}

func TestJanitorUsesFreshTimeWhenClaimingExpiredCleanupLease(t *testing.T) {
	service, database, staging, base := newCandidateHarness(t)
	summary, err := service.AcceptNewEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	expiry := base.Add(24 * time.Hour)
	if _, err := database.QueueExpiredCandidateCleanups(context.Background(), expiry, 1); err != nil {
		t.Fatal(err)
	}
	oldToken := "33333333-3333-4333-8333-333333333333"
	oldOperation := "44444444-4444-4444-8444-444444444444"
	claims, err := database.ClaimCandidateCleanups(context.Background(), expiry, time.Minute, 1, oldToken, []string{oldOperation})
	if err != nil || len(claims) != 1 {
		t.Fatalf("old claim=%+v err=%v", claims, err)
	}
	times := []time.Time{expiry, expiry, expiry.Add(2 * time.Minute)}
	index := 0
	janitor, err := NewJanitor(JanitorOptions{
		Store: database, StagingRoot: staging,
		Now: func() time.Time {
			if index >= len(times) {
				return times[len(times)-1]
			}
			value := times[index]
			index++
			return value
		},
		Random: bytes.NewReader(bytes.Repeat([]byte{0x91}, 4096)),
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(service.paths(summary.CandidateID).archive); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("freshly expired cleanup lease was not reclaimed: %v", err)
	}
}

func TestJanitorRequeuesCompletedCleanupWhenLateAdoptionRestoresCandidatePaths(t *testing.T) {
	service, database, staging, base := newCandidateHarness(t)
	summary, err := service.AcceptNewEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	expired := base.Add(24 * time.Hour)
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return expired }, Random: bytes.NewReader(bytes.Repeat([]byte{0x81}, 4096))})
	if err != nil {
		t.Fatal(err)
	}
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	paths := service.paths(summary.CandidateID)
	if err := os.WriteFile(paths.archive, []byte("late archive"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(paths.extraction, 0o700); err != nil {
		t.Fatal(err)
	}
	restarted, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return expired }, Random: bytes.NewReader(bytes.Repeat([]byte{0x82}, 4096))})
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.RunOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{paths.archive, paths.extraction} {
		if _, err := os.Lstat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("late-adopted candidate path remains %s: %v", path, err)
		}
	}
}

func TestJanitorContinuesPastOneCleanupFailureAndBacksOffItsRetry(t *testing.T) {
	service, database, staging, base := newCandidateHarness(t)
	first, err := service.AcceptNewEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	secondID := "99999999-9999-4999-8999-999999999999"
	if err := database.CreateCandidate(context.Background(), store.Candidate{ID: secondID, SessionID: testSessionID, State: store.CandidateUploading, CreatedAt: base, ExpiresAt: base.Add(24 * time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := database.TransitionCandidate(context.Background(), secondID, store.CandidateFailed); err != nil {
		t.Fatal(err)
	}
	secondPaths := service.paths(secondID)
	if err := os.WriteFile(secondPaths.archive, []byte("candidate"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(secondPaths.extraction, 0o700); err != nil {
		t.Fatal(err)
	}
	now := base.Add(24 * time.Hour)
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return now }, Random: uniqueUUIDReader(16)})
	if err != nil {
		t.Fatal(err)
	}
	attempts := 0
	janitor.deleteCandidate = func(id string) error {
		if id == first.CandidateID {
			attempts++
			return errors.New("injected cleanup failure")
		}
		return deleteCandidatePaths(staging, id)
	}
	if err := janitor.RunOnce(context.Background()); err == nil {
		t.Fatal("cleanup failure was not reported")
	}
	if attempts != 1 {
		t.Fatalf("failed cleanup attempts=%d", attempts)
	}
	for _, path := range []string{secondPaths.archive, secondPaths.extraction} {
		if _, err := os.Lstat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("later candidate starved at %s: %v", path, err)
		}
	}
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatalf("backoff run err=%v", err)
	}
	if attempts != 1 {
		t.Fatalf("failed cleanup retried without backoff: %d", attempts)
	}
}

func uniqueUUIDReader(count int) *bytes.Reader {
	data := make([]byte, count*16)
	for index := range count {
		for offset := range 16 {
			data[index*16+offset] = byte(index + 1)
		}
	}
	return bytes.NewReader(data)
}
