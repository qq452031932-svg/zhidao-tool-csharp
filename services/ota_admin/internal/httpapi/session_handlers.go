package httpapi

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"mime"
	"net/http"
	"strings"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/session"
)

var (
	errInvalidJSON  = errors.New("invalid JSON request")
	errBodyTooLarge = errors.New("JSON request body is too large")
)

type loginRequest struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

type passwordRequest struct {
	CurrentPassword string `json:"current_password"`
	NewPassword     string `json:"new_password"`
}

func (router *Router) bootstrap(response http.ResponseWriter, request *http.Request) {
	if !requestBodyIsEmpty(request) {
		writeError(response, http.StatusBadRequest, "invalid_request")
		return
	}
	token, csrfToken, expiresAt, maxAge, err := router.sessions.Bootstrap(security.TrustedSourceAddress(request))
	if err != nil {
		if errors.Is(err, session.ErrBootstrapLimited) {
			response.Header().Set("Retry-After", retryAfterSeconds(maxAge))
			writeError(response, http.StatusTooManyRequests, "request_unavailable")
			return
		}
		writeError(response, http.StatusInternalServerError, "operation_failed")
		return
	}
	setAdminCookie(response, preauthCookieName, token, expiresAt, maxAge)
	writeJSON(response, http.StatusOK, map[string]any{"csrf_token": csrfToken})
}

func (router *Router) login(response http.ResponseWriter, request *http.Request) {
	preauth, havePreauth := uniqueCookie(request, preauthCookieName)
	csrfToken, haveCSRF := uniqueHeader(request, "X-CSRF-Token")
	grant := router.sessions.ConsumePreauthorization(cookieValue(preauth, havePreauth), headerValue(csrfToken, haveCSRF))
	clearAdminCookie(response, preauthCookieName)

	if !security.SameOriginRequest(request, router.publicOrigin) {
		writeError(response, http.StatusForbidden, "request_rejected")
		return
	}
	if !havePreauth || !haveCSRF {
		writeError(response, http.StatusUnauthorized, "authentication_failed")
		return
	}
	var input loginRequest
	if err := decodeStrictJSON(request, router.maxJSONBytes, &input); err != nil {
		writeJSONDecodeError(response, err)
		return
	}
	if input.Username == "" || len(input.Username) > 128 || input.Password == "" || len(input.Password) > security.MaximumPasswordBytes {
		writeError(response, http.StatusUnauthorized, "authentication_failed")
		return
	}
	result, retryAfter, err := router.sessions.Login(request.Context(), grant, input.Username, input.Password, security.TrustedSourceAddress(request))
	if err != nil {
		if errors.Is(err, session.ErrLocked) {
			response.Header().Set("Retry-After", retryAfterSeconds(retryAfter))
			writeError(response, http.StatusTooManyRequests, "authentication_failed")
		} else if errors.Is(err, session.ErrUnauthorized) {
			writeError(response, http.StatusUnauthorized, "authentication_failed")
		} else {
			writeError(response, http.StatusInternalServerError, "operation_failed")
		}
		return
	}
	setAdminCookie(response, sessionCookieName, result.Token, result.ExpiresAt, result.CookieMaxAge)
	writeJSON(response, http.StatusOK, map[string]any{
		"csrf_token":           result.CSRFToken,
		"must_change_password": result.MustChangePassword,
	})
}

func (router *Router) logout(response http.ResponseWriter, request *http.Request, principal session.Principal) {
	var input struct{}
	if err := decodeStrictJSON(request, router.maxJSONBytes, &input); err != nil {
		writeJSONDecodeError(response, err)
		return
	}
	if err := router.sessions.Logout(request.Context(), principal); err != nil {
		if errors.Is(err, session.ErrUnauthorized) {
			writeError(response, http.StatusUnauthorized, "authentication_failed")
		} else {
			writeError(response, http.StatusInternalServerError, "operation_failed")
		}
		return
	}
	clearAdminCookie(response, sessionCookieName)
	response.WriteHeader(http.StatusNoContent)
}

func (router *Router) changePassword(response http.ResponseWriter, request *http.Request, principal session.Principal) {
	var input passwordRequest
	if err := decodeStrictJSON(request, router.maxJSONBytes, &input); err != nil {
		writeJSONDecodeError(response, err)
		return
	}
	if input.CurrentPassword == "" || input.NewPassword == "" || len(input.CurrentPassword) > security.MaximumPasswordBytes || len(input.NewPassword) > security.MaximumPasswordBytes {
		writeError(response, http.StatusBadRequest, "password_rejected")
		return
	}
	result, err := router.sessions.ChangePassword(request.Context(), principal, input.CurrentPassword, input.NewPassword)
	if err != nil {
		if errors.Is(err, session.ErrPassword) {
			writeError(response, http.StatusBadRequest, "password_rejected")
		} else {
			writeError(response, http.StatusInternalServerError, "operation_failed")
		}
		return
	}
	setAdminCookie(response, sessionCookieName, result.Token, result.ExpiresAt, result.CookieMaxAge)
	writeJSON(response, http.StatusOK, map[string]any{"csrf_token": result.CSRFToken, "must_change_password": false})
}

