package httpapi_test

import (
	"bytes"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/licenseadmin"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func TestLicenseRoutesRequireSessionPasswordRotationAndMutationCSRF(t *testing.T) {
	secret := bytes.Repeat([]byte{0x91}, 32)
	var calls atomic.Int32
	downstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"items":[]}`))
	}))
	defer downstream.Close()
	h := newHarness(t)
	installLicenseClient(t, h, newLicenseClient(t, downstream.URL, secret), nil)

	if response := h.request(http.MethodGet, "/admin/api/license/software?page=1&page_size=20", "", ""); response.Code != http.StatusUnauthorized {
		t.Fatalf("anonymous status=%d body=%s", response.Code, response.Body.String())
	}
	cookie, csrf, _, _ := login(t, h, oldPassword)
	if response := h.request(http.MethodGet, "/admin/api/license/software?page=1&page_size=20", "", "", cookie); response.Code != http.StatusForbidden || decodeBody(t, response)["code"] != "password_change_required" {
		t.Fatalf("forced-change status=%d body=%s", response.Code, response.Body.String())
	}
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	if response := h.request(http.MethodPost, "/admin/api/license/software", `{"name":"SuperPan"}`, "", cookie); response.Code != http.StatusForbidden {
		t.Fatalf("missing csrf status=%d body=%s", response.Code, response.Body.String())
	}
	if calls.Load() != 0 {
		t.Fatalf("unauthorized requests reached downstream: %d", calls.Load())
	}
	if response := h.request(http.MethodGet, "/admin/api/license/software?page=1&page_size=20", "", "", cookie); response.Code != http.StatusOK {
		t.Fatalf("authorized status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestLicenseMutationPreservesIdempotencyAndUsesOnlySessionActor(t *testing.T) {
	secret := bytes.Repeat([]byte{0x31}, 32)
	const key = "1b385465-f173-49c7-b19e-10fb8e38bd9d"
	downstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Idempotency-Key"); got != key {
			t.Fatalf("Idempotency-Key=%q", got)
		}
		if actor := r.Header.Get("X-Admin-Actor"); actor == "" || actor == "browser-supplied" {
			t.Fatalf("actor was not injected from session: %q", actor)
		}
		want := "Bearer " + base64.RawURLEncoding.EncodeToString(secret)
		if got := r.Header.Get("Authorization"); got != want {
			t.Fatalf("Authorization=%q", got)
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusCreated)
		_, _ = w.Write([]byte(`{"id":"created"}`))
	}))
	defer downstream.Close()
	h := newHarness(t)
	installLicenseClient(t, h, newLicenseClient(t, downstream.URL, secret), nil)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/license/software", strings.NewReader(`{"name":"SuperPan"}`))
	request.RemoteAddr = "127.0.0.1:43210"
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("Origin", publicOrigin)
	request.Header.Set("Referer", publicOrigin+"/admin/")
	request.Header.Set("X-CSRF-Token", csrf)
	request.Header.Set("Idempotency-Key", key)
	request.Header.Set("X-Admin-Actor", "browser-supplied")
	request.AddCookie(cookie)
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusCreated || response.Header().Get("Authorization") != "" || strings.Contains(response.Body.String(), base64.RawURLEncoding.EncodeToString(secret)) {
		t.Fatalf("proxy response status=%d headers=%v body=%s", response.Code, response.Header(), response.Body.String())
	}
}

func TestLicenseProxyRejectsPathInjectionAndBoundsBodies(t *testing.T) {
	secret := bytes.Repeat([]byte{0x41}, 32)
	var calls atomic.Int32
	downstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		_, _ = w.Write([]byte(strings.Repeat("x", 70<<10)))
	}))
	defer downstream.Close()
	h := newHarness(t)
	installLicenseClient(t, h, newLicenseClient(t, downstream.URL, secret), nil)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	for _, path := range []string{
		"/admin/api/license/software/../cards",
		"/admin/api/license/software/%2e%2e/cards",
		"/admin/api/license/software//cards",
	} {
		response := h.request(http.MethodGet, path, "", "", cookie)
		if response.Code != http.StatusNotFound {
			t.Fatalf("injected path %q status=%d body=%s", path, response.Code, response.Body.String())
		}
	}
	oversized := h.request(http.MethodPost, "/admin/api/license/software", strings.Repeat("x", (4<<10)+1), csrf, cookie)
	if oversized.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("oversized request status=%d body=%s", oversized.Code, oversized.Body.String())
	}
	largeResponse := h.request(http.MethodGet, "/admin/api/license/software?page=1&page_size=20", "", "", cookie)
	if largeResponse.Code != http.StatusServiceUnavailable || decodeBody(t, largeResponse)["code"] != "license_service_unavailable" {
		t.Fatalf("oversized response status=%d body=%s", largeResponse.Code, largeResponse.Body.String())
	}
	if calls.Load() != 1 {
		t.Fatalf("unexpected downstream calls=%d", calls.Load())
	}
}

func TestLicenseDowntimeDoesNotBreakOTAReleaseEndpoint(t *testing.T) {
	h := newHarness(t)
	client := newLicenseClient(t, "http://127.0.0.1:1", make([]byte, 32))
	operations := &fakeReleaseOperations{history: []store.ReleaseHistoryItem{}}
	installLicenseClient(t, h, client, operations)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, _ = changeTestPassword(t, h, cookie, csrf)

	licenseResponse := h.request(http.MethodGet, "/admin/api/license/packages?page=1&page_size=20", "", "", cookie)
	if licenseResponse.Code != http.StatusServiceUnavailable || decodeBody(t, licenseResponse)["code"] != "license_service_unavailable" || !strings.Contains(licenseResponse.Body.String(), "授权服务") {
		t.Fatalf("license downtime status=%d body=%s", licenseResponse.Code, licenseResponse.Body.String())
	}
	releases := h.request(http.MethodGet, "/admin/api/releases", "", "", cookie)
	if releases.Code != http.StatusOK {
		t.Fatalf("release endpoint status=%d body=%s", releases.Code, releases.Body.String())
	}
}

func newLicenseClient(t *testing.T, baseURL string, secret []byte) *licenseadmin.Client {
	t.Helper()
	client, err := licenseadmin.NewClient(licenseadmin.Options{BaseURL: baseURL, Secret: secret, MaxRequestBytes: 4 << 10, MaxResponseBytes: 64 << 10})
	if err != nil {
		t.Fatal(err)
	}
	return client
}

func installLicenseClient(t *testing.T, h *harness, client *licenseadmin.Client, releases httpapi.ReleaseOperations) {
	t.Helper()
	var err error
	h.handler, err = httpapi.NewRouter(httpapi.Options{
		Sessions: h.sessions, PublicOrigin: publicOrigin, MaxJSONBytes: 4 << 10,
		LicenseAdmin: client, ReleaseOperations: releases,
	})
	if err != nil {
		t.Fatal(err)
	}
}
