package webui

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestAdminConsoleInRealBrowser(t *testing.T) {
	chrome := installedTestBrowser()
	if chrome == "" {
		t.Skip("Chrome or Edge is not installed")
	}
	for _, mode := range []string{"happy", "failure"} {
		t.Run(mode, func(t *testing.T) {
			server := newBrowserTestServer(t, mode)
			defer server.Close()
			profile := t.TempDir()
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()
			command := exec.CommandContext(ctx, chrome,
				"--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
				"--user-data-dir="+profile, "--window-size=420,900", "--virtual-time-budget=10000", "--dump-dom", server.URL+"/admin/?mode="+mode)
			output, err := command.CombinedOutput()
			if ctx.Err() != nil {
				t.Fatalf("browser timed out: %v\n%s", ctx.Err(), output)
			}
			if err != nil {
				t.Fatalf("browser failed: %v\n%s", err, output)
			}
			document := string(output)
			if !strings.Contains(document, `id="test-result">PASS `+mode) {
				t.Fatalf("browser scenario did not pass:\n%s", tail(document, 4000))
			}
		})
	}
}

func installedTestBrowser() string {
	if runtime.GOOS != "windows" {
		return ""
	}
	for _, path := range []string{
		`C:\Program Files\Google\Chrome\Application\chrome.exe`,
		`C:\Program Files (x86)\Google\Chrome\Application\chrome.exe`,
		`C:\Program Files\Microsoft\Edge\Application\msedge.exe`,
		`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`,
	} {
		if info, err := os.Stat(path); err == nil && !info.IsDir() {
			return path
		}
	}
	return ""
}

