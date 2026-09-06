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
	inflight, inflightKind, haveInflight, flightErr := publisher.store.InFlightReleaseOperation(ctx)
	if currentErr != nil || flightErr != nil {
		return publisher.recoveryFailure(errors.Join(currentErr, flightErr))
	}
	generation, err := publisher.disk.RecoverGeneration(ctx, publisher.validator)
	if errors.Is(err, os.ErrNotExist) {
		if haveCurrent {
			return publisher.recoveryFailure(errors.New("stable link is missing for recorded release state"))
		}
		if haveInflight {
			if err := publisher.abortInflight(ctx, inflight, inflightKind); err != nil {
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
			if inflightKind == store.OperationPublish && inflight.OperationID == releaseID || inflightKind == store.OperationRollback && inflight.ReleaseID == releaseID {
				return publisher.recoveryFailure(errors.New("current release also has an in-flight operation"))
			}
			if err := publisher.abortInflight(ctx, inflight, inflightKind); err != nil {
				return publisher.recoveryFailure(err)
			}
		}
		publisher.clearProtection()
		return nil
	}
	if !haveInflight {
		return publisher.recoveryFailure(errors.New("stable generation has no matching publish transaction"))
	}
	switch inflightKind {
	case store.OperationPublish:
		if inflight.OperationID != releaseID {
			return publisher.recoveryFailure(errors.New("stable generation has no matching publish transaction"))
		}
		commit := store.PublishCommit{Release: store.Release{ID: releaseID, Version: generation.Report.Version, GenerationName: generation.Name, CommittedAt: publisher.now().UTC(), Notes: generation.Report.NotesMarkdown}, OperationID: inflight.OperationID, CandidateID: inflight.CandidateID, Assets: generation.Assets}
		if err := publisher.store.CommitPublishedRelease(ctx, commit); err != nil {
			return publisher.recoveryFailure(err)
		}
	case store.OperationRollback:
		target, found, targetErr := publisher.store.ReleaseByID(ctx, inflight.ReleaseID)
		if targetErr != nil || !found || target.ID != releaseID || target.GenerationName != generation.Name || target.Version != generation.Report.Version {
			return publisher.recoveryFailure(errors.Join(targetErr, errors.New("stable generation does not match rollback target")))
		}
		assets, assetErr := publisher.store.ReleaseAssets(ctx, target.ID)
		if assetErr != nil || !sameAssets(assets, generation.Assets) {
			return publisher.recoveryFailure(errors.Join(assetErr, errors.New("stable generation assets do not match rollback target")))
		}
		if err := publisher.store.CommitRollback(ctx, inflight.OperationID, inflight.ReleaseID, publisher.now().UTC()); err != nil {
			return publisher.recoveryFailure(err)
		}
	default:
		return publisher.recoveryFailure(errors.New("unknown in-flight release operation"))
	}
	publisher.clearProtection()
	return nil
}

func (publisher *Publisher) abortInflight(ctx context.Context, operation store.ReleaseOperation, kind store.OperationKind) error {
	switch kind {
	case store.OperationPublish:
		if err := publisher.disk.abortUnpublished(operation.OperationID); err != nil {
			return err
		}
		return publisher.store.FailPublish(ctx, operation.OperationID, operation.CandidateID, "internal_failure", publisher.now().UTC())
	case store.OperationRollback:
		target, found, err := publisher.store.ReleaseByID(ctx, operation.ReleaseID)
		if err != nil || !found {
			return errors.Join(err, store.ErrNotFound)
		}
		if err := publisher.disk.abortRollback(operation.OperationID, target.GenerationName); err != nil {
			return err
		}
		return publisher.store.FailRollback(ctx, operation.OperationID, operation.ReleaseID, "internal_failure", publisher.now().UTC())
	default:
		return errors.New("unknown in-flight release operation")
	}
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
