package webui

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestEmbeddedAdminPageHasCompleteAccessibleChineseWorkflow(t *testing.T) {
	handler := Handler()
	index := requestAsset(t, handler, "/admin/")
	for _, required := range []string{
		`lang="zh-CN"`, `id="login-view"`, `id="password-view"`, `id="console-view"`,
		`当前版本`, `选择发布包`, `accept=".zip,application/zip"`, `更新说明`, `上传进度`,
		`校验清单`, `确认发布`, `版本历史`, `操作者`, `回滚到此版本`, `当前管理员密码`,
		`id="loading-state"`, `id="empty-state"`, `id="error-state"`, `id="readonly-state"`,
		`aria-live="polite"`, `aria-describedby=`, `<progress`, `<dialog`, `<label`,
		`class="visually-hidden-file"`, `至少 8 位`,
	} {
		if !strings.Contains(index, required) {
			t.Errorf("index missing %q", required)
		}
	}
	for _, forbidden := range []string{"webkitdirectory", `type="text" name="path"`, `至少 16 位`, `minlength="16"`, "http://", "https://", "document.write", "eval("} {
		if strings.Contains(index, forbidden) {
			t.Errorf("index contains forbidden capability %q", forbidden)
		}
	}

	styles := requestAsset(t, handler, "/admin/styles.css")
	for _, required := range []string{"@media", ":focus-visible", "overflow-wrap", "max-width", "prefers-reduced-motion", "td:nth-child(-n+3)", "white-space: nowrap"} {
		if !strings.Contains(styles, required) {
			t.Errorf("styles missing %q", required)
		}
	}

	script := requestAsset(t, handler, "/admin/app.js")
	for _, required := range []string{"/admin/api/session/bootstrap", "/admin/api/session/login", "/admin/api/uploads", "/admin/api/candidates/current", "/admin/api/releases", "historyResult.value.read_only", "current.csrf_token", "suppressSessionExpiry", "candidatePublishBlocked", "dashboardEpoch", "restoreAuthoritativeCandidate", "renderGateEvidence", "allGatesPassed", "candidate.state === \"published\"", "Array.from(value).length", "XMLHttpRequest", "crypto.randomUUID", "showModal"} {
		if !strings.Contains(script, required) {
			t.Errorf("script missing %q", required)
		}
	}
	for _, forbidden := range []string{"http://", "https://", "eval(", "new Function", "innerHTML"} {
		if strings.Contains(script, forbidden) {
			t.Errorf("script contains forbidden construct %q", forbidden)
		}
	}
}

func TestEmbeddedAssetsServeOnlyExactAdminFiles(t *testing.T) {
	handler := Handler()
	for path, contentType := range map[string]string{
		"/admin/":           "text/html; charset=utf-8",
		"/admin/index.html": "text/html; charset=utf-8",
		"/admin/styles.css": "text/css; charset=utf-8",
		"/admin/app.js":     "text/javascript; charset=utf-8",
	} {
		request := httptest.NewRequest(http.MethodGet, path, nil)
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		if response.Code != http.StatusOK || response.Header().Get("Content-Type") != contentType || response.Body.Len() == 0 {
			t.Fatalf("%s status=%d type=%q bytes=%d", path, response.Code, response.Header().Get("Content-Type"), response.Body.Len())
		}
	}
	for _, path := range []string{"/admin", "/admin/../config.json", "/admin/static/index.html", "/admin/favicon.ico", "/"} {
		request := httptest.NewRequest(http.MethodGet, path, nil)
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		if response.Code != http.StatusNotFound {
			t.Fatalf("unsafe/unknown %s status=%d", path, response.Code)
		}
	}
}

func requestAsset(t *testing.T, handler http.Handler, path string) string {
	t.Helper()
	request := httptest.NewRequest(http.MethodGet, path, nil)
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("GET %s status=%d body=%s", path, response.Code, response.Body.String())
	}
	value, err := io.ReadAll(response.Result().Body)
	if err != nil {
		t.Fatal(err)
	}
	return string(value)
}
