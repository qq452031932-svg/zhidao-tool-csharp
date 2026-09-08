package httpapi

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"fanfh.cn/superpan/ota-admin/internal/licensing/service"
	"fanfh.cn/superpan/ota-admin/internal/licensing/signing"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
)

type testClock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *testClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *testClock) Advance(delta time.Duration) {
	c.mu.Lock()
	c.now = c.now.Add(delta)
	c.mu.Unlock()
}

type apiHarness struct {
	t        *testing.T
	clock    *testClock
	store    *licensestore.Store
	handler  http.Handler
	key      *ecdsa.PrivateKey
	card     string
	software string
	machine  string
}

func newAPIHarness(t *testing.T, activationLimit, validationLimit int) *apiHarness {
	t.Helper()
	ctx := context.Background()
	st, err := licensestore.Open(ctx, t.TempDir()+`\license.db`)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = st.Close() })
	clock := &testClock{now: time.Date(2026, 9, 9, 2, 0, 0, 0, time.UTC)}
	svc, err := service.New(st, clock, rand.Reader, bytes.Repeat([]byte{0x11}, 32), bytes.Repeat([]byte{0x22}, 32))
	if err != nil {
		t.Fatal(err)
	}
	software, err := st.CreateSoftware(ctx, "HTTP Product")
	if err != nil {
		t.Fatal(err)
	}
	pkg, err := st.CreatePackage(ctx, "HTTP Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	cards, err := svc.GenerateCards(ctx, pkg.ID, 30, 1, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := signing.New("test-key", key, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	handler, err := NewRouter(Options{
		Service: svc, ReplayStore: st, Signer: signer, Clock: clock,
		MaxBodyBytes: 2048, MaxHeaderBytes: 2048,
		TimestampWindow: 5 * time.Minute, NonceLifetime: 10 * time.Minute,
		MaxReplayNonces:   10_000,
		ActivationRate:    RateLimit{Requests: activationLimit, Window: time.Minute},
		ValidationRate:    RateLimit{Requests: validationLimit, Window: time.Minute},
		MaxLimiterEntries: 32,
	})
	if err != nil {
		t.Fatal(err)
	}
	return &apiHarness{t: t, clock: clock, store: st, handler: handler, key: key, card: cards[0].Plaintext, software: software.ID, machine: "machine-http-1"}
}

func TestActivateAndValidateUseSignedExactEnvelopeAndBusinessIdempotency(t *testing.T) {
	h := newAPIHarness(t, 20, 20)
	first := h.do(http.MethodPost, "/license-api/v1/activate", h.activateBody("activation-stable", nonce(1)))
	if first.Code != http.StatusOK {
		t.Fatalf("first activation status = %d, body=%s", first.Code, first.Body.String())
	}
	firstClaims := decodeClaims(t, first, &h.key.PublicKey)
	if firstClaims.RequestID != "activation-stable" || firstClaims.Decision != model.DecisionAllow || firstClaims.Code != model.CodeOK {
		t.Fatalf("first claims = %#v", firstClaims)
	}
	if firstClaims.PackageName == nil || *firstClaims.PackageName != "HTTP Package" || firstClaims.ExpiresAt == nil || firstClaims.LeaseUntil == nil {
		t.Fatalf("success-only claims missing: %#v", firstClaims)
	}
	assertNotContains(t, first.Body.String(), h.card, h.machine)

	second := h.do(http.MethodPost, "/license-api/v1/activate", h.activateBody("activation-stable", nonce(2)))
	secondClaims := decodeClaims(t, second, &h.key.PublicKey)
	if second.Code != http.StatusOK || secondClaims.RequestID != firstClaims.RequestID || !secondClaims.ExpiresAt.Equal(*firstClaims.ExpiresAt) || !secondClaims.LeaseUntil.Equal(*firstClaims.LeaseUntil) {
		t.Fatalf("idempotent result changed: first=%#v second=%#v", firstClaims, secondClaims)
	}

	validation := h.do(http.MethodPost, "/license-api/v1/validate", h.validateBody("validation-1", nonce(3)))
	validationClaims := decodeClaims(t, validation, &h.key.PublicKey)
	if validation.Code != http.StatusOK || validationClaims.RequestID != "validation-1" || validationClaims.Code != model.CodeOK {
		t.Fatalf("validation response = %d %#v", validation.Code, validationClaims)
	}
}

