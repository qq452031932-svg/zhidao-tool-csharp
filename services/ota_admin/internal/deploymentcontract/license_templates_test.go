package deploymentcontract

import (
	"encoding/json"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"strings"
	"testing"

	otaconfig "fanfh.cn/superpan/ota-admin/internal/config"
)

func moduleRoot(t *testing.T) string {
	t.Helper()
	d, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for {
		if _, err := os.Stat(filepath.Join(d, "go.mod")); err == nil {
			return d
		}
		parent := filepath.Dir(d)
		if parent == d {
			t.Fatal("Go module root not found")
		}
		d = parent
	}
}

func repositoryRoot(t *testing.T) string {
	t.Helper()
	root := filepath.Clean(filepath.Join(moduleRoot(t), "..", ".."))
	if _, err := os.Stat(filepath.Join(root, "deploy")); err != nil {
		t.Fatalf("resolve repository root from Go module: %v", err)
	}
	return root
}

func read(t *testing.T, parts ...string) string {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(append([]string{repositoryRoot(t)}, parts...)...))
	if err != nil {
		t.Fatalf("read template: %v", err)
	}
	return strings.ReplaceAll(string(b), "\r\n", "\n")
}

func readJSON(t *testing.T, parts ...string) map[string]any {
	t.Helper()
	var value map[string]any
	if err := json.Unmarshal([]byte(read(t, parts...)), &value); err != nil {
		t.Fatalf("parse JSON template: %v", err)
	}
	return value
}

func uncommentedLines(body string) []string {
	var result []string
	for _, line := range strings.Split(body, "\n") {
		line = strings.TrimSpace(line)
		if line != "" && !strings.HasPrefix(line, "#") {
			result = append(result, line)
		}
	}
	return result
}

func requireExactLine(t *testing.T, body, line string) {
	t.Helper()
	if hasExactLine(body, line) {
		return
	}
	t.Errorf("template missing exact directive %q", line)
}

func hasExactLine(body, line string) bool {
	for _, got := range uncommentedLines(body) {
		if got == line {
			return true
		}
	}
	return false
}

func sectionValues(body, section, key string) []string {
	wantedSection := "[" + section + "]"
	inSection := false
	var result []string
	for _, line := range uncommentedLines(body) {
		if strings.HasPrefix(line, "[") {
			inSection = line == wantedSection
			continue
		}
		name, value, ok := strings.Cut(line, "=")
		if inSection && ok && name == key {
			result = append(result, value)
		}
	}
	return result
}

func TestLicenseAndOTAConfigsUseIsolatedLoopbackContracts(t *testing.T) {
	license := readJSON(t, "deploy", "license-service", "config.example.json")
	if got := license["listen_address"]; got != "127.0.0.1:18092" {
		t.Errorf("license listen_address = %v", got)
	}
	if got, ok := license["license_database_path"].(string); !ok || !strings.HasPrefix(got, "/var/lib/superpan-license/") {
		t.Errorf("license_database_path must be below the dedicated data root; got %v", license["license_database_path"])
	}
	wantPaths := map[string]string{
		"license_database_path":    "/var/lib/superpan-license/state.db",
		"encryption_key_path":      "/etc/superpan-license/encryption.key",
		"lookup_hmac_key_path":     "/etc/superpan-license/lookup.key",
		"signing_private_key_path": "/etc/superpan-license/response-signing.key",
		"internal_secret_path":     "/etc/superpan-license/admin.secret",
	}
	for field, want := range wantPaths {
		got, ok := license[field].(string)
		if !ok || got != want || path.Clean(got) != got {
			t.Errorf("%s = %v, want canonical path %q", field, license[field], want)
		}
	}
	keyID, _ := license["signing_key_id"].(string)
	if keyID != "__SUPERPAN_LICENSE_RESPONSE_SIGNING_KEY_ID__" {
		t.Errorf("signing_key_id must use the installer-only sentinel, got %q", keyID)
	}

	otaBody := read(t, "deploy", "ota-admin", "config.example.json")
	if _, err := otaconfig.Parse(strings.NewReader(otaBody)); err != nil {
		t.Fatalf("OTA example does not match the production schema: %v", err)
	}
	ota := readJSON(t, "deploy", "ota-admin", "config.example.json")
	if got := ota["listen_address"]; got != "127.0.0.1:18091" {
		t.Errorf("OTA listen_address = %v", got)
	}
	if got := ota["license_admin_base_url"]; got != "http://127.0.0.1:18092" {
		t.Errorf("license_admin_base_url = %v", got)
	}
	if got := ota["license_admin_secret_path"]; got != "/etc/superpan-license/admin.secret" {
		t.Errorf("license_admin_secret_path = %v", got)
	}
}

