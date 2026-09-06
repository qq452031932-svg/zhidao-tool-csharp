package deploy

import (
	"bytes"
	"context"
	"errors"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/store"
)

type fakeRetentionStore struct {
	targets          []store.ReleaseHistoryItem
	finished         []bool
	pending          store.ReleaseHistoryItem
	pendingOperation string
	finishedSignal   chan struct{}
	failBetweenRuns  bool
	failedOnce       bool
}

func (storage *fakeRetentionStore) InFlightRetentionCleanup(context.Context) (store.ReleaseHistoryItem, string, bool, error) {
	if storage.pending.ID == "" {
		return store.ReleaseHistoryItem{}, "", false, nil
	}
	target, operationID := storage.pending, storage.pendingOperation
	storage.pending = store.ReleaseHistoryItem{}
	return target, operationID, true, nil
}

func (storage *fakeRetentionStore) BeginRetentionCleanup(context.Context, string, time.Time) (store.ReleaseHistoryItem, bool, error) {
	if storage.failBetweenRuns && len(storage.finished) == 1 && !storage.failedOnce {
		storage.failedOnce = true
		return store.ReleaseHistoryItem{}, false, errors.New("retry on next worker tick")
	}
	if len(storage.targets) == 0 {
		return store.ReleaseHistoryItem{}, false, nil
	}
	target := storage.targets[0]
	storage.targets = storage.targets[1:]
	return target, true, nil
}

func (storage *fakeRetentionStore) FinishRetentionCleanup(_ context.Context, _, _ string, succeeded bool, _ time.Time) error {
	storage.finished = append(storage.finished, succeeded)
	if storage.finishedSignal != nil {
		storage.finishedSignal <- struct{}{}
	}
	return nil
}

type fakeGenerationRemover struct {
	removed []string
	err     error
}

func (remover *fakeGenerationRemover) mutationLockKey() string { return "fake-retention-root" }

type blockingGenerationRemover struct {
	entered chan struct{}
	release chan struct{}
}

func (remover *blockingGenerationRemover) mutationLockKey() string { return "blocking-retention-root" }
func (remover *blockingGenerationRemover) RemoveRetainedGeneration(string) error {
	close(remover.entered)
	<-remover.release
	return nil
}

func (remover *fakeGenerationRemover) RemoveRetainedGeneration(name string) error {
	remover.removed = append(remover.removed, name)
	return remover.err
}

func TestRetentionRemovesOnlyTheStoreProvenGenerationAndAuditsCompletion(t *testing.T) {
	storage := &fakeRetentionStore{targets: []store.ReleaseHistoryItem{{Release: store.Release{ID: testReleaseID, GenerationName: "release-" + testReleaseID}, State: "removed"}}}
	remover := &fakeGenerationRemover{}
	service, err := NewRetention(RetentionOptions{Store: storage, Generations: remover, Random: bytes.NewReader(make([]byte, 32)), Now: func() time.Time { return time.Unix(2_000_000_000, 0).UTC() }})
	if err != nil {
		t.Fatal(err)
	}
	if err := service.Run(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(remover.removed) != 1 || remover.removed[0] != "release-"+testReleaseID || len(storage.finished) != 1 || !storage.finished[0] {
		t.Fatalf("removed=%v finished=%v", remover.removed, storage.finished)
	}
}

func TestRetentionCleanupFailureIsPersistedButReturnedWithoutASecondDelete(t *testing.T) {
	want := errors.New("disk cleanup failed")
	storage := &fakeRetentionStore{targets: []store.ReleaseHistoryItem{{Release: store.Release{ID: testReleaseID, GenerationName: "release-" + testReleaseID}, State: "removed"}}}
	remover := &fakeGenerationRemover{err: want}
	service, err := NewRetention(RetentionOptions{Store: storage, Generations: remover, Random: bytes.NewReader(make([]byte, 16)), Now: time.Now})
	if err != nil {
		t.Fatal(err)
	}
	if err := service.Run(context.Background()); !errors.Is(err, want) {
		t.Fatalf("Run() error=%v", err)
	}
	if len(remover.removed) != 1 || len(storage.finished) != 1 || storage.finished[0] {
		t.Fatalf("removed=%v finished=%v", remover.removed, storage.finished)
	}
}

func TestRetentionResumesAnInFlightCleanupOperationWithoutAllocatingAnother(t *testing.T) {
	storage := &fakeRetentionStore{pending: store.ReleaseHistoryItem{Release: store.Release{ID: testReleaseID, GenerationName: "release-" + testReleaseID}, State: "removed"}, pendingOperation: "77777777-7777-4777-8777-777777777777"}
	remover := &fakeGenerationRemover{}
	service, err := NewRetention(RetentionOptions{Store: storage, Generations: remover, Random: bytes.NewReader(make([]byte, 16)), Now: time.Now})
	if err != nil {
		t.Fatal(err)
	}
	if err := service.Run(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(remover.removed) != 1 || len(storage.finished) != 1 || !storage.finished[0] {
		t.Fatalf("removed=%v finished=%v", remover.removed, storage.finished)
	}
}

func TestRetentionAndPublisherForOneRootShareTheMutationLock(t *testing.T) {
	publisher, _, _, _, _ := newPublisherHarness(t, nil)
	retention, err := NewRetention(RetentionOptions{Store: &fakeRetentionStore{}, Generations: publisher.disk, Random: bytes.NewReader(make([]byte, 16)), Now: time.Now})
	if err != nil {
		t.Fatal(err)
	}
	if retention.lock != publisher.lock {
		t.Fatal("retention and publisher received different mutation locks")
	}
}

func TestRetentionHoldsMutationLockThroughGenerationRemoval(t *testing.T) {
	target := store.ReleaseHistoryItem{Release: store.Release{ID: testReleaseID, GenerationName: "release-" + testReleaseID}, State: "removed"}
	remover := &blockingGenerationRemover{entered: make(chan struct{}), release: make(chan struct{})}
	retention, err := NewRetention(RetentionOptions{Store: &fakeRetentionStore{targets: []store.ReleaseHistoryItem{target}}, Generations: remover, Random: bytes.NewReader(make([]byte, 32)), Now: time.Now})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- retention.Run(context.Background()) }()
	select {
	case <-remover.entered:
	case <-time.After(time.Second):
		t.Fatal("retention did not reach generation removal")
	}
	if retention.lock.TryLock() {
		retention.lock.Unlock()
		close(remover.release)
		t.Fatal("mutation lock was released before generation removal completed")
	}
	close(remover.release)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

func TestRetentionStartRunsAtStartupAndOnThePeriodicRetry(t *testing.T) {
	signal := make(chan struct{}, 2)
	target := store.ReleaseHistoryItem{Release: store.Release{ID: testReleaseID, GenerationName: "release-" + testReleaseID}, State: "removed"}
	storage := &fakeRetentionStore{targets: []store.ReleaseHistoryItem{target, target}, finishedSignal: signal, failBetweenRuns: true}
	randomBytes := append(make([]byte, 16), bytes.Repeat([]byte{1}, 16)...)
	randomBytes = append(randomBytes, bytes.Repeat([]byte{2}, 16)...)
	retention, err := NewRetention(RetentionOptions{
		Store: storage, Generations: &fakeGenerationRemover{}, Random: bytes.NewReader(randomBytes), Now: time.Now,
		Interval: 10 * time.Millisecond, AttemptTimeout: time.Second,
	})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	retention.Start(ctx)
	for attempt := range 2 {
		select {
		case <-signal:
		case <-time.After(time.Second):
			t.Fatalf("cleanup attempt %d was not run", attempt+1)
		}
	}
}
