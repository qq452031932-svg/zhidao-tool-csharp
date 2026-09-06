package httpapi_test

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

type fakeCandidatePublisher struct {
	calls                               int
	sessionID, candidateID, operationID string
	result                              deploy.PublishResult
	err                                 error
}

func (publisher *fakeCandidatePublisher) PublishForActiveSession(_ context.Context, sessionID, candidateID, operationID string) (deploy.PublishResult, error) {
	publisher.calls++
	publisher.sessionID, publisher.candidateID, publisher.operationID = sessionID, candidateID, operationID
	return publisher.result, publisher.err
}

func TestPublishEndpointRequiresExplicitConfirmationAndPassesBoundIdentifiers(t *testing.T) {
	h := newHarness(t)
	publisher := &fakeCandidatePublisher{result: deploy.PublishResult{OperationID: testOperationID, CandidateID: testCandidateID, ReleaseID: testOperationID, Version: "1.2.3", State: "succeeded"}}
	installPublisher(t, h, publisher)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	request := newPublishRequest(`{"confirm":false}`, csrf, cookie)
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || publisher.calls != 0 {
		t.Fatalf("unconfirmed status=%d calls=%d body=%s", response.Code, publisher.calls, response.Body.String())
	}

	request = newPublishRequest(`{"confirm":true}`, csrf, cookie)
	response = httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusOK || publisher.calls != 1 || publisher.sessionID == "" || publisher.candidateID != testCandidateID || publisher.operationID != testOperationID {
		t.Fatalf("status=%d calls=%d session=%q candidate=%q operation=%q body=%s", response.Code, publisher.calls, publisher.sessionID, publisher.candidateID, publisher.operationID, response.Body.String())
	}
}

func TestPublishEndpointRejectsAnonymousCrossOriginCSRFAndNoncanonicalIDs(t *testing.T) {
	h := newHarness(t)
	publisher := &fakeCandidatePublisher{}
	installPublisher(t, h, publisher)
	request := newPublishRequest(`{"confirm":true}`, "", nil)
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusUnauthorized {
		t.Fatalf("anonymous status=%d", response.Code)
	}
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	for name, mutate := range map[string]func(*http.Request){
		"csrf":      func(r *http.Request) { r.Header.Del("X-CSRF-Token") },
		"origin":    func(r *http.Request) { r.Header.Set("Origin", "https://evil.example") },
		"candidate": func(r *http.Request) { r.URL.Path = "/admin/api/candidates/NOT-A-UUID/publish" },
		"operation": func(r *http.Request) { r.Header.Set("Idempotency-Key", "NOT-A-UUID") },
	} {
		t.Run(name, func(t *testing.T) {
			request := newPublishRequest(`{"confirm":true}`, csrf, cookie)
			mutate(request)
			response := httptest.NewRecorder()
			h.handler.ServeHTTP(response, request)
			if response.Code < 400 {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
	if publisher.calls != 0 {
		t.Fatalf("publisher called for rejected requests: %d", publisher.calls)
	}
}

func TestPublishEndpointReturnsSafeStableErrorsWithOperationID(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want int
		code string
	}{
		{name: "expired", err: store.ErrCandidateExpired, want: http.StatusGone, code: "candidate_expired"},
		{name: "signature", err: release.ErrSignature, want: http.StatusUnprocessableEntity, code: "signature_mismatch"},
		{name: "release closure", err: release.ErrManifest, want: http.StatusUnprocessableEntity, code: "release_validation_failed"},
		{name: "session revoked at commit", err: store.ErrSessionInactive, want: http.StatusUnauthorized, code: "authentication_failed"},
		{name: "recovery required", err: errors.Join(deploy.ErrReadOnly, deploy.ErrRecoveryRequired), want: http.StatusServiceUnavailable, code: "publish_unavailable"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			h := newHarness(t)
			publisher := &fakeCandidatePublisher{err: test.err}
			installPublisher(t, h, publisher)
			cookie, csrf, _, _ := login(t, h, oldPassword)
			cookie, csrf = changeTestPassword(t, h, cookie, csrf)

			request := newPublishRequest(`{"confirm":true}`, csrf, cookie)
			response := httptest.NewRecorder()
			h.handler.ServeHTTP(response, request)
			body := decodeBody(t, response)
			if response.Code != test.want || body["code"] != test.code || body["operation_id"] != testOperationID || body["message"] == "" {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
}

func installPublisher(t *testing.T, h *harness, publisher httpapi.CandidatePublisher) {
	t.Helper()
	var err error
	h.handler, err = httpapi.NewRouter(httpapi.Options{Sessions: h.sessions, PublicOrigin: publicOrigin, MaxJSONBytes: 4 << 10, Publisher: publisher})
	if err != nil {
		t.Fatal(err)
	}
}

func newPublishRequest(body, csrf string, cookie *http.Cookie) *http.Request {
	request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/candidates/"+testCandidateID+"/publish", stringReader(body))
	request.RemoteAddr = "127.0.0.1:43210"
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("Origin", publicOrigin)
	request.Header.Set("Referer", publicOrigin+"/admin/")
	request.Header.Set("Idempotency-Key", testOperationID)
	if csrf != "" {
		request.Header.Set("X-CSRF-Token", csrf)
	}
	if cookie != nil {
		request.AddCookie(cookie)
	}
	return request
}

func stringReader(value string) *strings.Reader { return strings.NewReader(value) }
