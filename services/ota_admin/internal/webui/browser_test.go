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
			mu.Unlock()
			if !active {
				writeBrowserJSON(response, http.StatusUnauthorized, map[string]any{"code": "authentication_failed"})
				return
			}
			writeBrowserJSON(response, http.StatusOK, map[string]any{"authenticated": true, "must_change_password": false, "csrf_token": "csrf-browser-test"})
		case "/admin/api/session/bootstrap":
			writeBrowserJSON(response, http.StatusOK, map[string]any{"csrf_token": "preauth-browser-test"})
		case "/admin/api/session/login":
			mu.Lock()
			loggedIn = true
			mu.Unlock()
			writeBrowserJSON(response, http.StatusOK, map[string]any{"csrf_token": "csrf-browser-test", "must_change_password": false})
		case "/admin/api/session/logout":
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
			writeBrowserJSON(response, http.StatusOK, browserCandidate("21.0.0"))
		default:
			if strings.HasSuffix(request.URL.Path, "/publish") {
				writeBrowserJSON(response, http.StatusOK, map[string]any{"version": "21.1.0", "state": "succeeded"})
				return
			}
			if strings.HasSuffix(request.URL.Path, "/rollback") {
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
		{"id": "11111111-1111-4111-8111-111111111111", "version": "20.5.0", "state": "current", "committed_at": now, "notes": "当前版本", "assets": []map[string]any{{"size_bytes": 1024}}},
		{"id": "22222222-2222-4222-8222-222222222222", "version": "20.4.0", "state": "retained", "committed_at": now.Add(-time.Hour), "notes": "上一版本", "assets": []map[string]any{{"size_bytes": 1024}}},
		{"id": "33333333-3333-4333-8333-333333333333", "version": "20.3.0", "state": "retained", "committed_at": now.Add(-2 * time.Hour), "notes": "更早版本", "assets": []map[string]any{{"size_bytes": 1024}}},
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
class TestUploadTarget {
  constructor() { this.listeners = {}; }
  addEventListener(name, listener) { this.listeners[name] = listener; }
  dispatch(name, event) { if (this.listeners[name]) this.listeners[name](event); }
}
class TestXMLHttpRequest {
  constructor() { this.upload = new TestUploadTarget(); this.listeners = {}; this.status = 0; this.responseText = ""; }
  open() {}
  setRequestHeader() {}
  addEventListener(name, listener) { this.listeners[name] = listener; }
  send() {
    window.__xhrSends += 1;
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
    await waitFor(() => !document.getElementById("console-view").hidden, "登录后控制台");
  }
  await waitFor(() => document.querySelectorAll("#validation-list li.passed").length === 7, "逐项校验证据");
  const fileInput = document.getElementById("release-file");
  assert(!fileInput.hidden && fileInput.tabIndex >= 0, "ZIP选择器不可通过键盘到达");
  fileInput.focus();
  assert(document.activeElement === fileInput, "ZIP选择器不能获得焦点");
  assert(document.documentElement.scrollWidth <= window.innerWidth + 1, "窄屏页面出现整体横向溢出");
  assert(getComputedStyle(document.querySelector("#history-body td")).whiteSpace === "nowrap", "版本号可被拆行");
  assert(document.querySelector(".table-scroll").scrollWidth > document.querySelector(".table-scroll").clientWidth, "历史表缺少横向滚动");

  const rollback = document.querySelector("#history-body button");
  rollback.click();
  await waitFor(() => document.getElementById("rollback-dialog").open, "回滚对话框");
  document.getElementById("rollback-password").value = "temporary secret";
  document.querySelector('[data-close-dialog="rollback-dialog"]').click();
  assert(document.getElementById("rollback-password").value === "", "取消后密码未清空");

  Object.defineProperty(fileInput, "files", { configurable: true, value: [new File(["zip"], "release.zip", { type: "application/zip" })] });
  fileInput.dispatchEvent(new Event("change"));
  assert(document.getElementById("publish-button").disabled, "选择新ZIP后旧候选仍可发布");
  document.getElementById("upload-button").click();
  document.getElementById("upload-button").click();
  assert(window.__xhrSends === 1, "忙碌期间重复上传未被阻止");
  await waitFor(() => document.getElementById("upload-progress").value >= 61, "上传进度");

  if (window.__mode === "happy") {
    await waitFor(() => document.getElementById("candidate-version").textContent === "v21.1.0", "新候选");
    assert(!document.getElementById("publish-button").disabled, "校验通过后发布不可用");
    document.getElementById("publish-button").click();
    document.getElementById("publish-confirm-check").checked = true;
    document.getElementById("publish-form").requestSubmit();
    await waitFor(() => !document.getElementById("publish-dialog").open, "发布完成");
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
  result.textContent = "PASS " + window.__mode;
} catch (error) {
  result.textContent = "FAIL " + window.__mode + ": " + error.message;
}
`
