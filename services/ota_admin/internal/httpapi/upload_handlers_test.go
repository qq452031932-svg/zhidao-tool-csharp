package httpapi_test

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

type fakeUploadReceiver struct {
	result        *upload.Envelope
	err           error
	calls         int
	contentLength int64
	body          []byte
}

type blockingUploadReceiver struct {
	result  *upload.Envelope
	started chan struct{}
	release chan struct{}
}

type fakeEnvelopeAcceptor struct {
	calls       int
	lookupCalls int
	failCalls   int
	sessionID   string
	operationID string
	envelope    *upload.Envelope
	result      candidate.Summary
	existing    *candidate.Summary
	err         error
}

func (acceptor *fakeEnvelopeAcceptor) ReserveEnvelopeForActiveSession(_ context.Context, sessionID, operationID string) (candidate.Summary, bool, error) {
	acceptor.lookupCalls++
	acceptor.sessionID = sessionID
	acceptor.operationID = operationID
	if acceptor.err != nil && acceptor.existing != nil {
		return candidate.Summary{}, false, acceptor.err
	}
	if acceptor.existing != nil {
		return *acceptor.existing, false, nil
	}
	return candidate.Summary{CandidateID: testCandidateID, OperationID: operationID, State: "uploading"}, true, nil
}

func (acceptor *fakeEnvelopeAcceptor) FailEnvelopeForActiveSession(_ context.Context, _, operationID, code string) (candidate.Summary, error) {
	acceptor.failCalls++
	failed := candidate.Summary{CandidateID: testCandidateID, OperationID: operationID, State: "failed", ResultCode: code}
	acceptor.existing = &failed
	return failed, nil
}

func (acceptor *fakeEnvelopeAcceptor) AcceptEnvelopeForActiveSession(_ context.Context, sessionID, operationID string, envelope *upload.Envelope) (candidate.Summary, error) {
	acceptor.calls++
	acceptor.sessionID = sessionID
	acceptor.operationID = operationID
	acceptor.envelope = envelope
	if acceptor.result.CandidateID == "" {
		acceptor.result = testCandidateSummary(operationID)
	}
	return acceptor.result, acceptor.err
}

func (acceptor *fakeEnvelopeAcceptor) CandidateForOperationForActiveSession(_ context.Context, _ string, operationID string) (candidate.Summary, bool, error) {
	acceptor.lookupCalls++
	if acceptor.err != nil && acceptor.existing != nil {
		return candidate.Summary{}, false, acceptor.err
	}
	if acceptor.existing == nil {
		return candidate.Summary{}, false, nil
	}
	return *acceptor.existing, true, nil
}

func (acceptor *fakeEnvelopeAcceptor) CurrentForActiveSession(context.Context, string) (candidate.Summary, error) {
	if acceptor.result.CandidateID == "" {
		return candidate.Summary{}, acceptor.err
	}
	return acceptor.result, acceptor.err
}

func (acceptor *fakeEnvelopeAcceptor) StatusForActiveSession(context.Context, string, string) (candidate.Summary, error) {
	return acceptor.CurrentForActiveSession(context.Background(), "")
}

func (receiver *fakeUploadReceiver) Receive(ctx context.Context, body io.Reader, contentLength int64) (*upload.Envelope, error) {
	receiver.calls++
	receiver.contentLength = contentLength
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	receiver.body, _ = io.ReadAll(body)
	return receiver.result, receiver.err
}

