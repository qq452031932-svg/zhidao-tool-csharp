package deploy

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

type RetentionStore interface {
	InFlightRetentionCleanup(context.Context) (store.ReleaseHistoryItem, string, bool, error)
	BeginRetentionCleanup(context.Context, string, time.Time) (store.ReleaseHistoryItem, bool, error)
	FinishRetentionCleanup(context.Context, string, string, bool, time.Time) error
}

type GenerationRemover interface {
	RemoveRetainedGeneration(string) error
	mutationLockKey() string
}

type RetentionOptions struct {
	Store          RetentionStore
	Generations    GenerationRemover
	Random         io.Reader
	Now            func() time.Time
	Interval       time.Duration
	AttemptTimeout time.Duration
	OnError        func(error)
}

type Retention struct {
	store          RetentionStore
	generations    GenerationRemover
	random         io.Reader
	now            func() time.Time
	lock           *sync.Mutex
	interval       time.Duration
	attemptTimeout time.Duration
	onError        func(error)
	trigger        chan struct{}
	startOnce      sync.Once
}

func NewRetention(options RetentionOptions) (*Retention, error) {
	if options.Store == nil || options.Generations == nil {
		return nil, errors.New("retention dependencies are required")
	}
	if options.Random == nil {
		options.Random = rand.Reader
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	if options.Interval <= 0 {
		options.Interval = time.Hour
	}
	if options.AttemptTimeout <= 0 {
		options.AttemptTimeout = time.Minute
	}
	key := options.Generations.mutationLockKey()
	if key == "" {
		return nil, errors.New("retention mutation lock key is required")
	}
	return &Retention{
		store: options.Store, generations: options.Generations, random: options.Random, now: options.Now,
		lock: processMutationLock(key), interval: options.Interval, attemptTimeout: options.AttemptTimeout,
		onError: options.OnError, trigger: make(chan struct{}, 1),
	}, nil
}

// Start launches the bounded retention worker. It runs once at startup, then
// retries periodically and whenever Trigger is called after a durable publish.
func (retention *Retention) Start(ctx context.Context) {
	retention.startOnce.Do(func() {
		go func() {
			ticker := time.NewTicker(retention.interval)
			defer ticker.Stop()
			retention.runAttempt(ctx)
			for {
				select {
				case <-ctx.Done():
					return
				case <-ticker.C:
					retention.runAttempt(ctx)
				case <-retention.trigger:
					retention.runAttempt(ctx)
				}
			}
		}()
	})
}

// Trigger schedules cleanup without doing filesystem or database work on the
// caller's response path. Duplicate requests collapse into one pending run.
func (retention *Retention) Trigger() {
	select {
	case retention.trigger <- struct{}{}:
	default:
	}
}

func (retention *Retention) runAttempt(parent context.Context) {
	ctx, cancel := context.WithTimeout(parent, retention.attemptTimeout)
	defer cancel()
	if err := retention.Run(ctx); err != nil && retention.onError != nil {
		retention.onError(err)
	}
}

func (retention *Retention) Run(ctx context.Context) error {
	retention.lock.Lock()
	defer retention.lock.Unlock()
	for range 100 {
		target, operationID, pending, err := retention.store.InFlightRetentionCleanup(ctx)
		if err != nil {
			return err
		}
		if !pending {
			operationID, err = retention.nextOperationID()
			if err != nil {
				return err
			}
			var created bool
			target, created, err = retention.store.BeginRetentionCleanup(ctx, operationID, retention.now().UTC())
			if err != nil {
				return err
			}
			if !created && target.ID == "" {
				return nil
			}
		}
		if target.ID == "" || target.GenerationName == "" || target.State != "removed" {
			return store.ErrInvalidTransition
		}
		removeErr := retention.generations.RemoveRetainedGeneration(target.GenerationName)
		finishErr := retention.store.FinishRetentionCleanup(context.WithoutCancel(ctx), operationID, target.ID, removeErr == nil, retention.now().UTC())
		if removeErr != nil || finishErr != nil {
			return errors.Join(removeErr, finishErr)
		}
	}
	return errors.New("retention cleanup batch limit reached")
}

func (retention *Retention) nextOperationID() (string, error) {
	value := make([]byte, 16)
	if _, err := io.ReadFull(retention.random, value); err != nil {
		return "", err
	}
	value[6] = value[6]&0x0f | 0x40
	value[8] = value[8]&0x3f | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x", value[0:4], value[4:6], value[6:8], value[8:10], value[10:16]), nil
}
