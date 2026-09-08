package httpapi

import (
	"math"
	"sync"
	"time"
)

type RateLimit struct {
	Requests int
	Window   time.Duration
}

type rateEntry struct {
	windowStart  time.Time
	count        int
	denialSigned bool
}

type fixedWindowLimiter struct {
	mu         sync.Mutex
	limit      RateLimit
	maxEntries int
	clock      Clock
	entries    map[string]rateEntry
}

func newFixedWindowLimiter(limit RateLimit, maxEntries int, clock Clock) *fixedWindowLimiter {
	return &fixedWindowLimiter{limit: limit, maxEntries: maxEntries, clock: clock, entries: make(map[string]rateEntry)}
}

func (l *fixedWindowLimiter) allow(key string) (bool, int) {
	allowed, retry, _ := l.charge(key)
	return allowed, retry
}

func (l *fixedWindowLimiter) charge(key string) (bool, int, bool) {
	now := l.clock.Now().UTC()
	l.mu.Lock()
	defer l.mu.Unlock()
	for existingKey, entry := range l.entries {
		if !now.Before(entry.windowStart.Add(l.limit.Window)) {
			delete(l.entries, existingKey)
		}
	}
	entry, exists := l.entries[key]
	if !exists {
		if len(l.entries) >= l.maxEntries {
			return false, retrySeconds(l.oldestReset(now)), false
		}
		l.entries[key] = rateEntry{windowStart: now, count: 1}
		return true, 0, false
	}
	if entry.count >= l.limit.Requests {
		sign := !entry.denialSigned
		entry.denialSigned = true
		l.entries[key] = entry
		return false, retrySeconds(entry.windowStart.Add(l.limit.Window).Sub(now)), sign
	}
	entry.count++
	l.entries[key] = entry
	return true, 0, false
}

func (l *fixedWindowLimiter) oldestReset(now time.Time) time.Duration {
	remaining := l.limit.Window
	for _, entry := range l.entries {
		candidate := entry.windowStart.Add(l.limit.Window).Sub(now)
		if candidate < remaining {
			remaining = candidate
		}
	}
	return remaining
}

func retrySeconds(remaining time.Duration) int {
	seconds := int(math.Ceil(remaining.Seconds()))
	if seconds < 1 {
		return 1
	}
	return seconds
}
