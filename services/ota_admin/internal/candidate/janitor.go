package candidate

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"io"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/store"
)

type JanitorOptions struct {
	Store       *store.Store
	StagingRoot string
	Interval    time.Duration
	Now         func() time.Time
	Random      io.Reader
	OnError     func(error)
}

type Janitor struct {
	store           *store.Store
	stagingRoot     string
	interval        time.Duration
	now             func() time.Time
	random          io.Reader
	onError         func(error)
	randomMu        sync.Mutex
	deleteCandidate func(string) error
}

func NewJanitor(options JanitorOptions) (*Janitor, error) {
	if options.Store == nil {
		return nil, errors.New("janitor store is required")
	}
	root, err := secureStagingRoot(options.StagingRoot)
	if err != nil {
		return nil, err
	}
	if options.Interval == 0 {
		options.Interval = time.Hour
	}
	if options.Interval <= 0 || options.Interval > time.Hour {
		return nil, errors.New("janitor interval must be positive and at most one hour")
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	if options.Random == nil {
		options.Random = rand.Reader
	}
	janitor := &Janitor{store: options.Store, stagingRoot: root, interval: options.Interval, now: options.Now, random: options.Random, onError: options.OnError}
	janitor.deleteCandidate = func(id string) error { return deleteCandidatePaths(root, id) }
	return janitor, nil
}

func (janitor *Janitor) RunOnce(ctx context.Context) error {
	if janitor == nil {
		return errors.New("janitor is nil")
	}
	if err := cleanupStaleUploadArtifacts(janitor.stagingRoot, janitor.now().UTC().Add(-24*time.Hour)); err != nil {
		return err
	}
	if err := scanCandidateArtifacts(janitor.stagingRoot, func(candidateID string) error {
		_, err := janitor.store.RequeueCandidateCleanupIfDue(ctx, candidateID, janitor.now().UTC())
		return err
	}); err != nil {
		return err
	}
	const pageSize = 64
	for {
		ids, err := janitor.store.QueueExpiredCandidateCleanups(ctx, janitor.now().UTC(), pageSize)
		if err != nil {
			return err
		}
		if len(ids) < pageSize {
			break
		}
	}
	var failures []error
	for {
		claimToken, err := janitor.nextOperationID()
		if err != nil {
			return errors.Join(errors.Join(failures...), err)
		}
		operationIDs := make([]string, 1)
		for index := range operationIDs {
			operationIDs[index], err = janitor.nextOperationID()
			if err != nil {
				return errors.Join(errors.Join(failures...), err)
			}
		}
		claims, err := janitor.store.ClaimCandidateCleanups(ctx, janitor.now().UTC(), 5*time.Minute, 1, claimToken, operationIDs)
		if err != nil {
			return errors.Join(errors.Join(failures...), err)
		}
		if len(claims) == 0 {
			break
		}
		for _, claim := range claims {
			if err := ctx.Err(); err != nil {
				return errors.Join(errors.Join(failures...), err)
			}
			cleanupErr := janitor.deleteCandidate(claim.CandidateID)
			var ledgerErr error
			if cleanupErr == nil {
				ledgerErr = janitor.store.CompleteCandidateCleanup(ctx, claim, janitor.now().UTC())
			} else {
				ledgerErr = janitor.store.FailCandidateCleanup(ctx, claim, janitor.now().UTC())
			}
			if cleanupErr != nil || ledgerErr != nil {
				var cleanupFailure error
				if cleanupErr != nil {
					cleanupFailure = fmt.Errorf("clean candidate %s: %w", claim.CandidateID, cleanupErr)
				}
				failures = append(failures, errors.Join(cleanupFailure, ledgerErr))
			}
		}
	}
	return errors.Join(failures...)
}

func (janitor *Janitor) Start(ctx context.Context) {
	go func() {
		janitor.report(janitor.RunOnce(ctx))
		ticker := time.NewTicker(janitor.interval)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				janitor.report(janitor.RunOnce(ctx))
			}
		}
	}()
}

func (janitor *Janitor) report(err error) {
	if err != nil && janitor.onError != nil {
		janitor.onError(err)
	}
}

func (janitor *Janitor) nextOperationID() (string, error) {
	janitor.randomMu.Lock()
	defer janitor.randomMu.Unlock()
	return readUUID(janitor.random)
}

func readUUID(reader io.Reader) (string, error) {
	var raw [16]byte
	if _, err := io.ReadFull(reader, raw[:]); err != nil {
		return "", fmt.Errorf("generate operation ID: %w", err)
	}
	raw[6] = raw[6]&0x0f | 0x40
	raw[8] = raw[8]&0x3f | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", raw[0:4], raw[4:6], raw[6:8], raw[8:10], raw[10:16]), nil
}
