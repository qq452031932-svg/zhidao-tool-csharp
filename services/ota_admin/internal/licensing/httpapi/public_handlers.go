package httpapi

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"mime"
	"net/http"
	"regexp"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"fanfh.cn/superpan/ota-admin/internal/licensing/service"
)

const (
	fallbackRequestID = "request-unavailable"
	nonceLength       = 32
	maximumCardBytes  = service.MaxRawCardInputBytes
)

var noncePattern = regexp.MustCompile(`^[A-Za-z0-9_-]{32}$`)

var activateRequestFields = map[string]struct{}{"protocol_version": {}, "activation_request_id": {}, "card": {}, "software_id": {}, "machine_hash": {}, "timestamp": {}, "nonce": {}}
var validateRequestFields = map[string]struct{}{"protocol_version": {}, "request_id": {}, "card": {}, "software_id": {}, "machine_hash": {}, "timestamp": {}, "nonce": {}}

type activateRequest struct {
	ProtocolVersion     int       `json:"protocol_version"`
	ActivationRequestID string    `json:"activation_request_id"`
	Card                string    `json:"card"`
	SoftwareID          string    `json:"software_id"`
	MachineHash         string    `json:"machine_hash"`
	Timestamp           time.Time `json:"timestamp"`
	Nonce               string    `json:"nonce"`
}

type validateRequest struct {
	ProtocolVersion int       `json:"protocol_version"`
	RequestID       string    `json:"request_id"`
	Card            string    `json:"card"`
	SoftwareID      string    `json:"software_id"`
	MachineHash     string    `json:"machine_hash"`
	Timestamp       time.Time `json:"timestamp"`
	Nonce           string    `json:"nonce"`
}

func (a *api) activate(w http.ResponseWriter, r *http.Request) {
	if headerBytes(r.Header) > a.options.MaxHeaderBytes {
		if a.charge(w, r, a.activation, fallbackRequestID) {
			a.writeClaims(w, http.StatusBadRequest, fallbackRequestID, model.CodeInvalidRequest, nil)
		}
		return
	}
	var request activateRequest
	if err := a.decodeRequest(w, r, &request, activateRequestFields); err != nil {
		if a.charge(w, r, a.activation, fallbackRequestID) {
			a.writeClaims(w, http.StatusBadRequest, fallbackRequestID, model.CodeInvalidRequest, nil)
		}
		return
	}
	requestID := validRequestID(request.ActivationRequestID)
	if request.ProtocolVersion != model.ProtocolVersion {
		if a.charge(w, r, a.activation, requestID) {
			a.writeClaims(w, http.StatusBadRequest, requestID, model.CodeUnsupportedProtocol, nil)
		}
		return
	}
	if !validPublicFields(request.ActivationRequestID, request.Card, request.SoftwareID, request.MachineHash, request.Timestamp, request.Nonce, a.options.Clock.Now(), a.options.TimestampWindow) {
		if a.charge(w, r, a.activation, requestID) {
			a.writeClaims(w, http.StatusBadRequest, requestID, model.CodeInvalidRequest, nil)
		}
		return
	}
	if !a.charge(w, r, a.activation, requestID) {
		return
	}
	claimed, err := a.options.ReplayStore.ClaimReplayNonce(r.Context(), request.Nonce, a.options.Clock.Now().UTC().Add(a.options.NonceLifetime), a.options.Clock.Now().UTC(), a.options.MaxReplayNonces)
	if err != nil {
		a.writeClaims(w, http.StatusServiceUnavailable, requestID, model.CodeServiceUnavailable, nil)
		return
	}
	if !claimed {
		a.writeClaims(w, http.StatusBadRequest, requestID, model.CodeInvalidRequest, nil)
		return
	}
	state, err := a.options.Service.Activate(r.Context(), request.ActivationRequestID, request.Card, request.SoftwareID, request.MachineHash)
	status, code := mapServiceError(err)
	if err != nil {
		a.writeClaims(w, status, requestID, code, nil)
		return
	}
	a.writeClaims(w, http.StatusOK, requestID, model.CodeOK, &state)
}

