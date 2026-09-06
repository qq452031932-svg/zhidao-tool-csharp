package httpapi_test

import (
	"net/http"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func TestCandidateCurrentAndStatusRequireSessionAndReturnSafeSummary(t *testing.T) {
	h := newHarness(t)
	acceptor := &fakeEnvelopeAcceptor{result: testCandidateSummary(testOperationID)}
	installUploadReceiver(t, h, &fakeUploadReceiver{}, acceptor)

	if response := h.request(http.MethodGet, "/admin/api/candidates/current", "", ""); response.Code != http.StatusUnauthorized {
		t.Fatalf("anonymous current status=%d body=%s", response.Code, response.Body.String())
	}
	cookie, csrf, _, _ := login(t, h, oldPassword)
	if response := h.request(http.MethodGet, "/admin/api/candidates/current", "", "", cookie); response.Code != http.StatusForbidden {
		t.Fatalf("forced-change current status=%d body=%s", response.Code, response.Body.String())
	}
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	for _, path := range []string{"/admin/api/candidates/current", "/admin/api/candidates/" + testCandidateID} {
		response := h.request(http.MethodGet, path, "", "", cookie)
		if response.Code != http.StatusOK {
			t.Fatalf("%s status=%d body=%s", path, response.Code, response.Body.String())
		}
		body := decodeBody(t, response)
		if body["candidate_id"] != testCandidateID || body["operation_id"] != testOperationID || body["state"] != "validated" {
			t.Fatalf("%s summary=%v", path, body)
		}
		report, ok := body["report"].(map[string]any)
		if !ok || report["notes_markdown"] != "修复批量转存。" || report["manifest_sha256"] != "manifest-hash" || report["publisher_spki_sha256"] != "publisher-pin" {
			t.Fatalf("%s report=%#v", path, body["report"])
		}
		gates, ok := report["gates"].(map[string]any)
		if !ok || len(gates) != 7 {
			t.Fatalf("%s gates=%#v", path, report["gates"])
		}
		for _, name := range []string{"package", "channel", "version", "signature", "files", "references", "storage"} {
			gate, ok := gates[name].(map[string]any)
			if !ok || gate["passed"] != true || gate["evidence"] == "" {
				t.Errorf("%s gate %s=%#v", path, name, gates[name])
			}
		}
	}
}

func TestCandidateHandlersDoNotDiscloseInvalidOrMissingCandidates(t *testing.T) {
	h := newHarness(t)
	acceptor := &fakeEnvelopeAcceptor{err: store.ErrNotFound}
	installUploadReceiver(t, h, &fakeUploadReceiver{}, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	for _, path := range []string{"/admin/api/candidates/current", "/admin/api/candidates/not-a-uuid", "/admin/api/candidates/" + testCandidateID} {
		response := h.request(http.MethodGet, path, "", "", cookie)
		if response.Code != http.StatusNotFound || decodeBody(t, response)["code"] != "not_found" {
			t.Fatalf("%s status=%d body=%s", path, response.Code, response.Body.String())
		}
	}
	request := httptestRequest(http.MethodPost, "/admin/api/candidates/current", csrf, cookie)
	response := h.request(http.MethodPost, request.URL.Path, `{}`, csrf, cookie)
	if response.Code != http.StatusMethodNotAllowed || response.Header().Get("Allow") != http.MethodGet {
		t.Fatalf("method status=%d allow=%q body=%s", response.Code, response.Header().Get("Allow"), response.Body.String())
	}
}

func httptestRequest(method, path, csrf string, cookie *http.Cookie) *http.Request {
	request, _ := http.NewRequest(method, "http://backend"+path, nil)
	request.Header.Set("X-CSRF-Token", csrf)
	request.AddCookie(cookie)
	return request
}

var _ httpapi.EnvelopeAcceptor = (*fakeEnvelopeAcceptor)(nil)
