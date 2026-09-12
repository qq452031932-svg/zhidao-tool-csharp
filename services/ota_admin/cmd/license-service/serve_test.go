package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"go/parser"
	"go/token"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/authwire"
	licenseconfig "fanfh.cn/superpan/ota-admin/internal/licensing/config"
	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
)

func TestRunRequiresExplicitStrictConfigWithoutLeakingPath(t *testing.T) {
	var stdout, stderr bytes.Buffer
	if code := run(nil, &stdout, &stderr); code != 2 {
		t.Fatalf("run without arguments = %d, want 2", code)
	}
	secretPath := filepath.Join(t.TempDir(), "private-config-name.json")
	stderr.Reset()
	if code := run([]string{"serve", "--config", secretPath}, &stdout, &stderr); code != 1 {
		t.Fatalf("run invalid config = %d, want 1", code)
	}
	if strings.Contains(stderr.String(), secretPath) || strings.Contains(stderr.String(), "private-config-name") {
		t.Fatalf("stderr leaked config path: %s", stderr.String())
	}
}

func TestRunCheckConfigUsesExactProductionContractWithoutStartingServer(t *testing.T) {
	cfg := productionLicenseConfigForTest()
	path := filepath.Join(t.TempDir(), "production.json")
	body, err := json.Marshal(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, body, 0o600); err != nil {
		t.Fatal(err)
	}
	var stdout, stderr bytes.Buffer
	if code := run([]string{"check-config", "--config", path}, &stdout, &stderr); code != 0 {
		t.Fatalf("check-config code=%d stderr=%q", code, stderr.String())
	}
	if stdout.String() != "license-service: production configuration valid\n" {
		t.Fatalf("check-config stdout=%q", stdout.String())
	}

	cfg.ListenAddress = "127.0.0.1:19092"
	body, err = json.Marshal(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, body, 0o600); err != nil {
		t.Fatal(err)
	}
	stdout.Reset()
	stderr.Reset()
	if code := run([]string{"check-config", "--config", path}, &stdout, &stderr); code != 1 {
		t.Fatalf("non-production check-config code=%d", code)
	}
	if strings.Contains(stderr.String(), path) {
		t.Fatalf("check-config leaked path: %q", stderr.String())
	}
}

func productionLicenseConfigForTest() licenseconfig.Config {
	return licenseconfig.Config{
		ListenAddress: "127.0.0.1:18092", LicenseDatabasePath: "/var/lib/superpan-license/state.db",
		EncryptionKeyPath: "/etc/superpan-license/encryption.key", PreviousEncryptionKeyPaths: []string{},
		LookupHMACKeyPath: "/etc/superpan-license/lookup.key", SigningPrivateKeyPath: "/etc/superpan-license/response-signing.key",
		InternalSecretPath: "/etc/superpan-license/admin.secret", LoadTestEnabled: false, LoadTestSecretPath: "",
		SigningKeyID: "license-signing-0123456789abcdef0123456789abcdef", PublicAPIBasePath: "/license-api/v1",
		Limits: licenseconfig.Limits{MaxBodyBytes: 65536, MaxHeaderBytes: 16384, TimestampWindowSeconds: 300, NonceLifetimeSeconds: 600, MaxReplayNonces: 4096, MaxAdminIdempotencyEntries: 1024, MaxAdminIdempotencyBytes: 10485760, ActivationRequests: 30, ValidationRequests: 60, AdminRequests: 120, RateWindowSeconds: 60, ReadHeaderTimeoutSeconds: 5, ReadTimeoutSeconds: 15, WriteTimeoutSeconds: 15, IdleTimeoutSeconds: 60, ShutdownTimeoutSeconds: 30},
	}
}

