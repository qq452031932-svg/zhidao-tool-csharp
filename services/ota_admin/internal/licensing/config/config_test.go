package config

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"strings"
	"testing"
)

func TestLoadAcceptsStrictLocalConfiguration(t *testing.T) {
	if runtime.GOOS == "linux" {
		t.Skip("Linux Load acceptance requires installed production ownership")
	}
	cfg := validConfig(t)
	path := writeConfig(t, cfg)
	loaded, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if loaded.ListenAddress != cfg.ListenAddress || loaded.PublicAPIBasePath != "/license-api/v1" {
		t.Fatalf("loaded config = %#v", loaded)
	}
}

func TestParseAndLoadShareStrictJSONBehavior(t *testing.T) {
	cfg := validConfig(t)
	encoded, err := json.Marshal(cfg)
	if err != nil {
		t.Fatal(err)
	}
	parsed, err := Parse(encoded)
	if err != nil {
		t.Fatalf("Parse rejected valid config: %v", err)
	}
	if !reflect.DeepEqual(parsed, cfg) {
		t.Fatalf("Parse result = %#v, want %#v", parsed, cfg)
	}
	mutations := []struct {
		name string
		body []byte
	}{
		{"unknown", append(bytes.TrimSuffix(encoded, []byte("}")), []byte(`,"unexpected":true}`)...)},
		{"duplicate", bytes.Replace(encoded, []byte(`"listen_address":`), []byte(`"listen_address":"127.0.0.1:18092","listen_address":`), 1)},
		{"trailing", append(append([]byte(nil), encoded...), []byte(` {}`)...)},
	}
	for _, mutation := range mutations {
		t.Run(mutation.name, func(t *testing.T) {
			if _, err := Parse(mutation.body); err == nil {
				t.Fatal("Parse accepted invalid JSON structure")
			}
			if runtime.GOOS == "linux" {
				return
			}
			path := filepath.Join(t.TempDir(), mutation.name+".json")
			if err := os.WriteFile(path, mutation.body, 0o600); err != nil {
				t.Fatal(err)
			}
			if _, err := Load(path); err == nil {
				t.Fatal("Load accepted JSON structure rejected by Parse")
			}
		})
	}
}