func (receiver *blockingUploadReceiver) Receive(ctx context.Context, _ io.Reader, _ int64) (*upload.Envelope, error) {
	close(receiver.started)
	select {
	case <-receiver.release:
		return receiver.result, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func TestUploadEndpointRequiresAuthenticationSameOriginCSRFAndPasswordChange(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	acceptor := &fakeEnvelopeAcceptor{}
	installUploadReceiver(t, h, receiver, acceptor)

	if response := uploadRequest(h, []byte("zip"), "application/zip", "", nil); response.Code != http.StatusUnauthorized {
		t.Fatalf("anonymous status=%d body=%s", response.Code, response.Body.String())
	}
	cookie, csrf, _, _ := login(t, h, oldPassword)
	if response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie); response.Code != http.StatusForbidden || decodeBody(t, response)["code"] != "password_change_required" {
		t.Fatalf("forced-change status=%d body=%s", response.Code, response.Body.String())
	}
	if receiver.calls != 0 {
		t.Fatalf("receiver called before password change: %d", receiver.calls)
	}

	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	for name, configure := range map[string]func(*http.Request){
		"missing csrf":   func(request *http.Request) { request.Header.Del("X-CSRF-Token") },
		"foreign origin": func(request *http.Request) { request.Header.Set("Origin", "https://evil.example") },
	} {
		t.Run(name, func(t *testing.T) {
			request := newUploadRequest([]byte("zip"), csrf, cookie)
			configure(request)
			response := httptest.NewRecorder()
			h.handler.ServeHTTP(response, request)
			if response.Code != http.StatusForbidden {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
	if receiver.calls != 0 {
		t.Fatalf("receiver called for rejected request: %d", receiver.calls)
	}
}

func TestUploadEndpointStreamsRawZIPAndReturnsValidatedCandidateSummary(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	acceptor := &fakeEnvelopeAcceptor{}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	payload := []byte("streamed zip bytes")
	response := uploadRequest(h, payload, "application/zip", csrf, cookie)
	if response.Code != http.StatusAccepted {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if receiver.calls != 1 || receiver.contentLength != int64(len(payload)) || !bytes.Equal(receiver.body, payload) {
		t.Fatalf("receiver calls=%d length=%d body=%q", receiver.calls, receiver.contentLength, receiver.body)
	}
	if acceptor.calls != 1 || acceptor.sessionID == "" || acceptor.operationID != testOperationID || acceptor.envelope != receiver.result {
		t.Fatalf("ownership handoff calls=%d session=%q operation=%q envelope=%p", acceptor.calls, acceptor.sessionID, acceptor.operationID, acceptor.envelope)
	}
	body := response.Body.String()
	if strings.Contains(body, `C:\\secret`) || strings.Contains(body, "/srv/secret") {
		t.Fatalf("response leaked paths or verification claim: %s", body)
	}
	decoded := decodeBody(t, response)
	if decoded["state"] != "validated" || decoded["candidate_id"] != testCandidateID || decoded["operation_id"] != testOperationID {
		t.Fatalf("response metadata=%v", decoded)
	}
}

func TestUploadEndpointReturnsSafeChineseValidationErrorWithOperationID(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	acceptor := &fakeEnvelopeAcceptor{err: release.ErrSignature}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	body := decodeBody(t, response)
	if response.Code != http.StatusUnprocessableEntity || body["code"] != "signature_mismatch" || body["message"] != "签名校验失败" || body["operation_id"] != testOperationID {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if acceptor.failCalls != 1 {
		t.Fatalf("failed reservation terminalizations=%d", acceptor.failCalls)
	}
	replayed := uploadRequest(h, []byte("must not be consumed"), "application/zip", csrf, cookie)
	if replayed.Code != http.StatusUnprocessableEntity || decodeBody(t, replayed)["code"] != "signature_mismatch" || receiver.calls != 1 {
		t.Fatalf("replay status=%d receiver=%d body=%s", replayed.Code, receiver.calls, replayed.Body.String())
	}
}

func TestUploadEndpointTerminalizesEveryPostReservationFailure(t *testing.T) {
	tests := []struct {
		name     string
		receiver *fakeUploadReceiver
		acceptor *fakeEnvelopeAcceptor
	}{
		{name: "receiver", receiver: &fakeUploadReceiver{err: upload.ErrInvalidArchive}, acceptor: &fakeEnvelopeAcceptor{}},
		{name: "nil envelope", receiver: &fakeUploadReceiver{}, acceptor: &fakeEnvelopeAcceptor{}},
		{name: "acceptance", receiver: &fakeUploadReceiver{result: testEnvelope()}, acceptor: &fakeEnvelopeAcceptor{err: release.ErrMetadata}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			h := newHarness(t)
			installUploadReceiver(t, h, test.receiver, test.acceptor)
			cookie, csrf, _, _ := login(t, h, oldPassword)
			cookie, csrf = changeTestPassword(t, h, cookie, csrf)
			_ = uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
			if test.acceptor.failCalls != 1 {
				t.Fatalf("failed reservation terminalizations=%d", test.acceptor.failCalls)
			}
		})
	}
}

func TestUploadEndpointRequiresCanonicalIdempotencyKeyAndReturnsExistingWithoutReceiving(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	existing := testCandidateSummary(testOperationID)
	acceptor := &fakeEnvelopeAcceptor{existing: &existing}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	request := newUploadRequest([]byte("zip"), csrf, cookie)
	request.Header.Del("Idempotency-Key")
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || receiver.calls != 0 {
		t.Fatalf("missing key status=%d receiver=%d body=%s", response.Code, receiver.calls, response.Body.String())
	}
	request = newUploadRequest([]byte("zip"), csrf, cookie)
	request.Header.Set("Idempotency-Key", strings.ToUpper(testOperationID))
	response = httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || receiver.calls != 0 {
		t.Fatalf("noncanonical key status=%d receiver=%d body=%s", response.Code, receiver.calls, response.Body.String())
	}
	response = uploadRequest(h, []byte("retry bytes are not consumed"), "application/zip", csrf, cookie)
	if response.Code != http.StatusAccepted || receiver.calls != 0 || acceptor.calls != 0 || decodeBody(t, response)["candidate_id"] != testCandidateID {
		t.Fatalf("retry status=%d receiver=%d accept=%d body=%s", response.Code, receiver.calls, acceptor.calls, response.Body.String())
	}
}

func TestUploadEndpointRevalidatesSessionAfterBlockedReceive(t *testing.T) {
	tests := []struct {
		name       string
		invalidate func(*testing.T, *harness, *http.Cookie, string)
	}{
		{"logout", func(t *testing.T, h *harness, cookie *http.Cookie, csrf string) {
			response := h.request(http.MethodPost, "/admin/api/session/logout", `{}`, csrf, cookie)
			if response.Code != http.StatusNoContent {
				t.Fatalf("logout status=%d body=%s", response.Code, response.Body.String())
			}
		}},
		{"password rotation", func(t *testing.T, h *harness, cookie *http.Cookie, csrf string) {
			response := h.request(http.MethodPost, "/admin/api/session/password", `{"current_password":"`+newPassword+`","new_password":"third password with enough entropy"}`, csrf, cookie)
			if response.Code != http.StatusOK {
				t.Fatalf("password rotation status=%d body=%s", response.Code, response.Body.String())
			}
		}},
		{"expiry", func(_ *testing.T, h *harness, _ *http.Cookie, _ string) {
			h.clock.Advance(31 * time.Minute)
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			h := newHarness(t)
			cookie, csrf, _, _ := login(t, h, oldPassword)
			cookie, csrf = changeTestPassword(t, h, cookie, csrf)
			receiver := &blockingUploadReceiver{result: testEnvelope(), started: make(chan struct{}), release: make(chan struct{})}
			acceptor := &fakeEnvelopeAcceptor{}
			installUploadReceiver(t, h, receiver, acceptor)

			responses := make(chan *httptest.ResponseRecorder, 1)
			go func() {
				responses <- uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
			}()
			select {
			case <-receiver.started:
			case <-time.After(5 * time.Second):
				t.Fatal("upload did not reach blocked receiver")
			}
			test.invalidate(t, h, cookie, csrf)
			close(receiver.release)

			select {
			case response := <-responses:
				if response.Code != http.StatusUnauthorized || decodeBody(t, response)["code"] != "authentication_failed" {
					t.Fatalf("upload status=%d body=%s", response.Code, response.Body.String())
				}
			case <-time.After(5 * time.Second):
				t.Fatal("upload did not finish after receiver release")
			}
			if acceptor.calls != 0 {
				t.Fatalf("acceptor called for stale session: %d", acceptor.calls)
			}
		})
	}
}

func TestUploadEndpointRequiresOneExactZIPContentType(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	installUploadReceiver(t, h, receiver, &fakeEnvelopeAcceptor{})
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	for _, contentType := range []string{"", "application/octet-stream", "application/zip; charset=utf-8", "Application/ZIP"} {
		response := uploadRequest(h, []byte("zip"), contentType, csrf, cookie)
		if response.Code != http.StatusUnsupportedMediaType {
			t.Fatalf("content-type=%q status=%d body=%s", contentType, response.Code, response.Body.String())
		}
	}
	request := newUploadRequest([]byte("zip"), csrf, cookie)
	request.Header.Add("Content-Type", "application/zip")
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusUnsupportedMediaType {
		t.Fatalf("duplicate content type status=%d body=%s", response.Code, response.Body.String())
	}
	if receiver.calls != 0 {
		t.Fatalf("receiver called for invalid content type: %d", receiver.calls)
	}
}

func TestUploadEndpointNormalizesEnvelopeFailures(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want int
		code string
	}{
		{"compressed limit", upload.ErrCompressedLimit, http.StatusRequestEntityTooLarge, "upload_too_large"},
		{"expanded limit", upload.ErrExpandedLimit, http.StatusUnprocessableEntity, "invalid_zip_envelope"},
		{"unsafe name", upload.ErrUnsafeName, http.StatusUnprocessableEntity, "invalid_zip_envelope"},
		{"disk", upload.ErrInsufficientDisk, http.StatusInsufficientStorage, "insufficient_storage"},
		{"internal", errors.New("secret disk path /srv/secret"), http.StatusInternalServerError, "operation_failed"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			h := newHarness(t)
			receiver := &fakeUploadReceiver{err: test.err}
			installUploadReceiver(t, h, receiver, &fakeEnvelopeAcceptor{})
			cookie, csrf, _, _ := login(t, h, oldPassword)
			cookie, csrf = changeTestPassword(t, h, cookie, csrf)
			response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
			if response.Code != test.want || decodeBody(t, response)["code"] != test.code || strings.Contains(response.Body.String(), "secret") {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
			replayed := uploadRequest(h, []byte("retry must not be consumed"), "application/zip", csrf, cookie)
			if replayed.Code != test.want || decodeBody(t, replayed)["code"] != test.code || receiver.calls != 1 {
				t.Fatalf("replay status=%d receiver=%d body=%s", replayed.Code, receiver.calls, replayed.Body.String())
			}
		})
	}
}

func TestUploadEndpointNormalizesSessionRevocationAtTransactionalAcceptance(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	acceptor := &fakeEnvelopeAcceptor{err: session.ErrUnauthorized}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	if response.Code != http.StatusUnauthorized || decodeBody(t, response)["code"] != "authentication_failed" {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if acceptor.calls != 1 {
		t.Fatalf("acceptor calls=%d", acceptor.calls)
	}
}

func TestUploadEndpointIsUnavailableUntilReceiverIsConfigured(t *testing.T) {
	h := newHarness(t)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	if response.Code != http.StatusServiceUnavailable || decodeBody(t, response)["code"] != "upload_unavailable" {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestUploadEndpointRequiresAnExplicitEnvelopeOwner(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	installUploadReceiver(t, h, receiver, nil)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	if response.Code != http.StatusServiceUnavailable || receiver.calls != 0 {
		t.Fatalf("status=%d receiver calls=%d body=%s", response.Code, receiver.calls, response.Body.String())
	}
}

func installUploadReceiver(t *testing.T, h *harness, receiver httpapi.UploadReceiver, acceptor httpapi.EnvelopeAcceptor) {
	t.Helper()
	var err error
	h.handler, err = httpapi.NewRouter(httpapi.Options{Sessions: h.sessions, PublicOrigin: publicOrigin, MaxJSONBytes: 4 << 10, Uploads: receiver, UploadEnvelopes: acceptor})
	if err != nil {
		t.Fatal(err)
	}
}

func changeTestPassword(t *testing.T, h *harness, cookie *http.Cookie, csrf string) (*http.Cookie, string) {
	t.Helper()
	response := h.request(http.MethodPost, "/admin/api/session/password", `{"current_password":"`+oldPassword+`","new_password":"`+newPassword+`"}`, csrf, cookie)
	if response.Code != http.StatusOK {
		t.Fatalf("password change status=%d body=%s", response.Code, response.Body.String())
	}
	return cookieNamed(t, response, "ota_admin_session"), decodeBody(t, response)["csrf_token"].(string)
}

func newUploadRequest(body []byte, csrf string, cookie *http.Cookie) *http.Request {
	request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/uploads", bytes.NewReader(body))
	request.RemoteAddr = "127.0.0.1:43210"
	request.Header.Set("Content-Type", "application/zip")
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

const (
	testOperationID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
	testCandidateID = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
)

func testCandidateSummary(operationID string) candidate.Summary {
	return candidate.Summary{
		CandidateID: testCandidateID,
		OperationID: operationID,
		State:       "validated",
		CreatedAt:   time.Unix(2_000_000_000, 0).UTC(),
		ExpiresAt:   time.Unix(2_000_000_000, 0).UTC().Add(24 * time.Hour),
		Report: release.Report{
			SchemaVersion: 1,
			Verified:      true,
			PackageID:     "SuperPan",
			Channel:       "stable",
			Version:       "21.0.0",
			FileCount:     7,
		},
	}
}

func uploadRequest(h *harness, body []byte, contentType, csrf string, cookie *http.Cookie) *httptest.ResponseRecorder {
	request := newUploadRequest(body, csrf, cookie)
	if contentType == "" {
		request.Header.Del("Content-Type")
	} else {
		request.Header.Set("Content-Type", contentType)
	}
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	return response
}

func testEnvelope() *upload.Envelope {
	return &upload.Envelope{
		ArchivePath: "C:\\secret\\candidate.zip", ExtractionRoot: "/srv/secret/extracted",
		SHA256: strings.Repeat("a", 64), CompressedBytes: 18, ExpandedBytes: 7,
		Entries:  []upload.Entry{{Name: "upload-manifest.sha256", Path: "/srv/secret/extracted/upload-manifest.sha256", Size: 7}},
		Verified: false,
	}
}