func TestStrictRequestAndReplayValidation(t *testing.T) {
	h := newAPIHarness(t, 50, 50)
	valid := h.activateBody("activation-1", nonce(10))
	tests := []struct {
		name        string
		method      string
		path        string
		contentType string
		body        string
		header      string
		wantStatus  int
		wantCode    model.Code
	}{
		{name: "wrong content type", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "text/plain", body: valid, wantStatus: 400, wantCode: model.CodeInvalidRequest},
		{name: "unknown field", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "application/json", body: strings.TrimSuffix(valid, "}") + `,"extra":1}`, wantStatus: 400, wantCode: model.CodeInvalidRequest},
		{name: "duplicate field", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "application/json", body: strings.Replace(valid, `"activation_request_id":"activation-1"`, `"activation_request_id":"first","activation_request_id":"second"`, 1), wantStatus: 400, wantCode: model.CodeInvalidRequest},
		{name: "multiple documents", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "application/json", body: valid + `{}`, wantStatus: 400, wantCode: model.CodeInvalidRequest},
		{name: "unsupported protocol", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "application/json", body: strings.Replace(valid, `"protocol_version":1`, `"protocol_version":2`, 1), wantStatus: 400, wantCode: model.CodeUnsupportedProtocol},
		{name: "stale timestamp", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "application/json", body: strings.Replace(valid, h.clock.Now().Format(time.RFC3339), h.clock.Now().Add(-6*time.Minute).Format(time.RFC3339), 1), wantStatus: 400, wantCode: model.CodeInvalidRequest},
		{name: "invalid nonce", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "application/json", body: strings.Replace(valid, nonce(10), "too-short!", 1), wantStatus: 400, wantCode: model.CodeInvalidRequest},
		{name: "body too large", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "application/json", body: strings.Repeat("x", 2049), wantStatus: 400, wantCode: model.CodeInvalidRequest},
		{name: "header too large", method: http.MethodPost, path: "/license-api/v1/activate", contentType: "application/json", body: valid, header: strings.Repeat("h", 2049), wantStatus: 400, wantCode: model.CodeInvalidRequest},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest(tt.method, tt.path, strings.NewReader(tt.body))
			req.Header.Set("Content-Type", tt.contentType)
			if tt.header != "" {
				req.Header.Set("X-Oversized", tt.header)
			}
			recorder := httptest.NewRecorder()
			h.handler.ServeHTTP(recorder, req)
			if recorder.Code != tt.wantStatus {
				t.Fatalf("status = %d, want %d, body=%s", recorder.Code, tt.wantStatus, recorder.Body.String())
			}
			claims := decodeClaims(t, recorder, &h.key.PublicKey)
			if claims.Code != tt.wantCode || claims.Decision != model.DecisionDeny {
				t.Fatalf("claims = %#v", claims)
			}
			if claims.CardID != nil || claims.SoftwareID != nil || claims.MachineHash != nil || claims.PackageName != nil || claims.ExpiresAt != nil || claims.LeaseUntil != nil {
				t.Fatalf("denial success-only fields are not null: %#v", claims)
			}
		})
	}

	accepted := h.do(http.MethodPost, "/license-api/v1/activate", h.activateBody("replay-first", nonce(90)))
	if accepted.Code != http.StatusOK {
		t.Fatalf("initial nonce status = %d", accepted.Code)
	}
	replayed := h.do(http.MethodPost, "/license-api/v1/activate", h.activateBody("replay-second", nonce(90)))
	if replayed.Code != http.StatusBadRequest || decodeClaims(t, replayed, &h.key.PublicKey).Code != model.CodeInvalidRequest {
		t.Fatalf("replay response = %d %s", replayed.Code, replayed.Body.String())
	}
}

