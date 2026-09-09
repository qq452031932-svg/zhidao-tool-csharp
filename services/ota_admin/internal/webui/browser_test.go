package webui

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"runtime"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestBrowserTestArgumentsDisableGpuDiskCache(t *testing.T) {
	arguments := browserTestArguments(`C:\test-profile`, "http://127.0.0.1/admin/")
	if !slices.Contains(arguments, "--disable-gpu-shader-disk-cache") {
		t.Fatalf("browser arguments %v do not disable the GPU shader/persistent disk cache", arguments)
	}
	if !slices.Contains(arguments, "--disable-features=SkiaGraphiteUsePersistentCache,GpuPersistentCache") {
		t.Fatalf("browser arguments %v do not disable Chromium persistent GPU caches", arguments)
	}
}

func browserTestArguments(profile, url string) []string {
	return []string{
		"--headless=new", "--disable-gpu", "--disable-gpu-shader-disk-cache", "--disable-features=SkiaGraphiteUsePersistentCache,GpuPersistentCache", "--no-first-run", "--no-default-browser-check",
		"--user-data-dir=" + profile, "--window-size=420,900", "--virtual-time-budget=30000", "--dump-dom", url,
	}
}

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
			command := exec.CommandContext(ctx, chrome, browserTestArguments(profile, server.URL+"/admin/?mode="+mode)...)
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

func TestAdminConsoleInRealBrowserLicenseManagement(t *testing.T) {
	chrome := installedTestBrowser()
	if chrome == "" {
		t.Skip("Chrome or Edge is not installed")
	}
	for _, mode := range []string{"license-happy", "license-unavailable", "license-edge"} {
		t.Run(mode, func(t *testing.T) {
			server := newLicenseBrowserTestServer(t, mode)
			defer server.Close()
			ctx, cancel := context.WithTimeout(context.Background(), 35*time.Second)
			defer cancel()
			command := exec.CommandContext(ctx, chrome, browserTestArguments(t.TempDir(), server.URL+"/admin/?mode="+mode)...)
			output, err := command.CombinedOutput()
			if ctx.Err() != nil {
				t.Fatalf("browser timed out: %v\n%s", ctx.Err(), output)
			}
			if err != nil {
				t.Fatalf("browser failed: %v\n%s", err, output)
			}
			document := string(output)
			if !strings.Contains(document, `id="test-result">PASS `+mode) {
				t.Fatalf("license browser scenario did not pass:\n%s", tail(document, 5000))
			}
		})
	}
}

