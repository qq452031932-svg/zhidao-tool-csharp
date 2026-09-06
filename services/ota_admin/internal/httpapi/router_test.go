package httpapi_test

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/httpapi"
	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

const (
	publicOrigin = "https://ota.fanfh.cn"
	oldPassword  = "temporary password with enough entropy"
	newPassword  = "replacement password with enough entropy"
)

type testClock struct {
	mu  sync.Mutex
	now time.Time
}

func (clock *testClock) Now() time.Time {
	clock.mu.Lock()
	defer clock.mu.Unlock()
	return clock.now
}

func (clock *testClock) Advance(delta time.Duration) {
	clock.mu.Lock()
	clock.now = clock.now.Add(delta)
	clock.mu.Unlock()
}

type harness struct {
	t            *testing.T
	databasePath string
	store        *store.Store
	clock        *testClock
	sessions     *session.Service
	handler      http.Handler
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	databasePath := filepath.Join(privateStateTestDir(t), "state.db")
	database, err := store.Open(context.Background(), databasePath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	hash, err := security.HashPassword(oldPassword, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.InitializeAdmin(context.Background(), hash); err != nil {
		t.Fatal(err)
	}
	clock := &testClock{now: time.Unix(2_000_000_000, 0).UTC()}
	h := &harness{t: t, databasePath: databasePath, store: database, clock: clock}
	h.restart()
	return h
}

func privateStateTestDir(t *testing.T) string {
	t.Helper()
	root := t.TempDir()
	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatalf("make state test directory private: %v", err)
	}
	return root
}

func (h *harness) restart() {
	h.t.Helper()
	service, err := session.NewService(h.store, session.Options{
		Now:            h.clock.Now,
		Random:         rand.Reader,
		SourceHMACKey:  bytes.Repeat([]byte{0x42}, 32),
		PreauthTTL:     5 * time.Minute,
		IdleTimeout:    30 * time.Minute,
		AbsoluteExpiry: 8 * time.Hour,
		FailureLimit:   5,
		FailureWindow:  15 * time.Minute,
		FailureLock:    15 * time.Minute,
	})
	if err != nil {
		h.t.Fatal(err)
	}
	h.sessions = service
	h.handler, err = httpapi.NewRouter(httpapi.Options{
		Sessions:     service,
		PublicOrigin: publicOrigin,
		MaxJSONBytes: 4 << 10,
	})
	if err != nil {
		h.t.Fatal(err)
	}
}

func (h *harness) request(method, path, body, csrf string, cookies ...*http.Cookie) *httptest.ResponseRecorder {
	h.t.Helper()
	request := httptest.NewRequest(method, "http://backend"+path, strings.NewReader(body))
	request.RemoteAddr = "127.0.0.1:43210"
	if method != http.MethodGet {
		request.Header.Set("Content-Type", "application/json")
		request.Header.Set("Origin", publicOrigin)
		request.Header.Set("Referer", publicOrigin+"/admin/")
	}
	if csrf != "" {
		request.Header.Set("X-CSRF-Token", csrf)
	}
	for _, cookie := range cookies {
		request.AddCookie(cookie)
	}
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	return response
}

func decodeBody(t *testing.T, response *httptest.ResponseRecorder) map[string]any {
	t.Helper()
	var result map[string]any
	if err := json.Unmarshal(response.Body.Bytes(), &result); err != nil {
		t.Fatalf("decode %q: %v", response.Body.String(), err)
	}
	return result
}

func cookieNamed(t *testing.T, response *httptest.ResponseRecorder, name string) *http.Cookie {
	t.Helper()
	for _, cookie := range response.Result().Cookies() {
		if cookie.Name == name {
			return cookie
		}
	}
	t.Fatalf("cookie %q missing from %v", name, response.Header().Values("Set-Cookie"))
	return nil
}

func bootstrap(t *testing.T, h *harness) (*http.Cookie, string) {
	t.Helper()
	response := h.request(http.MethodGet, "/admin/api/session/bootstrap", "", "")
	if response.Code != http.StatusOK {
		t.Fatalf("bootstrap status=%d body=%s", response.Code, response.Body.String())
	}
	cookie := cookieNamed(t, response, "ota_admin_preauth")
	csrf, _ := decodeBody(t, response)["csrf_token"].(string)
	if csrf == "" {
		t.Fatal("bootstrap csrf token is empty")
	}
	return cookie, csrf
}

func login(t *testing.T, h *harness, password string) (*http.Cookie, string, bool, *httptest.ResponseRecorder) {
	t.Helper()
	preauth, csrf := bootstrap(t, h)
	body, _ := json.Marshal(map[string]string{"username": "admin", "password": password})
	response := h.request(http.MethodPost, "/admin/api/session/login", string(body), csrf, preauth)
	if response.Code != http.StatusOK {
		return nil, "", false, response
	}
	result := decodeBody(t, response)
	sessionCookie := cookieNamed(t, response, "ota_admin_session")
	formalCSRF, _ := result["csrf_token"].(string)
	mustChange, _ := result["must_change_password"].(bool)
	return sessionCookie, formalCSRF, mustChange, response
}

func assertSecureAdminCookie(t *testing.T, cookie *http.Cookie) {
	t.Helper()
	if !cookie.Secure || !cookie.HttpOnly || cookie.SameSite != http.SameSiteStrictMode || cookie.Path != "/admin/" {
		t.Fatalf("insecure cookie: %#v", cookie)
	}
}

func TestBootstrapIssuesBoundOneUsePreauthAndHardenedCookie(t *testing.T) {
	h := newHarness(t)
	preauth, csrf := bootstrap(t, h)
	assertSecureAdminCookie(t, preauth)
	if preauth.MaxAge != 300 {
		t.Fatalf("preauth MaxAge=%d, want 300", preauth.MaxAge)
	}

	body := `{"username":"admin","password":"wrong password"}`
	first := h.request(http.MethodPost, "/admin/api/session/login", body, csrf+"x", preauth)
	second := h.request(http.MethodPost, "/admin/api/session/login", body, csrf, preauth)
	if first.Code != http.StatusUnauthorized || second.Code != http.StatusUnauthorized {
		t.Fatalf("preauth responses=%d,%d", first.Code, second.Code)
	}
	if decodeBody(t, first)["code"] != decodeBody(t, second)["code"] {
		t.Fatal("authentication failures enumerate their cause")
	}
	cleared := cookieNamed(t, first, "ota_admin_preauth")
	if cleared.MaxAge >= 0 || !cleared.Secure || !cleared.HttpOnly || cleared.Path != "/admin/" || cleared.SameSite != http.SameSiteStrictMode {
		t.Fatalf("preauth was not securely cleared: %#v", cleared)
	}
}

func TestLoginRequiresStrictSameOriginAndConsumesPreauthOnEveryFailure(t *testing.T) {
	h := newHarness(t)
	for _, mutate := range []func(*http.Request){
		func(r *http.Request) { r.Header.Del("Origin"); r.Header.Del("Referer") },
		func(r *http.Request) { r.Header.Set("Origin", "https://evil.example") },
		func(r *http.Request) { r.Header.Set("Referer", "https://evil.example/admin/") },
	} {
		preauth, csrf := bootstrap(t, h)
		request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/session/login", strings.NewReader(`{"username":"admin","password":"`+oldPassword+`"}`))
		request.RemoteAddr = "127.0.0.1:43210"
		request.Header.Set("Content-Type", "application/json")
		request.Header.Set("Origin", publicOrigin)
		request.Header.Set("Referer", publicOrigin+"/admin/")
		request.Header.Set("X-CSRF-Token", csrf)
		request.AddCookie(preauth)
		mutate(request)
		response := httptest.NewRecorder()
		h.handler.ServeHTTP(response, request)
		if response.Code != http.StatusForbidden {
			t.Fatalf("same-origin status=%d body=%s", response.Code, response.Body.String())
		}
		replay := h.request(http.MethodPost, "/admin/api/session/login", `{"username":"admin","password":"`+oldPassword+`"}`, csrf, preauth)
		if replay.Code != http.StatusUnauthorized {
			t.Fatalf("preauth replay status=%d", replay.Code)
		}
	}
}

func TestSuccessfulLoginRotatesToFormalSessionAndOnlyTwoRoutesAreAnonymous(t *testing.T) {
	h := newHarness(t)
	cookie, csrf, mustChange, response := login(t, h, oldPassword)
	if response.Code != http.StatusOK || !mustChange || csrf == "" {
		t.Fatalf("login status=%d body=%s", response.Code, response.Body.String())
	}
	assertSecureAdminCookie(t, cookie)
	if cookie.MaxAge != 8*60*60 {
		t.Fatalf("session MaxAge=%d, want %d", cookie.MaxAge, 8*60*60)
	}
	if cookie.Name != "ota_admin_session" {
		t.Fatalf("cookie name=%q", cookie.Name)
	}

	for _, endpoint := range []string{"/admin/api/session/current", "/admin/api/session/logout", "/admin/api/session/password", "/admin/api/future-mutation"} {
		method := http.MethodGet
		body := ""
		if endpoint != "/admin/api/session/current" {
			method, body = http.MethodPost, `{}`
		}
		response := h.request(method, endpoint, body, "")
		if response.Code != http.StatusUnauthorized {
			t.Fatalf("anonymous %s status=%d", endpoint, response.Code)
		}
	}

	current := h.request(http.MethodGet, "/admin/api/session/current", "", "", cookie)
	if current.Code != http.StatusOK || decodeBody(t, current)["must_change_password"] != true {
		t.Fatalf("current status=%d body=%s", current.Code, current.Body.String())
	}
}

func TestAnonymousBootstrapIsBoundedPerSource(t *testing.T) {
	h := newHarness(t)
	for attempt := 1; attempt <= 8; attempt++ {
		response := h.request(http.MethodGet, "/admin/api/session/bootstrap", "", "")
		if response.Code != http.StatusOK {
			t.Fatalf("bootstrap %d status=%d body=%s", attempt, response.Code, response.Body.String())
		}
	}
	response := h.request(http.MethodGet, "/admin/api/session/bootstrap", "", "")
	if response.Code != http.StatusTooManyRequests || response.Header().Get("Retry-After") == "" {
		t.Fatalf("bootstrap overflow status=%d headers=%v body=%s", response.Code, response.Header(), response.Body.String())
	}
}

func TestBootstrapRejectsChunkedOrUnknownLengthBodies(t *testing.T) {
	h := newHarness(t)
	request := httptest.NewRequest(http.MethodGet, "http://backend/admin/api/session/bootstrap", strings.NewReader("x"))
	request.RemoteAddr = "127.0.0.1:43210"
	request.ContentLength = -1
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest {
		t.Fatalf("chunked bootstrap body status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestForcedPasswordChangeBlocksPrivilegeAndRotationRevokesEveryOldSession(t *testing.T) {
	h := newHarness(t)
	firstCookie, firstCSRF, _, _ := login(t, h, oldPassword)
	secondCookie, _, _, _ := login(t, h, oldPassword)

	blocked := h.request(http.MethodPost, "/admin/api/future-mutation", `{}`, firstCSRF, firstCookie)
	if blocked.Code != http.StatusForbidden || decodeBody(t, blocked)["code"] != "password_change_required" {
		t.Fatalf("forced-change status=%d body=%s", blocked.Code, blocked.Body.String())
	}

	changeBody := `{"current_password":"` + oldPassword + `","new_password":"` + newPassword + `"}`
	changed := h.request(http.MethodPost, "/admin/api/session/password", changeBody, firstCSRF, firstCookie)
	if changed.Code != http.StatusOK {
		t.Fatalf("password change status=%d body=%s", changed.Code, changed.Body.String())
	}
	rotated := cookieNamed(t, changed, "ota_admin_session")
	assertSecureAdminCookie(t, rotated)
	rotatedCSRF, _ := decodeBody(t, changed)["csrf_token"].(string)
	if rotated.Value == firstCookie.Value || rotatedCSRF == firstCSRF {
		t.Fatal("password change did not rotate credentials")
	}
	for _, old := range []*http.Cookie{firstCookie, secondCookie} {
		if response := h.request(http.MethodGet, "/admin/api/session/current", "", "", old); response.Code != http.StatusUnauthorized {
			t.Fatalf("old session remained valid: %d", response.Code)
		}
	}
	if response := h.request(http.MethodPost, "/admin/api/future-mutation", `{}`, rotatedCSRF, rotated); response.Code != http.StatusNotFound {
		t.Fatalf("rotated session privilege status=%d body=%s", response.Code, response.Body.String())
	}
	if _, _, _, response := login(t, h, oldPassword); response.Code != http.StatusUnauthorized {
		t.Fatalf("old password login status=%d", response.Code)
	}
	if _, _, mustChange, response := login(t, h, newPassword); response.Code != http.StatusOK || mustChange {
		t.Fatalf("new password login status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestCurrentSessionRotatesCSRFSoARefreshedPageCanMutate(t *testing.T) {
	h := newHarness(t)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	cookie, csrf = changeTestPassword(t, h, cookie, csrf)
	current := h.request(http.MethodGet, "/admin/api/session/current", "", "", cookie)
	body := decodeBody(t, current)
	refreshed, _ := body["csrf_token"].(string)
	if current.Code != http.StatusOK || refreshed == "" || refreshed == csrf {
		t.Fatalf("status=%d csrf=%q old=%q body=%s", current.Code, refreshed, csrf, current.Body.String())
	}
	if response := h.request(http.MethodPost, "/admin/api/future-mutation", `{}`, csrf, cookie); response.Code != http.StatusForbidden {
		t.Fatalf("old csrf status=%d body=%s", response.Code, response.Body.String())
	}
	if response := h.request(http.MethodPost, "/admin/api/future-mutation", `{}`, refreshed, cookie); response.Code != http.StatusNotFound {
		t.Fatalf("refreshed csrf status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestMutationsRequireFormalCSRFAndSameOrigin(t *testing.T) {
	h := newHarness(t)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	for name, configure := range map[string]func(*http.Request){
		"missing csrf": func(r *http.Request) { r.Header.Del("X-CSRF-Token") },
		"wrong csrf":   func(r *http.Request) { r.Header.Set("X-CSRF-Token", csrf+"x") },
		"bad origin":   func(r *http.Request) { r.Header.Set("Origin", "https://evil.example") },
	} {
		t.Run(name, func(t *testing.T) {
			request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/session/logout", strings.NewReader(`{}`))
			request.RemoteAddr = "127.0.0.1:1"
			request.Header.Set("Content-Type", "application/json")
			request.Header.Set("Origin", publicOrigin)
			request.Header.Set("Referer", publicOrigin+"/admin/")
			request.Header.Set("X-CSRF-Token", csrf)
			request.AddCookie(cookie)
			configure(request)
			response := httptest.NewRecorder()
			h.handler.ServeHTTP(response, request)
			if response.Code != http.StatusForbidden {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
		})
	}
}

func TestLogoutRevocationExpiryAndRestartInvalidation(t *testing.T) {
	h := newHarness(t)
	cookie, csrf, _, _ := login(t, h, oldPassword)
	logout := h.request(http.MethodPost, "/admin/api/session/logout", `{}`, csrf, cookie)
	if logout.Code != http.StatusNoContent {
		t.Fatalf("logout status=%d body=%s", logout.Code, logout.Body.String())
	}
	cleared := cookieNamed(t, logout, "ota_admin_session")
	if cleared.MaxAge >= 0 || cleared.Path != "/admin/" || !cleared.Secure || !cleared.HttpOnly || cleared.SameSite != http.SameSiteStrictMode {
		t.Fatalf("session cookie not securely cleared: %#v", cleared)
	}
	if response := h.request(http.MethodGet, "/admin/api/session/current", "", "", cookie); response.Code != http.StatusUnauthorized {
		t.Fatalf("logged-out session status=%d", response.Code)
	}

	idleCookie, _, _, _ := login(t, h, oldPassword)
	h.clock.Advance(30*time.Minute + time.Nanosecond)
	if response := h.request(http.MethodGet, "/admin/api/session/current", "", "", idleCookie); response.Code != http.StatusUnauthorized {
		t.Fatalf("idle-expired status=%d", response.Code)
	}

	absoluteCookie, _, _, _ := login(t, h, oldPassword)
	for range 15 {
		h.clock.Advance(29 * time.Minute)
		if response := h.request(http.MethodGet, "/admin/api/session/current", "", "", absoluteCookie); response.Code != http.StatusOK {
			t.Fatalf("session expired before absolute limit: %d", response.Code)
		}
	}
	h.clock.Advance(46 * time.Minute)
	if response := h.request(http.MethodGet, "/admin/api/session/current", "", "", absoluteCookie); response.Code != http.StatusUnauthorized {
		t.Fatalf("absolute-expired status=%d", response.Code)
	}

	restartCookie, _, _, _ := login(t, h, oldPassword)
	h.restart()
	if response := h.request(http.MethodGet, "/admin/api/session/current", "", "", restartCookie); response.Code != http.StatusUnauthorized {
		t.Fatalf("restart-preserved session status=%d", response.Code)
	}
}

func TestLogoutStoreFailureIsNormalizedAsInternal(t *testing.T) {
	h := newHarness(t)
	cookie, csrf, _, response := login(t, h, oldPassword)
	if response.Code != http.StatusOK {
		t.Fatal(response.Body.String())
	}
	observer, err := sql.Open("sqlite", h.databasePath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = observer.Close() })
	if _, err := observer.Exec(`CREATE TRIGGER fail_session_revoke
		BEFORE UPDATE OF state ON sessions
		WHEN NEW.state='revoked'
		BEGIN SELECT RAISE(FAIL, 'forced storage failure'); END`); err != nil {
		t.Fatal(err)
	}

	response = h.request(http.MethodPost, "/admin/api/session/logout", `{}`, csrf, cookie)
	if response.Code != http.StatusInternalServerError || decodeBody(t, response)["code"] != "operation_failed" {
		t.Fatalf("logout store failure status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestFiveFailuresLockAccountOrSourceForFifteenMinutes(t *testing.T) {
	h := newHarness(t)
	for attempt := 1; attempt <= 5; attempt++ {
		_, _, _, response := login(t, h, "incorrect password")
		if response.Code != http.StatusUnauthorized {
			t.Fatalf("failure %d status=%d body=%s", attempt, response.Code, response.Body.String())
		}
	}
	locked := h.request(http.MethodGet, "/admin/api/session/bootstrap", "", "")
	if locked.Code != http.StatusTooManyRequests || locked.Header().Get("Retry-After") == "" {
		t.Fatalf("locked status=%d headers=%v body=%s", locked.Code, locked.Header(), locked.Body.String())
	}
	h.clock.Advance(15*time.Minute + time.Nanosecond)
	_, _, _, recovered := login(t, h, oldPassword)
	if recovered.Code != http.StatusOK {
		t.Fatalf("post-lock login status=%d body=%s", recovered.Code, recovered.Body.String())
	}
}

func TestBootstrapRetryAfterReflectsTheActiveLimiter(t *testing.T) {
	t.Run("source bootstrap throttle", func(t *testing.T) {
		h := newHarness(t)
		for attempt := 1; attempt <= 8; attempt++ {
			response := h.request(http.MethodGet, "/admin/api/session/bootstrap", "", "")
			if response.Code != http.StatusOK {
				t.Fatalf("bootstrap %d status=%d body=%s", attempt, response.Code, response.Body.String())
			}
		}
		h.clock.Advance(45 * time.Second)
		response := h.request(http.MethodGet, "/admin/api/session/bootstrap", "", "")
		if response.Code != http.StatusTooManyRequests || response.Header().Get("Retry-After") != "255" {
			t.Fatalf("source throttle status=%d retry-after=%q body=%s", response.Code, response.Header().Get("Retry-After"), response.Body.String())
		}
	})

	t.Run("credential lock", func(t *testing.T) {
		h := newHarness(t)
		for attempt := 1; attempt <= 5; attempt++ {
			_, _, _, response := login(t, h, "incorrect password")
			if response.Code != http.StatusUnauthorized {
				t.Fatalf("failure %d status=%d body=%s", attempt, response.Code, response.Body.String())
			}
		}
		h.clock.Advance(45 * time.Second)
		response := h.request(http.MethodGet, "/admin/api/session/bootstrap", "", "")
		if response.Code != http.StatusTooManyRequests || response.Header().Get("Retry-After") != "855" {
			t.Fatalf("credential lock status=%d retry-after=%q body=%s", response.Code, response.Header().Get("Retry-After"), response.Body.String())
		}
	})
}

func TestStrictJSONContentTypeBodyLimitAndExactSchemas(t *testing.T) {
	h := newHarness(t)
	tests := []struct {
		name        string
		contentType string
		body        string
		want        int
	}{
		{"wrong content type", "text/plain", `{}`, http.StatusUnsupportedMediaType},
		{"unknown property", "application/json", `{"username":"admin","password":"x","extra":true}`, http.StatusBadRequest},
		{"duplicate property", "application/json", `{"username":"admin","username":"admin","password":"x"}`, http.StatusBadRequest},
		{"trailing json", "application/json", `{"username":"admin","password":"x"}{}`, http.StatusBadRequest},
		{"oversized", "application/json", `{"username":"admin","password":"` + strings.Repeat("x", 5000) + `"}`, http.StatusRequestEntityTooLarge},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			preauth, csrf := bootstrap(t, h)
			request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/session/login", strings.NewReader(test.body))
			request.RemoteAddr = "127.0.0.1:2"
			request.Header.Set("Content-Type", test.contentType)
			request.Header.Set("Origin", publicOrigin)
			request.Header.Set("Referer", publicOrigin+"/admin/")
			request.Header.Set("X-CSRF-Token", csrf)
			request.AddCookie(preauth)
			response := httptest.NewRecorder()
			h.handler.ServeHTTP(response, request)
			if response.Code != test.want {
				t.Fatalf("status=%d want=%d body=%s", response.Code, test.want, response.Body.String())
			}
		})
	}
}

func TestFixedAdministratorNameIsExactAndDuplicateContentTypeIsRejected(t *testing.T) {
	h := newHarness(t)
	preauth, csrf := bootstrap(t, h)
	uppercase := h.request(http.MethodPost, "/admin/api/session/login", `{"username":"ADMIN","password":"`+oldPassword+`"}`, csrf, preauth)
	if uppercase.Code != http.StatusUnauthorized {
		t.Fatalf("uppercase administrator status=%d body=%s", uppercase.Code, uppercase.Body.String())
	}

	preauth, csrf = bootstrap(t, h)
	request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/session/login", strings.NewReader(`{"username":"admin","password":"`+oldPassword+`"}`))
	request.RemoteAddr = "127.0.0.1:3"
	request.Header.Add("Content-Type", "application/json")
	request.Header.Add("Content-Type", "text/plain")
	request.Header.Set("Origin", publicOrigin)
	request.Header.Set("Referer", publicOrigin+"/admin/")
	request.Header.Set("X-CSRF-Token", csrf)
	request.AddCookie(preauth)
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusUnsupportedMediaType {
		t.Fatalf("duplicate content type status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestDuplicateOriginHeadersCannotSelectATrustedFirstValue(t *testing.T) {
	h := newHarness(t)
	preauth, csrf := bootstrap(t, h)
	request := httptest.NewRequest(http.MethodPost, "http://backend/admin/api/session/login", strings.NewReader(`{"username":"admin","password":"`+oldPassword+`"}`))
	request.RemoteAddr = "127.0.0.1:4"
	request.Header.Set("Content-Type", "application/json")
	request.Header.Add("Origin", publicOrigin)
	request.Header.Add("Origin", "https://evil.example")
	request.Header.Set("Referer", publicOrigin+"/admin/")
	request.Header.Set("X-CSRF-Token", csrf)
	request.AddCookie(preauth)
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusForbidden {
		t.Fatalf("duplicate origin status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestEveryAdminResponseIsNoCacheAndHardened(t *testing.T) {
	h := newHarness(t)
	for _, path := range []string{"/admin/api/session/bootstrap", "/admin/api/unknown", "/admin/unknown"} {
		response := h.request(http.MethodGet, path, "", "")
		for header, want := range map[string]string{
			"Cache-Control":             "no-store, max-age=0",
			"Pragma":                    "no-cache",
			"Strict-Transport-Security": "max-age=31536000; includeSubDomains",
			"X-Content-Type-Options":    "nosniff",
			"X-Frame-Options":           "DENY",
			"Referrer-Policy":           "no-referrer",
		} {
			if got := response.Header().Get(header); got != want {
				t.Fatalf("%s %s=%q want=%q", path, header, got, want)
			}
		}
		if !strings.Contains(response.Header().Get("Content-Security-Policy"), "frame-ancestors 'none'") {
			t.Fatalf("%s CSP=%q", path, response.Header().Get("Content-Security-Policy"))
		}
	}
}

func TestRouterRejectsUnsupportedMethodsFromAnExplicitMethodSet(t *testing.T) {
	h := newHarness(t)
	for _, method := range []string{http.MethodHead, http.MethodPut, http.MethodPatch, http.MethodDelete, http.MethodOptions, http.MethodTrace, http.MethodConnect} {
		request := httptest.NewRequest(method, "http://backend/admin/api/session/current", nil)
		response := httptest.NewRecorder()
		h.handler.ServeHTTP(response, request)
		if response.Code != http.StatusMethodNotAllowed {
			t.Fatalf("method %s status=%d body=%s", method, response.Code, response.Body.String())
		}
	}
}

func TestSessionEndpointsRejectAValidGlobalMethodWhenRouteDoesNotAllowIt(t *testing.T) {
	h := newHarness(t)
	cookie, csrf, _, response := login(t, h, oldPassword)
	if response.Code != http.StatusOK {
		t.Fatal(response.Body.String())
	}
	for _, test := range []struct {
		method string
		path   string
		body   string
	}{
		{http.MethodPost, "/admin/api/session/current", `{}`},
		{http.MethodGet, "/admin/api/session/logout", ""},
		{http.MethodGet, "/admin/api/session/password", ""},
	} {
		response := h.request(test.method, test.path, test.body, csrf, cookie)
		if response.Code != http.StatusMethodNotAllowed {
			t.Fatalf("%s %s status=%d body=%s", test.method, test.path, response.Code, response.Body.String())
		}
	}
}

func TestSessionStoreFailureIsNormalizedAsInternalNotInvalidCredentials(t *testing.T) {
	h := newHarness(t)
	cookie, _, _, response := login(t, h, oldPassword)
	if response.Code != http.StatusOK {
		t.Fatal(response.Body.String())
	}
	if err := h.store.Close(); err != nil {
		t.Fatal(err)
	}
	response = h.request(http.MethodGet, "/admin/api/session/current", "", "", cookie)
	if response.Code != http.StatusInternalServerError || decodeBody(t, response)["code"] != "operation_failed" {
		t.Fatalf("store failure status=%d body=%s", response.Code, response.Body.String())
	}
}