func TestTimestampFreshnessRejectsExtremeTimesWithoutDurationOverflow(t *testing.T) {
	now := time.Date(2026, 9, 9, 8, 0, 0, 0, time.UTC)
	for _, timestamp := range []time.Time{
		time.Date(9999, 12, 31, 23, 59, 59, 0, time.UTC),
		time.Date(1, 1, 1, 0, 0, 0, 0, time.UTC),
	} {
		if validPublicFields("request", "card", "software", "machine", timestamp, nonce(500), now, 5*time.Minute) {
			t.Fatalf("extreme timestamp %s was accepted", timestamp)
		}
	}
}

func TestRequestJSONKeysMustUseExactCanonicalNames(t *testing.T) {
	h := newAPIHarness(t, 20, 20)
	body := strings.Replace(h.activateBody("case-request", nonce(501)), `"protocol_version"`, `"PROTOCOL_VERSION"`, 1)
	response := h.do(http.MethodPost, "/license-api/v1/activate", body)
	if response.Code != http.StatusBadRequest || decodeClaims(t, response, &h.key.PublicKey).Code != model.CodeInvalidRequest {
		t.Fatalf("case-variant key response = %d %s", response.Code, response.Body.String())
	}
}

func TestRoutesExposeOnlyTwoPostEndpoints(t *testing.T) {
	h := newAPIHarness(t, 10, 10)
	for _, tc := range []struct{ method, path string }{
		{http.MethodGet, "/license-api/v1/activate"},
		{http.MethodPost, "/license-api/v1/licenses"},
		{http.MethodPost, "/license-api/v1/software"},
		{http.MethodPost, "/activate"},
	} {
		req := httptest.NewRequest(tc.method, tc.path, nil)
		recorder := httptest.NewRecorder()
		h.handler.ServeHTTP(recorder, req)
		if recorder.Code != http.StatusNotFound && recorder.Code != http.StatusMethodNotAllowed {
			t.Fatalf("%s %s status = %d", tc.method, tc.path, recorder.Code)
		}
	}
}

func TestRateLimitsHaveSeparateStableBuckets(t *testing.T) {
	h := newAPIHarness(t, 1, 1)
	if got := h.do(http.MethodPost, "/license-api/v1/activate", h.activateBody("rate-first", nonce(100))); got.Code != 200 {
		t.Fatalf("first activation = %d", got.Code)
	}
	limited := h.do(http.MethodPost, "/license-api/v1/activate", h.activateBody("rate-second", nonce(101)))
	if limited.Code != 429 || limited.Header().Get("Retry-After") != "60" || decodeClaims(t, limited, &h.key.PublicKey).Code != model.CodeRateLimited {
		t.Fatalf("limited = %d retry=%q body=%s", limited.Code, limited.Header().Get("Retry-After"), limited.Body.String())
	}
	if got := h.do(http.MethodPost, "/license-api/v1/validate", h.validateBody("rate-validation", nonce(102))); got.Code != 200 {
		t.Fatalf("validation shared activation bucket: status=%d body=%s", got.Code, got.Body.String())
	}
	h.clock.Advance(30 * time.Second)
	limited = h.do(http.MethodPost, "/license-api/v1/activate", h.activateBody("rate-third", nonce(103)))
	if limited.Code != 429 || limited.Header().Get("Retry-After") != "30" {
		t.Fatalf("stable retry = %d/%q", limited.Code, limited.Header().Get("Retry-After"))
	}
	h.clock.Advance(30 * time.Second)
	if got := h.do(http.MethodPost, "/license-api/v1/activate", h.activateBody("rate-reset", nonce(104))); got.Code != 200 {
		t.Fatalf("reset activation = %d body=%s", got.Code, got.Body.String())
	}
}

type fakeService struct {
	err   error
	calls atomic.Int64
}

