package httpapi_test

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

const testReleaseID = "33333333-3333-4333-8333-333333333333"

type fakeReleaseOperations struct {
	history       []store.ReleaseHistoryItem
	rollback      deploy.RollbackResult
	err           error
	rollbackCalls int
	principal     session.Principal
	releaseID     string
	operationID   string
	password      string
	readOnly      error
}

func (operations *fakeReleaseOperations) ReleaseHistory(context.Context) ([]store.ReleaseHistoryItem, error) {
	return operations.history, operations.err
}

func (operations *fakeReleaseOperations) ReadOnlyError() error { return operations.readOnly }

func (operations *fakeReleaseOperations) RollbackForActiveSession(_ context.Context, principal session.Principal, releaseID, operationID, password string) (deploy.RollbackResult, error) {
	operations.rollbackCalls++
	operations.principal = principal
	operations.releaseID = releaseID
	operations.operationID = operationID
	operations.password = password
	return operations.rollback, operations.err
}

func TestHistoryEndpointReturnsThreeRetainedReleasesAndNoDowngradePromise(t *testing.T) {
	h := newHarness(t)
	operations := &fakeReleaseOperations{history: []store.ReleaseHistoryItem{{Release: store.Release{ID: testReleaseID, Version: "1.0.3", GenerationName: "release-" + testReleaseID, CommittedAt: time.Unix(2_000_000_000, 0), Notes: "修复批量转存。"}, State: "current", Assets: []store.ReleaseAsset{{FileName: "SuperPan-1.0.3-full.nupkg", SizeBytes: 123, SHA256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}}}
	installReleaseOperations(t, h, operations)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, _ = changeTestPassword(t, h, cookie, csrf)

	request := httptest.NewRequest(http.MethodGet, "http://backend/admin/api/releases", nil)
	request.AddCookie(cookie)
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	body := decodeBody(t, response)
	if response.Code != http.StatusOK || body["client_auto_downgrade"] != false {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if body["read_only"] != false {
		t.Fatalf("read_only=%#v", body["read_only"])
	}
	releases, ok := body["releases"].([]any)
	if !ok || len(releases) != 1 {
		t.Fatalf("releases=%#v", body["releases"])
	}
	item, ok := releases[0].(map[string]any)
	if !ok || item["id"] != testReleaseID || item["version"] != "1.0.3" || item["state"] != "current" || item["notes"] != "修复批量转存。" {
		t.Fatalf("release item=%#v", releases[0])
	}
	assets, ok := item["assets"].([]any)
	if !ok || len(assets) != 1 {
		t.Fatalf("assets=%#v", item["assets"])
	}
}

func TestHistoryEndpointExposesReadOnlyProtectionWithoutInternalDetails(t *testing.T) {
	h := newHarness(t)
	operations := &fakeReleaseOperations{readOnly: errors.New("private filesystem detail")}
	installReleaseOperations(t, h, operations)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, _ = changeTestPassword(t, h, cookie, csrf)
	request := httptest.NewRequest(http.MethodGet, "http://backend/admin/api/releases", nil)
	request.AddCookie(cookie)
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	body := decodeBody(t, response)
	if response.Code != http.StatusOK || body["read_only"] != true || strings.Contains(response.Body.String(), "private filesystem detail") {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestRollbackEndpointRequiresConfirmationPasswordAndCanonicalIdempotencyKey(t *testing.T) {
	h := newHarness(t)
	operations := &fakeReleaseOperations{rollback: deploy.RollbackResult{OperationID: testOperationID, ReleaseID: testReleaseID, Version: "1.0.2", State: "succeeded", ClientAutoDowngrade: false}}
	installReleaseOperations(t, h, operations)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	for _, body := range []string{`{"confirm":false,"current_password":"replacement administrator password"}`, `{"confirm":true,"current_password":""}`} {
		request := newRollbackRequest(body, csrf, cookie)
		response := httptest.NewRecorder()
		h.handler.ServeHTTP(response, request)
		if response.Code != http.StatusBadRequest {
			t.Fatalf("body=%s status=%d response=%s", body, response.Code, response.Body.String())
		}
	}
	if operations.rollbackCalls != 0 {
		t.Fatalf("rollback calls before valid confirmation=%d", operations.rollbackCalls)
	}

	request := newRollbackRequest(`{"confirm":true,"current_password":"replacement administrator password"}`, csrf, cookie)
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusOK || operations.rollbackCalls != 1 || operations.principal.IDHash == "" || operations.releaseID != testReleaseID || operations.operationID != testOperationID || operations.password != "replacement administrator password" {
		t.Fatalf("status=%d calls=%d principal=%+v release=%q operation=%q password=%q body=%s", response.Code, operations.rollbackCalls, operations.principal, operations.releaseID, operations.operationID, operations.password, response.Body.String())
	}
}

func TestRollbackEndpointMapsPasswordConflictAndReadOnlyFailuresToStableChineseErrors(t *testing.T) {
	for _, test := range []struct {
		name string
		err  error
		want int
		code string
	}{
		{name: "password", err: session.ErrPassword, want: http.StatusBadRequest, code: "password_rejected"},
		{name: "busy", err: store.ErrInvalidTransition, want: http.StatusConflict, code: "operation_conflict"},
		{name: "read only", err: deploy.ErrReadOnly, want: http.StatusServiceUnavailable, code: "publish_unavailable"},
		{name: "signature", err: release.ErrSignature, want: http.StatusUnprocessableEntity, code: "signature_mismatch"},
		{name: "hash", err: candidate.ErrDigestMismatch, want: http.StatusUnprocessableEntity, code: "release_validation_failed"},
		{name: "terminal internal", err: deploy.ErrRollbackFailed, want: http.StatusInternalServerError, code: "operation_failed"},
	} {
		t.Run(test.name, func(t *testing.T) {
			h := newHarness(t)
			operations := &fakeReleaseOperations{err: test.err}
			installReleaseOperations(t, h, operations)
			cookie, csrf, _, _ := login(t, h, oldPassword)
			cookie, csrf = changeTestPassword(t, h, cookie, csrf)
			response := httptest.NewRecorder()
			h.handler.ServeHTTP(response, newRollbackRequest(`{"confirm":true,"current_password":"replacement administrator password"}`, csrf, cookie))
			body := decodeBody(t, response)
			if response.Code != test.want || body["code"] != test.code || body["operation_id"] != testOperationID || body["message"] == "" {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
}

func installReleaseOperations(t *testing.T, h *harness, operations httpapi.ReleaseOperations) {
	t.Helper()
	var err error
	h.handler, err = httpapi.NewRouter(httpapi.Options{Sessions: h.sessions, PublicOrigin: publicOrigin, MaxJSONBytes: 4 << 10, ReleaseOperations: operations})
	if err != nil {
		t.Fatal(err)
	}
}

func newRollbackRequest(body, csrf string, cookie *http.Cookie) *http.Request {
	request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/releases/"+testReleaseID+"/rollback", stringReader(body))
	request.RemoteAddr = "127.0.0.1:43210"
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("Origin", publicOrigin)
	request.Header.Set("Referer", publicOrigin+"/admin/")
	request.Header.Set("X-CSRF-Token", csrf)
	request.Header.Set("Idempotency-Key", testOperationID)
	request.AddCookie(cookie)
	return request
}
