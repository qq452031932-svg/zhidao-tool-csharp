package httpapi

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/authwire"
	"fanfh.cn/superpan/ota-admin/internal/licensing/signing"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
)

type fixedStoreMetrics struct{ snapshot licensestore.Metrics }

func (m fixedStoreMetrics) Metrics() licensestore.Metrics { return m.snapshot }

func TestLoadMetricsExistOnlyInLoadModeAndRequireLoopbackCredential(t *testing.T) {
	secret := bytes.Repeat([]byte{0x5a}, 32)
	loadHandler := newRouterWithMetrics(t, secret, fixedStoreMetrics{snapshot: licensestore.Metrics{Busy: 7, Locked: 3}})
	token := "Bearer " + authwire.EncodeInternalToken(secret)
	for _, tt := range []struct {
		name, remote, token string
		want                int
	}{
		{"ok", "127.0.0.1:1", token, http.StatusOK},
		{"wrong token", "127.0.0.1:1", token + "x", http.StatusUnauthorized},
		{"forwarded loopback", "203.0.113.9:1", token, http.StatusNotFound},
	} {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest(http.MethodGet, "/internal/load-metrics", nil)
			req.RemoteAddr = tt.remote
			req.Header.Set("Authorization", tt.token)
			req.Header.Set("X-Forwarded-For", "127.0.0.1")
			rec := httptest.NewRecorder()
			loadHandler.ServeHTTP(rec, req)
			if rec.Code != tt.want {
				t.Fatalf("status = %d body=%s", rec.Code, rec.Body.String())
			}
			if tt.want == http.StatusOK {
				var got licensestore.Metrics
				if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
					t.Fatal(err)
				}
				if got.Busy != 7 || got.Locked != 3 {
					t.Fatalf("metrics = %+v", got)
				}
				if rec.Header().Get("Cache-Control") != "no-store" {
					t.Fatal("metrics response is cacheable")
				}
			}
		})
	}
}

func TestLoadMetricsAreNotRegisteredWithoutLoadMode(t *testing.T) {
	for _, handler := range []http.Handler{newAPIHarness(t, 1, 1).handler, newAdminHarness(t, 10).handler} {
		req := httptest.NewRequest(http.MethodGet, "/internal/load-metrics", nil)
		req.RemoteAddr = "127.0.0.1:1"
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, req)
		if rec.Code != http.StatusNotFound {
			t.Fatalf("disabled metrics status = %d", rec.Code)
		}
	}
}

func newRouterWithMetrics(t *testing.T, secret []byte, metrics StoreMetrics) http.Handler {
	t.Helper()
	clock := &testClock{now: time.Date(2026, 9, 9, 9, 0, 0, 0, time.UTC)}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := signing.New("metrics-key", key, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	handler, err := NewRouter(Options{Service: &fakeService{}, ReplayStore: acceptingReplayStore{}, Signer: signer, Clock: clock, MaxBodyBytes: 2048, MaxHeaderBytes: 2048, TimestampWindow: time.Minute, NonceLifetime: time.Minute, MaxReplayNonces: 10, ActivationRate: RateLimit{Requests: 1, Window: time.Minute}, ValidationRate: RateLimit{Requests: 1, Window: time.Minute}, MaxLimiterEntries: 8, LoadTestSecret: bytes.Clone(secret), StoreMetrics: metrics})
	if err != nil {
		t.Fatal(err)
	}
	return handler
}