func newBrowserTestServer(t *testing.T, mode string) *httptest.Server {
	t.Helper()
	page, err := staticFiles.ReadFile("static/index.html")
	if err != nil {
		t.Fatal(err)
	}
	modified := strings.Replace(string(page), `<script src="/admin/app.js" defer></script>`, `<script src="/test-bootstrap.js"></script><script src="/admin/app.js" defer></script><script type="module" src="/test-driver.js"></script>`, 1)
	var mu sync.Mutex
	loggedIn := mode == "failure"
	releaseReads := 0
	candidateReads := 0
	published := false
	refreshRelease := make(chan struct{})
	var refreshReleaseOnce sync.Once
	publishKeys, rollbackKeys := []string{}, []string{}
	protocolFailures := []string{}
	csrf := "csrf-current"
	recordMutation := func(request *http.Request, requireID bool) {
		if request.Header.Get("Origin") != "http://"+request.Host {
			protocolFailures = append(protocolFailures, "origin")
		}
		if request.Header.Get("X-CSRF-Token") != csrf {
			protocolFailures = append(protocolFailures, "csrf")
		}
		if cookie, err := request.Cookie("ota_browser_test"); err != nil || cookie.Value != "active" {
			protocolFailures = append(protocolFailures, "cookie")
		}
		if requireID && request.Header.Get("Idempotency-Key") == "" {
			protocolFailures = append(protocolFailures, "idempotency")
		}
	}
	web := Handler()
	handler := http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		switch request.URL.Path {
		case "/admin/":
			response.Header().Set("Content-Type", "text/html; charset=utf-8")
			_, _ = response.Write([]byte(modified))
		case "/test-bootstrap.js":
			response.Header().Set("Content-Type", "text/javascript; charset=utf-8")
			_, _ = response.Write([]byte(browserBootstrapScript))
		case "/test-driver.js":
			response.Header().Set("Content-Type", "text/javascript; charset=utf-8")
			_, _ = response.Write([]byte(browserDriverScript))
		case "/admin/api/session/current":
			mu.Lock()
			active := loggedIn
			currentCSRF := csrf
			mu.Unlock()
			if !active {
				writeBrowserJSON(response, http.StatusUnauthorized, map[string]any{"code": "authentication_failed"})
				return
			}
			http.SetCookie(response, &http.Cookie{Name: "ota_browser_test", Value: "active", Path: "/admin/", SameSite: http.SameSiteStrictMode})
			writeBrowserJSON(response, http.StatusOK, map[string]any{"authenticated": true, "must_change_password": false, "csrf_token": currentCSRF})
		case "/admin/api/session/bootstrap":
			writeBrowserJSON(response, http.StatusOK, map[string]any{"csrf_token": "preauth-browser-test"})
		case "/admin/api/session/login":
			mu.Lock()
			if request.Header.Get("Origin") != "http://"+request.Host || request.Header.Get("X-CSRF-Token") != "preauth-browser-test" {
				protocolFailures = append(protocolFailures, "login_security")
			}
			loggedIn = true
			csrf = "csrf-login"
			mu.Unlock()
			http.SetCookie(response, &http.Cookie{Name: "ota_browser_test", Value: "active", Path: "/admin/", SameSite: http.SameSiteStrictMode})
			writeBrowserJSON(response, http.StatusOK, map[string]any{"csrf_token": "csrf-login", "must_change_password": true})
		case "/admin/api/session/password":
			mu.Lock()
			recordMutation(request, false)
			csrf = "csrf-password"
			mu.Unlock()
			writeBrowserJSON(response, http.StatusOK, map[string]any{"csrf_token": "csrf-password"})
		case "/admin/api/session/logout":
			mu.Lock()
			recordMutation(request, false)
			mu.Unlock()
			if mode == "failure" {
				writeBrowserJSON(response, http.StatusInternalServerError, map[string]any{"code": "operation_failed"})
				return
			}
			response.WriteHeader(http.StatusNoContent)
		case "/admin/api/releases":
			mu.Lock()
			releaseReads++
			reads := releaseReads
			mu.Unlock()
			if mode == "failure" && reads >= 3 {
				writeBrowserJSON(response, http.StatusUnauthorized, map[string]any{"code": "authentication_failed"})
				return
			}
			writeBrowserJSON(response, http.StatusOK, map[string]any{"releases": browserReleases(), "read_only": mode == "failure" && reads == 2, "client_auto_downgrade": false})
		case "/admin/api/candidates/current":
			mu.Lock()
			candidateReads++
			reads := candidateReads
			isPublished := published
			mu.Unlock()
			if mode == "happy" && reads == 2 {
				select {
				case <-refreshRelease:
				case <-time.After(2 * time.Second):
				}
			}
			candidate := browserCandidate("21.0.0")
			if isPublished {
				candidate["state"] = "published"
			}
			writeBrowserJSON(response, http.StatusOK, candidate)
		case "/test/unblock":
			refreshReleaseOnce.Do(func() { close(refreshRelease) })
			response.WriteHeader(http.StatusNoContent)
		case "/test/assertions":
			mu.Lock()
			defer mu.Unlock()
			writeBrowserJSON(response, http.StatusOK, map[string]any{"publish_keys": publishKeys, "rollback_keys": rollbackKeys, "protocol_failures": protocolFailures})
		default:
			if strings.HasSuffix(request.URL.Path, "/publish") {
				mu.Lock()
				recordMutation(request, true)
				publishKeys = append(publishKeys, request.Header.Get("Idempotency-Key"))
				published = true
				mu.Unlock()
				time.Sleep(40 * time.Millisecond)
				writeBrowserJSON(response, http.StatusOK, map[string]any{"version": "21.1.0", "state": "succeeded"})
				return
			}
			if strings.HasSuffix(request.URL.Path, "/rollback") {
				mu.Lock()
				recordMutation(request, true)
				rollbackKeys = append(rollbackKeys, request.Header.Get("Idempotency-Key"))
				mu.Unlock()
				if mode == "failure" {
					writeBrowserJSON(response, http.StatusBadRequest, map[string]any{"code": "password_rejected", "operation_id": request.Header.Get("Idempotency-Key")})
					return
				}
				time.Sleep(40 * time.Millisecond)
				writeBrowserJSON(response, http.StatusOK, map[string]any{"version": "20.4.0", "state": "succeeded", "client_auto_downgrade": false})
				return
			}
			web.ServeHTTP(response, request)
		}
	})
	return httptest.NewServer(handler)
}