func (s *fakeService) Activate(context.Context, string, string, string, string) (model.ActivationState, error) {
	s.calls.Add(1)
	return successState(), s.err
}
func (s *fakeService) Validate(context.Context, string, string, string) (model.ActivationState, error) {
	s.calls.Add(1)
	return successState(), s.err
}

type acceptingReplayStore struct{}

func (acceptingReplayStore) ClaimReplayNonce(context.Context, string, time.Time, time.Time, int) (bool, error) {
	return true, nil
}

type trackingReplayStore struct {
	mu      sync.Mutex
	nonces  []string
	lastMax int
	err     error
}

func (s *trackingReplayStore) ClaimReplayNonce(_ context.Context, nonce string, _ time.Time, _ time.Time, maximum int) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.nonces = append(s.nonces, nonce)
	s.lastMax = maximum
	if s.err != nil {
		return false, s.err
	}
	return true, nil
}

func (s *trackingReplayStore) snapshot() ([]string, int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]string(nil), s.nonces...), s.lastMax
}

type countingSigner struct {
	inner *signing.Signer
	calls atomic.Int64
}

func (s *countingSigner) Sign(claims model.Claims) (model.SignedEnvelope, error) {
	s.calls.Add(1)
	return s.inner.Sign(claims)
}

func TestMalformedTrafficIsChargedBeforeSigningAndSigningStopsAfterFirst429(t *testing.T) {
	for _, route := range []string{"activate", "validate"} {
		t.Run(route, func(t *testing.T) {
			clock := &testClock{now: time.Date(2026, 9, 9, 8, 30, 0, 0, time.UTC)}
			key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
			inner, _ := signing.New("count-key", key, rand.Reader)
			counter := &countingSigner{inner: inner}
			svc := &fakeService{}
			replays := &trackingReplayStore{}
			handler, err := NewRouter(Options{
				Service: svc, ReplayStore: replays, Signer: counter, Clock: clock,
				MaxBodyBytes: 256, MaxHeaderBytes: 256, TimestampWindow: time.Minute, NonceLifetime: time.Minute, MaxReplayNonces: 8,
				ActivationRate: RateLimit{Requests: 1, Window: time.Minute}, ValidationRate: RateLimit{Requests: 1, Window: time.Minute}, MaxLimiterEntries: 8,
			})
			if err != nil {
				t.Fatal(err)
			}
			path := "/license-api/v1/" + route
			idName := "request_id"
			if route == "activate" {
				idName = "activation_request_id"
			}
			unsupported := fmt.Sprintf(`{"protocol_version":2,%q:"unsupported","card":"card","software_id":"software","machine_hash":"machine","timestamp":%q,"nonce":%q}`, idName, clock.Now().Format(time.RFC3339), nonce(510))
			requests := []struct {
				body   string
				header string
				want   int
			}{
				{body: `{`, want: 400},
				{body: unsupported, want: 429},
				{body: strings.Repeat("x", 257), want: 429},
				{body: `{}`, header: strings.Repeat("h", 257), want: 429},
			}
			for index, item := range requests {
				req := httptest.NewRequest(http.MethodPost, path, strings.NewReader(item.body))
				req.Header.Set("Content-Type", "application/json")
				if item.header != "" {
					req.Header.Set("X-Oversized", item.header)
				}
				req.RemoteAddr = "127.0.0.1:42000"
				recorder := httptest.NewRecorder()
				handler.ServeHTTP(recorder, req)
				if recorder.Code != item.want {
					t.Fatalf("request %d status = %d, want %d body=%s", index, recorder.Code, item.want, recorder.Body.String())
				}
			}
			if counter.calls.Load() != 2 {
				t.Fatalf("signature calls = %d, want bounded 2", counter.calls.Load())
			}
			if svc.calls.Load() != 0 {
				t.Fatalf("service calls = %d, want 0", svc.calls.Load())
			}
			if nonces, _ := replays.snapshot(); len(nonces) != 0 {
				t.Fatalf("malformed/rate-limited traffic claimed nonces: %v", nonces)
			}
		})
	}
}

