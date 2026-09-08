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

func (acceptingReplayStore) ClaimReplayNonce(context.Context, string, time.Time, time.Time) (bool, error) {
	return true, nil
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
			handler, err := NewRouter(Options{Service: svc, ReplayStore: acceptingReplayStore{}, Signer: signer, Clock: clock, MaxBodyBytes: 2048, MaxHeaderBytes: 2048, TimestampWindow: time.Minute, NonceLifetime: time.Minute, ActivationRate: RateLimit{Requests: 10, Window: time.Minute}, ValidationRate: RateLimit{Requests: 10, Window: time.Minute}, MaxLimiterEntries: 8})
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
