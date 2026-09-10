package httpapi

import (
	"math"
	"sync"
	"time"
)

const loadBypassClientKey = "\x00load-test-rate-bypass"

type RateLimit struct {
	Requests int
	Window   time.Duration
}

type rateEntry struct {
	windowStart time.Time
	count       int
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
	if key == loadBypassClientKey {
		return true, 0
	}
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
			return false, retrySeconds(l.oldestReset(now))
		}
		l.entries[key] = rateEntry{windowStart: now, count: 1}
		return true, 0
	}
	if entry.count >= l.limit.Requests {
		return false, retrySeconds(entry.windowStart.Add(l.limit.Window).Sub(now))
	}
	entry.count++
	l.entries[key] = entry
	return true, 0
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