func TestRateLimitedValidRequestsSignOnlyFirstDenialPerWindow(t *testing.T) {
	for _, route := range []string{"activate", "validate"} {
		t.Run(route, func(t *testing.T) {
			clock := &testClock{now: time.Date(2026, 9, 9, 2, 30, 0, 0, time.UTC)}
			key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
			if err != nil {
				t.Fatal(err)
			}
			innerSigner, err := signing.New("rate-key", key, rand.Reader)
			if err != nil {
				t.Fatal(err)
			}
			signer := &countingSigner{inner: innerSigner}
			svc := &fakeService{}
			replays := &trackingReplayStore{}
			handler, err := NewRouter(Options{
				Service: svc, ReplayStore: replays, Signer: signer, Clock: clock,
				MaxBodyBytes: 2048, MaxHeaderBytes: 2048,
				TimestampWindow: time.Minute, NonceLifetime: time.Minute, MaxReplayNonces: 7,
				ActivationRate: RateLimit{Requests: 1, Window: time.Minute},
				ValidationRate: RateLimit{Requests: 1, Window: time.Minute}, MaxLimiterEntries: 8,
			})
			if err != nil {
				t.Fatal(err)
			}
			requestBody := func(requestID, requestNonce string) string {
				idField := "request_id"
				if route == "activate" {
					idField = "activation_request_id"
				}
				return fmt.Sprintf(`{"protocol_version":1,%q:%q,"card":"CARD-PLAINTEXT","software_id":"software","machine_hash":"machine","timestamp":%q,"nonce":%q}`, idField, requestID, clock.Now().Format(time.RFC3339), requestNonce)
			}
			do := func(body string) *httptest.ResponseRecorder {
				req := httptest.NewRequest(http.MethodPost, "/license-api/v1/"+route, strings.NewReader(body))
				req.Header.Set("Content-Type", "application/json")
				req.RemoteAddr = "127.0.0.1:41000"
				recorder := httptest.NewRecorder()
				handler.ServeHTTP(recorder, req)
				return recorder
			}
			if first := do(requestBody("first-"+route, nonce(300))); first.Code != http.StatusOK {
				t.Fatalf("first status = %d body=%s", first.Code, first.Body.String())
			}
			limited := do(requestBody("limited-"+route, nonce(301)))
			if limited.Code != http.StatusTooManyRequests || limited.Header().Get("Retry-After") != "60" {
				t.Fatalf("limited response = %d retry=%q body=%s", limited.Code, limited.Header().Get("Retry-After"), limited.Body.String())
			}
			claims := decodeClaims(t, limited, &key.PublicKey)
			if claims.Code != model.CodeRateLimited || claims.RequestID != "limited-"+route {
				t.Fatalf("rate-limit claims = %#v", claims)
			}
			limitedAgain := do(requestBody("limited-again-"+route, nonce(302)))
			var protective map[string]json.RawMessage
			if limitedAgain.Code != http.StatusTooManyRequests || limitedAgain.Header().Get("Retry-After") != "60" || json.Unmarshal(limitedAgain.Body.Bytes(), &protective) != nil || len(protective) != 1 || string(protective["code"]) != `"rate_limited"` {
				t.Fatalf("protective rate-limit response = %d headers=%v body=%s", limitedAgain.Code, limitedAgain.Header(), limitedAgain.Body.String())
			}
			if signer.calls.Load() != 2 {
				t.Fatalf("signature calls after repeated denial = %d, want 2", signer.calls.Load())
			}
			clock.Advance(time.Minute)
			if resetAllowed := do(requestBody("reset-allowed-"+route, nonce(303))); resetAllowed.Code != http.StatusOK {
				t.Fatalf("first request after reset = %d body=%s", resetAllowed.Code, resetAllowed.Body.String())
			}
			resetDenied := do(requestBody("reset-denied-"+route, nonce(304)))
			resetClaims := decodeClaims(t, resetDenied, &key.PublicKey)
			if resetDenied.Code != http.StatusTooManyRequests || resetClaims.Code != model.CodeRateLimited || resetClaims.RequestID != "reset-denied-"+route {
				t.Fatalf("first denial after reset = %d %#v", resetDenied.Code, resetClaims)
			}
			if signer.calls.Load() != 4 {
				t.Fatalf("signature calls after reset denial = %d, want 4", signer.calls.Load())
			}
			nonces, maximum := replays.snapshot()
			if len(nonces) != 2 || nonces[0] != nonce(300) || nonces[1] != nonce(303) || maximum != 7 {
				t.Fatalf("replay calls = %v max=%d; limited nonce was consumed", nonces, maximum)
			}
			if svc.calls.Load() != 2 {
				t.Fatalf("service calls = %d, want 2", svc.calls.Load())
			}
		})
	}
}

