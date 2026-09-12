package deploymentcontract

import (
	"encoding/json"
	"fmt"
	"os"
	"path"
	"path/filepath"
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

func serviceDirectives(body string) (map[string][]string, error) {
	values := make(map[string][]string)
	inService := false
	for _, line := range uncommentedLines(body) {
		if strings.HasPrefix(line, ".include") {
			return nil, fmt.Errorf("systemd .include is forbidden in the deployment template")
		}
		if strings.HasPrefix(line, "[") {
			inService = line == "[Service]"
			continue
		}
		if !inService {
			continue
		}
		key, value, ok := strings.Cut(line, "=")
		if !ok || key == "" {
			return nil, fmt.Errorf("malformed [Service] directive %q", line)
		}
		values[key] = append(values[key], value)
	}
	return values, nil
}

func validateLicenseServiceUnit(body string) error {
	wants := map[string]string{
		"Type": "simple", "User": "superpan-license", "Group": "superpan-license",
		"SupplementaryGroups": "superpan-license-admin", "UMask": "0077",
		"WorkingDirectory": "/var/lib/superpan-license",
		"ExecStart":        "/opt/superpan-license/superpan-license serve --config /etc/superpan-license/config.json",
		"Restart":          "on-failure", "RestartSec": "5s", "TimeoutStopSec": "30s",
		"NoNewPrivileges": "true", "PrivateTmp": "true", "PrivateDevices": "true",
		"ProtectSystem": "full", "ProtectHome": "true", "InaccessibleDirectories": "/var/www",
		"ReadOnlyDirectories": "/", "ReadWriteDirectories": "/var/lib/superpan-license",
		"CapabilityBoundingSet": "", "RestrictAddressFamilies": "AF_UNIX AF_INET AF_INET6",
		"LimitNOFILE": "4096",
	}
	got, err := serviceDirectives(body)
	if err != nil {
		return err
	}
	if len(got) != len(wants) {
		return fmt.Errorf("[Service] directive key count = %d, want %d", len(got), len(wants))
	}
	for key, want := range wants {
		values := got[key]
		if len(values) != 1 || values[0] != want {
			return fmt.Errorf("%s assignments = %q, want only %q", key, values, want)
		}
	}
	return nil
}

func validateOTAServiceUnit(body string) error {
	wants := map[string][]string{
		"Type": {"simple"}, "User": {"superpan-ota-admin"}, "Group": {"superpan-ota-admin"},
		"SupplementaryGroups": {"superpan-license-admin"}, "UMask": {"0077"},
		"WorkingDirectory": {"/var/lib/superpan-ota-admin"},
		"ExecStart":        {"/opt/superpan-ota-admin/ota-admin serve --config /etc/superpan-ota-admin/config.json"},
		"Restart":          {"on-failure"}, "RestartSec": {"5s"}, "TimeoutStopSec": {"30s"}, "KillSignal": {"SIGTERM"},
		"NoNewPrivileges": {"true"}, "PrivateTmp": {"true"}, "PrivateDevices": {"true"},
		"ProtectSystem": {"full"}, "ProtectHome": {"true"}, "InaccessibleDirectories": {"/www/wwwroot"},
		"ReadWriteDirectories":  {"/var/lib/superpan-ota-admin", "/var/lib/otaadmin/releases"},
		"CapabilityBoundingSet": {""}, "RestrictAddressFamilies": {"AF_UNIX AF_INET AF_INET6"},
		"LimitNOFILE": {"4096"},
	}
	got, err := serviceDirectives(body)
	if err != nil {
		return err
	}
	if len(got) != len(wants) {
		return fmt.Errorf("OTA [Service] directive key count = %d, want %d", len(got), len(wants))
	}
	for key, want := range wants {
		values := got[key]
		if len(values) != len(want) {
			return fmt.Errorf("OTA %s assignments = %q, want %q", key, values, want)
		}
		for i := range want {
			if values[i] != want[i] {
				return fmt.Errorf("OTA %s assignments = %q, want %q", key, values, want)
			}
		}
	}
	return nil
}

type nginxLocation struct {
	match string
	body  []string
}

func parseNginxLocations(body string) ([]nginxLocation, error) {
	lines := uncommentedLines(body)
	var locations []nginxLocation
	for i := 0; i < len(lines); {
		line := lines[i]
		if !strings.HasPrefix(line, "location ") || !strings.HasSuffix(line, "{") {
			return nil, fmt.Errorf("directive outside an exact location: %q", line)
		}
		match := strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(line, "location "), "{"))
		location := nginxLocation{match: match}
		depth := 1
		i++
		for ; i < len(lines) && depth > 0; i++ {
			line = lines[i]
			depth += strings.Count(line, "{") - strings.Count(line, "}")
			if depth < 0 || strings.Count(line, "{") > 0 {
				return nil, fmt.Errorf("nested or unbalanced Nginx block at %q", line)
			}
			if depth == 0 {
				if line != "}" {
					return nil, fmt.Errorf("location closing brace must stand alone: %q", line)
				}
				i++
				break
			}
			location.body = append(location.body, line)
		}
		if depth != 0 {
			return nil, fmt.Errorf("unclosed Nginx location %q", match)
		}
		locations = append(locations, location)
	}
	return locations, nil
}

func nginxDirectiveKey(line string) string {
	fields := strings.Fields(line)
	if len(fields) >= 2 && fields[0] == "proxy_set_header" {
		return fields[0] + " " + fields[1]
	}
	if len(fields) == 0 {
		return ""
	}
	return fields[0]
}