func TestServeBindsLoopbackServesSignedAPIAndShutsDown(t *testing.T) {
	cfg := writeRuntimeConfig(t)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	var stdout, stderr lockedBuffer
	done := make(chan error, 1)
	go func() { done <- serve(ctx, cfg, &stdout, &stderr) }()

	client := &http.Client{Timeout: 250 * time.Millisecond}
	baseURL := "http://" + cfg.ListenAddress
	var response *http.Response
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		body := strings.NewReader(`{"protocol_version":1,"activation_request_id":"serve-request","card":"AAAAAAAA-AAAAAAAA-AAAAAAAA-AAAAAAAA","software_id":"software","machine_hash":"machine","timestamp":"` + time.Now().UTC().Format(time.RFC3339) + `","nonce":"nonce_00000000000000000000000001"}`)
		request, err := http.NewRequest(http.MethodPost, baseURL+"/license-api/v1/activate", body)
		if err != nil {
			t.Fatal(err)
		}
		request.Header.Set("Content-Type", "application/json")
		response, err = client.Do(request)
		if err == nil {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if response == nil {
		cancel()
		t.Fatalf("server did not start; stderr=%s", stderr.String())
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusForbidden {
		t.Fatalf("activation status = %d, want 403", response.StatusCode)
	}
	var envelope model.SignedEnvelope
	if err := json.NewDecoder(response.Body).Decode(&envelope); err != nil {
		t.Fatal(err)
	}
	if err := envelope.Validate(); err != nil {
		t.Fatalf("invalid signed envelope: %v", err)
	}

	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("serve shutdown: %v", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("serve did not shut down promptly")
	}
	if !strings.Contains(stdout.String(), "loopback") {
		t.Fatalf("startup output = %q", stdout.String())
	}
	assertNoRuntimeSecrets(t, stdout.String()+stderr.String(), cfg)
}

func TestServeRejectsNonLoopbackBeforeOpeningState(t *testing.T) {
	cfg := writeRuntimeConfig(t)
	cfg.ListenAddress = "0.0.0.0:18092"
	if err := serve(context.Background(), cfg, &bytes.Buffer{}, &bytes.Buffer{}); err == nil {
		t.Fatal("serve accepted a non-loopback listener")
	}
	if _, err := os.Stat(cfg.LicenseDatabasePath); !os.IsNotExist(err) {
		t.Fatalf("database was opened before config rejection: %v", err)
	}
}

func TestServeWiresLoadCredentialHealthAndMetricsWithoutLoggingSecrets(t *testing.T) {
	cfg := writeRuntimeConfig(t)
	cfg.LoadTestEnabled = true
	cfg.LoadTestSecretPath = filepath.Join(filepath.Dir(cfg.EncryptionKeyPath), "load-test.key")
	loadSecret := bytes.Repeat([]byte{0x79}, 32)
	if err := os.WriteFile(cfg.LoadTestSecretPath, loadSecret, 0o600); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	var stdout, stderr lockedBuffer
	done := make(chan error, 1)
	go func() { done <- serve(ctx, cfg, &stdout, &stderr) }()
	client := &http.Client{Timeout: 250 * time.Millisecond}
	baseURL := "http://" + cfg.ListenAddress
	var response *http.Response
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		request, err := http.NewRequest(http.MethodGet, baseURL+"/internal/load-metrics", nil)
		if err != nil {
			t.Fatal(err)
		}
		request.Header.Set("X-SuperPan-Load-Test", authwire.EncodeInternalToken(loadSecret))
		response, err = client.Do(request)
		if err == nil {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if response == nil {
		cancel()
		t.Fatalf("server did not start; stderr=%s", stderr.String())
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("metrics status = %d", response.StatusCode)
	}
	var metrics licensestore.Metrics
	if err := json.NewDecoder(response.Body).Decode(&metrics); err != nil {
		t.Fatal(err)
	}
	if metrics != (licensestore.Metrics{}) {
		t.Fatalf("initial metrics = %+v", metrics)
	}

	health, err := client.Get(baseURL + "/internal/health")
	if err != nil {
		t.Fatal(err)
	}
	health.Body.Close()
	if health.StatusCode != http.StatusOK {
		t.Fatalf("health status = %d", health.StatusCode)
	}
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("serve did not stop")
	}
	output := stdout.String() + stderr.String()
	if strings.Contains(output, cfg.LoadTestSecretPath) || strings.Contains(output, string(loadSecret)) || strings.Contains(output, authwire.EncodeInternalToken(loadSecret)) {
		t.Fatalf("load secret leaked: %s", output)
	}
}

func TestServeRejectsLoadSecretContentReuseBeforeListenerAndDatabase(t *testing.T) {
	const wantError = "load-test secret must be independent"
	tests := []struct {
		name   string
		mutate func(*testing.T, *licenseconfig.Config, []byte)
	}{
		{name: "current encryption", mutate: func(t *testing.T, cfg *licenseconfig.Config, secret []byte) {
			if err := os.WriteFile(cfg.EncryptionKeyPath, secret, 0o600); err != nil {
				t.Fatal(err)
			}
		}},
		{name: "previous encryption", mutate: func(t *testing.T, cfg *licenseconfig.Config, secret []byte) {
			path := filepath.Join(filepath.Dir(cfg.EncryptionKeyPath), "previous-encryption.key")
			if err := os.WriteFile(path, secret, 0o600); err != nil {
				t.Fatal(err)
			}
			cfg.PreviousEncryptionKeyPaths = []string{path}
		}},
		{name: "lookup hmac", mutate: func(t *testing.T, cfg *licenseconfig.Config, secret []byte) {
			if err := os.WriteFile(cfg.LookupHMACKeyPath, secret, 0o600); err != nil {
				t.Fatal(err)
			}
		}},
		{name: "admin shared", mutate: func(t *testing.T, cfg *licenseconfig.Config, secret []byte) {
			if err := os.WriteFile(cfg.InternalSecretPath, secret, 0o600); err != nil {
				t.Fatal(err)
			}
		}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := writeRuntimeConfig(t)
			cfg.LoadTestEnabled = true
			cfg.LoadTestSecretPath = filepath.Join(filepath.Dir(cfg.EncryptionKeyPath), "load-test.key")
			loadSecret := bytes.Repeat([]byte{0x6d}, 32)
			if err := os.WriteFile(cfg.LoadTestSecretPath, loadSecret, 0o600); err != nil {
				t.Fatal(err)
			}
			tt.mutate(t, &cfg, loadSecret)

			blocker, err := net.Listen("tcp", cfg.ListenAddress)
			if err != nil {
				t.Fatal(err)
			}
			defer blocker.Close()
			var stdout, stderr bytes.Buffer
			err = serve(context.Background(), cfg, &stdout, &stderr)
			if err == nil || err.Error() != wantError {
				t.Fatalf("serve error = %v, want %q", err, wantError)
			}
			if _, statErr := os.Stat(cfg.LicenseDatabasePath); !os.IsNotExist(statErr) {
				t.Fatalf("database was opened before independence rejection: %v", statErr)
			}
			output := stdout.String() + stderr.String() + err.Error()
			if strings.Contains(output, string(loadSecret)) || strings.Contains(output, authwire.EncodeInternalToken(loadSecret)) || strings.Contains(output, cfg.LoadTestSecretPath) {
				t.Fatalf("independence rejection leaked load secret: %s", output)
			}
		})
	}
}

func TestClearPreviousEncryptionKeysZeroesEveryOriginalByte(t *testing.T) {
	keys := [][]byte{bytes.Repeat([]byte{0x51}, 32), bytes.Repeat([]byte{0xa7}, 32)}
	clearPreviousEncryptionKeys(keys)
	for keyIndex, key := range keys {
		for byteIndex, value := range key {
			if value != 0 {
				t.Fatalf("key %d byte %d was not cleared: %x", keyIndex, byteIndex, value)
			}
		}
	}
}

func TestLicenseServiceImportBoundary(t *testing.T) {
	moduleRoot, err := filepath.Abs(filepath.Join("..", ".."))
	if err != nil {
		t.Fatal(err)
	}
	forbidden := []string{
		"fanfh.cn/superpan/ota-admin/internal/candidate",
		"fanfh.cn/superpan/ota-admin/internal/deploy",
		"fanfh.cn/superpan/ota-admin/internal/publisher",
		"fanfh.cn/superpan/ota-admin/internal/release",
		"fanfh.cn/superpan/ota-admin/internal/upload",
		"fanfh.cn/superpan/ota-admin/internal/operation",
		"fanfh.cn/superpan/ota-admin/internal/session",
		"fanfh.cn/superpan/ota-admin/internal/store",
	}
	roots := []string{filepath.Join(moduleRoot, "cmd", "license-service"), filepath.Join(moduleRoot, "internal", "licensing")}
	for _, root := range roots {
		err := filepath.WalkDir(root, func(path string, entry os.DirEntry, walkErr error) error {
			if walkErr != nil {
				return walkErr
			}
			if entry.IsDir() || filepath.Ext(path) != ".go" || strings.HasSuffix(path, "_test.go") {
				return nil
			}
			file, err := parser.ParseFile(token.NewFileSet(), path, nil, parser.ImportsOnly)
			if err != nil {
				return err
			}
			for _, imported := range file.Imports {
				value, err := strconv.Unquote(imported.Path.Value)
				if err != nil {
					return err
				}
				for _, denied := range forbidden {
					if value == denied || strings.HasPrefix(value, denied+"/") {
						t.Errorf("%s imports forbidden OTA package %s", path, value)
					}
				}
			}
			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}
}

type lockedBuffer struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (b *lockedBuffer) Write(data []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.buf.Write(data)
}
func (b *lockedBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.buf.String()
}

func writeRuntimeConfig(t *testing.T) licenseconfig.Config {
	t.Helper()
	dir := t.TempDir()
	address := unusedLoopbackAddress(t)
	cfg := licenseconfig.Config{
		ListenAddress: address, LicenseDatabasePath: filepath.Join(dir, "license.db"),
		EncryptionKeyPath: filepath.Join(dir, "encryption.key"), LookupHMACKeyPath: filepath.Join(dir, "hmac.key"),
		SigningPrivateKeyPath: filepath.Join(dir, "signing.pem"), InternalSecretPath: filepath.Join(dir, "internal.key"),
		SigningKeyID: "serve-key", PublicAPIBasePath: "/license-api/v1",
		Limits: licenseconfig.Limits{MaxBodyBytes: 8192, MaxHeaderBytes: 16384, TimestampWindowSeconds: 300, NonceLifetimeSeconds: 600, MaxReplayNonces: 10_000, MaxAdminIdempotencyEntries: 1000, MaxAdminIdempotencyBytes: 16 << 20, ActivationRequests: 20, ValidationRequests: 60, AdminRequests: 30, RateWindowSeconds: 60, ReadHeaderTimeoutSeconds: 2, ReadTimeoutSeconds: 5, WriteTimeoutSeconds: 5, IdleTimeoutSeconds: 10, ShutdownTimeoutSeconds: 2},
	}
	for index, path := range []string{cfg.EncryptionKeyPath, cfg.LookupHMACKeyPath, cfg.InternalSecretPath} {
		if err := os.WriteFile(path, bytes.Repeat([]byte{byte(0x41 + index)}, 32), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	der, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(cfg.SigningPrivateKeyPath, pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: der}), 0o600); err != nil {
		t.Fatal(err)
	}
	return cfg
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

func assertNoRuntimeSecrets(t *testing.T, output string, cfg licenseconfig.Config) {
	t.Helper()
	for _, secret := range []string{cfg.LicenseDatabasePath, cfg.EncryptionKeyPath, cfg.LookupHMACKeyPath, cfg.SigningPrivateKeyPath, cfg.InternalSecretPath, strings.Repeat("A", 32)} {
		if strings.Contains(output, secret) {
			t.Fatalf("runtime output leaked %q: %s", secret, output)
		}
	}
}