func TestReplayCapacityFailureIsSignedGenericAndRedacted(t *testing.T) {
	clock := &testClock{now: time.Date(2026, 9, 9, 2, 45, 0, 0, time.UTC)}
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	signer, _ := signing.New("capacity-key", key, rand.Reader)
	svc := &fakeService{}
	replays := &trackingReplayStore{err: errors.New(`replay state full at C:\private\license.db for CARD-PLAINTEXT machine-raw`)}
	handler, err := NewRouter(Options{
		Service: svc, ReplayStore: replays, Signer: signer, Clock: clock,
		MaxBodyBytes: 2048, MaxHeaderBytes: 2048,
		TimestampWindow: time.Minute, NonceLifetime: time.Minute, MaxReplayNonces: 3,
		ActivationRate: RateLimit{Requests: 10, Window: time.Minute}, ValidationRate: RateLimit{Requests: 10, Window: time.Minute}, MaxLimiterEntries: 8,
	})
	if err != nil {
		t.Fatal(err)
	}
	body := fmt.Sprintf(`{"protocol_version":1,"activation_request_id":"capacity-request","card":"CARD-PLAINTEXT","software_id":"software","machine_hash":"machine-raw","timestamp":%q,"nonce":%q}`, clock.Now().Format(time.RFC3339), nonce(400))
	req := httptest.NewRequest(http.MethodPost, "/license-api/v1/activate", strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, req)
	if recorder.Code != http.StatusServiceUnavailable {
		t.Fatalf("status = %d, want 503 body=%s", recorder.Code, recorder.Body.String())
	}
	claims := decodeClaims(t, recorder, &key.PublicKey)
	if claims.Code != model.CodeServiceUnavailable || claims.RequestID != "capacity-request" {
		t.Fatalf("claims = %#v", claims)
	}
	if svc.calls.Load() != 0 {
		t.Fatalf("service calls = %d, want 0", svc.calls.Load())
	}
	assertNotContains(t, recorder.Body.String(), "CARD-PLAINTEXT", "machine-raw", "private", "license.db", replays.err.Error())
}

