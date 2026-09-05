package security

import (
	"container/list"
	"strings"
	"sync"
	"time"
)

type RateLimiter struct {
	mu      sync.Mutex
	limit   int
	window  time.Duration
	lockFor time.Duration
	entries map[string]*failureEntry
	order   *list.List
}

type failureEntry struct {
	failures    []time.Time
	lockedUntil time.Time
	element     *list.Element
}

const maximumRateEntries = 4096

func NewRateLimiter(limit int, window, lockFor time.Duration) *RateLimiter {
	return &RateLimiter{limit: limit, window: window, lockFor: lockFor, entries: make(map[string]*failureEntry), order: list.New()}
}

func (limiter *RateLimiter) Locked(account, source string, now time.Time) (bool, time.Duration) {
	limiter.mu.Lock()
	defer limiter.mu.Unlock()
	remaining := time.Duration(0)
	for _, key := range rateKeys(account, source) {
		entry := limiter.entries[key]
		if entry == nil {
			continue
		}
		limiter.prune(entry, now)
		if len(entry.failures) == 0 && entry.lockedUntil.IsZero() {
			limiter.remove(key, entry)
			continue
		}
		if entry.lockedUntil.After(now) && entry.lockedUntil.Sub(now) > remaining {
			remaining = entry.lockedUntil.Sub(now)
		}
	}
	return remaining > 0, remaining
}

func (limiter *RateLimiter) RecordFailure(account, source string, now time.Time) {
	limiter.mu.Lock()
	defer limiter.mu.Unlock()
	for _, key := range rateKeys(account, source) {
		entry := limiter.entry(key, now)
		if entry == nil {
			continue
		}
		limiter.prune(entry, now)
		entry.failures = append(entry.failures, now)
		if len(entry.failures) >= limiter.limit {
			entry.lockedUntil = now.Add(limiter.lockFor)
		}
	}
}

func (limiter *RateLimiter) RecordSuccess(account, source string) {
	limiter.mu.Lock()
	defer limiter.mu.Unlock()
	for _, key := range rateKeys(account, source) {
		if entry := limiter.entries[key]; entry != nil {
			limiter.remove(key, entry)
		}
	}
}

func (limiter *RateLimiter) entry(key string, now time.Time) *failureEntry {
	entry := limiter.entries[key]
	if entry == nil {
		if len(limiter.entries) >= maximumRateEntries {
			limiter.removeExpiredFromFront(now)
		}
		if len(limiter.entries) >= maximumRateEntries {
			return nil
		}
		entry = &failureEntry{element: limiter.order.PushBack(key)}
		limiter.entries[key] = entry
	}
	limiter.touch(entry)
	return entry
}

func (limiter *RateLimiter) touch(entry *failureEntry) {
	if entry.element != nil {
		limiter.order.MoveToBack(entry.element)
	}
}

func (limiter *RateLimiter) removeExpiredFromFront(now time.Time) {
	for element := limiter.order.Front(); element != nil; element = limiter.order.Front() {
		key, _ := element.Value.(string)
		entry := limiter.entries[key]
		if entry == nil {
			limiter.order.Remove(element)
			continue
		}
		limiter.prune(entry, now)
		if len(entry.failures) != 0 || !entry.lockedUntil.IsZero() {
			return
		}
		limiter.remove(key, entry)
	}
}

func (limiter *RateLimiter) remove(key string, entry *failureEntry) {
	delete(limiter.entries, key)
	if entry.element != nil {
		limiter.order.Remove(entry.element)
		entry.element = nil
	}
}

func (limiter *RateLimiter) prune(entry *failureEntry, now time.Time) {
	threshold := now.Add(-limiter.window)
	kept := entry.failures[:0]
	for _, failure := range entry.failures {
		if failure.After(threshold) {
			kept = append(kept, failure)
		}
	}
	entry.failures = kept
	if !entry.lockedUntil.After(now) {
		entry.lockedUntil = time.Time{}
		if len(entry.failures) >= limiter.limit {
			entry.failures = nil
		}
	}
}

func rateKeys(account, source string) []string {
	return []string{"account:" + strings.ToLower(strings.TrimSpace(account)), "source:" + source}
}