func writeBrowserJSON(response http.ResponseWriter, status int, body any) {
	response.Header().Set("Content-Type", "application/json")
	response.WriteHeader(status)
	_ = json.NewEncoder(response).Encode(body)
}

func browserCandidate(version string) map[string]any {
	gates := map[string]any{}
	for _, name := range []string{"package", "channel", "version", "signature", "files", "references", "storage"} {
		gates[name] = map[string]any{"passed": true, "evidence": "已验证 " + name}
	}
	return map[string]any{
		"candidate_id": "55555555-5555-4555-8555-555555555555", "operation_id": "44444444-4444-4444-8444-444444444444", "state": "validated",
		"expires_at": time.Now().Add(time.Hour).UTC(),
		"report":     map[string]any{"verified": true, "version": version, "file_count": 7, "notes_markdown": "签名更新说明", "gates": gates},
	}
}

func browserReleases() []map[string]any {
	now := time.Now().UTC()
	return []map[string]any{
		{"id": "11111111-1111-4111-8111-111111111111", "version": "20.5.0", "state": "current", "actor": "admin", "committed_at": now, "notes": "当前版本", "assets": []map[string]any{{"size_bytes": 1024}}},
		{"id": "22222222-2222-4222-8222-222222222222", "version": "20.4.0", "state": "retained", "actor": "admin", "committed_at": now.Add(-time.Hour), "notes": "上一版本", "assets": []map[string]any{{"size_bytes": 1024}}},
		{"id": "33333333-3333-4333-8333-333333333333", "version": "20.3.0", "state": "retained", "actor": "admin", "committed_at": now.Add(-2 * time.Hour), "notes": "更早版本", "assets": []map[string]any{{"size_bytes": 1024}}},
	}
}

func tail(value string, limit int) string {
	if len(value) <= limit {
		return value
	}
	return value[len(value)-limit:]
}

const browserBootstrapScript = `
window.__xhrSends = 0;
window.__mode = new URL(location.href).searchParams.get("mode");
window.__runtimeErrors = [];
window.addEventListener("error", event => window.__runtimeErrors.push(event.message || "window.error"));
window.addEventListener("unhandledrejection", event => window.__runtimeErrors.push(String(event.reason || "unhandledrejection")));
class TestUploadTarget {
  constructor() { this.listeners = {}; }
  addEventListener(name, listener) { this.listeners[name] = listener; }
  dispatch(name, event) { if (this.listeners[name]) this.listeners[name](event); }
}
class TestXMLHttpRequest {
  constructor() { this.upload = new TestUploadTarget(); this.listeners = {}; this.headers = {}; this.status = 0; this.responseText = ""; this.withCredentials = false; }
  open() {}
  setRequestHeader(name, value) { this.headers[name] = value; }
  addEventListener(name, listener) { this.listeners[name] = listener; }
  send() {
    window.__xhrSends += 1;
    window.__lastXHRHeaders = this.headers;
    window.__lastXHRWithCredentials = this.withCredentials;
    this.upload.dispatch("progress", { lengthComputable: true, loaded: 61, total: 100 });
    setTimeout(() => {
      if (window.__mode === "failure") {
        this.status = 422;
        this.responseText = JSON.stringify({ code: "release_validation_failed", operation_id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" });
      } else {
        this.status = 202;
        this.responseText = JSON.stringify(window.__candidate("21.1.0"));
      }
      this.listeners.load();
      if (window.__mode === "happy") window.__unblockPromise = fetch("/test/unblock", { method: "POST" });
    }, 25);
  }
}
window.XMLHttpRequest = TestXMLHttpRequest;
window.__candidate = (version) => ({
  candidate_id: "55555555-5555-4555-8555-555555555555", operation_id: "44444444-4444-4444-8444-444444444444", state: "validated",
  expires_at: new Date(Date.now() + 3600000).toISOString(), report: { verified: true, version, file_count: 7, notes_markdown: "签名更新说明", gates: Object.fromEntries(["package","channel","version","signature","files","references","storage"].map(name => [name, { passed: true, evidence: "已验证 " + name }])) }
});
`

