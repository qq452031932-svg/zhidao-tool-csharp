package httpapi_test

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func TestRouterServesEmbeddedConsoleAndKeepsAPIProtected(t *testing.T) {
	h := newHarness(t)
	for path, contentType := range map[string]string{
		"/admin/":           "text/html; charset=utf-8",
		"/admin/index.html": "text/html; charset=utf-8",
		"/admin/styles.css": "text/css; charset=utf-8",
		"/admin/app.js":     "text/javascript; charset=utf-8",
	} {
		request := httptest.NewRequest(http.MethodGet, "http://backend"+path, nil)
		response := httptest.NewRecorder()
		h.handler.ServeHTTP(response, request)
		if response.Code != http.StatusOK || response.Header().Get("Content-Type") != contentType {
			t.Fatalf("%s status=%d type=%q body=%s", path, response.Code, response.Header().Get("Content-Type"), response.Body.String())
		}
		if response.Header().Get("Cache-Control") != "no-store, max-age=0" || !strings.Contains(response.Header().Get("Content-Security-Policy"), "default-src 'self'") {
			t.Fatalf("%s security headers=%v", path, response.Header())
		}
	}
	if response := h.request(http.MethodGet, "/admin/api/session/current", "", ""); response.Code != http.StatusUnauthorized || !strings.HasPrefix(response.Header().Get("Content-Type"), "application/json") {
		t.Fatalf("protected API status=%d type=%q body=%s", response.Code, response.Header().Get("Content-Type"), response.Body.String())
	}
	for _, path := range []string{"/admin/config.json", "/admin/static/index.html", "/admin/../state.db"} {
		request := httptest.NewRequest(http.MethodGet, "http://backend"+path, nil)
		response := httptest.NewRecorder()
		h.handler.ServeHTTP(response, request)
		if response.Code != http.StatusNotFound {
			t.Fatalf("unknown asset %s status=%d", path, response.Code)
		}
	}
}

func TestConsoleHTTPHappyPathUsesOneAuthenticatedSameOriginWorkflow(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	acceptor := &fakeEnvelopeAcceptor{result: testCandidateSummary(testOperationID)}
	publisher := &fakeCandidatePublisher{result: deploy.PublishResult{OperationID: testOperationID, CandidateID: testCandidateID, ReleaseID: testOperationID, Version: "21.0.0", State: "succeeded"}}
	operations := &fakeReleaseOperations{
		history:  []store.ReleaseHistoryItem{{Release: store.Release{ID: testReleaseID, Version: "20.0.0", GenerationName: "release-" + testReleaseID, CommittedAt: time.Unix(2_000_000_000, 0), Notes: "上一版说明"}, State: "retained"}},
		rollback: deploy.RollbackResult{OperationID: testOperationID, ReleaseID: testReleaseID, Version: "20.0.0", State: "succeeded", ClientAutoDowngrade: false},
	}
	var err error
	h.handler, err = httpapi.NewRouter(httpapi.Options{Sessions: h.sessions, PublicOrigin: publicOrigin, MaxJSONBytes: 4 << 10, Uploads: receiver, UploadEnvelopes: acceptor, Publisher: publisher, ReleaseOperations: operations})
	if err != nil {
		t.Fatal(err)
	}

	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	current := h.request(http.MethodGet, "/admin/api/session/current", "", "", cookie)
	csrf, _ = decodeBody(t, current)["csrf_token"].(string)
	if current.Code != http.StatusOK || csrf == "" {
		t.Fatalf("current status=%d body=%s", current.Code, current.Body.String())
	}
	history := h.request(http.MethodGet, "/admin/api/releases", "", "", cookie)
	if history.Code != http.StatusOK || !strings.Contains(history.Body.String(), "上一版说明") {
		t.Fatalf("history status=%d body=%s", history.Code, history.Body.String())
	}
	uploaded := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	if uploaded.Code != http.StatusAccepted || decodeBody(t, uploaded)["state"] != "validated" {
		t.Fatalf("upload status=%d body=%s", uploaded.Code, uploaded.Body.String())
	}
	publish := newPublishRequest(`{"confirm":true}`, csrf, cookie)
	published := httptest.NewRecorder()
	h.handler.ServeHTTP(published, publish)
	if published.Code != http.StatusOK || publisher.calls != 1 {
		t.Fatalf("publish status=%d calls=%d body=%s", published.Code, publisher.calls, published.Body.String())
	}
	rollback := newRollbackRequest(`{"confirm":true,"current_password":"`+newPassword+`"}`, csrf, cookie)
	rolledBack := httptest.NewRecorder()
	h.handler.ServeHTTP(rolledBack, rollback)
	if rolledBack.Code != http.StatusOK || operations.rollbackCalls != 1 {
		t.Fatalf("rollback status=%d calls=%d body=%s", rolledBack.Code, operations.rollbackCalls, rolledBack.Body.String())
	}
}
