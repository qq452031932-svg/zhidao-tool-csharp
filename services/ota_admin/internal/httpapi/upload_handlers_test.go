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
	calls           int
	lookupCalls     int
	failCalls       int
	sessionID       string
	operationID     string
	envelope        *upload.Envelope
	result          candidate.Summary
	existing        *candidate.Summary
	err             error
	failErr         error
	failResult      *candidate.Summary
	failUntilExpiry bool
	lookupResult    *candidate.Summary
	lookupErr       error
	lookupSawLive   bool
	takeoverExpired bool
}

func (acceptor *fakeEnvelopeAcceptor) ReserveEnvelopeForActiveSession(_ context.Context, sessionID, operationID string) (candidate.Summary, bool, error) {
	acceptor.lookupCalls++
	acceptor.sessionID = sessionID
	acceptor.operationID = operationID
	if acceptor.err != nil && acceptor.existing != nil {
		return candidate.Summary{}, false, acceptor.err
	}
	if acceptor.existing != nil {
		if acceptor.takeoverExpired && acceptor.existing.State == "uploading" {
			failed := *acceptor.existing
			failed.State = "failed"
			failed.ResultCode = "operation_failed"
			acceptor.existing = &failed
		}
		return *acceptor.existing, false, nil
	}
	created := candidate.Summary{CandidateID: testCandidateID, OperationID: operationID, State: "uploading", LeaseOwner: testLeaseOwner}
	acceptor.existing = &created
	return created, true, nil
}

func (acceptor *fakeEnvelopeAcceptor) RenewEnvelopeLeaseForActiveSession(context.Context, string, string, string) error {
	return nil
}

func (acceptor *fakeEnvelopeAcceptor) FailEnvelopeForActiveSession(ctx context.Context, _, operationID, code, _ string) (candidate.Summary, error) {
	acceptor.failCalls++
	if acceptor.failUntilExpiry {
		<-ctx.Done()
		return candidate.Summary{}, ctx.Err()
	}
	if acceptor.failErr != nil {
		return candidate.Summary{}, acceptor.failErr
	}
	if acceptor.failResult != nil {
		failed := *acceptor.failResult
		acceptor.existing = &failed
		return failed, nil
	}
	failed := candidate.Summary{CandidateID: testCandidateID, OperationID: operationID, State: "failed", ResultCode: code}
	acceptor.existing = &failed
	return failed, nil
}

func (acceptor *fakeEnvelopeAcceptor) AcceptEnvelopeForActiveSession(_ context.Context, sessionID, operationID string, envelope *upload.Envelope, _ string) (candidate.Summary, error) {
	acceptor.calls++
	acceptor.sessionID = sessionID
	acceptor.operationID = operationID
	acceptor.envelope = envelope
	if acceptor.result.CandidateID == "" {
		acceptor.result = testCandidateSummary(operationID)
	}
	if acceptor.err == nil {
		accepted := acceptor.result
		acceptor.existing = &accepted
	}
	return acceptor.result, acceptor.err
}

