package security

import (
	"bytes"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestPasswordAndRandomTokensAreHashedAndComparedSafely(t *testing.T) {
	password := "a sufficiently long test password"
	first, err := HashPassword(password, bytes.NewReader(bytes.Repeat([]byte{1}, 16)))
	if err != nil {
		t.Fatal(err)
	}
	second, err := HashPassword(password, bytes.NewReader(bytes.Repeat([]byte{2}, 16)))
	if err != nil {
		t.Fatal(err)
	}
	if first == second || strings.Contains(first, password) || !VerifyPassword(password, first) || VerifyPassword(password+"x", first) {
		t.Fatal("password hash did not provide salted one-way verification")
	}

	token := strings.Repeat("token-value-", 4)
	hash := HashToken(token)
	if strings.Contains(hash, token) || !TokenMatchesHash(token, hash) || TokenMatchesHash(token+"x", hash) {
		t.Fatal("random token hash comparison failed")
	}
}

func TestHashPasswordEnforcesCharacterMinimum(t *testing.T) {
	tests := []struct {
		name     string
		password string
		wantOK   bool
	}{
		{name: "seven ASCII characters", password: "1234567"},
		{name: "eight ASCII characters", password: "12345678", wantOK: true},
		{name: "seven Unicode code points", password: "密码密码密码a"},
		{name: "eight Unicode code points", password: "密码密码密码ab", wantOK: true},
		{name: "1024 UTF-8 bytes", password: strings.Repeat("密", 341) + "a", wantOK: true},
		{name: "1025 UTF-8 bytes", password: strings.Repeat("密", 341) + "ab"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			hash, err := HashPassword(test.password, bytes.NewReader(bytes.Repeat([]byte{1}, 16)))
			if test.wantOK && (err != nil || hash == "") {
				t.Fatalf("HashPassword() hash=%q err=%v, want success", hash, err)
			}
			if !test.wantOK && err == nil {
				t.Fatalf("HashPassword() succeeded for %d bytes, want rejection", len(test.password))
			}
		})
	}
}

func TestOriginValidationAndForwardedSourceTrustAreStrict(t *testing.T) {
	request := httptest.NewRequest("POST", "http://backend/admin/api/test", nil)
	request.Header.Set("Origin", "https://ota.fanfh.cn:443")
	request.Header.Set("Referer", "https://ota.fanfh.cn/admin/")
	if !SameOriginRequest(request, "https://ota.fanfh.cn") {
		t.Fatal("canonical same origin was rejected")
	}
	request.Header.Set("Referer", "https://ota.fanfh.cn.evil.example/admin/")
	if SameOriginRequest(request, "https://ota.fanfh.cn") {
		t.Fatal("lookalike referer was accepted")
	}

	request.RemoteAddr = "203.0.113.7:1234"
	request.Header.Set("X-Forwarded-For", "198.51.100.8")
	if got := TrustedSourceAddress(request); got != "203.0.113.7" {
		t.Fatalf("untrusted forwarded address=%q", got)
	}
	request.RemoteAddr = "127.0.0.1:1234"
	if got := TrustedSourceAddress(request); got != "198.51.100.8" {
		t.Fatalf("trusted forwarded address=%q", got)
	}
	request.Header.Set("X-Forwarded-For", "198.51.100.8, 203.0.113.9")
	if got := TrustedSourceAddress(request); got != "127.0.0.1" {
		t.Fatalf("ambiguous forwarded chain=%q", got)
	}
}

func TestRateLimiterLocksByEitherAccountOrPseudonymizedSource(t *testing.T) {
	limiter := NewRateLimiter(5, 15*time.Minute, 15*time.Minute)
	now := time.Unix(1_000, 0)
	for index := range 5 {
		limiter.RecordFailure("admin", "source-"+string(rune('a'+index)), now)
	}
	if locked, _ := limiter.Locked("admin", "new-source", now); !locked {
		t.Fatal("account was not locked across sources")
	}

	limiter = NewRateLimiter(5, 15*time.Minute, 15*time.Minute)
	for index := range 5 {
		limiter.RecordFailure("name-"+string(rune('a'+index)), "same-source", now)
	}
	if locked, _ := limiter.Locked("new-name", "same-source", now); !locked {
		t.Fatal("source was not locked across account names")
	}
	if locked, _ := limiter.Locked("new-name", "same-source", now.Add(15*time.Minute+time.Nanosecond)); locked {
		t.Fatal("source lock did not expire")
	}
}

func TestRateLimiterDoesNotRetainUnboundedExpiredIdentityChurn(t *testing.T) {
	limiter := NewRateLimiter(5, time.Minute, time.Minute)
	now := time.Unix(10_000, 0)
	for index := range 10_000 {
		limiter.RecordFailure("account-"+string(rune(index)), "source-"+string(rune(index)), now)
	}
	limiter.Locked("admin", "current-source", now.Add(2*time.Minute))
	if len(limiter.entries) > 4096 {
		t.Fatalf("rate limiter retained %d entries, maximum %d", len(limiter.entries), 4096)
	}
}

func TestRateLimiterCapacityNeverEvictsAnActiveAccountLock(t *testing.T) {
	limiter := NewRateLimiter(5, 15*time.Minute, 15*time.Minute)
	now := time.Unix(20_000, 0)
	for range 5 {
		limiter.RecordFailure("admin", "initial-source", now)
	}
	for index := range 10_000 {
		limiter.RecordFailure("churn-"+string(rune(index)), "source-"+string(rune(index)), now)
	}
	if locked, _ := limiter.Locked("admin", "new-source", now); !locked {
		t.Fatal("active administrator lock was evicted by identity churn")
	}
}
