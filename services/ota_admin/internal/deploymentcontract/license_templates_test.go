package deploymentcontract

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	otaconfig "fanfh.cn/superpan/ota-admin/internal/config"
	licenseconfig "fanfh.cn/superpan/ota-admin/internal/licensing/config"
)

const (
	licenseGroupCheck = `/bin/sh -ec 'actual=$$(id -Gn | tr " " "\n" | LC_ALL=C sort | tr "\n" " "); test "$$actual" = "superpan-license superpan-license-admin "'`
	otaGroupCheck     = `/bin/sh -ec 'actual=$$(id -Gn | tr " " "\n" | LC_ALL=C sort | tr "\n" " "); test "$$actual" = "superpan-license-admin superpan-ota-admin "'`
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

func unitDirectives(body string) (map[string]map[string][]string, error) {
	values := make(map[string]map[string][]string)
	section := ""
	for _, line := range uncommentedLines(body) {
		if strings.HasPrefix(line, ".include") {
			return nil, fmt.Errorf("systemd .include is forbidden in the deployment template")
		}
		if strings.HasPrefix(line, "[") {
			if line != "[Unit]" && line != "[Service]" && line != "[Install]" {
				return nil, fmt.Errorf("unreviewed systemd section %q", line)
			}
			section = strings.TrimSuffix(strings.TrimPrefix(line, "["), "]")
			if _, exists := values[section]; exists {
				return nil, fmt.Errorf("duplicate systemd section %q", section)
			}
			values[section] = make(map[string][]string)
			continue
		}
		if section == "" {
			return nil, fmt.Errorf("systemd directive outside a section: %q", line)
		}
		key, value, ok := strings.Cut(line, "=")
		if !ok || key == "" {
			return nil, fmt.Errorf("malformed [%s] directive %q", section, line)
		}
		values[section][key] = append(values[section][key], value)
	}
	return values, nil
}

func equalValues(key string, got, want []string) bool {
	if len(got) != len(want) {
		return false
	}
	if key != "ReadWriteDirectories" {
		return reflect.DeepEqual(got, want)
	}
	remaining := append([]string(nil), got...)
	for _, value := range want {
		found := false
		for i, candidate := range remaining {
			if candidate == value {
				remaining = append(remaining[:i], remaining[i+1:]...)
				found = true
				break
			}
		}
		if !found {
			return false
		}
	}
	return len(remaining) == 0
}

func validateExactUnit(body string, wants map[string]map[string][]string) error {
	got, err := unitDirectives(body)
	if err != nil {
		return err
	}
	if len(got) != len(wants) {
		return fmt.Errorf("systemd section count = %d, want %d", len(got), len(wants))
	}
	for section, wantDirectives := range wants {
		gotDirectives, ok := got[section]
		if !ok || len(gotDirectives) != len(wantDirectives) {
			return fmt.Errorf("[%s] directive key count = %d, want %d", section, len(gotDirectives), len(wantDirectives))
		}
		for key, want := range wantDirectives {
			if values := gotDirectives[key]; !equalValues(key, values, want) {
				return fmt.Errorf("[%s] %s assignments = %q, want %q", section, key, values, want)
			}
		}
	}
	return nil
}

func validateLicenseServiceUnit(body string) error {
	return validateExactUnit(body, map[string]map[string][]string{
		"Unit": {"Description": {"SuperPan license service"}, "After": {"network.target"}},
		"Service": {
			"Type": {"simple"}, "User": {"superpan-license"}, "Group": {"superpan-license"},
			"SupplementaryGroups": {"superpan-license-admin"}, "UMask": {"0077"},
			"WorkingDirectory": {"/var/lib/superpan-license"}, "ExecStartPre": {licenseGroupCheck},
			"ExecStart": {"/opt/superpan-license/superpan-license serve --config /etc/superpan-license/config.json"},
			"Restart":   {"on-failure"}, "RestartSec": {"5s"}, "TimeoutStopSec": {"30s"},
			"NoNewPrivileges": {"true"}, "PrivateTmp": {"true"}, "PrivateDevices": {"true"},
			"ProtectSystem": {"full"}, "ProtectHome": {"true"}, "InaccessibleDirectories": {"/var/www"},
			"ReadOnlyDirectories": {"/"}, "ReadWriteDirectories": {"/var/lib/superpan-license"},
			"CapabilityBoundingSet": {""}, "RestrictAddressFamilies": {"AF_UNIX AF_INET AF_INET6"},
			"LimitNOFILE": {"4096"},
		},
		"Install": {"WantedBy": {"multi-user.target"}},
	})
}

func validateOTAServiceUnit(body string) error {
	return validateExactUnit(body, map[string]map[string][]string{
		"Unit": {
			"Description": {"SuperPan OTA administration service"}, "After": {"network.target", "var-lib-otaadmin-releases.mount"},
			"Requires": {"var-lib-otaadmin-releases.mount"},
		},
		"Service": {
			"Type": {"simple"}, "User": {"superpan-ota-admin"}, "Group": {"superpan-ota-admin"},
			"SupplementaryGroups": {"superpan-license-admin"}, "UMask": {"0077"},
			"WorkingDirectory": {"/var/lib/superpan-ota-admin"}, "ExecStartPre": {otaGroupCheck},
			"ExecStart": {"/opt/superpan-ota-admin/ota-admin serve --config /etc/superpan-ota-admin/config.json"},
			"Restart":   {"on-failure"}, "RestartSec": {"5s"}, "TimeoutStopSec": {"30s"}, "KillSignal": {"SIGTERM"},
			"NoNewPrivileges": {"true"}, "PrivateTmp": {"true"}, "PrivateDevices": {"true"},
			"ProtectSystem": {"full"}, "ProtectHome": {"true"}, "InaccessibleDirectories": {"/www/wwwroot"},
			"ReadWriteDirectories":  {"/var/lib/superpan-ota-admin", "/var/lib/otaadmin/releases"},
			"CapabilityBoundingSet": {""}, "RestrictAddressFamilies": {"AF_UNIX AF_INET AF_INET6"},
			"LimitNOFILE": {"4096"},
		},
		"Install": {"WantedBy": {"multi-user.target"}},
	})
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
	wantMatches := map[string]bool{"= /license-api/v1/activate": true, "= /license-api/v1/validate": true}
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
	seenMatches := make(map[string]bool, len(locations))
	for _, location := range locations {
		if !wantMatches[location.match] || seenMatches[location.match] {
			return fmt.Errorf("unexpected or duplicate location %q", location.match)
		}
		seenMatches[location.match] = true
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

func TestLicenseConfigExamplePassesProductionStrictParser(t *testing.T) {
	body := read(t, "deploy", "license-service", "config.example.json")
	const placeholder = "replace-with-generated-key-id"
	if strings.Count(body, placeholder) != 1 {
		t.Fatalf("signing key placeholder count = %d, want 1", strings.Count(body, placeholder))
	}
	rendered := strings.Replace(body, placeholder, "production-p256-2026-09", 1)
	loaded, err := licenseconfig.Parse([]byte(rendered))
	if err != nil {
		t.Fatalf("production strict parser rejected rendered template: %v", err)
	}
	want := licenseconfig.Config{
		ListenAddress: "127.0.0.1:18092", LicenseDatabasePath: "/var/lib/superpan-license/state.db",
		EncryptionKeyPath: "/etc/superpan-license/encryption.key", PreviousEncryptionKeyPaths: []string{},
		LookupHMACKeyPath: "/etc/superpan-license/lookup.key", SigningPrivateKeyPath: "/etc/superpan-license/response-signing.key",
		InternalSecretPath: "/etc/superpan-license/admin.secret", LoadTestEnabled: false, LoadTestSecretPath: "",
		SigningKeyID: "production-p256-2026-09", PublicAPIBasePath: "/license-api/v1",
		Limits: licenseconfig.Limits{
			MaxBodyBytes: 65536, MaxHeaderBytes: 16384, TimestampWindowSeconds: 300,
			NonceLifetimeSeconds: 600, MaxReplayNonces: 131072, MaxAdminIdempotencyEntries: 1024,
			MaxAdminIdempotencyBytes: 10485760, ActivationRequests: 30, ValidationRequests: 60,
			AdminRequests: 120, RateWindowSeconds: 60, ReadHeaderTimeoutSeconds: 5,
			ReadTimeoutSeconds: 15, WriteTimeoutSeconds: 15, IdleTimeoutSeconds: 60,
			ShutdownTimeoutSeconds: 30,
		},
	}
	if !reflect.DeepEqual(loaded, want) {
		t.Fatalf("loaded config = %#v, want %#v", loaded, want)
	}
	mutations := []struct{ name, body string }{
		{"unknown field", strings.Replace(rendered, "{\n", "{\n  \"unexpected\": true,\n", 1)},
		{"duplicate field", strings.Replace(rendered, `"listen_address": "127.0.0.1:18092"`, `"listen_address": "127.0.0.1:18092", "listen_address": "127.0.0.1:18092"`, 1)},
	}
	for _, mutation := range mutations {
		t.Run(mutation.name, func(t *testing.T) {
			if _, err := licenseconfig.Parse([]byte(mutation.body)); err == nil {
				t.Fatalf("production strict parser accepted %s", mutation.name)
			}
		})
	}
}

func TestOTAConfigUsesIsolatedLoopbackContract(t *testing.T) {
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
	for _, mutation := range []struct{ name, body string }{
		{"extra section", license + "\n[Socket]\nListenStream=18092\n"},
		{"extra Unit directive", strings.Replace(license, "After=network.target", "After=network.target\nDocumentation=https://example.invalid", 1)},
		{"extra Install directive", strings.Replace(license, "WantedBy=multi-user.target", "WantedBy=multi-user.target\nAlias=other.service", 1)},
	} {
		t.Run("rejects "+mutation.name, func(t *testing.T) {
			if err := validateLicenseServiceUnit(mutation.body); err == nil {
				t.Fatalf("accepted %s", mutation.name)
			}
		})
	}
	for _, tc := range []struct{ name, body, exact, extra string }{
		{"license extra runtime group", license, licenseGroupCheck, strings.Replace(licenseGroupCheck, `admin "`, `admin superpan-ota-admin "`, 1)},
		{"OTA extra runtime group", ota, otaGroupCheck, strings.Replace(otaGroupCheck, `admin superpan-ota-admin "`, `admin superpan-license superpan-ota-admin "`, 1)},
	} {
		t.Run("rejects "+tc.name, func(t *testing.T) {
			mutated := strings.Replace(tc.body, "ExecStartPre="+tc.exact, "ExecStartPre="+tc.extra, 1)
			if mutated == tc.body {
				t.Fatal("runtime group-check mutation did not apply")
			}
			validator := validateLicenseServiceUnit
			if tc.name == "OTA extra runtime group" {
				validator = validateOTAServiceUnit
			}
			if err := validator(mutated); err == nil {
				t.Fatal("accepted runtime group check that permits an extra group")
			}
		})
	}
	for _, name := range []string{"User", "Group", "SupplementaryGroups", "NoNewPrivileges", "PrivateTmp", "PrivateDevices", "ProtectSystem", "ProtectHome", "InaccessibleDirectories", "ReadOnlyDirectories", "ReadWriteDirectories", "CapabilityBoundingSet", "RestrictAddressFamilies"} {
		if len(sectionValues(license, "Service", name)) != 1 {
			t.Errorf("license %s must occur exactly once in [Service]", name)
		}
	}
	t.Run("OTA writable directories are order independent", func(t *testing.T) {
		first := "ReadWriteDirectories=/var/lib/superpan-ota-admin\nReadWriteDirectories=/var/lib/otaadmin/releases"
		second := "ReadWriteDirectories=/var/lib/otaadmin/releases\nReadWriteDirectories=/var/lib/superpan-ota-admin"
		mutated := strings.Replace(ota, first, second, 1)
		if mutated == ota {
			t.Fatal("OTA writable-directory reorder did not apply")
		}
		if err := validateOTAServiceUnit(mutated); err != nil {
			t.Fatalf("order-only OTA writable-directory change failed: %v", err)
		}
	})
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
	t.Run("locations are order independent", func(t *testing.T) {
		second := strings.Index(nginx, "location = /license-api/v1/validate")
		if second < 0 {
			t.Fatal("validate location not found")
		}
		reordered := nginx[second:] + "\n" + nginx[:second]
		if err := validateNginxInclude(reordered); err != nil {
			t.Fatalf("order-only location change failed: %v", err)
		}
	})
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
