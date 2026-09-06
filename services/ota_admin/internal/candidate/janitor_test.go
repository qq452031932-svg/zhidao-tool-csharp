package candidate

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"
)

func TestJanitorExpiresAndDeletesOnlyDerivedCandidatePaths(t *testing.T) {
	service, database, staging, now := newCandidateHarness(t)
	summary, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	sentinel := filepath.Join(staging, "keep.txt")
	if err := os.WriteFile(sentinel, []byte("keep"), 0o600); err != nil {
		t.Fatal(err)
	}
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return now.Add(24 * time.Hour) }})
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
	summary, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
	if err != nil {
		t.Fatal(err)
	}
	janitor, err := NewJanitor(JanitorOptions{Store: database, StagingRoot: staging, Now: func() time.Time { return now.Add(24 * time.Hour) }})
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
	janitor.deleteCandidate = realDelete
	if err := janitor.RunOnce(context.Background()); err != nil {
		t.Fatalf("retry: %v", err)
	}
	if _, err := os.Lstat(service.paths(summary.CandidateID).archive); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("archive remains after retry: %v", err)
	}
}

func TestJanitorRefusesUnexpectedExtractionMembersAndStartDoesNotBlock(t *testing.T) {
	service, database, staging, now := newCandidateHarness(t)
	summary, err := service.AcceptEnvelopeForActiveSession(context.Background(), testSessionID, testOperationID, receiveGolden(t, staging))
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
