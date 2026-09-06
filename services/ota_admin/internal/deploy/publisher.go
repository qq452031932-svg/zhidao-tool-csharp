package deploy

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

type CandidatePreparer interface {
	RevalidateForConfirm(context.Context, string, string) (candidate.Prepared, error)
}

type PublisherOptions struct {
	Disk       *Disk
	Store      *store.Store
	Candidates CandidatePreparer
	Validator  Validator
	Now        func() time.Time
}
type Publisher struct {
	disk       *Disk
	store      *store.Store
	candidates CandidatePreparer
	validator  Validator
	now        func() time.Time
	lock       *sync.Mutex
	stateMu    sync.RWMutex
	readOnly   error
}
type PublishResult struct {
	OperationID string `json:"operation_id"`
	CandidateID string `json:"candidate_id"`
	ReleaseID   string `json:"release_id"`
	Version     string `json:"version"`
	State       string `json:"state"`
}

type RollbackResult struct {
	OperationID         string `json:"operation_id"`
	ReleaseID           string `json:"release_id"`
	Version             string `json:"version"`
	State               string `json:"state"`
	ClientAutoDowngrade bool   `json:"client_auto_downgrade"`
}

var processLocks sync.Map

func processMutationLock(root string) *sync.Mutex {
	value, _ := processLocks.LoadOrStore(root, &sync.Mutex{})
	return value.(*sync.Mutex)
}

var (
	ErrReadOnly         = errors.New("publisher is in read-only protection")
	ErrRecoveryRequired = errors.New("publisher startup recovery is required")
	ErrRollbackFailed   = errors.New("rollback operation failed")
)