func TestBusinessErrorMappingAndUnexpectedErrorRedaction(t *testing.T) {
	tests := []struct {
		err    error
		status int
		code   model.Code
	}{
		{service.ErrInvalidRequest, 400, model.CodeInvalidRequest},
		{service.ErrNotFound, 403, model.CodeLicenseNotFound},
		{service.ErrExpired, 403, model.CodeLicenseExpired},
		{service.ErrAppNotAllowed, 403, model.CodeAppNotAllowed},
		{service.ErrAppDisabled, 403, model.CodeAppDisabled},
		{service.ErrMachineMismatch, 409, model.CodeMachineMismatch},
		{errors.New(`database failed at C:\secret\license.db for CARD-PLAINTEXT machine-raw`), 503, model.CodeServiceUnavailable},
	}
	for index, tt := range tests {
		t.Run(string(tt.code), func(t *testing.T) {
			clock := &testClock{now: time.Date(2026, 9, 9, 3, 0, 0, 0, time.UTC)}
			key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
			signer, _ := signing.New("test", key, rand.Reader)
			svc := &fakeService{err: tt.err}
			handler, err := NewRouter(Options{Service: svc, ReplayStore: acceptingReplayStore{}, Signer: signer, Clock: clock, MaxBodyBytes: 2048, MaxHeaderBytes: 2048, TimestampWindow: time.Minute, NonceLifetime: time.Minute, MaxReplayNonces: 10_000, ActivationRate: RateLimit{Requests: 10, Window: time.Minute}, ValidationRate: RateLimit{Requests: 10, Window: time.Minute}, MaxLimiterEntries: 8})
			if err != nil {
				t.Fatal(err)
			}
			body := fmt.Sprintf(`{"protocol_version":1,"activation_request_id":"request-%d","card":"CARD-PLAINTEXT","software_id":"software","machine_hash":"machine-raw","timestamp":"%s","nonce":"%s"}`, index, clock.Now().Format(time.RFC3339), nonce(index+200))
			req := httptest.NewRequest(http.MethodPost, "/license-api/v1/activate", strings.NewReader(body))
			req.Header.Set("Content-Type", "application/json")
			recorder := httptest.NewRecorder()
			handler.ServeHTTP(recorder, req)
			if recorder.Code != tt.status {
				t.Fatalf("status = %d, want %d body=%s", recorder.Code, tt.status, recorder.Body.String())
			}
			claims := decodeClaims(t, recorder, &key.PublicKey)
			if claims.Code != tt.code || claims.RequestID != fmt.Sprintf("request-%d", index) {
				t.Fatalf("claims = %#v", claims)
			}
			assertNotContains(t, recorder.Body.String(), "CARD-PLAINTEXT", "machine-raw", "secret", "license.db", tt.err.Error())
		})
	}
}

func TestFixedWindowLimiterIsRaceSafeAndBounded(t *testing.T) {
	clock := &testClock{now: time.Date(2026, 9, 9, 4, 0, 0, 0, time.UTC)}
	limiter := newFixedWindowLimiter(RateLimit{Requests: 10, Window: time.Minute}, 16, clock)
	var allowed atomic.Int64
	var wg sync.WaitGroup
	for i := 0; i < 100; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if ok, _ := limiter.allow("same-client"); ok {
				allowed.Add(1)
			}
		}()
	}
	wg.Wait()
	if allowed.Load() != 10 {
		t.Fatalf("allowed = %d, want 10", allowed.Load())
	}
	for i := 0; i < 100; i++ {
		limiter.allow(fmt.Sprintf("client-%03d", i))
	}
	limiter.mu.Lock()
	entries := len(limiter.entries)
	limiter.mu.Unlock()
	if entries > 16 {
		t.Fatalf("limiter entries = %d, cap 16", entries)
	}
}