func (a *api) validate(w http.ResponseWriter, r *http.Request) {
	if headerBytes(r.Header) > a.options.MaxHeaderBytes {
		if a.charge(w, r, a.validation, fallbackRequestID) {
			a.writeClaims(w, http.StatusBadRequest, fallbackRequestID, model.CodeInvalidRequest, nil)
		}
		return
	}
	var request validateRequest
	if err := a.decodeRequest(w, r, &request, validateRequestFields); err != nil {
		if a.charge(w, r, a.validation, fallbackRequestID) {
			a.writeClaims(w, http.StatusBadRequest, fallbackRequestID, model.CodeInvalidRequest, nil)
		}
		return
	}
	requestID := validRequestID(request.RequestID)
	if request.ProtocolVersion != model.ProtocolVersion {
		if a.charge(w, r, a.validation, requestID) {
			a.writeClaims(w, http.StatusBadRequest, requestID, model.CodeUnsupportedProtocol, nil)
		}
		return
	}
	if !validPublicFields(request.RequestID, request.Card, request.SoftwareID, request.MachineHash, request.Timestamp, request.Nonce, a.options.Clock.Now(), a.options.TimestampWindow) {
		if a.charge(w, r, a.validation, requestID) {
			a.writeClaims(w, http.StatusBadRequest, requestID, model.CodeInvalidRequest, nil)
		}
		return
	}
	if !a.charge(w, r, a.validation, requestID) {
		return
	}
	claimed, err := a.options.ReplayStore.ClaimReplayNonce(r.Context(), request.Nonce, a.options.Clock.Now().UTC().Add(a.options.NonceLifetime), a.options.Clock.Now().UTC(), a.options.MaxReplayNonces)
	if err != nil {
		a.writeClaims(w, http.StatusServiceUnavailable, requestID, model.CodeServiceUnavailable, nil)
		return
	}
	if !claimed {
		a.writeClaims(w, http.StatusBadRequest, requestID, model.CodeInvalidRequest, nil)
		return
	}
	state, err := a.options.Service.Validate(r.Context(), request.Card, request.SoftwareID, request.MachineHash)
	status, code := mapServiceError(err)
	if err != nil {
		a.writeClaims(w, status, requestID, code, nil)
		return
	}
	a.writeClaims(w, http.StatusOK, requestID, model.CodeOK, &state)
}

func (a *api) decodeRequest(w http.ResponseWriter, r *http.Request, destination any, allowedFields map[string]struct{}) error {
	mediaType, parameters, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if err != nil || mediaType != "application/json" || (len(parameters) != 0 && !(len(parameters) == 1 && strings.EqualFold(parameters["charset"], "utf-8"))) {
		return errors.New("invalid content type")
	}
	if r.ContentLength > a.options.MaxBodyBytes {
		return errors.New("request body too large")
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, a.options.MaxBodyBytes))
	if err != nil {
		return errors.New("request body too large or unreadable")
	}
	if err := rejectDuplicateTopLevelFields(body, allowedFields); err != nil {
		return err
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(destination); err != nil {
		return errors.New("invalid request JSON")
	}
	var trailing any
	if err := decoder.Decode(&trailing); err != io.EOF {
		return errors.New("request must contain exactly one JSON object")
	}
	return nil
}

func rejectDuplicateTopLevelFields(body []byte, allowedFields map[string]struct{}) error {
	decoder := json.NewDecoder(bytes.NewReader(body))
	token, err := decoder.Token()
	if err != nil {
		return errors.New("invalid request JSON")
	}
	delimiter, ok := token.(json.Delim)
	if !ok || delimiter != '{' {
		return errors.New("request must be a JSON object")
	}
	seen := make(map[string]struct{})
	for decoder.More() {
		token, err := decoder.Token()
		if err != nil {
			return errors.New("invalid request JSON")
		}
		name, ok := token.(string)
		if !ok {
			return errors.New("invalid request JSON field")
		}
		if _, duplicate := seen[name]; duplicate {
			return errors.New("duplicate request JSON field")
		}
		if _, allowed := allowedFields[name]; !allowed {
			return errors.New("unknown request JSON field")
		}
		seen[name] = struct{}{}
		var value json.RawMessage
		if err = decoder.Decode(&value); err != nil {
			return errors.New("invalid request JSON value")
		}
	}
	if _, err := decoder.Token(); err != nil {
		return errors.New("invalid request JSON")
	}
	var trailing any
	if err := decoder.Decode(&trailing); err != io.EOF {
		return errors.New("request must contain exactly one JSON object")
	}
	return nil
}