func decodeStrictJSON(request *http.Request, maximum int64, destination any) error {
	contentTypes := request.Header.Values("Content-Type")
	if len(contentTypes) != 1 {
		return &httpStatusError{status: http.StatusUnsupportedMediaType}
	}
	mediaType, parameters, err := mime.ParseMediaType(contentTypes[0])
	if err != nil || mediaType != "application/json" || len(parameters) > 1 || (len(parameters) == 1 && !strings.EqualFold(parameters["charset"], "utf-8")) {
		return &httpStatusError{status: http.StatusUnsupportedMediaType}
	}
	if request.ContentLength > maximum {
		return errBodyTooLarge
	}
	data, err := io.ReadAll(io.LimitReader(request.Body, maximum+1))
	if err != nil {
		return errInvalidJSON
	}
	if int64(len(data)) > maximum {
		return errBodyTooLarge
	}
	if err := rejectDuplicateTopLevelKeys(data); err != nil {
		return errInvalidJSON
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(destination); err != nil {
		return errInvalidJSON
	}
	if err := ensureJSONEOF(decoder); err != nil {
		return errInvalidJSON
	}
	return nil
}

func rejectDuplicateTopLevelKeys(data []byte) error {
	decoder := json.NewDecoder(bytes.NewReader(data))
	token, err := decoder.Token()
	if err != nil || token != json.Delim('{') {
		return errInvalidJSON
	}
	seen := make(map[string]struct{})
	for decoder.More() {
		token, err := decoder.Token()
		key, ok := token.(string)
		if err != nil || !ok {
			return errInvalidJSON
		}
		if _, duplicate := seen[key]; duplicate {
			return errInvalidJSON
		}
		seen[key] = struct{}{}
		var value json.RawMessage
		if err := decoder.Decode(&value); err != nil {
			return errInvalidJSON
		}
	}
	if token, err = decoder.Token(); err != nil || token != json.Delim('}') {
		return errInvalidJSON
	}
	return ensureJSONEOF(decoder)
}

func ensureJSONEOF(decoder *json.Decoder) error {
	var extra any
	if err := decoder.Decode(&extra); !errors.Is(err, io.EOF) {
		return errInvalidJSON
	}
	return nil
}

type httpStatusError struct{ status int }

func (err *httpStatusError) Error() string { return http.StatusText(err.status) }

func writeJSONDecodeError(response http.ResponseWriter, err error) {
	var statusError *httpStatusError
	switch {
	case errors.As(err, &statusError):
		writeError(response, statusError.status, "invalid_request")
	case errors.Is(err, errBodyTooLarge):
		writeError(response, http.StatusRequestEntityTooLarge, "invalid_request")
	default:
		writeError(response, http.StatusBadRequest, "invalid_request")
	}
}

func writeError(response http.ResponseWriter, status int, code string) {
	writeJSON(response, status, map[string]any{"code": code, "message": http.StatusText(status)})
}

func writeJSON(response http.ResponseWriter, status int, body any) {
	response.Header().Set("Content-Type", "application/json; charset=utf-8")
	response.WriteHeader(status)
	_ = json.NewEncoder(response).Encode(body)
}

func setAdminCookie(response http.ResponseWriter, name, value string, expiresAt time.Time, maxAge time.Duration) {
	http.SetCookie(response, &http.Cookie{Name: name, Value: value, Path: "/admin/", Secure: true, HttpOnly: true, SameSite: http.SameSiteStrictMode, Expires: expiresAt, MaxAge: int(maxAge / time.Second)})
}

func clearAdminCookie(response http.ResponseWriter, name string) {
	http.SetCookie(response, &http.Cookie{Name: name, Value: "", Path: "/admin/", Secure: true, HttpOnly: true, SameSite: http.SameSiteStrictMode, Expires: time.Unix(1, 0).UTC(), MaxAge: -1})
}

func uniqueCookie(request *http.Request, name string) (*http.Cookie, bool) {
	var found *http.Cookie
	for _, cookie := range request.Cookies() {
		if cookie.Name != name {
			continue
		}
		if found != nil || cookie.Value == "" {
			return nil, false
		}
		copy := *cookie
		found = &copy
	}
	return found, found != nil
}

func uniqueHeader(request *http.Request, name string) (string, bool) {
	values := request.Header.Values(name)
	if len(values) != 1 {
		return "", false
	}
	value := strings.TrimSpace(values[0])
	return value, value != "" && !strings.Contains(value, ",")
}

func cookieValue(cookie *http.Cookie, ok bool) string {
	if !ok {
		return ""
	}
	return cookie.Value
}

func headerValue(value string, ok bool) string {
	if !ok {
		return ""
	}
	return value
}

func requestBodyIsEmpty(request *http.Request) bool {
	if request.Body == nil || request.Body == http.NoBody {
		return true
	}
	var probe [1]byte
	read, err := request.Body.Read(probe[:])
	return read == 0 && errors.Is(err, io.EOF)
}

func stringInt(value int64) string {
	if value <= 0 {
		return "1"
	}
	const digits = "0123456789"
	var buffer [20]byte
	index := len(buffer)
	for value > 0 {
		index--
		buffer[index] = digits[value%10]
		value /= 10
	}
	return string(buffer[index:])
}

func retryAfterSeconds(duration time.Duration) string {
	seconds := int64(duration / time.Second)
	if duration%time.Second != 0 {
		seconds++
	}
	return json.Number(stringInt(seconds)).String()
}