func TestClientKeyTrustsCanonicalRealIPOnlyFromLoopbackPeer(t *testing.T) {
	tests := []struct {
		name, remote string
		headers      []string
		want         string
	}{
		{name: "normal IPv4", remote: "127.0.0.1:1", headers: []string{"203.0.113.9"}, want: "203.0.113.9"},
		{name: "normal IPv6", remote: "[::1]:1", headers: []string{"2001:db8::5"}, want: "2001:db8::5"},
		{name: "mapped IPv6", remote: "127.0.0.1:1", headers: []string{"::ffff:203.0.113.9"}, want: "203.0.113.9"},
		{name: "spoof non-loopback", remote: "198.51.100.4:1", headers: []string{"203.0.113.9"}, want: "198.51.100.4"},
		{name: "multiple headers", remote: "127.0.0.1:1", headers: []string{"203.0.113.1", "203.0.113.2"}, want: "127.0.0.1"},
		{name: "comma list", remote: "127.0.0.1:1", headers: []string{"203.0.113.1, 203.0.113.2"}, want: "127.0.0.1"},
		{name: "whitespace", remote: "127.0.0.1:1", headers: []string{" 203.0.113.1"}, want: "127.0.0.1"},
		{name: "invalid", remote: "127.0.0.1:1", headers: []string{"not-an-ip"}, want: "127.0.0.1"},
		{name: "oversized", remote: "127.0.0.1:1", headers: []string{strings.Repeat("1", 64)}, want: "127.0.0.1"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest(http.MethodPost, "/", nil)
			req.RemoteAddr = tt.remote
			for _, value := range tt.headers {
				req.Header.Add("X-Real-IP", value)
			}
			if got := clientKey(req); got != tt.want {
				t.Fatalf("clientKey = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestLoopbackProxyClientsHaveIndependentRateBuckets(t *testing.T) {
	h := newAPIHarness(t, 1, 10)
	for index, ip := range []string{"203.0.113.10", "203.0.113.11"} {
		req := httptest.NewRequest(http.MethodPost, "/license-api/v1/activate", strings.NewReader(h.activateBody(fmt.Sprintf("proxy-%d", index), nonce(520+index))))
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("X-Real-IP", ip)
		req.RemoteAddr = "127.0.0.1:43000"
		recorder := httptest.NewRecorder()
		h.handler.ServeHTTP(recorder, req)
		if recorder.Code != http.StatusOK {
			t.Fatalf("proxy client %s status = %d body=%s", ip, recorder.Code, recorder.Body.String())
		}
	}
}

func (h *apiHarness) activateBody(requestID, requestNonce string) string {
	return fmt.Sprintf(`{"protocol_version":1,"activation_request_id":%q,"card":%q,"software_id":%q,"machine_hash":%q,"timestamp":%q,"nonce":%q}`, requestID, h.card, h.software, h.machine, h.clock.Now().Format(time.RFC3339), requestNonce)
}

func (h *apiHarness) validateBody(requestID, requestNonce string) string {
	return fmt.Sprintf(`{"protocol_version":1,"request_id":%q,"card":%q,"software_id":%q,"machine_hash":%q,"timestamp":%q,"nonce":%q}`, requestID, h.card, h.software, h.machine, h.clock.Now().Format(time.RFC3339), requestNonce)
}

func (h *apiHarness) do(method, path, body string) *httptest.ResponseRecorder {
	h.t.Helper()
	req := httptest.NewRequest(method, path, strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	req.RemoteAddr = "127.0.0.1:43210"
	recorder := httptest.NewRecorder()
	h.handler.ServeHTTP(recorder, req)
	return recorder
}

func decodeClaims(t *testing.T, recorder *httptest.ResponseRecorder, publicKey *ecdsa.PublicKey) model.Claims {
	t.Helper()
	var envelope model.SignedEnvelope
	decoder := json.NewDecoder(bytes.NewReader(recorder.Body.Bytes()))
	if err := decoder.Decode(&envelope); err != nil {
		t.Fatalf("decode envelope: %v body=%s", err, recorder.Body.String())
	}
	payload, err := base64.StdEncoding.DecodeString(envelope.Payload)
	if err != nil {
		t.Fatal(err)
	}
	signature, err := base64.StdEncoding.DecodeString(envelope.Signature)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256Sum(payload)
	if !ecdsa.VerifyASN1(publicKey, digest, signature) {
		t.Fatal("response signature did not verify over exact decoded bytes")
	}
	var claims model.Claims
	if err := json.Unmarshal(payload, &claims); err != nil {
		t.Fatal(err)
	}
	return claims
}

func sha256Sum(data []byte) []byte {
	sum := sha256.Sum256(data)
	return sum[:]
}

func nonce(index int) string { return fmt.Sprintf("nonce_%026d", index) }

func successState() model.ActivationState {
	now := time.Date(2026, 9, 9, 3, 0, 0, 0, time.UTC)
	return model.ActivationState{CardID: "card-id", SoftwareID: "software", MachineHash: "machine", PackageName: "package", ActivatedAt: now, ExpiresAt: now.Add(time.Hour), LeaseUntil: now.Add(time.Minute)}
}

func assertNotContains(t *testing.T, value string, forbidden ...string) {
	t.Helper()
	for _, item := range forbidden {
		if item != "" && strings.Contains(value, item) {
			t.Fatalf("response contains forbidden value %q: %s", item, value)
		}
	}
}
