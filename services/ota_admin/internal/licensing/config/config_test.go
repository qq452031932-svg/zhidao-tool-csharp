package config

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestLoadAcceptsStrictLocalConfiguration(t *testing.T) {
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

func TestLoadRejectsUnknownFieldsAndMultipleDocuments(t *testing.T) {
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
		{name: "unbounded body", mutate: func(c *Config) { c.Limits.MaxBodyBytes = 0 }},
		{name: "unsafe timestamp window", mutate: func(c *Config) { c.Limits.TimestampWindowSeconds = 3601 }},
		{name: "missing activation limit", mutate: func(c *Config) { c.Limits.ActivationRequests = 0 }},
		{name: "missing validation limit", mutate: func(c *Config) { c.Limits.ValidationRequests = 0 }},
		{name: "missing admin limit", mutate: func(c *Config) { c.Limits.AdminRequests = 0 }},
		{name: "missing replay cap", mutate: func(c *Config) { c.Limits.MaxReplayNonces = 0 }},
		{name: "excessive replay cap", mutate: func(c *Config) { c.Limits.MaxReplayNonces = 1_000_001 }},
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

func TestReadSecret32RequiresExactlyRaw32Bytes(t *testing.T) {
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
	if runtime.GOOS == "windows" {
		t.Skip("Windows ACLs are not represented by Unix mode bits")
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
		ListenAddress:         "127.0.0.1:18092",
		LicenseDatabasePath:   filepath.Join(dir, "license.db"),
		EncryptionKeyPath:     filepath.Join(dir, "encryption.key"),
		LookupHMACKeyPath:     filepath.Join(dir, "lookup-hmac.key"),
		SigningPrivateKeyPath: filepath.Join(dir, "signing.pem"),
		InternalSecretPath:    filepath.Join(dir, "internal.key"),
		SigningKeyID:          "local-signing-1",
		PublicAPIBasePath:     "/license-api/v1",
		Limits: Limits{
			MaxBodyBytes: 8192, MaxHeaderBytes: 16384,
			TimestampWindowSeconds: 300, NonceLifetimeSeconds: 600,
			MaxReplayNonces:    10_000,
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
