package deploy

import (
	"context"
	"errors"
	"os"
	"reflect"
	"sort"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/store"
)

func (publisher *Publisher) Recover(ctx context.Context) error {
	publisher.lock.Lock()
	defer publisher.lock.Unlock()
	return publisher.recoverLocked(ctx)
}

func (publisher *Publisher) recoverLocked(ctx context.Context) error {
	if err := publisher.disk.MigrateEmptyStable(); err != nil {
		return publisher.recoveryFailure(err)
	}
	current, currentAssets, haveCurrent, currentErr := publisher.store.CurrentRelease(ctx)
	inflight, haveInflight, flightErr := publisher.store.InFlightPublish(ctx)
	if currentErr != nil || flightErr != nil {
		return publisher.recoveryFailure(errors.Join(currentErr, flightErr))
	}
	generation, err := publisher.disk.RecoverGeneration(ctx, publisher.validator)
	if errors.Is(err, os.ErrNotExist) {
		if haveCurrent {
			return publisher.recoveryFailure(errors.New("stable link is missing for recorded release state"))
		}
		if haveInflight {
			if err := publisher.abortInflight(ctx, inflight); err != nil {
				return publisher.recoveryFailure(err)
			}
		}
		publisher.clearProtection()
		return nil
	}
	if err != nil {
		return publisher.recoveryFailure(err)
	}
	releaseID := strings.TrimPrefix(generation.Name, "release-")
	if haveCurrent && current.ID == releaseID && current.GenerationName == generation.Name && current.Version == generation.Report.Version && sameAssets(currentAssets, generation.Assets) {
		if haveInflight {
			if inflight.OperationID == releaseID {
				return publisher.recoveryFailure(errors.New("current release also has an in-flight operation"))
			}
			if err := publisher.abortInflight(ctx, inflight); err != nil {
				return publisher.recoveryFailure(err)
			}
		}
		publisher.clearProtection()
		return nil
	}
	if !haveInflight || inflight.OperationID != releaseID {
		return publisher.recoveryFailure(errors.New("stable generation has no matching publish transaction"))
	}
	commit := store.PublishCommit{Release: store.Release{ID: releaseID, Version: generation.Report.Version, GenerationName: generation.Name, CommittedAt: publisher.now().UTC()}, OperationID: inflight.OperationID, CandidateID: inflight.CandidateID, Assets: generation.Assets}
	if err := publisher.store.CommitPublishedRelease(ctx, commit); err != nil {
		return publisher.recoveryFailure(err)
	}
	publisher.clearProtection()
	return nil
}

func (publisher *Publisher) abortInflight(ctx context.Context, operation store.PublishOperation) error {
	if err := publisher.disk.abortUnpublished(operation.OperationID); err != nil {
		return err
	}
	return publisher.store.FailPublish(ctx, operation.OperationID, operation.CandidateID, "internal_failure", publisher.now().UTC())
}

func sameAssets(left, right []store.ReleaseAsset) bool {
	left = append([]store.ReleaseAsset(nil), left...)
	right = append([]store.ReleaseAsset(nil), right...)
	sort.Slice(left, func(i, j int) bool { return left[i].FileName < left[j].FileName })
	sort.Slice(right, func(i, j int) bool { return right[i].FileName < right[j].FileName })
	return reflect.DeepEqual(left, right)
}
func (publisher *Publisher) recoveryFailure(err error) error { publisher.protect(err); return err }
func (publisher *Publisher) clearProtection() {
	publisher.stateMu.Lock()
	publisher.readOnly = nil
	publisher.stateMu.Unlock()
}