func (acceptor *fakeEnvelopeAcceptor) CandidateForOperationForActiveSession(ctx context.Context, _ string, operationID string) (candidate.Summary, bool, error) {
	acceptor.lookupCalls++
	if ctx.Err() == nil {
		acceptor.lookupSawLive = true
	}
	if acceptor.lookupErr != nil {
		return candidate.Summary{}, false, acceptor.lookupErr
	}
	if acceptor.err != nil && acceptor.existing != nil {
		return candidate.Summary{}, false, acceptor.err
	}
	if acceptor.lookupResult != nil {
		return *acceptor.lookupResult, true, nil
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
	if strings.Contains(body, `C:\\secret`) || strings.Contains(body, "/srv/secret") || strings.Contains(body, testLeaseOwner) || strings.Contains(body, "lease") {
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

func TestUploadEndpointMapsAConcurrentTerminalCandidateIdenticallyOnFirstResponseAndReplay(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	terminal := testCandidateSummary(testOperationID)
	terminal.State = "failed"
	terminal.ResultCode = "candidate_expired"
	acceptor := &fakeEnvelopeAcceptor{result: terminal}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	first := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	replayed := uploadRequest(h, []byte("must not be consumed"), "application/zip", csrf, cookie)
	for name, response := range map[string]*httptest.ResponseRecorder{"first": first, "replay": replayed} {
		body := decodeBody(t, response)
		if response.Code != http.StatusGone || body["code"] != "candidate_expired" || body["operation_id"] != testOperationID {
			t.Fatalf("%s status=%d body=%s", name, response.Code, response.Body.String())
		}
	}
	if receiver.calls != 1 || acceptor.calls != 1 {
		t.Fatalf("receiver=%d acceptor=%d", receiver.calls, acceptor.calls)
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

func TestUploadEndpointReportsPersistedPendingStateWhenTerminalizationDoesNotCommit(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{err: upload.ErrInvalidArchive}
	acceptor := &fakeEnvelopeAcceptor{failErr: errors.New("injected persistence failure")}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	if response.Code != http.StatusConflict || decodeBody(t, response)["code"] != "upload_in_progress" || response.Header().Get("Retry-After") == "" || acceptor.failCalls != 1 {
		t.Fatalf("status=%d failCalls=%d body=%s", response.Code, acceptor.failCalls, response.Body.String())
	}
}

func TestUploadEndpointUsesIndependentLookupContextAfterFinalizationDeadline(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{err: upload.ErrInvalidArchive}
	acceptor := &fakeEnvelopeAcceptor{failUntilExpiry: true}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	if response.Code != http.StatusConflict || decodeBody(t, response)["code"] != "upload_in_progress" || response.Header().Get("Retry-After") == "" {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if !acceptor.lookupSawLive {
		t.Fatal("terminal-state lookup reused the exhausted finalization context")
	}
}

func TestUploadEndpointConservativelyReportsPendingWhenFailureStateCannotBeRead(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{err: upload.ErrInvalidArchive}
	acceptor := &fakeEnvelopeAcceptor{
		failErr:   errors.New("injected finalization failure"),
		lookupErr: errors.New("injected lookup failure"),
	}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	if response.Code != http.StatusConflict || decodeBody(t, response)["code"] != "upload_in_progress" || response.Header().Get("Retry-After") == "" {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestUploadEndpointUsesPersistedFailureSummaryInsteadOfTheLocalError(t *testing.T) {
	tests := []struct {
		name     string
		acceptor *fakeEnvelopeAcceptor
	}{
		{
			name: "fail returns an existing terminal result",
			acceptor: &fakeEnvelopeAcceptor{failResult: &candidate.Summary{
				CandidateID: testCandidateID, OperationID: testOperationID, State: "failed", ResultCode: "candidate_expired",
			}},
		},
		{
			name: "fallback lookup finds a terminal result",
			acceptor: &fakeEnvelopeAcceptor{
				failErr: errors.New("lost finalization acknowledgement"),
				lookupResult: &candidate.Summary{
					CandidateID: testCandidateID, OperationID: testOperationID, State: "failed", ResultCode: "signature_mismatch",
				},
			},
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			h := newHarness(t)
			receiver := &fakeUploadReceiver{err: upload.ErrInvalidArchive}
			installUploadReceiver(t, h, receiver, test.acceptor)
			cookie, csrf, _, _ := login(t, h, oldPassword)
			cookie, csrf = changeTestPassword(t, h, cookie, csrf)

			response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
			body := decodeBody(t, response)
			if test.name == "fail returns an existing terminal result" {
				if response.Code != http.StatusGone || body["code"] != "candidate_expired" {
					t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
				}
				return
			}
			if response.Code != http.StatusUnprocessableEntity || body["code"] != "signature_mismatch" {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
}

func TestUploadEndpointReturnsTheSamePendingResultOnImmediateReplayWhenFailurePersistenceWasLost(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{err: upload.ErrInvalidArchive}
	acceptor := &fakeEnvelopeAcceptor{failErr: errors.New("injected persistence failure")}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	first := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	firstBody := decodeBody(t, first)
	if first.Code != http.StatusConflict || firstBody["code"] != "upload_in_progress" || first.Header().Get("Retry-After") == "" || receiver.calls != 1 {
		t.Fatalf("first status=%d body=%s", first.Code, first.Body.String())
	}
	replayed := uploadRequest(h, []byte("must not be consumed"), "application/zip", csrf, cookie)
	replayBody := decodeBody(t, replayed)
	if replayed.Code != http.StatusConflict || replayBody["code"] != "upload_in_progress" || replayed.Header().Get("Retry-After") == "" || receiver.calls != 1 {
		t.Fatalf("replay status=%d receiver=%d body=%s", replayed.Code, receiver.calls, replayed.Body.String())
	}
	if firstBody["code"] != replayBody["code"] || firstBody["operation_id"] != replayBody["operation_id"] {
		t.Fatalf("first=%v replay=%v", firstBody, replayBody)
	}
}

func TestUploadEndpointReportsAnExistingReservationAsInProgress(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	pending := candidate.Summary{CandidateID: testCandidateID, OperationID: testOperationID, State: "uploading"}
	acceptor := &fakeEnvelopeAcceptor{existing: &pending}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	response := uploadRequest(h, []byte("must not be consumed"), "application/zip", csrf, cookie)
	if response.Code != http.StatusConflict || decodeBody(t, response)["code"] != "upload_in_progress" || receiver.calls != 0 {
		t.Fatalf("status=%d receiver=%d body=%s", response.Code, receiver.calls, response.Body.String())
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
				if response.Code != http.StatusInternalServerError || decodeBody(t, response)["code"] != "operation_failed" {
					t.Fatalf("upload status=%d body=%s", response.Code, response.Body.String())
				}
			case <-time.After(5 * time.Second):
				t.Fatal("upload did not finish after receiver release")
			}
			if acceptor.calls != 0 {
				t.Fatalf("acceptor called for stale session: %d", acceptor.calls)
			}
			if acceptor.failCalls != 1 {
				t.Fatalf("reservation terminalizations=%d", acceptor.failCalls)
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

func TestUploadEndpointUsesPersistedFailureAtTransactionalSessionRevocation(t *testing.T) {
	h := newHarness(t)
	receiver := &fakeUploadReceiver{result: testEnvelope()}
	acceptor := &fakeEnvelopeAcceptor{err: session.ErrUnauthorized}
	installUploadReceiver(t, h, receiver, acceptor)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)

	response := uploadRequest(h, []byte("zip"), "application/zip", csrf, cookie)
	if response.Code != http.StatusInternalServerError || decodeBody(t, response)["code"] != "operation_failed" {
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
	testLeaseOwner  = "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
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