const browserDriverScript = `
const waitFor = async (predicate, label) => {
  const until = Date.now() + 8000;
  while (Date.now() < until) { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 20)); }
  throw new Error("等待超时：" + label);
};
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const result = document.createElement("pre"); result.id = "test-result"; document.body.append(result);
try {
  await waitFor(() => !document.getElementById(window.__mode === "happy" ? "login-view" : "console-view").hidden, "初始视图");
  if (window.__mode === "happy") {
    assert(document.getElementById("login-error").hidden, "首次访问错误地显示登录失效");
    document.getElementById("login-password").value = "browser-test-password";
    document.getElementById("login-form").requestSubmit();
    await waitFor(() => !document.getElementById("password-view").hidden, "首次强制改密");
    assert(document.getElementById("login-password").value === "", "登录密码未及时清空");
    document.getElementById("new-password").value = "replacement-browser-password";
    document.getElementById("password-form").requestSubmit();
    await waitFor(() => !document.getElementById("console-view").hidden, "改密后控制台");
    assert(document.getElementById("current-password").value === "" && document.getElementById("new-password").value === "", "改密后密码字段未清空");
  }
  await waitFor(() => document.getElementById("loading-state").hidden, "初始化加载结束");
  await waitFor(() => document.querySelectorAll("#validation-list li.passed").length === 7, "逐项校验证据");
  const incomplete = window.__candidate("21.0.1");
  incomplete.report.gates.storage.passed = false;
  renderCandidate(incomplete);
  assert(document.getElementById("publish-button").disabled && document.getElementById("candidate-state").textContent !== "全部通过", "门禁不完整仍允许发布");
  renderCandidate(window.__candidate("21.0.0"));
  const fileInput = document.getElementById("release-file");
  assert(!fileInput.hidden && fileInput.tabIndex >= 0, "ZIP选择器不可通过键盘到达");
  fileInput.focus();
  assert(document.activeElement === fileInput, "ZIP选择器不能获得焦点");
  assert(document.documentElement.scrollWidth <= window.innerWidth + 1, "窄屏页面出现整体横向溢出");
  assert(getComputedStyle(document.querySelector("#history-body td")).whiteSpace === "nowrap", "版本号可被拆行");
  assert(document.querySelector(".table-scroll").scrollWidth > document.querySelector(".table-scroll").clientWidth, "历史表缺少横向滚动");
  assert([...document.querySelectorAll("th")].some(cell => cell.textContent === "操作者"), "历史缺少操作者列");

  const rollback = document.querySelector("#history-body button");
  rollback.click();
  await waitFor(() => document.getElementById("rollback-dialog").open, "回滚对话框");
  document.getElementById("rollback-password").value = "temporary secret";
  document.querySelector('[data-close-dialog="rollback-dialog"]').click();
  assert(document.getElementById("rollback-password").value === "", "取消后密码未清空");
  if (window.__mode === "failure") {
    rollback.click();
    document.getElementById("rollback-password").value = "wrong-browser-password";
    document.getElementById("rollback-confirm-check").checked = true;
    document.getElementById("rollback-form").dispatchEvent(new Event("submit", { cancelable: true }));
    await waitFor(() => !document.getElementById("rollback-error").hidden, "回滚失败提示");
    await waitFor(() => document.getElementById("rollback-password").value === "", "回滚失败清空密码");
    document.querySelector('[data-close-dialog="rollback-dialog"]').click();
  }

  if (window.__mode === "happy") document.getElementById("refresh-button").click();
  Object.defineProperty(fileInput, "files", { configurable: true, value: [new File(["zip"], "release.zip", { type: "application/zip" })] });
  fileInput.dispatchEvent(new Event("change"));
  assert(document.getElementById("publish-button").disabled, "选择新ZIP后旧候选仍可发布");
  document.getElementById("upload-button").click();
  document.getElementById("upload-button").click();
  assert(window.__xhrSends === 1, "忙碌期间重复上传未被阻止");
  await waitFor(() => document.getElementById("upload-progress").value >= 61, "上传进度");
  assert(window.__lastXHRWithCredentials === true, "上传未携带同源凭据");
  assert(window.__lastXHRHeaders["X-CSRF-Token"] && window.__lastXHRHeaders["Idempotency-Key"], "上传缺少CSRF或幂等键");

  if (window.__mode === "happy") {
    await waitFor(() => document.getElementById("candidate-version").textContent === "v21.1.0", "新候选");
    await window.__unblockPromise;
    await new Promise(resolve => setTimeout(resolve, 80));
    assert(document.getElementById("candidate-version").textContent === "v21.1.0", "迟到刷新覆盖了新候选");
    assert(!document.getElementById("publish-button").disabled, "校验通过后发布不可用");
    document.getElementById("publish-button").click();
    document.getElementById("publish-confirm-check").checked = true;
    document.getElementById("publish-form").dispatchEvent(new Event("submit", { cancelable: true }));
    document.getElementById("publish-form").dispatchEvent(new Event("submit", { cancelable: true }));
    await waitFor(() => !document.getElementById("publish-dialog").open, "发布完成");
    await waitFor(() => document.getElementById("candidate-version").textContent === "—", "已发布候选清空");
    const rollbackAfterPublish = document.querySelector("#history-body button");
    rollbackAfterPublish.click();
    document.getElementById("rollback-password").value = "browser-test-current-password";
    document.getElementById("rollback-confirm-check").checked = true;
    document.getElementById("rollback-form").dispatchEvent(new Event("submit", { cancelable: true }));
    document.getElementById("rollback-form").dispatchEvent(new Event("submit", { cancelable: true }));
    await waitFor(() => !document.getElementById("rollback-dialog").open, "回滚完成");
    await waitFor(() => document.getElementById("rollback-password").value === "", "回滚后密码清空");
  } else {
    await waitFor(() => document.getElementById("candidate-state").textContent.includes("不可发布"), "失败后权威旧候选");
    assert(document.getElementById("publish-button").disabled, "失败后旧候选被重新启用");
    document.getElementById("logout-button").click();
    await waitFor(() => !document.getElementById("error-state").hidden, "退出失败提示");
    assert(!document.getElementById("console-view").hidden, "退出失败却切换到登录页");
    document.getElementById("refresh-button").click();
    await waitFor(() => !document.getElementById("readonly-state").hidden, "只读状态");
    assert([...document.querySelectorAll("#history-body button")].every(button => button.disabled), "只读切换未禁用回滚");
    document.getElementById("refresh-button").click();
    await waitFor(() => !document.getElementById("login-view").hidden, "会话过期");
  }
  const protocol = await (await fetch("/test/assertions")).json();
  assert(protocol.protocol_failures.length === 0, "浏览器请求缺少安全头或Cookie：" + protocol.protocol_failures.join(","));
  if (window.__mode === "happy") {
    assert(protocol.publish_keys.length === 2 && protocol.publish_keys[0] === protocol.publish_keys[1], "重复发布未沿用幂等键");
    assert(protocol.rollback_keys.length === 2 && protocol.rollback_keys[0] === protocol.rollback_keys[1], "重复回滚未沿用幂等键");
  }
  assert(window.__runtimeErrors.length === 0, "页面存在未处理异常：" + window.__runtimeErrors.join(","));
  result.textContent = "PASS " + window.__mode;
} catch (error) {
  result.textContent = "FAIL " + window.__mode + ": " + error.message;
}
`
