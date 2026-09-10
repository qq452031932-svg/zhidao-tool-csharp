//go:build license_local_acceptance && windows

package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/http/cookiejar"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
	"unsafe"

	"fanfh.cn/superpan/ota-admin/internal/store"
	"golang.org/x/sys/windows"
)

func TestValidateLoopbackAddressAcceptsOnlyLiteralIPv4Loopback(t *testing.T) {
	for _, address := range []string{"0.0.0.0:19090", "localhost:19090", "[::1]:19090", "127.0.0.2:19090", "127.0.0.1:0", "127.0.0.1:19090/path"} {
		if err := validateLoopbackAddress(address); err == nil {
			t.Fatalf("validateLoopbackAddress(%q) succeeded", address)
		}
	}
	if err := validateLoopbackAddress("127.0.0.1:19090"); err != nil {
		t.Fatalf("literal loopback rejected: %v", err)
	}
}

func TestPrepareRuntimeCreatesPrivateDisposableStateAndClientSettings(t *testing.T) {
	root := filepath.Join(t.TempDir(), ".tmp", "license-local")
	runtime, err := prepareRuntime(root, "127.0.0.1:19090", "127.0.0.1:19091")
	if err != nil {
		t.Fatal(err)
	}
	defer runtime.clear()

	for _, path := range []string{
		runtime.licenseDatabasePath,
		runtime.adminDatabasePath,
		runtime.encryptionKeyPath,
		runtime.lookupKeyPath,
		runtime.signingPrivateKeyPath,
		runtime.signingPublicKeyPath,
		runtime.internalSecretPath,
		runtime.sourceHMACKeyPath,
		filepath.Join(root, "client.json"),
	} {
		if !filepath.IsAbs(path) {
			t.Fatalf("runtime path is not absolute: %s", path)
		}
		if _, err := os.Stat(path); err != nil {
			t.Fatalf("runtime artifact missing %s: %v", path, err)
		}
	}
	client, err := os.ReadFile(filepath.Join(root, "client.json"))
	if err != nil {
		t.Fatal(err)
	}
	text := string(client)
	for _, required := range []string{"http://127.0.0.1:19091", "local-acceptance-v1", "signing-public.pem"} {
		if !strings.Contains(text, required) {
			t.Fatalf("client settings missing %q: %s", required, text)
		}
	}
	for _, secret := range [][]byte{runtime.encryptionKey, runtime.lookupKey, runtime.internalSecret, runtime.sourceHMACKey} {
		if strings.Contains(text, string(secret)) {
			t.Fatal("client settings leaked a server secret")
		}
	}
}