func TestLoadRejectsUnknownFieldsAndMultipleDocuments(t *testing.T) {
	if runtime.GOOS == "linux" {
		t.Skip("Linux Load requires installed production ownership")
	}
	cfg := validConfig(t)
	encoded, err := json.Marshal(cfg)
	if err != nil {
		t.Fatal(err)
	}
	withUnknown := bytes.TrimSuffix(encoded, []byte("}"))
	withUnknown = append(withUnknown, []byte(`,"unexpected":true}`)...)
	path := filepath.Join(t.TempDir(), "unknown.json")
	if err := os.WriteFile(path, withUnknown, 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err == nil {
		t.Fatal("Load accepted an unknown field")
	}

	multiple := filepath.Join(t.TempDir(), "multiple.json")
	if err := os.WriteFile(multiple, append(append(encoded, '\n'), encoded...), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(multiple); err == nil {
		t.Fatal("Load accepted multiple JSON values")
	}
}

func TestLoadRejectsDuplicateAndCaseVariantKeysAtEveryLevel(t *testing.T) {
	if runtime.GOOS == "linux" {
		t.Skip("Linux Load requires installed production ownership")
	}
	encoded, err := json.Marshal(validConfig(t))
	if err != nil {
		t.Fatal(err)
	}
	tests := [][]byte{
		bytes.Replace(encoded, []byte(`"listen_address":"127.0.0.1:18092"`), []byte(`"LISTEN_ADDRESS":"127.0.0.1:18092"`), 1),
		bytes.Replace(encoded, []byte(`"listen_address":"127.0.0.1:18092"`), []byte(`"listen_address":"127.0.0.1:18092","listen_address":"127.0.0.1:18092"`), 1),
		bytes.Replace(encoded, []byte(`"max_body_bytes":8192`), []byte(`"MAX_BODY_BYTES":8192`), 1),
		bytes.Replace(encoded, []byte(`"max_body_bytes":8192`), []byte(`"max_body_bytes":8192,"max_body_bytes":8192`), 1),
	}
	for index, data := range tests {
		path := filepath.Join(t.TempDir(), fmt.Sprintf("strict-%d.json", index))
		if err := os.WriteFile(path, data, 0o600); err != nil {
			t.Fatal(err)
		}
		if _, err := Load(path); err == nil {
			t.Fatalf("Load accepted ambiguous config %s", data)
		}
	}
}

func TestValidateRejectsRemoteRelativeAndOverlappingPaths(t *testing.T) {
	tests := []struct {
		name   string
		mutate func(*Config)
	}{
		{name: "remote listen", mutate: func(c *Config) { c.ListenAddress = "0.0.0.0:18092" }},
		{name: "hostname listen", mutate: func(c *Config) { c.ListenAddress = "localhost:18092" }},
		{name: "wrong base path", mutate: func(c *Config) { c.PublicAPIBasePath = "/api/v1" }},
		{name: "relative database", mutate: func(c *Config) { c.LicenseDatabasePath = "license.db" }},
		{name: "relative secret", mutate: func(c *Config) { c.InternalSecretPath = "internal.key" }},
		{name: "shared secrets", mutate: func(c *Config) { c.InternalSecretPath = c.LookupHMACKeyPath }},
		{name: "database is encryption key", mutate: func(c *Config) { c.LicenseDatabasePath = c.EncryptionKeyPath }},
		{name: "database contains secret path", mutate: func(c *Config) { c.LicenseDatabasePath = filepath.Dir(c.EncryptionKeyPath) }},
		{name: "missing key id", mutate: func(c *Config) { c.SigningKeyID = "" }},
		{name: "load secret while disabled", mutate: func(c *Config) { c.LoadTestSecretPath = filepath.Join(filepath.Dir(c.EncryptionKeyPath), "load.key") }},
		{name: "load mode without secret", mutate: func(c *Config) { c.LoadTestEnabled = true }},
		{name: "load secret overlaps encryption", mutate: func(c *Config) { c.LoadTestEnabled = true; c.LoadTestSecretPath = c.EncryptionKeyPath }},
		{name: "load secret overlaps lookup", mutate: func(c *Config) { c.LoadTestEnabled = true; c.LoadTestSecretPath = c.LookupHMACKeyPath }},
		{name: "load secret overlaps signing", mutate: func(c *Config) { c.LoadTestEnabled = true; c.LoadTestSecretPath = c.SigningPrivateKeyPath }},
		{name: "load secret overlaps admin", mutate: func(c *Config) { c.LoadTestEnabled = true; c.LoadTestSecretPath = c.InternalSecretPath }},
		{name: "unbounded body", mutate: func(c *Config) { c.Limits.MaxBodyBytes = 0 }},
		{name: "unsafe timestamp window", mutate: func(c *Config) { c.Limits.TimestampWindowSeconds = 3601 }},
		{name: "missing activation limit", mutate: func(c *Config) { c.Limits.ActivationRequests = 0 }},
		{name: "missing validation limit", mutate: func(c *Config) { c.Limits.ValidationRequests = 0 }},
		{name: "missing admin limit", mutate: func(c *Config) { c.Limits.AdminRequests = 0 }},
		{name: "missing replay cap", mutate: func(c *Config) { c.Limits.MaxReplayNonces = 0 }},
		{name: "excessive replay cap", mutate: func(c *Config) { c.Limits.MaxReplayNonces = 1_000_001 }},
		{name: "missing admin idempotency entries", mutate: func(c *Config) { c.Limits.MaxAdminIdempotencyEntries = 0 }},
		{name: "missing admin idempotency bytes", mutate: func(c *Config) { c.Limits.MaxAdminIdempotencyBytes = 0 }},
		{name: "too many previous encryption keys", mutate: func(c *Config) {
			c.PreviousEncryptionKeyPaths = []string{filepath.Join(filepath.Dir(c.EncryptionKeyPath), "old1.key"), filepath.Join(filepath.Dir(c.EncryptionKeyPath), "old2.key"), filepath.Join(filepath.Dir(c.EncryptionKeyPath), "old3.key")}
		}},
		{name: "previous key overlaps current", mutate: func(c *Config) { c.PreviousEncryptionKeyPaths = []string{c.EncryptionKeyPath} }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := validConfig(t)
			tt.mutate(&cfg)
			if err := cfg.Validate(); err == nil {
				t.Fatal("Validate error = nil, want rejection")
			}
		})
	}
}

func TestValidateAcceptsDistinctExplicitLoadTestSecret(t *testing.T) {
	cfg := validConfig(t)
	cfg.LoadTestEnabled = true
	cfg.LoadTestSecretPath = filepath.Join(filepath.Dir(cfg.EncryptionKeyPath), "load-test.key")
	if err := cfg.Validate(); err != nil {
		t.Fatalf("Validate rejected explicit load mode: %v", err)
	}
}

func TestValidateRejectsWindowsMixedSeparatorPOSIXPath(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("Windows path canonicalization")
	}
	cfg := validConfig(t)
	cfg.InternalSecretPath = `/etc/superpan-license/foo\..\admin.secret`
	if err := cfg.Validate(); err == nil {
		t.Fatal("Validate accepted a POSIX-looking path with Windows traversal separators")
	}
}