func newLicenseBrowserTestServer(t *testing.T, mode string) *httptest.Server {
	t.Helper()
	page, err := staticFiles.ReadFile("static/index.html")
	if err != nil {
		t.Fatal(err)
	}
	modified := strings.Replace(string(page), `<script src="/admin/app.js" defer></script>`, `<script src="/license-test-bootstrap.js"></script><script src="/admin/app.js" defer></script><script type="module" src="/license-test-driver.js"></script>`, 1)
	const softwareA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
	const softwareB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
	const packageID = "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
	const cardID = "dddddddd-dddd-4ddd-8ddd-dddddddddddd"
	const cardText = "ABCD-EFGH-JKLM-NPQR-STUV-WXYZ-2345-6789"
	now := time.Now().UTC()
	var mu sync.Mutex
	loggedIn := false
	csrf := "license-csrf"
	softwareCreateRequests := 0
	searchRequests := 0
	copyRequests := 0
	deviceReads := 0
	deviceUnbindPath := ""
	generateKeys := []string{}
	renewKeys := []string{}
	committedGenerateKeys := map[string]bool{}
	committedRenewKeys := map[string]bool{}
	cardDeleted := false
	securityFailures := []string{}
	actions := map[string]int{}
	searchBody := map[string]any{}
	checkMutation := func(r *http.Request) {
		if r.Header.Get("Origin") != "http://"+r.Host || r.Header.Get("X-CSRF-Token") != csrf || r.Header.Get("Idempotency-Key") == "" {
			securityFailures = append(securityFailures, r.URL.Path)
		}
		if cookie, cookieErr := r.Cookie("license_browser_test"); cookieErr != nil || cookie.Value != "active" {
			securityFailures = append(securityFailures, "cookie:"+r.URL.Path)
		}
	}
	itemsSoftware := func() []map[string]any {
		return []map[string]any{{"id": softwareA, "name": "SuperPan", "enabled": true, "created_at": now}, {"id": softwareB, "name": "助手", "enabled": true, "created_at": now}}
	}
	itemsPackages := func() []map[string]any {
		return []map[string]any{{"id": packageID, "name": "全能套餐", "software_ids": []string{softwareA, softwareB}, "created_at": now}}
	}
	itemsCards := func() []map[string]any {
		return []map[string]any{{"id": cardID, "package_id": packageID, "package_name": "全能套餐", "prefix8": "ABCDEFGH", "last4": "6789", "card_type": "month", "duration_days": 30, "state": "effective", "activated_at": now.Add(-time.Hour), "expires_at": now.Add(29 * 24 * time.Hour), "binding_short_id": "MACHINE1", "note": "张三", "created_at": now.Add(-2 * time.Hour)}}
	}
	edgeCards := func(page int) []map[string]any {
		if cardDeleted && page > 1 {
			return nil
		}
		count := 100
		if page == 2 {
			count = 1
		}
		items := make([]map[string]any, 0, count)
		for index := 0; index < count; index++ {
			number := index + 1
			if page == 2 {
				number = 101
			}
			item := map[string]any{"id": fmt.Sprintf("%08d-0000-4000-8000-%012d", number, number), "package_id": packageID, "package_name": "全能套餐", "prefix8": "ABCDEFGH", "last4": fmt.Sprintf("%04d", number), "card_type": "month", "duration_days": 30, "state": "effective", "activated_at": now.Add(-time.Hour), "expires_at": now.Add(29 * 24 * time.Hour), "binding_short_id": "MACHINE1", "note": fmt.Sprintf("用户%d", number), "created_at": now.Add(-2 * time.Hour)}
			items = append(items, item)
		}
		return items
	}
	web := Handler()
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		switch r.URL.Path {
		case "/admin/":
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			_, _ = w.Write([]byte(modified))
		case "/license-test-bootstrap.js":
			w.Header().Set("Content-Type", "text/javascript; charset=utf-8")
			_, _ = w.Write([]byte(licenseBrowserBootstrapScript))
		case "/license-test-driver.js":
			w.Header().Set("Content-Type", "text/javascript; charset=utf-8")
			_, _ = w.Write([]byte(licenseBrowserDriverScript))
		case "/admin/api/session/current":
			if !loggedIn {
				writeBrowserJSON(w, http.StatusUnauthorized, map[string]any{"code": "authentication_failed"})
				return
			}
			writeBrowserJSON(w, http.StatusOK, map[string]any{"authenticated": true, "must_change_password": false, "csrf_token": csrf})
		case "/admin/api/session/bootstrap":
			writeBrowserJSON(w, http.StatusOK, map[string]any{"csrf_token": "license-preauth"})
		case "/admin/api/session/login":
			if r.Header.Get("Origin") != "http://"+r.Host || r.Header.Get("X-CSRF-Token") != "license-preauth" {
				securityFailures = append(securityFailures, "login")
			}
			loggedIn = true
			http.SetCookie(w, &http.Cookie{Name: "license_browser_test", Value: "active", Path: "/admin/", SameSite: http.SameSiteStrictMode})
			writeBrowserJSON(w, http.StatusOK, map[string]any{"csrf_token": csrf, "must_change_password": false})
		case "/admin/api/license/software":
			if mode == "license-unavailable" {
				writeBrowserJSON(w, http.StatusServiceUnavailable, map[string]any{"code": "license_service_unavailable", "message": "授权服务暂时不可用，请稍后重试。"})
				return
			}
			if r.Method == http.MethodPost {
				softwareCreateRequests++
				checkMutation(r)
				if softwareCreateRequests == 1 {
					w.Header().Set("Retry-After", "1")
					writeBrowserJSON(w, http.StatusTooManyRequests, map[string]any{"code": "rate_limited"})
					return
				}
				actions["software_create"]++
				writeBrowserJSON(w, http.StatusCreated, map[string]any{"id": "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", "name": "限流软件", "enabled": true, "created_at": now})
				return
			}
			if mode == "license-edge" && r.URL.Query().Get("page") == "2" {
				writeBrowserJSON(w, http.StatusOK, map[string]any{"items": []map[string]any{{"id": "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", "name": "第二页软件", "enabled": true, "package_count": 0, "created_at": now}}, "page": 2, "page_size": 50, "total": 51})
				return
			}
			if mode == "license-edge" && r.URL.Query().Get("page") != "" && r.URL.Query().Get("page") != "1" {
				writeBrowserJSON(w, http.StatusOK, map[string]any{"items": []any{}, "page": 3, "page_size": 100, "total": 51})
				return
			}
			total := 2
			if mode == "license-edge" {
				total = 51
			}
			writeBrowserJSON(w, http.StatusOK, map[string]any{"items": itemsSoftware(), "page": 1, "page_size": 50, "total": total})
		case "/admin/api/license/packages":
			if r.Method == http.MethodPost {
				checkMutation(r)
				actions["package_create"]++
				writeBrowserJSON(w, http.StatusCreated, itemsPackages()[0])
				return
			}
			if mode == "license-edge" && r.URL.Query().Get("page") == "2" {
				writeBrowserJSON(w, http.StatusOK, map[string]any{"items": []map[string]any{{"id": "ffffffff-ffff-4fff-8fff-ffffffffffff", "name": "第二页套餐", "software_ids": []string{softwareA}, "created_at": now}}, "page": 2, "page_size": 50, "total": 51})
				return
			}
			if mode == "license-edge" && r.URL.Query().Get("page") != "" && r.URL.Query().Get("page") != "1" {
				writeBrowserJSON(w, http.StatusOK, map[string]any{"items": []any{}, "page": 3, "page_size": 100, "total": 51})
				return
			}
			total := 1
			if mode == "license-edge" {
				total = 51
			}
			writeBrowserJSON(w, http.StatusOK, map[string]any{"items": itemsPackages(), "page": 1, "page_size": 50, "total": total})
		case "/admin/api/license/cards/search":
			searchRequests++
			if r.URL.RawQuery != "" || r.Header.Get("Idempotency-Key") != "" || r.Header.Get("Origin") != "http://"+r.Host || r.Header.Get("X-CSRF-Token") != csrf {
				securityFailures = append(securityFailures, "sensitive_search")
			}
			_ = json.NewDecoder(r.Body).Decode(&searchBody)
			if mode == "license-edge" && searchRequests == 2 {
				w.Header().Set("Retry-After", "1")
				writeBrowserJSON(w, http.StatusTooManyRequests, map[string]any{"code": "rate_limited"})
				return
			}
			if mode == "license-edge" {
				page := 1
				if value, ok := searchBody["page"].(float64); ok {
					page = int(value)
				}
				total := 101
				if cardDeleted {
					total = 100
				}
				writeBrowserJSON(w, http.StatusOK, map[string]any{"items": edgeCards(page), "page": page, "page_size": 100, "total": total})
				return
			}
			writeBrowserJSON(w, http.StatusOK, map[string]any{"items": itemsCards(), "page": 1, "page_size": 100, "total": 1})
		case "/admin/api/license/cards/generate":
			checkMutation(r)
			key := r.Header.Get("Idempotency-Key")
			generateKeys = append(generateKeys, key)
			if !committedGenerateKeys[key] {
				committedGenerateKeys[key] = true
				actions["generate"]++
			}
			if mode == "license-edge" && len(generateKeys) == 1 {
				writeBrowserJSON(w, http.StatusServiceUnavailable, map[string]any{"code": "license_service_unavailable"})
				return
			}
			if mode == "license-edge" && len(generateKeys) >= 3 {
				time.Sleep(200 * time.Millisecond)
			}
			writeBrowserJSON(w, http.StatusCreated, map[string]any{"items": []map[string]any{{"card": itemsCards()[0], "plaintext": cardText}}})
		case "/admin/api/license/cards/export":
			checkMutation(r)
			actions["export"]++
			w.Header().Set("Content-Type", "text/csv; charset=utf-8")
			w.Header().Set("Content-Disposition", `attachment; filename="license-cards.csv"`)
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write(append([]byte{0xef, 0xbb, 0xbf}, []byte("完整卡密,套餐\r\n"+cardText+",全能套餐\r\n")...))
		case "/admin/api/license/devices":
			deviceReads++
			if mode == "license-edge" && deviceReads == 2 {
				time.Sleep(150 * time.Millisecond)
			}
			page, total := 1, 1
			if mode == "license-edge" {
				total = 51
			}
			if mode == "license-edge" && r.URL.Query().Get("page") == "2" {
				page = 2
			}
			writeBrowserJSON(w, http.StatusOK, map[string]any{"items": []map[string]any{{"id": cardID, "card_short_id": "dddddddd", "machine_short_id": "MACHINE1", "bound_at": now.Add(-time.Hour), "last_successful_validation": now, "expires_at": now.Add(29 * 24 * time.Hour)}}, "page": page, "page_size": 50, "total": total})
		case "/admin/api/releases":
			writeBrowserJSON(w, http.StatusOK, map[string]any{"releases": browserReleases(), "read_only": false, "client_auto_downgrade": false})
		case "/admin/api/candidates/current":
			writeBrowserJSON(w, http.StatusNotFound, map[string]any{"code": "not_found"})
		case "/test/license-assertions":
			writeBrowserJSON(w, http.StatusOK, map[string]any{"software_create_requests": softwareCreateRequests, "security_failures": securityFailures, "actions": actions, "search_body": searchBody, "search_requests": searchRequests, "copy_requests": copyRequests, "device_reads": deviceReads, "device_unbind_path": deviceUnbindPath, "generate_keys": generateKeys, "renew_keys": renewKeys})
		default:
			path := r.URL.Path
			if strings.HasPrefix(path, "/admin/api/license/software/") {
				checkMutation(r)
				if strings.HasSuffix(path, "/disable") {
					actions["software_disable"]++
				} else if strings.HasSuffix(path, "/enable") {
					actions["software_enable"]++
				} else {
					actions["software_rename"]++
				}
				writeBrowserJSON(w, http.StatusOK, map[string]any{"id": softwareA})
				return
			}
			if strings.HasPrefix(path, "/admin/api/license/packages/") {
				checkMutation(r)
				if strings.HasSuffix(path, "/impact") {
					actions["impact"]++
					writeBrowserJSON(w, http.StatusOK, map[string]any{"affected_valid_cards": 3, "max_offline_lease_delay_minutes": 30})
				} else {
					actions["members"]++
					writeBrowserJSON(w, http.StatusOK, map[string]any{"id": packageID, "software_ids": []string{softwareB}})
				}
				return
			}
			if strings.HasPrefix(path, "/admin/api/license/cards/") {
				if r.Method == http.MethodGet {
					copyRequests++
					if mode == "license-edge" && copyRequests == 1 {
						w.Header().Set("Retry-After", "1")
						writeBrowserJSON(w, http.StatusTooManyRequests, map[string]any{"code": "rate_limited"})
						return
					}
					if mode == "license-edge" && copyRequests == 3 {
						writeBrowserJSON(w, http.StatusUnauthorized, map[string]any{"code": "authentication_failed"})
						return
					}
					if mode == "license-edge" {
						time.Sleep(200 * time.Millisecond)
					}
					actions["copy"]++
					writeBrowserJSON(w, http.StatusOK, map[string]any{"card": itemsCards()[0], "plaintext": cardText})
					return
				}
				checkMutation(r)
				if strings.HasSuffix(path, "/renew") {
					key := r.Header.Get("Idempotency-Key")
					renewKeys = append(renewKeys, key)
					if !committedRenewKeys[key] {
						committedRenewKeys[key] = true
						actions["renew"]++
					}
					if mode == "license-edge" && len(renewKeys) == 1 {
						writeBrowserJSON(w, http.StatusServiceUnavailable, map[string]any{"code": "license_service_unavailable"})
						return
					}
				} else if strings.HasSuffix(path, "/remark") {
					actions["remark"]++
				} else if strings.HasSuffix(path, "/unbind") {
					actions["card_unbind"]++
				} else {
					actions["delete"]++
					if mode == "license-edge" {
						cardDeleted = true
					}
				}
				writeBrowserJSON(w, http.StatusOK, map[string]any{"id": cardID})
				return
			}
			if strings.HasPrefix(path, "/admin/api/license/devices/") && strings.HasSuffix(path, "/unbind") {
				checkMutation(r)
				deviceUnbindPath = path
				actions["device_unbind"]++
				writeBrowserJSON(w, http.StatusOK, map[string]any{"id": cardID})
				return
			}
			web.ServeHTTP(w, r)
		}
	})
	return httptest.NewServer(handler)
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
	passwordRequests := 0
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
			passwordRequests++
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
			writeBrowserJSON(response, http.StatusOK, map[string]any{"password_requests": passwordRequests, "publish_keys": publishKeys, "rollback_keys": rollbackKeys, "protocol_failures": protocolFailures})
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
    const newPassword = document.getElementById("new-password");
    newPassword.value = "密码密码密码a";
    document.getElementById("password-form").requestSubmit();
    await waitFor(() => !document.getElementById("password-error").hidden, "七位密码客户端拒绝");
    assert(document.activeElement === newPassword, "客户端拒绝后未聚焦新密码");
    assert(!document.getElementById("password-view").hidden, "七位密码错误离开改密页");
    const rejectedPassword = await (await fetch("/test/assertions")).json();
    assert(rejectedPassword.password_requests === 0, "七位密码错误请求了改密接口");
    newPassword.value = "密码密码密码ab";
    document.getElementById("password-form").requestSubmit();
    await waitFor(() => !document.getElementById("console-view").hidden, "改密后控制台");
    assert(document.getElementById("current-password").value === "" && document.getElementById("new-password").value === "", "改密后密码字段未清空");
  }
  document.getElementById("tab-ota").click();
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
  assert(document.querySelector(".history-card .table-scroll").scrollWidth > document.querySelector(".history-card .table-scroll").clientWidth, "历史表缺少横向滚动");
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
    assert(protocol.password_requests === 1, "八位密码未恰好请求一次改密接口");
    assert(protocol.publish_keys.length === 2 && protocol.publish_keys[0] === protocol.publish_keys[1], "重复发布未沿用幂等键");
    assert(protocol.rollback_keys.length === 2 && protocol.rollback_keys[0] === protocol.rollback_keys[1], "重复回滚未沿用幂等键");
  }
  assert(window.__runtimeErrors.length === 0, "页面存在未处理异常：" + window.__runtimeErrors.join(","));
  result.textContent = "PASS " + window.__mode;
} catch (error) {
  result.textContent = "FAIL " + window.__mode + ": " + error.message;
}
`

const licenseBrowserBootstrapScript = `
window.__licenseClipboard = "";
window.__licenseDownloads = 0;
window.__rejectLicenseClipboard = false;
Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async value => { if (window.__rejectLicenseClipboard) throw new Error("clipboard denied"); window.__licenseClipboard = value; } } });
URL.createObjectURL = () => { window.__licenseDownloads += 1; return "blob:license-test"; };
URL.revokeObjectURL = () => {};
HTMLAnchorElement.prototype.click = function() {};
window.__licenseRuntimeErrors = [];
window.addEventListener("error", event => window.__licenseRuntimeErrors.push(event.message || "window.error"));
window.addEventListener("unhandledrejection", event => window.__licenseRuntimeErrors.push(String(event.reason || "unhandledrejection")));
`

const licenseBrowserDriverScript = `
const waitForLicense = async (predicate, label, timeout = 9000) => {
  const until = Date.now() + timeout;
  while (Date.now() < until) { if (await predicate()) return; await new Promise(resolve => setTimeout(resolve, 20)); }
  throw new Error("等待超时：" + label);
};
const assertLicense = (condition, message) => { if (!condition) throw new Error(message); };
const clickTab = id => document.getElementById(id).click();
const result = document.createElement("pre"); result.id = "test-result"; document.body.append(result);
try {
  await waitForLicense(() => !document.getElementById("login-view").hidden, "登录页");
  document.getElementById("login-password").value = "browser-test-password";
  document.getElementById("login-form").requestSubmit();
  await waitForLicense(() => !document.getElementById("console-view").hidden, "控制台");
  const navText = [...document.querySelectorAll("#admin-navigation [role=tab]")].map(item => item.textContent.trim()).join("|");
  assertLicense(navText === "卡密管理|软件管理|套餐管理|设备绑定|OTA 更新", "导航顺序不正确：" + navText);
  assertLicense(document.getElementById("tab-cards").getAttribute("aria-selected") === "true" && !document.getElementById("cards-panel").hidden, "登录后未默认进入卡密管理");

  if (new URL(location.href).searchParams.get("mode") === "license-unavailable") {
    await waitForLicense(() => !document.getElementById("license-error").hidden, "授权服务不可用提示");
    assertLicense(document.getElementById("license-error").textContent.includes("授权服务暂时不可用"), "授权服务错误未中文化");
    clickTab("tab-ota");
    await waitForLicense(() => !document.getElementById("ota-panel").hidden && document.getElementById("current-version").textContent === "v20.5.0", "授权故障时 OTA 仍可用");
    assertLicense(window.__licenseRuntimeErrors.length === 0, "页面异常：" + window.__licenseRuntimeErrors.join(","));
    result.textContent = "PASS license-unavailable";
  } else if (new URL(location.href).searchParams.get("mode") === "license-edge") {
    await waitForLicense(() => document.querySelectorAll("#cards-body tr[data-card-id]").length === 100, "百张卡密第一页");
    document.getElementById("tab-cards").dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true }));
    await waitForLicense(() => document.activeElement === document.getElementById("tab-software") && !document.getElementById("software-panel").hidden, "方向键切换标签");
    document.getElementById("tab-software").dispatchEvent(new KeyboardEvent("keydown", { key: "Home", bubbles: true }));
    await waitForLicense(() => document.activeElement === document.getElementById("tab-cards") && !document.getElementById("cards-panel").hidden, "Home 返回首标签");

    document.getElementById("filter-prefix8").value = "ABCDEFGH";
    document.getElementById("card-filter-form").requestSubmit();
    document.getElementById("card-filter-form").requestSubmit();
    await waitForLicense(() => document.getElementById("search-cards-button").disabled, "查询 429 冷却");
    let edge = await (await fetch("/test/license-assertions")).json();
    assertLicense(edge.search_requests === 2, "查询忙碌期间发生重叠请求");
    await waitForLicense(() => !document.getElementById("search-cards-button").disabled, "查询冷却结束");
    document.getElementById("cards-page-next").click();
    await waitForLicense(() => document.getElementById("cards-page-range").textContent.includes("101–101") && document.querySelectorAll("#cards-body tr[data-card-id]").length === 1, "第 101 张卡密");
    document.querySelector("#cards-body [data-action=card-delete]").click();
    document.getElementById("delete-card-confirm-check").checked = true;
    document.getElementById("delete-card-form").requestSubmit();
    await waitForLicense(() => document.getElementById("cards-page-range").textContent.includes("1–100 / 100") && document.querySelectorAll("#cards-body tr[data-card-id]").length === 100, "删除最后一条后回退第一页");

    document.getElementById("generate-package").value = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    document.getElementById("generate-count").value = "1";
    document.getElementById("generate-note").value = "幂等备注";
    document.getElementById("card-generate-form").requestSubmit();
    await waitForLicense(() => !document.getElementById("generate-cards-button").disabled, "生成首响应失败后可重试");
    document.getElementById("card-generate-form").requestSubmit();
    await waitForLicense(() => !document.getElementById("generated-card-window").hidden, "幂等重试生成成功");
    edge = await (await fetch("/test/license-assertions")).json();
    assertLicense(edge.generate_keys.length === 2 && edge.generate_keys[0] === edge.generate_keys[1] && edge.actions.generate === 1, "生成重试未复用幂等键或重复提交：" + JSON.stringify(edge));
    window.__rejectLicenseClipboard = true;
    document.getElementById("copy-generated-cards").click();
    await waitForLicense(() => document.getElementById("card-action-status").textContent.includes("复制失败"), "剪贴板拒绝中文提示");
    window.__rejectLicenseClipboard = false;

    let firstCard = document.querySelector("#cards-body tr[data-card-id]");
    firstCard.querySelector('[data-action="card-copy"]').click();
    firstCard.querySelector('[data-action="card-copy"]').click();
    await waitForLicense(() => firstCard.querySelector('[data-action="card-copy"]').disabled, "单卡复制 429 冷却");
    edge = await (await fetch("/test/license-assertions")).json();
    assertLicense(edge.copy_requests === 1, "复制忙碌期间发生重叠请求");
    await waitForLicense(() => !firstCard.querySelector('[data-action="card-copy"]').disabled, "复制冷却结束");
    firstCard.querySelector('[data-action="card-copy"]').click();
    clickTab("tab-software");
    await waitForLicense(() => !document.getElementById("software-panel").hidden && document.getElementById("software-page-range").textContent.includes("/ 51"), "复制期间切换标签");
    await new Promise(resolve => setTimeout(resolve, 260));
    assertLicense(window.__licenseClipboard === "", "切页后的迟到明文写入剪贴板");
    document.getElementById("software-page-next").click();
    await waitForLicense(() => document.getElementById("software-page-range").textContent.includes("51–51"), "软件第二页");

    clickTab("tab-packages");
    await waitForLicense(() => !document.getElementById("packages-panel").hidden && document.getElementById("packages-page-range").textContent.includes("/ 51") && !document.getElementById("packages-page-next").disabled, "套餐分页可用");
    document.getElementById("packages-page-next").click();
    await waitForLicense(() => document.getElementById("packages-page-range").textContent.includes("51–51"), "套餐第二页");

    clickTab("tab-devices");
    await waitForLicense(() => document.querySelector("#devices-body [data-action=device-unbind]"), "设备列表");
    document.getElementById("refresh-devices").click();
    document.getElementById("refresh-devices").click();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).device_reads === 2, "设备刷新完成");
    document.getElementById("devices-page-next").click();
    await waitForLicense(() => document.getElementById("devices-page-range").textContent.includes("51–51"), "设备第二页");
    document.querySelector("#devices-body [data-action=device-unbind]").click();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.device_unbind === 1, "设备精确解绑");
    edge = await (await fetch("/test/license-assertions")).json();
    assertLicense(edge.device_unbind_path.endsWith("/devices/dddddddd-dddd-4ddd-8ddd-dddddddddddd/unbind"), "设备解绑未使用稳定完整 ID");

    clickTab("tab-cards");
    await waitForLicense(() => document.querySelector("#cards-body tr[data-card-id]"), "返回卡密页");
    firstCard = document.querySelector("#cards-body tr[data-card-id]");
    firstCard.querySelector('[data-action="card-renew"]').click();
    document.getElementById("renew-form").requestSubmit();
    await waitForLicense(() => !document.querySelector("#renew-form button[type=submit]").disabled, "续期首响应失败后可重试");
    document.getElementById("renew-form").requestSubmit();
    await waitForLicense(() => !document.getElementById("renew-card-dialog").open, "续期幂等重试成功");
    edge = await (await fetch("/test/license-assertions")).json();
    assertLicense(edge.renew_keys.length === 2 && edge.renew_keys[0] === edge.renew_keys[1] && edge.actions.renew === 1, "续期重试未复用幂等键或重复提交");

    document.getElementById("card-generate-form").requestSubmit();
    clickTab("tab-ota");
    await waitForLicense(() => !document.getElementById("ota-panel").hidden, "生成期间切换 OTA");
    await new Promise(resolve => setTimeout(resolve, 260));
    assertLicense(document.getElementById("generated-card-window").hidden && document.getElementById("generated-card-list").textContent === "", "切页后的迟到生成明文写入 DOM");
    edge = await (await fetch("/test/license-assertions")).json();
    assertLicense(edge.generate_keys.length >= 3 && edge.generate_keys[2] !== edge.generate_keys[0], "成功后新生成操作未使用新幂等键");

    clickTab("tab-cards");
    await waitForLicense(() => document.querySelector("#cards-body tr[data-card-id]"), "会话失效前卡密页");
    firstCard = document.querySelector("#cards-body tr[data-card-id]");
    firstCard.querySelector('[data-action="card-remark"]').click();
    document.getElementById("remark-value").value = "不得残留的备注";
    document.getElementById("filter-complete-card").value = "不得残留的完整卡密";
    firstCard.querySelector('[data-action="card-copy"]').click();
    await waitForLicense(() => !document.getElementById("login-view").hidden, "真正 401 后返回登录页");
    assertLicense(!document.getElementById("remark-card-dialog").open && document.getElementById("remark-value").value === "" && document.getElementById("filter-complete-card").value === "", "会话失效未清除卡密临时状态");
    assertLicense(window.__licenseRuntimeErrors.length === 0, "页面异常：" + window.__licenseRuntimeErrors.join(","));
    result.textContent = "PASS license-edge";
  } else {
    await waitForLicense(() => document.querySelectorAll("#cards-body tr[data-card-id]").length === 1, "卡密列表");
    clickTab("tab-software");
    await waitForLicense(() => document.querySelectorAll("#software-body tr[data-software-id]").length === 2, "软件列表");
    document.getElementById("software-name").value = "限流软件";
    document.getElementById("software-create-form").requestSubmit();
    await waitForLicense(() => document.getElementById("software-create-button").disabled, "429 禁用受影响操作");
    document.getElementById("software-create-form").requestSubmit();
    let assertions = await (await fetch("/test/license-assertions")).json();
    assertLicense(assertions.software_create_requests === 1, "429 期间重复提交未被阻止");
    assertLicense(!document.getElementById("tab-cards").disabled, "429 错误地禁用了其他操作");
    await waitForLicense(() => !document.getElementById("software-create-button").disabled, "429 重试时间");
    document.getElementById("software-create-form").requestSubmit();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.software_create === 1, "创建软件");
    const softwareRow = document.querySelector('#software-body tr[data-software-id="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"]');
    softwareRow.querySelector("input").value = "SuperPan 新名";
    softwareRow.querySelector('[data-action="software-save"]').click();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.software_rename === 1, "软件改名");
    softwareRow.querySelector('[data-action="software-toggle"]').click();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.software_disable === 1, "停用软件");
    await waitForLicense(() => document.querySelector('#software-body tr[data-software-id="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"] [data-action="software-toggle"]'), "软件重绘");
    document.querySelector('#software-body tr[data-software-id="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"] [data-action="software-toggle"]').click();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.software_enable === 1, "启用软件");

    clickTab("tab-packages");
    await waitForLicense(() => document.querySelectorAll("#package-create-members input").length === 2, "套餐软件成员");
    document.getElementById("package-name").value = "新套餐";
    for (const input of document.querySelectorAll("#package-create-members input")) input.checked = true;
    document.getElementById("package-create-form").requestSubmit();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.package_create === 1, "创建多软件套餐");
    const packageRow = document.querySelector('#packages-body tr[data-package-id="cccccccc-cccc-4ccc-8ccc-cccccccccccc"]');
    const memberChecks = packageRow.querySelectorAll('[data-package-member]');
    memberChecks[0].checked = false;
    packageRow.querySelector('[data-action="package-preview"]').click();
    await waitForLicense(() => document.getElementById("package-impact-dialog").open, "移除成员影响预览");
    assertLicense(document.getElementById("package-impact-summary").textContent.includes("3") && document.getElementById("package-impact-summary").textContent.includes("30"), "影响预览缺少有效卡数或 30 分钟延迟");
    document.getElementById("package-impact-confirm").click();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.members === 1, "应用套餐成员");

    clickTab("tab-cards");
    await waitForLicense(() => document.getElementById("generate-package").options.length > 1, "生成套餐列表");
    document.getElementById("generate-package").value = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    document.getElementById("generate-card-type").value = "30";
    document.getElementById("generate-count").value = "2";
    document.getElementById("generate-note").value = "批次备注";
    document.getElementById("card-generate-form").requestSubmit();
    await waitForLicense(() => !document.getElementById("generated-card-window").hidden, "明文安全窗口");
    assertLicense(document.getElementById("generated-card-list").textContent.includes("ABCD-EFGH"), "生成后未显示明文");
    document.getElementById("copy-generated-cards").click();
    await waitForLicense(() => window.__licenseClipboard.includes("ABCD-EFGH"), "复制生成卡密");
    document.querySelector("#cards-body input[type=checkbox]").checked = true;
    document.querySelector("#cards-body input[type=checkbox]").dispatchEvent(new Event("change"));
    document.getElementById("export-selected-cards").click();
    await waitForLicense(() => window.__licenseDownloads === 1, "导出 CSV");

    document.getElementById("filter-complete-card").value = "ABCD-EFGH-JKLM-NPQR-STUV-WXYZ-2345-6789";
    document.getElementById("filter-prefix8").value = "ABCDEFGH";
    document.getElementById("filter-last4").value = "6789";
    document.getElementById("filter-note").value = "张三";
    document.getElementById("filter-package").value = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    document.getElementById("filter-card-type").value = "month";
    document.getElementById("filter-state").value = "effective";
    document.getElementById("card-filter-form").requestSubmit();
    await waitForLicense(async () => {
      const body = (await (await fetch("/test/license-assertions")).json()).search_body;
      return body.complete_card && body.prefix8 && body.last4 && body.note_contains && body.package_id && body.card_type && body.state;
    }, "全部卡密筛选");
    let cardRow = document.querySelector('#cards-body tr[data-card-id="dddddddd-dddd-4ddd-8ddd-dddddddddddd"]');
    cardRow.querySelector('[data-action="card-copy"]').click();
    await waitForLicense(() => window.__licenseClipboard.includes("ABCD-EFGH"), "复制单卡");
    cardRow.querySelector('[data-action="card-renew"]').click();
    document.getElementById("renew-duration").value = "90";
    document.getElementById("renew-form").requestSubmit();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.renew === 1, "续期");
    cardRow = document.querySelector('#cards-body tr[data-card-id="dddddddd-dddd-4ddd-8ddd-dddddddddddd"]');
    cardRow.querySelector('[data-action="card-remark"]').click();
    document.getElementById("remark-value").value = "新备注";
    document.getElementById("remark-form").requestSubmit();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.remark === 1, "备注");
    cardRow = document.querySelector('#cards-body tr[data-card-id="dddddddd-dddd-4ddd-8ddd-dddddddddddd"]');
    cardRow.querySelector('[data-action="card-unbind"]').click();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.card_unbind === 1, "卡密解绑");
    cardRow = document.querySelector('#cards-body tr[data-card-id="dddddddd-dddd-4ddd-8ddd-dddddddddddd"]');
    cardRow.querySelector('[data-action="card-delete"]').click();
    assertLicense(document.getElementById("delete-card-dialog").open, "删除卡密未二次确认");
    assertLicense(document.getElementById("delete-card-target").textContent.includes("ABCDEFGH…6789") && document.getElementById("delete-card-target").textContent.includes("全能套餐"), "删除确认缺少卡密短号或套餐");
    document.getElementById("delete-card-form").requestSubmit();
    assertions = await (await fetch("/test/license-assertions")).json();
    assertLicense(!assertions.actions.delete, "未勾选确认就删除");
    document.getElementById("delete-card-confirm-check").checked = true;
    document.getElementById("delete-card-form").requestSubmit();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.delete === 1, "永久删除");

    clickTab("tab-devices");
    await waitForLicense(() => document.querySelector("#devices-body [data-action=device-unbind]"), "设备列表");
    document.querySelector("#devices-body [data-action=device-unbind]").click();
    await waitForLicense(async () => (await (await fetch("/test/license-assertions")).json()).actions.device_unbind === 1, "设备解绑");

    clickTab("tab-ota");
    await waitForLicense(() => !document.getElementById("ota-panel").hidden && document.getElementById("current-version").textContent === "v20.5.0", "OTA 标签页");
    assertLicense(document.getElementById("generated-card-window").hidden && !document.getElementById("generated-card-list").textContent.includes("ABCD-EFGH"), "离开卡密页后明文未清除");
    assertions = await (await fetch("/test/license-assertions")).json();
    assertLicense(assertions.security_failures.length === 0, "请求安全头缺失：" + assertions.security_failures.join(","));
    assertLicense(window.__licenseRuntimeErrors.length === 0, "页面异常：" + window.__licenseRuntimeErrors.join(","));
    result.textContent = "PASS license-happy";
  }
} catch (error) {
  result.textContent = "FAIL " + new URL(location.href).searchParams.get("mode") + ": " + error.message;
}
`