func TestServiceUnitsUseOnlyDedicatedSharedCredentialGroup(t *testing.T) {
	license := read(t, "deploy", "license-service", "superpan-license.service")
	ota := read(t, "deploy", "ota-admin", "superpan-ota-admin.service")
	for _, tc := range []struct{ name, body, user, primary, forbiddenPrimary string }{
		{"license", license, "superpan-license", "superpan-license", "superpan-ota-admin"},
		{"OTA", ota, "superpan-ota-admin", "superpan-ota-admin", "superpan-license"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			for key, want := range map[string]string{"User": tc.user, "Group": tc.primary, "SupplementaryGroups": "superpan-license-admin"} {
				values := sectionValues(tc.body, "Service", key)
				if len(values) != 1 || values[0] != want {
					t.Errorf("%s assignments = %q, want only %q", key, values, want)
				}
			}
		})
	}
	for _, want := range []string{"WorkingDirectory=/var/lib/superpan-license", "NoNewPrivileges=true", "CapabilityBoundingSet=", "ProtectSystem=full", "ReadOnlyDirectories=/etc/superpan-license", "ReadWriteDirectories=/var/lib/superpan-license"} {
		requireExactLine(t, license, want)
	}
	for _, forbidden := range []string{"/var/lib/otaadmin", "/var/lib/superpan-ota-admin", "/www/wwwroot", "User=root", "Group=root"} {
		if strings.Contains(license, forbidden) {
			t.Errorf("license unit contains forbidden identity/path %q", forbidden)
		}
	}
	if strings.Count(license, "ReadWriteDirectories=") != 1 {
		t.Errorf("license service must have exactly one writable directory declaration")
	}
	allowed := map[string]bool{
		"Type": true, "User": true, "Group": true, "SupplementaryGroups": true,
		"UMask": true, "WorkingDirectory": true, "ExecStart": true, "Restart": true,
		"RestartSec": true, "TimeoutStopSec": true, "NoNewPrivileges": true,
		"PrivateTmp": true, "PrivateDevices": true, "ProtectSystem": true,
		"ProtectHome": true, "InaccessibleDirectories": true, "ReadOnlyDirectories": true, "ReadWriteDirectories": true,
		"CapabilityBoundingSet": true, "RestrictAddressFamilies": true, "LimitNOFILE": true,
	}
	inService := false
	for _, line := range uncommentedLines(license) {
		if line == "[Service]" {
			inService = true
			continue
		}
		if strings.HasPrefix(line, "[") {
			inService = false
		}
		if inService {
			key, _, ok := strings.Cut(line, "=")
			if !ok || !allowed[key] {
				t.Errorf("license service directive is not in the reviewed v219 allow-list: %q", line)
			}
		}
	}
}