func TestTrustedReaderLinuxRejectsUnsafeFilesystemObjects(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("Linux descriptor trust boundary")
	}
	if os.Geteuid() != 0 {
		t.Skip("ownership fixtures require root")
	}
	root, err := os.MkdirTemp("/root", "license-trust-")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(root) })
	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatal(err)
	}
	approvedGID := os.Getegid()
	policy := trustedFilePolicy{allowedGIDs: map[uint32]struct{}{uint32(approvedGID): {}}, exactSize: 32, maximumSize: 32, allowedModes: map[os.FileMode]struct{}{0o600: {}, 0o640: {}}}
	write := func(name string, mode os.FileMode) string {
		path := filepath.Join(root, name)
		if err := os.WriteFile(path, bytes.Repeat([]byte{0x41}, 32), mode); err != nil {
			t.Fatal(err)
		}
		if err := os.Chown(path, 0, approvedGID); err != nil {
			t.Fatal(err)
		}
		return path
	}
	valid := write("valid.key", 0o640)
	if _, err := readTrustedFile(valid, policy, nil); err != nil {
		t.Fatalf("trusted file rejected: %v", err)
	}

	t.Run("symlink component", func(t *testing.T) {
		realDir := filepath.Join(root, "real")
		if err := os.Mkdir(realDir, 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.Chown(realDir, 0, approvedGID); err != nil {
			t.Fatal(err)
		}
		target := filepath.Join(realDir, "key")
		if err := os.WriteFile(target, bytes.Repeat([]byte{1}, 32), 0o600); err != nil {
			t.Fatal(err)
		}
		if err := os.Chown(target, 0, approvedGID); err != nil {
			t.Fatal(err)
		}
		linkDir := filepath.Join(root, "link")
		if err := os.Symlink(realDir, linkDir); err != nil {
			t.Fatal(err)
		}
		if _, err := readTrustedFile(filepath.Join(linkDir, "key"), policy, nil); err == nil {
			t.Fatal("accepted symlink path component")
		}
	})

	t.Run("owner group mode hardlink size and device", func(t *testing.T) {
		wrongOwner := write("wrong-owner", 0o600)
		if err := os.Chown(wrongOwner, 1, approvedGID); err != nil {
			t.Fatal(err)
		}
		if _, err := readTrustedFile(wrongOwner, policy, nil); err == nil {
			t.Fatal("accepted non-root owner")
		}
		wrongGroup := write("wrong-group", 0o600)
		if err := os.Chown(wrongGroup, 0, approvedGID+1); err != nil {
			t.Fatal(err)
		}
		if _, err := readTrustedFile(wrongGroup, policy, nil); err == nil {
			t.Fatal("accepted untrusted group")
		}
		wrongMode := write("wrong-mode", 0o644)
		if _, err := readTrustedFile(wrongMode, policy, nil); err == nil {
			t.Fatal("accepted wrong mode")
		}
		wrongSize := filepath.Join(root, "wrong-size")
		if err := os.WriteFile(wrongSize, bytes.Repeat([]byte{2}, 31), 0o600); err != nil {
			t.Fatal(err)
		}
		if err := os.Chown(wrongSize, 0, approvedGID); err != nil {
			t.Fatal(err)
		}
		if _, err := readTrustedFile(wrongSize, policy, nil); err == nil {
			t.Fatal("accepted wrong size")
		}
		hardlink := write("hardlink-source", 0o600)
		if err := os.Link(hardlink, filepath.Join(root, "hardlink-alias")); err != nil {
			t.Fatal(err)
		}
		if _, err := readTrustedFile(hardlink, policy, nil); err == nil {
			t.Fatal("accepted multiply-linked file")
		}
		if _, err := readTrustedFile("/dev/null", trustedFilePolicy{allowedGIDs: map[uint32]struct{}{0: {}}, maximumSize: 32, allowedModes: map[os.FileMode]struct{}{0o666: {}}}, nil); err == nil {
			t.Fatal("accepted device file")
		}
	})

	t.Run("writable parent and replacement", func(t *testing.T) {
		for _, mode := range []os.FileMode{0o770, 0o707} {
			unsafeDir := filepath.Join(root, fmt.Sprintf("unsafe-%o", mode))
			if err := os.Mkdir(unsafeDir, mode); err != nil {
				t.Fatal(err)
			}
			if err := os.Chmod(unsafeDir, mode); err != nil {
				t.Fatal(err)
			}
			if err := os.Chown(unsafeDir, 0, approvedGID); err != nil {
				t.Fatal(err)
			}
			unsafeFile := filepath.Join(unsafeDir, "key")
			if err := os.WriteFile(unsafeFile, bytes.Repeat([]byte{3}, 32), 0o600); err != nil {
				t.Fatal(err)
			}
			if err := os.Chown(unsafeFile, 0, approvedGID); err != nil {
				t.Fatal(err)
			}
			if _, err := readTrustedFile(unsafeFile, policy, nil); err == nil {
				t.Fatalf("accepted writable parent mode %o", mode)
			}
		}

		original := write("replace.key", 0o600)
		replacement := write("replacement.key", 0o600)
		_, err := readTrustedFile(original, policy, func() {
			if renameErr := os.Rename(replacement, original); renameErr != nil {
				t.Fatal(renameErr)
			}
		})
		if err == nil {
			t.Fatal("accepted path replacement during open")
		}
	})
}