func TestPrepareRuntimeRejectsExistingNonEmptyRoot(t *testing.T) {
	root := filepath.Join(t.TempDir(), ".tmp", "license-local")
	if err := os.MkdirAll(root, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "keep.txt"), []byte("do not overwrite"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := prepareRuntime(root, "127.0.0.1:19090", "127.0.0.1:19091"); err == nil {
		t.Fatal("prepareRuntime overwrote a non-empty root")
	}
}

func TestPrepareRuntimeRejectsExistingEmptyRoot(t *testing.T) {
	root := filepath.Join(t.TempDir(), ".tmp", "license-local")
	if err := os.MkdirAll(root, 0o700); err != nil {
		t.Fatal(err)
	}
	if _, err := prepareRuntime(root, "127.0.0.1:19090", "127.0.0.1:19091"); err == nil {
		t.Fatal("prepareRuntime accepted an existing empty root")
	}
}

func TestPrepareRuntimeAllowsOnlyOneConcurrentOwner(t *testing.T) {
	root := filepath.Join(t.TempDir(), ".tmp", "license-local")
	type result struct {
		runtime *localRuntime
		err     error
	}
	start := make(chan struct{})
	results := make(chan result, 2)
	var workers sync.WaitGroup
	for range 2 {
		workers.Add(1)
		go func() {
			defer workers.Done()
			<-start
			runtime, err := prepareRuntime(root, "127.0.0.1:19090", "127.0.0.1:19091")
			results <- result{runtime: runtime, err: err}
		}()
	}
	close(start)
	workers.Wait()
	close(results)

	successes := 0
	for outcome := range results {
		if outcome.err == nil {
			successes++
			outcome.runtime.clear()
		}
	}
	if successes != 1 {
		t.Fatalf("successful concurrent owners = %d, want 1", successes)
	}
}

func TestPrepareRuntimeProtectsDirectoryWithPrivateWindowsDACL(t *testing.T) {
	root := filepath.Join(t.TempDir(), ".tmp", "license-local")
	runtime, err := prepareRuntime(root, "127.0.0.1:19090", "127.0.0.1:19091")
	if err != nil {
		t.Fatal(err)
	}
	defer runtime.clear()

	descriptor, err := windows.GetNamedSecurityInfo(root, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	control, _, err := descriptor.Control()
	if err != nil {
		t.Fatal(err)
	}
	if control&windows.SE_DACL_PROTECTED == 0 {
		t.Fatal("runtime directory DACL still inherits permissions")
	}
	dacl, _, err := descriptor.DACL()
	if err != nil {
		t.Fatal(err)
	}
	if dacl == nil || dacl.AceCount == 0 {
		t.Fatal("runtime DACL is empty")
	}
	token, err := windows.OpenCurrentProcessToken()
	if err != nil {
		t.Fatal(err)
	}
	defer token.Close()
	user, err := token.GetTokenUser()
	if err != nil {
		t.Fatal(err)
	}
	system, err := windows.CreateWellKnownSid(windows.WinLocalSystemSid)
	if err != nil {
		t.Fatal(err)
	}
	administrators, err := windows.CreateWellKnownSid(windows.WinBuiltinAdministratorsSid)
	if err != nil {
		t.Fatal(err)
	}
	allowed := map[string]bool{
		user.User.Sid.String():  true,
		system.String():         true,
		administrators.String(): true,
	}
	for index := uint32(0); index < uint32(dacl.AceCount); index++ {
		var ace *windows.ACCESS_ALLOWED_ACE
		if err := windows.GetAce(dacl, index, &ace); err != nil {
			t.Fatal(err)
		}
		if ace.Header.AceType != windows.ACCESS_ALLOWED_ACE_TYPE {
			t.Fatalf("runtime DACL ACE %d has unexpected type %d", index, ace.Header.AceType)
		}
		sid := (*windows.SID)(unsafe.Pointer(&ace.SidStart))
		if !allowed[sid.String()] {
			t.Fatalf("runtime DACL grants access to unexpected SID %s", sid.String())
		}
	}
}

func TestSecureLocalRuntimeDirectoryIsAvailableFromTheTaggedHost(t *testing.T) {
	root := filepath.Join(t.TempDir(), "license-local")
	if err := os.Mkdir(root, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := secureLocalRuntimeDirectory(root); err != nil {
		t.Fatal(err)
	}
}

func TestRuntimeDACLAllowsCurrentUserInFreshProcess(t *testing.T) {
	root := filepath.Join(t.TempDir(), ".tmp", "license-local")
	runtime, err := prepareRuntime(root, "127.0.0.1:19090", "127.0.0.1:19091")
	if err != nil {
		t.Fatal(err)
	}
	defer runtime.clear()

	testExecutable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	command := exec.Command(testExecutable, "-test.run=^TestRuntimeDACLReadHelper$")
	command.Env = append(os.Environ(), "LICENSE_LOCAL_RUNTIME_READ="+root)
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("fresh current-user process could not read protected runtime: %v\n%s", err, output)
	}
}

func TestRuntimeDACLReadHelper(t *testing.T) {
	root := os.Getenv("LICENSE_LOCAL_RUNTIME_READ")
	if root == "" {
		return
	}
	if _, err := os.ReadDir(root); err != nil {
		t.Fatal(err)
	}
	if _, err := os.ReadFile(filepath.Join(root, "client.json")); err != nil {
		t.Fatal(err)
	}
}

func TestValidateInstanceTokenRequiresCanonicalRandomHex(t *testing.T) {
	for _, token := range []string{"", "ABCDEF0123456789ABCDEF0123456789", "abcdef", strings.Repeat("a", 33), strings.Repeat("g", 32)} {
		if err := validateInstanceToken(token); err == nil {
			t.Fatalf("validateInstanceToken(%q) succeeded", token)
		}
	}
	if err := validateInstanceToken("abcdef0123456789abcdef0123456789"); err != nil {
		t.Fatalf("canonical token rejected: %v", err)
	}
}

func TestLocalOTAStubIsEmptyAndReadOnly(t *testing.T) {
	stub := localOTAStub{}
	items, err := stub.ReleaseHistory(context.Background())
	if err != nil || len(items) != 0 {
		t.Fatalf("ReleaseHistory() = %#v, %v", items, err)
	}
	if stub.ReadOnlyError() == nil {
		t.Fatal("local OTA stub did not advertise read-only mode")
	}
	if _, err := stub.CurrentForActiveSession(context.Background(), "session"); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("CurrentForActiveSession() error = %v, want not found", err)
	}
}

func TestLocalStackSupportsBrowserStyleHTTPLoginOnLoopback(t *testing.T) {
	adminAddress := unusedLoopbackAddress(t)
	licenseAddress := unusedLoopbackAddress(t)
	root := filepath.Join(t.TempDir(), ".tmp", "license-local")
	runtime, err := prepareRuntime(root, adminAddress, licenseAddress)
	if err != nil {
		t.Fatal(err)
	}
	defer runtime.clear()

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() {
		done <- serveLocalAdmin(ctx, runtime, adminAddress, "http://"+licenseAddress, strings.Repeat("a", 32), io.Discard, io.Discard)
	}()
	defer func() {
		cancel()
		select {
		case err := <-done:
			if err != nil {
				t.Errorf("local stack shutdown: %v", err)
			}
		case <-time.After(10 * time.Second):
			t.Error("local stack did not stop")
		}
	}()

	passwordPath := filepath.Join(root, "admin-password.txt")
	readyPath := filepath.Join(root, "ready.json")
	deadline := time.Now().Add(10 * time.Second)
	for {
		_, passwordErr := os.Stat(passwordPath)
		_, readyErr := os.Stat(readyPath)
		if passwordErr == nil && readyErr == nil {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("local admin password was not generated")
		}
		time.Sleep(10 * time.Millisecond)
	}
	passwordBytes, err := os.ReadFile(passwordPath)
	if err != nil {
		t.Fatal(err)
	}
	if connection, err := net.DialTimeout("tcp", licenseAddress, 100*time.Millisecond); err == nil {
		connection.Close()
		t.Fatal("the tagged admin host unexpectedly owned the license-service listener")
	}

	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	client := &http.Client{Jar: jar, Timeout: 5 * time.Second}
	adminOrigin := "http://" + adminAddress
	bootstrap, err := client.Get(adminOrigin + "/admin/api/session/bootstrap")
	if err != nil {
		t.Fatal(err)
	}
	defer bootstrap.Body.Close()
	if bootstrap.StatusCode != http.StatusOK {
		t.Fatalf("bootstrap status = %d", bootstrap.StatusCode)
	}
	var bootstrapBody struct {
		CSRFToken string `json:"csrf_token"`
	}
	if err := json.NewDecoder(bootstrap.Body).Decode(&bootstrapBody); err != nil {
		t.Fatal(err)
	}
	loginBody, err := json.Marshal(map[string]string{"username": "admin", "password": strings.TrimSpace(string(passwordBytes))})
	if err != nil {
		t.Fatal(err)
	}
	request, err := http.NewRequest(http.MethodPost, adminOrigin+"/admin/api/session/login", bytes.NewReader(loginBody))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("Origin", adminOrigin)
	request.Header.Set("X-CSRF-Token", bootstrapBody.CSRFToken)
	login, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer login.Body.Close()
	if login.StatusCode != http.StatusOK {
		t.Fatalf("browser-style local login status = %d, want %d", login.StatusCode, http.StatusOK)
	}
}

func unusedLoopbackAddress(t *testing.T) string {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	address := listener.Addr().String()
	if err := listener.Close(); err != nil {
		t.Fatal(err)
	}
	return address
}