func validPublicFields(requestID, card, softwareID, machineHash string, timestamp time.Time, nonce string, now time.Time, window time.Duration) bool {
	if !validIdentifier(requestID) || !validIdentifier(softwareID) || !validIdentifier(machineHash) {
		return false
	}
	if card == "" || len(card) > maximumCardBytes || !utf8.ValidString(card) || strings.TrimSpace(card) != card {
		return false
	}
	if len(nonce) != nonceLength || !noncePattern.MatchString(nonce) || timestamp.IsZero() || now.IsZero() {
		return false
	}
	now = now.UTC()
	timestamp = timestamp.UTC()
	return !timestamp.Before(now.Add(-window)) && !timestamp.After(now.Add(window))
}

func validIdentifier(value string) bool {
	return value != "" && len(value) <= model.MaxIdentifierLength && utf8.ValidString(value) && strings.TrimSpace(value) == value
}

func validRequestID(value string) string {
	if validIdentifier(value) {
		return value
	}
	return fallbackRequestID
}

func mapServiceError(err error) (int, model.Code) {
	switch {
	case err == nil:
		return http.StatusOK, model.CodeOK
	case errors.Is(err, service.ErrInvalidRequest):
		return http.StatusBadRequest, model.CodeInvalidRequest
	case errors.Is(err, service.ErrNotFound):
		return http.StatusForbidden, model.CodeLicenseNotFound
	case errors.Is(err, service.ErrExpired):
		return http.StatusForbidden, model.CodeLicenseExpired
	case errors.Is(err, service.ErrAppNotAllowed):
		return http.StatusForbidden, model.CodeAppNotAllowed
	case errors.Is(err, service.ErrAppDisabled):
		return http.StatusForbidden, model.CodeAppDisabled
	case errors.Is(err, service.ErrMachineMismatch):
		return http.StatusConflict, model.CodeMachineMismatch
	default:
		return http.StatusServiceUnavailable, model.CodeServiceUnavailable
	}
}

// charge signs only the first denial for an endpoint/client/window. Later denials
// use a minimal unsigned 429; clients must treat either form as temporary rate limiting.
func (a *api) charge(w http.ResponseWriter, r *http.Request, limiter *fixedWindowLimiter, requestID string) bool {
	allowed, retry, signRejection := limiter.charge(clientKey(r))
	if allowed {
		return true
	}
	w.Header().Set("Retry-After", strconv.Itoa(retry))
	if signRejection {
		a.writeClaims(w, http.StatusTooManyRequests, requestID, model.CodeRateLimited, nil)
	} else {
		a.writeUnsignedCode(w, http.StatusTooManyRequests, model.CodeRateLimited)
	}
	return false
}

func (a *api) writeUnsignedCode(w http.ResponseWriter, status int, code model.Code) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(struct {
		Code model.Code `json:"code"`
	}{Code: code})
}

func (a *api) writeClaims(w http.ResponseWriter, status int, requestID string, code model.Code, state *model.ActivationState) {
	claims := model.Claims{ProtocolVersion: model.ProtocolVersion, RequestID: validRequestID(requestID), ServerTime: a.options.Clock.Now().UTC(), Decision: model.DecisionDeny, Code: code}
	if state != nil && code == model.CodeOK {
		claims.Decision = model.DecisionAllow
		claims.CardID = stringPointer(state.CardID)
		claims.SoftwareID = stringPointer(state.SoftwareID)
		claims.MachineHash = stringPointer(state.MachineHash)
		claims.PackageName = stringPointer(state.PackageName)
		claims.ExpiresAt = timePointer(state.ExpiresAt)
		claims.LeaseUntil = timePointer(state.LeaseUntil)
	}
	envelope, err := a.options.Signer.Sign(claims)
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	if err != nil {
		w.WriteHeader(http.StatusServiceUnavailable)
		_, _ = io.WriteString(w, `{"code":"service_unavailable"}`)
		return
	}
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(envelope)
}

func stringPointer(value string) *string     { return &value }
func timePointer(value time.Time) *time.Time { return &value }