func TestLicenseACLAndSigningIdentityContractIsMachineReadable(t *testing.T) {
	unit := read(t, "deploy", "license-service", "superpan-license.service")
	wants := map[string]string{
		"/etc/superpan-license":                              "root superpan-license-admin 0710 directory",
		"/etc/superpan-license/config.json":                  "root superpan-license 0640 private",
		"/etc/superpan-license/encryption.key":               "root superpan-license 0640 private",
		"/etc/superpan-license/lookup.key":                   "root superpan-license 0640 private",
		"/etc/superpan-license/response-signing.key":         "root superpan-license 0640 private",
		"/etc/superpan-license/admin.secret":                 "root superpan-license-admin 0640 shared",
		"/etc/superpan-license/response-signing.key-id":      "root root 0644 public",
		"/etc/superpan-license/response-signing.spki.der":    "root root 0644 public",
		"/etc/superpan-license/response-signing.spki.base64": "root root 0644 public",
		"/etc/superpan-license/response-signing.spki.sha256": "root root 0644 public",
		"/var/lib/superpan-license":                          "superpan-license superpan-license 0700 directory",
		"/var/lib/superpan-license/state.db":                 "superpan-license superpan-license 0600 database",
		"/var/lib/superpan-license/state.db-wal":             "superpan-license superpan-license 0600 database",
		"/var/lib/superpan-license/state.db-shm":             "superpan-license superpan-license 0600 database",
		"/var/backups/superpan-license":                      "root root 0700 directory",
		"/var/backups/superpan-license/<snapshot>":           "root root 0700 snapshot",
	}
	got := make(map[string]string)
	for _, line := range strings.Split(unit, "\n") {
		line = strings.TrimSpace(line)
		if !strings.HasPrefix(line, "# contract-acl ") {
			continue
		}
		fields := strings.Fields(strings.TrimPrefix(line, "# contract-acl "))
		if len(fields) != 5 {
			t.Errorf("malformed ACL contract: %q", line)
			continue
		}
		if _, exists := got[fields[0]]; exists {
			t.Errorf("duplicate ACL target %q", fields[0])
			continue
		}
		got[fields[0]] = strings.Join(fields[1:], " ")
	}
	if len(got) != len(wants) {
		t.Errorf("ACL target count = %d, want %d", len(got), len(wants))
	}
	for target, want := range wants {
		if got[target] != want {
			t.Errorf("ACL contract for %q = %q, want %q", target, got[target], want)
		}
	}
	for _, want := range []string{
		"# contract-existing lstat owner group mode nlink=1 regular non-symlink bounded-size format trusted-parents",
		"# contract-atomic targets=config-public-identity creator=root destination-group 0600 same-directory O_EXCL fsync verify-descriptor-owner-group-mode-format rename-no-follow final-mode",
		"# contract-signing key-id-file=/etc/superpan-license/response-signing.key-id reject-placeholder=true curve=P-256 format=canonical-SPKI-DER base64=true sha256=lowercase prove-private-match=true",
		"# contract-render source=/etc/superpan-license/response-signing.key-id target=/etc/superpan-license/config.json field=signing_key_id atomic=true",
		"# contract-desktop-input response-signing.spki.base64 response-signing.key-id forbid-PEM-private=true",
	} {
		if strings.Count(unit, want) != 1 {
			t.Errorf("operational contract must contain exactly once: %q", want)
		}
	}
}

func TestNginxIncludeExposesExactlyTwoHardenedPublicEndpoints(t *testing.T) {
	nginx := read(t, "deploy", "license-service", "ota.fanfh.cn.license-api.nginx.conf")
	var active []string
	for _, line := range strings.Split(nginx, "\n") {
		if trimmed := strings.TrimSpace(line); trimmed != "" && !strings.HasPrefix(trimmed, "#") {
			active = append(active, trimmed)
		}
	}
	activeBody := strings.Join(active, "\n")
	locationRE := regexp.MustCompile(`(?m)^location\s+([^\s{]+(?:\s+[^\s{]+)?)\s*\{`)
	matches := locationRE.FindAllStringSubmatch(activeBody, -1)
	if len(matches) != 2 {
		t.Fatalf("location count = %d, want 2", len(matches))
	}
	wantLocations := []string{"= /license-api/v1/activate", "= /license-api/v1/validate"}
	for i, match := range matches {
		if match[1] != wantLocations[i] {
			t.Errorf("location %d = %q, want %q", i, match[1], wantLocations[i])
		}
	}
	blocks := strings.Split(activeBody, "location = ")[1:]
	for i, block := range blocks {
		for _, directive := range []string{
			"proxy_pass http://127.0.0.1:18092;", "proxy_set_header Host ota.fanfh.cn;",
			"proxy_set_header X-Forwarded-Proto https;", "proxy_set_header X-Real-IP $remote_addr;",
			"proxy_set_header X-Forwarded-For $remote_addr;", "proxy_set_header X-SuperPan-Load-Test \"\";",
			"proxy_cache off;", "proxy_request_buffering on;", "client_max_body_size 64k;",
			"proxy_connect_timeout 5s;", "proxy_read_timeout 20s;", "proxy_send_timeout 20s;",
		} {
			if strings.Count(block, directive) != 1 {
				t.Errorf("location %d must contain exactly one %q", i, directive)
			}
		}
	}
	for _, forbidden := range []string{
		"server {", "location /", "/internal/", "/health", "/ota", "/admin", "/.well-known/acme-challenge", "root ",
		"proxy_cache_path", "proxy_cache_valid", "$proxy_add_x_forwarded_for", "$http_x_forwarded_for",
	} {
		if strings.Contains(activeBody, forbidden) {
			t.Errorf("nginx include contains forbidden route/directive %q", forbidden)
		}
	}
}
