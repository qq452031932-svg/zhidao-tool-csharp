package deploy

import (
	"context"
	"errors"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
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

var processLocks sync.Map

var (
	ErrReadOnly         = errors.New("publisher is in read-only protection")
	ErrRecoveryRequired = errors.New("publisher startup recovery is required")
)

func NewPublisher(options PublisherOptions) (*Publisher, error) {
	if options.Disk == nil || options.Store == nil || options.Candidates == nil || options.Validator == nil {
		return nil, errors.New("publisher dependencies are required")
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	lockValue, _ := processLocks.LoadOrStore(options.Disk.root, &sync.Mutex{})
	return &Publisher{disk: options.Disk, store: options.Store, candidates: options.Candidates, validator: options.Validator, now: options.Now, lock: lockValue.(*sync.Mutex), readOnly: errors.Join(ErrReadOnly, ErrRecoveryRequired)}, nil
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
		if op.State == store.OperationSucceeded {
			return PublishResult{OperationID: operationID, CandidateID: candidateID, ReleaseID: op.ReleaseID, Version: prepared.Report.Version, State: "succeeded"}, nil
		}
		if op.State == store.OperationFailed {
			return PublishResult{}, store.ErrInvalidTransition
		}
	}
	releaseID := operationID
	generation, switched, err := publisher.disk.PublishGeneration(ctx, prepared.Envelope, releaseID, prepared.Report, publisher.validator)
	if err != nil {
		if switched {
			publisher.protect(err)
		} else if errors.Is(err, ErrCrashSimulation) {
			publisher.protect(err)
		} else {
			if failureErr := publisher.store.FailPublish(context.Background(), operationID, candidateID, "internal_failure", publisher.now().UTC()); failureErr != nil {
				combined := errors.Join(err, failureErr)
				publisher.protect(combined)
				return PublishResult{}, combined
			}
		}
		return PublishResult{}, err
	}
	committedAt := publisher.now().UTC()
	commit := store.PublishCommit{Release: store.Release{ID: releaseID, Version: generation.Report.Version, GenerationName: generation.Name, CommittedAt: committedAt}, OperationID: operationID, CandidateID: candidateID, Assets: generation.Assets}
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
		return PublishResult{}, store.ErrInvalidTransition
	}
	current, _, found, err := publisher.store.CurrentRelease(ctx)
	expectedReleaseID := operation.ReleaseID
	if operation.State == store.OperationRunning {
		expectedReleaseID = operation.OperationID
	}
	if err != nil || !found || current.ID != expectedReleaseID {
		return PublishResult{}, errors.Join(err, store.ErrInvalidTransition)
	}
	return PublishResult{OperationID: operation.OperationID, CandidateID: operation.CandidateID, ReleaseID: current.ID, Version: current.Version, State: "succeeded"}, nil
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