func NewPublisher(options PublisherOptions) (*Publisher, error) {
	if options.Disk == nil || options.Store == nil || options.Candidates == nil || options.Validator == nil {
		return nil, errors.New("publisher dependencies are required")
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	return &Publisher{disk: options.Disk, store: options.Store, candidates: options.Candidates, validator: options.Validator, now: options.Now, lock: processMutationLock(options.Disk.root), readOnly: errors.Join(ErrReadOnly, ErrRecoveryRequired)}, nil
}

func (publisher *Publisher) PublishForActiveSession(ctx context.Context, sessionID, candidateID, operationID string) (PublishResult, error) {
	publisher.lock.Lock()
	defer publisher.lock.Unlock()
	if err := publisher.ReadOnlyError(); err != nil {
		return PublishResult{}, err
	}
	if operation, found, err := publisher.store.PublishOperationForActiveSession(ctx, sessionID, operationID, publisher.now().UTC()); err != nil {
		return PublishResult{}, err
	} else if found {
		if operation.CandidateID != candidateID {
			return PublishResult{}, store.ErrInvalidTransition
		}
		return publisher.replayPublishLocked(ctx, operation)
	}
	prepared, err := publisher.candidates.RevalidateForConfirm(ctx, sessionID, candidateID)
	if err != nil {
		return PublishResult{}, err
	}
	op, created, err := publisher.store.BeginPublishForActiveSession(ctx, sessionID, candidateID, operationID, publisher.now().UTC())
	if err != nil {
		return PublishResult{}, err
	}
	if !created {
		return publisher.replayPublishLocked(ctx, op)
	}
	releaseID := operationID
	generation, switched, err := publisher.disk.PublishGenerationAuthorized(ctx, prepared.Envelope, releaseID, prepared.Report, publisher.validator, func(authContext context.Context) error {
		return publisher.store.RequireActiveSession(authContext, sessionID, publisher.now().UTC())
	})
	if err != nil {
		if switched {
			publisher.protect(err)
		} else if errors.Is(err, ErrCrashSimulation) || errors.Is(err, ErrCleanupIncomplete) {
			publisher.protect(err)
		} else {
			failureCode := normalizedPublishFailureCode(err)
			if failureErr := publisher.store.FailPublish(context.Background(), operationID, candidateID, failureCode, publisher.now().UTC()); failureErr != nil {
				combined := errors.Join(err, failureErr)
				publisher.protect(combined)
				return PublishResult{}, combined
			}
			return PublishResult{}, errors.Join(persistedPublishFailure(failureCode), err)
		}
		return PublishResult{}, err
	}
	committedAt := publisher.now().UTC()
	commit := store.PublishCommit{Release: store.Release{ID: releaseID, Version: generation.Report.Version, GenerationName: generation.Name, CommittedAt: committedAt, Notes: generation.Report.NotesMarkdown}, OperationID: operationID, CandidateID: candidateID, Assets: generation.Assets}
	if err := publisher.store.CommitPublishedRelease(ctx, commit); err != nil {
		publisher.protect(err)
		return PublishResult{}, err
	}
	return PublishResult{OperationID: operationID, CandidateID: candidateID, ReleaseID: releaseID, Version: generation.Report.Version, State: "succeeded"}, nil
}

func (publisher *Publisher) replayPublishLocked(ctx context.Context, operation store.PublishOperation) (PublishResult, error) {
	if operation.State == store.OperationRunning {
		if err := publisher.recoverLocked(ctx); err != nil {
			return PublishResult{}, err
		}
	}
	if operation.State == store.OperationFailed {
		return PublishResult{}, persistedPublishFailure(operation.ResultCode)
	}
	expectedReleaseID := operation.ReleaseID
	if expectedReleaseID == "" {
		expectedReleaseID = operation.OperationID
	}
	published, found, err := publisher.store.PublishedRelease(ctx, expectedReleaseID)
	if err != nil || !found {
		return PublishResult{}, errors.Join(err, store.ErrInvalidTransition)
	}
	return PublishResult{OperationID: operation.OperationID, CandidateID: operation.CandidateID, ReleaseID: published.ID, Version: published.Version, State: "succeeded"}, nil
}

func (publisher *Publisher) RollbackForActiveSession(ctx context.Context, sessionID, releaseID, operationID string) (RollbackResult, error) {
	publisher.lock.Lock()
	defer publisher.lock.Unlock()
	if err := publisher.ReadOnlyError(); err != nil {
		return RollbackResult{}, err
	}
	operation, created, err := publisher.store.BeginRollbackForActiveSession(ctx, sessionID, releaseID, operationID, publisher.now().UTC())
	if err != nil {
		return RollbackResult{}, err
	}
	if !created {
		return publisher.replayRollbackLocked(ctx, sessionID, operation)
	}
	target, found, err := publisher.store.ReleaseByID(ctx, releaseID)
	if err != nil || !found {
		cause := errors.Join(err, store.ErrNotFound)
		return RollbackResult{}, publisher.failRollback(operationID, releaseID, cause)
	}
	if len(target.Assets) == 0 {
		return RollbackResult{}, publisher.failRollback(operationID, releaseID, errors.New("rollback release has no committed assets"))
	}
	generation, switched, err := publisher.disk.RollbackGenerationAuthorized(ctx, operationID, target.GenerationName, target.Version, target.Assets, publisher.validator, func(authContext context.Context) error {
		return publisher.store.RequireActiveSession(authContext, sessionID, publisher.now().UTC())
	})
	if err != nil {
		if switched || errors.Is(err, ErrCrashSimulation) || errors.Is(err, ErrCleanupIncomplete) {
			publisher.protect(err)
			return RollbackResult{}, err
		}
		return RollbackResult{}, publisher.failRollback(operationID, releaseID, err)
	}
	if err := publisher.store.CommitRollback(ctx, operationID, releaseID, publisher.now().UTC()); err != nil {
		publisher.protect(err)
		return RollbackResult{}, err
	}
	return RollbackResult{OperationID: operationID, ReleaseID: releaseID, Version: generation.Report.Version, State: "succeeded", ClientAutoDowngrade: false}, nil
}

func (publisher *Publisher) replayRollbackLocked(ctx context.Context, sessionID string, operation store.ReleaseOperation) (RollbackResult, error) {
	if operation.State == store.OperationRunning {
		if err := publisher.recoverLocked(ctx); err != nil {
			return RollbackResult{}, err
		}
		refreshed, _, err := publisher.store.BeginRollbackForActiveSession(ctx, sessionID, operation.ReleaseID, operation.OperationID, publisher.now().UTC())
		if err != nil {
			return RollbackResult{}, err
		}
		operation = refreshed
	}
	if operation.State == store.OperationFailed {
		return RollbackResult{}, persistedRollbackFailure(operation.ResultCode)
	}
	if operation.State != store.OperationSucceeded || operation.ResultCode != "completed" {
		return RollbackResult{}, store.ErrInvalidTransition
	}
	target, found, err := publisher.store.ReleaseByID(ctx, operation.ReleaseID)
	if err != nil || !found {
		return RollbackResult{}, errors.Join(err, store.ErrInvalidTransition)
	}
	return RollbackResult{OperationID: operation.OperationID, ReleaseID: target.ID, Version: target.Version, State: "succeeded", ClientAutoDowngrade: false}, nil
}

func (publisher *Publisher) failRollback(operationID, releaseID string, cause error) error {
	failureCode := normalizedRollbackFailureCode(cause)
	terminalContext, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if failureErr := publisher.store.FailRollback(terminalContext, operationID, releaseID, failureCode, publisher.now().UTC()); failureErr != nil {
		combined := errors.Join(cause, failureErr)
		publisher.protect(combined)
		return combined
	}
	// Preserve only the durable public classification in the error chain so the
	// first response and every idempotent replay map to the same status/code.
	return fmt.Errorf("%w: %v", persistedRollbackFailure(failureCode), cause)
}

func normalizedPublishFailureCode(err error) string {
	switch {
	case errors.Is(err, release.ErrSignature):
		return "signature_mismatch"
	case errors.Is(err, release.ErrVersionFloor):
		return "version_floor"
	case errors.Is(err, candidate.ErrDigestMismatch):
		return "hash_mismatch"
	case errors.Is(err, release.ErrManifest), errors.Is(err, release.ErrMetadata), errors.Is(err, release.ErrCatalog), errors.Is(err, release.ErrReleasesIndex):
		// The v2 operations/audit schema intentionally exposes the structural
		// release gate through its hash/closure mismatch bucket.
		return "hash_mismatch"
	default:
		return "internal_failure"
	}
}

func persistedPublishFailure(code string) error {
	switch code {
	case "signature_mismatch":
		return release.ErrSignature
	case "version_floor":
		return release.ErrVersionFloor
	case "hash_mismatch":
		return candidate.ErrDigestMismatch
	default:
		return store.ErrInvalidTransition
	}
}

func normalizedRollbackFailureCode(err error) string {
	code := normalizedPublishFailureCode(err)
	if code == "version_floor" {
		return "internal_failure"
	}
	return code
}

func persistedRollbackFailure(code string) error {
	switch code {
	case "signature_mismatch":
		return release.ErrSignature
	case "hash_mismatch":
		return candidate.ErrDigestMismatch
	default:
		return ErrRollbackFailed
	}
}

func (publisher *Publisher) ReadOnlyError() error {
	publisher.stateMu.RLock()
	defer publisher.stateMu.RUnlock()
	return publisher.readOnly
}
func (publisher *Publisher) protect(err error) {
	publisher.stateMu.Lock()
	defer publisher.stateMu.Unlock()
	if publisher.readOnly == nil {
		publisher.readOnly = errors.Join(ErrReadOnly, err)
	}
}
