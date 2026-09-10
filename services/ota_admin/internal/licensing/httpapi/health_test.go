package httpapi

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestHealthIsLoopbackOnlyUnauthenticatedAndFixed(t *testing.T) {
	h := newAPIHarness(t, 1, 1)
	request := httptest.NewRequest(http.MethodGet, "/internal/health", nil)
	request.RemoteAddr = "127.0.0.1:43210"
	recorder := httptest.NewRecorder()
	h.handler.ServeHTTP(recorder, request)
	if recorder.Code != http.StatusOK {
		t.Fatalf("health status = %d body=%s", recorder.Code, recorder.Body.String())
	}
	if got := recorder.Header().Get("Content-Type"); got != "application/json" {
		t.Fatalf("Content-Type = %q", got)
	}
	if got := recorder.Header().Get("Cache-Control"); got != "no-store" {
		t.Fatalf("Cache-Control = %q", got)
	}
	if got := recorder.Body.String(); got != "{\"status\":\"ok\"}\n" {
		t.Fatalf("health body = %q", got)
	}
	for _, forbidden := range []string{"database", "sqlite", "card", "license", "count"} {
		if strings.Contains(strings.ToLower(recorder.Body.String()), forbidden) {
			t.Fatalf("health body leaks detail %q: %s", forbidden, recorder.Body.String())
		}
	}
}

func TestHealthRejectsForwardedAndDirectNonLoopbackPeers(t *testing.T) {
	h := newAPIHarness(t, 1, 1)
	for _, remote := range []string{"203.0.113.9:443", "invalid"} {
		request := httptest.NewRequest(http.MethodGet, "/internal/health", nil)
		request.RemoteAddr = remote
		request.Header.Set("X-Forwarded-For", "127.0.0.1")
		request.Header.Set("X-Real-IP", "127.0.0.1")
		recorder := httptest.NewRecorder()
		h.handler.ServeHTTP(recorder, request)
		if recorder.Code != http.StatusNotFound {
			t.Fatalf("remote %q health status = %d, want 404", remote, recorder.Code)
		}
	}
}