func validateNginxInclude(body string) error {
	locations, err := parseNginxLocations(body)
	if err != nil {
		return err
	}
	wantMatches := []string{"= /license-api/v1/activate", "= /license-api/v1/validate"}
	if len(locations) != len(wantMatches) {
		return fmt.Errorf("location count = %d, want %d", len(locations), len(wantMatches))
	}
	wantLines := []string{
		"proxy_pass http://127.0.0.1:18092;", "proxy_http_version 1.1;",
		"proxy_set_header Host ota.fanfh.cn;", "proxy_set_header X-Forwarded-Proto https;",
		"proxy_set_header X-Real-IP $remote_addr;", "proxy_set_header X-Forwarded-For $remote_addr;",
		"proxy_set_header X-SuperPan-Load-Test \"\";", "proxy_cache off;",
		"proxy_request_buffering on;", "client_max_body_size 64k;", "proxy_connect_timeout 5s;",
		"proxy_read_timeout 20s;", "proxy_send_timeout 20s;",
	}
	wants := make(map[string]string, len(wantLines))
	for _, line := range wantLines {
		wants[nginxDirectiveKey(line)] = line
	}
	for i, location := range locations {
		if location.match != wantMatches[i] {
			return fmt.Errorf("location %d = %q, want %q", i, location.match, wantMatches[i])
		}
		got := make(map[string][]string)
		for _, line := range location.body {
			key := nginxDirectiveKey(line)
			if _, ok := wants[key]; !ok {
				return fmt.Errorf("location %q has unreviewed directive %q", location.match, line)
			}
			got[key] = append(got[key], line)
		}
		if len(got) != len(wants) {
			return fmt.Errorf("location %q directive key count = %d, want %d", location.match, len(got), len(wants))
		}
		for key, want := range wants {
			values := got[key]
			if len(values) != 1 || values[0] != want {
				return fmt.Errorf("location %q %s assignments = %q, want only %q", location.match, key, values, want)
			}
		}
	}
	return nil
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
	if err := validateLicenseServiceUnit(license); err != nil {
		t.Error(err)
	}
	if err := validateOTAServiceUnit(ota); err != nil {
		t.Error(err)
	}
	for _, forbidden := range []string{"/var/lib/otaadmin", "/var/lib/superpan-ota-admin", "/www/wwwroot", "User=root", "Group=root"} {
		if strings.Contains(license, forbidden) {
			t.Errorf("license unit contains forbidden identity/path %q", forbidden)
		}
	}

	for _, mutation := range []struct{ name, directive string }{
		{"conflicting ProtectSystem", "ProtectSystem=false"},
		{"nonempty capabilities", "CapabilityBoundingSet=CAP_NET_BIND_SERVICE"},
		{"second primary group", "Group=superpan-ota-admin"},
		{"extra supplementary group", "SupplementaryGroups=superpan-license-admin superpan-ota-admin"},
		{"extra writable directory", "ReadWriteDirectories=/tmp"},
	} {
		t.Run("rejects "+mutation.name, func(t *testing.T) {
			if !strings.Contains(license, "\n[Install]") {
				t.Fatal("normalized unit is missing [Install] insertion point")
			}
			mutated := strings.Replace(license, "\n[Install]", "\n"+mutation.directive+"\n\n[Install]", 1)
			if err := validateLicenseServiceUnit(mutated); err == nil {
				t.Fatalf("accepted mutation %q", mutation.directive)
			}
		})
	}
	t.Run("rejects systemd include", func(t *testing.T) {
		if err := validateLicenseServiceUnit(license + "\n.include /etc/systemd/system/license-overrides.conf\n"); err == nil {
			t.Fatal("accepted systemd .include override")
		}
	})
	for _, name := range []string{"User", "Group", "SupplementaryGroups", "NoNewPrivileges", "PrivateTmp", "PrivateDevices", "ProtectSystem", "ProtectHome", "InaccessibleDirectories", "ReadOnlyDirectories", "ReadWriteDirectories", "CapabilityBoundingSet", "RestrictAddressFamilies"} {
		if len(sectionValues(license, "Service", name)) != 1 {
			t.Errorf("license %s must occur exactly once in [Service]", name)
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
	if err := validateNginxInclude(nginx); err != nil {
		t.Fatal(err)
	}
	mutations := []struct{ name, body string }{
		{"directive outside blocks", strings.Replace(nginx, "    proxy_cache off;\n", "", 1) + "\nproxy_cache off;\n"},
		{"conflicting cache", strings.Replace(nginx, "    proxy_cache off;", "    proxy_cache off;\n    proxy_cache on;", 1)},
		{"second Host", strings.Replace(nginx, "    proxy_set_header Host ota.fanfh.cn;", "    proxy_set_header Host ota.fanfh.cn;\n    proxy_set_header Host attacker.invalid;", 1)},
		{"larger body", strings.Replace(nginx, "    client_max_body_size 64k;", "    client_max_body_size 64k;\n    client_max_body_size 1m;", 1)},
		{"alternate timeout", strings.Replace(nginx, "    proxy_read_timeout 20s;", "    proxy_read_timeout 20s;\n    proxy_read_timeout 120s;", 1)},
		{"indented extra location", nginx + "\n    location ~ /license-api/ {\n        proxy_pass http://127.0.0.1:18092;\n    }\n"},
	}
	for _, mutation := range mutations {
		t.Run("rejects "+mutation.name, func(t *testing.T) {
			if err := validateNginxInclude(mutation.body); err == nil {
				t.Fatalf("accepted Nginx mutation %q", mutation.name)
			}
		})
	}
}
