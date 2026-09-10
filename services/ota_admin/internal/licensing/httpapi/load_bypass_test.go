package httpapi

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/authwire"
	"fanfh.cn/superpan/ota-admin/internal/licensing/signing"
)

func loadTestRouter(t *testing.T, replay ReplayStore) (http.Handler, *fakeService, *testClock, []byte) {
	t.Helper()
	clock := &testClock{now: time.Date(2026, 9, 9, 9, 0, 0, 0, time.UTC)}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := signing.New("load-key", key, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	service := &fakeService{}
	secret := bytes.Repeat([]byte{0x5a}, 32)
	handler, err := NewRouter(Options{
		Service: service, ReplayStore: replay, Signer: signer, Clock: clock,
		MaxBodyBytes: 2048, MaxHeaderBytes: 2048, TimestampWindow: time.Minute, NonceLifetime: 2 * time.Minute,
		MaxReplayNonces: 100, ActivationRate: RateLimit{Requests: 1, Window: time.Minute}, ValidationRate: RateLimit{Requests: 1, Window: time.Minute}, MaxLimiterEntries: 8,
		LoadTestSecret: secret, StoreMetrics: fixedStoreMetrics{},
	})
	if err != nil {
		t.Fatal(err)
	}
	return handler, service, clock, secret
}

func TestLoadCredentialBypassesOnlyPublicRateLimitFromLoopback(t *testing.T) {
	handler, service, clock, secret := loadTestRouter(t, acceptingReplayStore{})
	body := func(id, nonceValue string) string {
		return `{"protocol_version":1,"activation_request_id":"` + id + `","card":"AAAAAAAA-AAAAAAAA-AAAAAAAA-AAAAAAAA","software_id":"software","machine_hash":"machine","timestamp":"` + clock.Now().Format(time.RFC3339) + `","nonce":"` + nonceValue + `"}`
	}
	do := func(remote, authorization, payload string) *httptest.ResponseRecorder {
		req := httptest.NewRequest(http.MethodPost, "/license-api/v1/activate", strings.NewReader(payload))
		req.RemoteAddr = remote
		req.Header.Set("Content-Type", "application/json")
		if authorization != "" {
			req.Header.Set("Authorization", authorization)
		}
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)
		return rec
	}
	if got := do("127.0.0.1:1", "", body("one", nonce(201))); got.Code != http.StatusOK {
		t.Fatalf("first = %d %s", got.Code, got.Body.String())
	}
	token := "Bearer " + authwire.EncodeInternalToken(secret)
	if got := do("127.0.0.1:2", token, body("two", nonce(202))); got.Code != http.StatusOK {
		t.Fatalf("authorized bypass = %d %s", got.Code, got.Body.String())
	}
	if got := do("127.0.0.1:3", token+"x", body("three", nonce(203))); got.Code != http.StatusTooManyRequests {
		t.Fatalf("wrong token = %d", got.Code)
	}
	if got := do("203.0.113.8:4", "", body("remote-first", nonce(204))); got.Code != http.StatusOK {
		t.Fatalf("first remote = %d", got.Code)
	}
	if got := do("203.0.113.8:4", token, body("four", nonce(205))); got.Code != http.StatusTooManyRequests {
		t.Fatalf("remote token = %d", got.Code)
	}
	if calls := service.calls.Load(); calls != 3 {
		t.Fatalf("service calls = %d, want 3", calls)
	}
}

func TestLoadBypassStillEnforcesBodyReplayAndSignatureControls(t *testing.T) {
	replay := &trackingReplayStore{}
	handler, service, clock, secret := loadTestRouter(t, replay)
	token := "Bearer " + authwire.EncodeInternalToken(secret)
	req := httptest.NewRequest(http.MethodPost, "/license-api/v1/activate", strings.NewReader(`{"protocol_version":1}`))
	req.RemoteAddr = "127.0.0.1:1"
	req.Header.Set("Authorization", token)
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("invalid body status = %d", rec.Code)
	}
	if service.calls.Load() != 0 {
		t.Fatal("invalid load request reached service")
	}

	replay.err = nil
	valid := `{"protocol_version":1,"activation_request_id":"valid","card":"AAAAAAAA-AAAAAAAA-AAAAAAAA-AAAAAAAA","software_id":"software","machine_hash":"machine","timestamp":"` + clock.Now().Format(time.RFC3339) + `","nonce":"` + nonce(210) + `"}`
	req = httptest.NewRequest(http.MethodPost, "/license-api/v1/activate", strings.NewReader(valid))
	req.RemoteAddr = "127.0.0.1:1"
	req.Header.Set("Authorization", token)
	req.Header.Set("Content-Type", "application/json")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("valid load request = %d %s", rec.Code, rec.Body.String())
	}
	if !strings.Contains(rec.Body.String(), "signature") {
		t.Fatalf("load response is not signed: %s", rec.Body.String())
	}
	nonces, _ := replay.snapshot()
	if len(nonces) != 1 || nonces[0] != nonce(210) {
		t.Fatalf("load request did not pass replay control: %v", nonces)
	}
}
