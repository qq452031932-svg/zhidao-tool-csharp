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
	if len(storage.targets) == 0 {
		return store.ReleaseHistoryItem{}, false, nil
	}
	target := storage.targets[0]
	storage.targets = storage.targets[1:]
	return target, true, nil
}

func (storage *fakeRetentionStore) FinishRetentionCleanup(_ context.Context, _, _ string, succeeded bool, _ time.Time) error {
	storage.finished = append(storage.finished, succeeded)
	return nil
}

type fakeGenerationRemover struct {
	removed []string
	err     error
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