func TestSharedSecretPolicyRequiresExactlyOneSupplementaryGroup(t *testing.T) {
	for _, groups := range [][]int{nil, {}, {1, 2}, {-1}} {
		if _, err := sharedFilePolicy(groups, 32, 32); err == nil {
			t.Fatalf("sharedFilePolicy(%v) accepted ambiguous groups", groups)
		}
	}
	policy, err := sharedFilePolicy([]int{4321}, 32, 32)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := policy.allowedGIDs[4321]; !ok || len(policy.allowedGIDs) != 1 {
		t.Fatalf("allowed groups = %v", policy.allowedGIDs)
	}
}

func TestReadSecret32RequiresExactlyRaw32Bytes(t *testing.T) {
	if runtime.GOOS == "linux" {
		t.Skip("Linux acceptance is covered by the root-owned trusted reader fixture")
	}
	dir := t.TempDir()
	valid := filepath.Join(dir, "valid.key")
	if err := os.WriteFile(valid, bytes.Repeat([]byte{0x31}, 32), 0o600); err != nil {
		t.Fatal(err)
	}
	secret, err := ReadSecret32(valid)
	if err != nil {
		t.Fatal(err)
	}
	if len(secret) != 32 {
		t.Fatalf("secret length = %d", len(secret))
	}
	for _, suffix := range [][]byte{{'\n'}, {0x31}} {
		path := filepath.Join(dir, "invalid-"+strings.TrimSpace(string(suffix))+".key")
		if err := os.WriteFile(path, append(bytes.Repeat([]byte{0x31}, 32), suffix...), 0o600); err != nil {
			t.Fatal(err)
		}
		if _, err := ReadSecret32(path); err == nil {
			t.Fatal("ReadSecret32 accepted a file other than exactly 32 raw bytes")
		}
	}
}

func TestReadSecret32UnixPermissionsAllowOnly0600Or0640(t *testing.T) {
	if runtime.GOOS == "windows" || runtime.GOOS == "linux" {
		t.Skip("platform uses a separate trusted-file policy")
	}
	for _, mode := range []os.FileMode{0o600, 0o640, 0o400, 0o440, 0o700, 0o650, 0o660, 0o604, 0o644} {
		path := filepath.Join(t.TempDir(), fmt.Sprintf("secret-%o", mode))
		if err := os.WriteFile(path, bytes.Repeat([]byte{0x42}, 32), mode); err != nil {
			t.Fatal(err)
		}
		_, err := ReadSecret32(path)
		wantOK := mode == 0o600 || mode == 0o640
		if (err == nil) != wantOK {
			t.Fatalf("mode %o error = %v, wantOK=%v", mode, err, wantOK)
		}
	}
}

func validConfig(t *testing.T) Config {
	t.Helper()
	dir, err := filepath.Abs(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	return Config{
		ListenAddress:              "127.0.0.1:18092",
		LicenseDatabasePath:        filepath.Join(dir, "license.db"),
		EncryptionKeyPath:          filepath.Join(dir, "encryption.key"),
		PreviousEncryptionKeyPaths: []string{filepath.Join(dir, "previous-encryption.key")},
		LookupHMACKeyPath:          filepath.Join(dir, "lookup-hmac.key"),
		SigningPrivateKeyPath:      filepath.Join(dir, "signing.pem"),
		InternalSecretPath:         filepath.Join(dir, "internal.key"),
		SigningKeyID:               "local-signing-1",
		PublicAPIBasePath:          "/license-api/v1",
		Limits: Limits{
			MaxBodyBytes: 8192, MaxHeaderBytes: 16384,
			TimestampWindowSeconds: 300, NonceLifetimeSeconds: 600,
			MaxReplayNonces:            10_000,
			MaxAdminIdempotencyEntries: 1000, MaxAdminIdempotencyBytes: 16 << 20,
			ActivationRequests: 10, ValidationRequests: 60, AdminRequests: 30, RateWindowSeconds: 60,
			ReadHeaderTimeoutSeconds: 5, ReadTimeoutSeconds: 10, WriteTimeoutSeconds: 10,
			IdleTimeoutSeconds: 30, ShutdownTimeoutSeconds: 10,
		},
	}
}

func writeConfig(t *testing.T, cfg Config) string {
	t.Helper()
	encoded, err := json.Marshal(cfg)
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "license-service.json")
	if err := os.WriteFile(path, encoded, 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}
