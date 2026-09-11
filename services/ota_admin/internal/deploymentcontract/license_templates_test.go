package deploymentcontract

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func repositoryRoot(t *testing.T) string {
	t.Helper()
	d, err := os.Getwd()
	if err != nil { t.Fatal(err) }
	for {
		if _, err := os.Stat(filepath.Join(d, "BaiduBatchTransfer.sln")); err == nil { return d }
		parent := filepath.Dir(d)
		if parent == d { t.Fatal("repository root not found") }
		d = parent
	}
}

func read(t *testing.T, parts ...string) string {
	b, err := os.ReadFile(filepath.Join(append([]string{repositoryRoot(t)}, parts...)...))
	if err != nil { t.Fatalf("read template: %v", err) }
	return strings.ReplaceAll(string(b), "\r\n", "\n")
}

func TestLicenseTemplatesDeclareIsolatedLoopbackContracts(t *testing.T) {
	unit := read(t, "deploy", "license-service", "superpan-license.service")
	for _, want := range []string{"User=superpan-license", "Group=superpan-license", "127.0.0.1:18092", "/var/lib/superpan-license", "/etc/superpan-license", "NoNewPrivileges=true", "CapabilityBoundingSet="} {
		if !strings.Contains(unit, want) { t.Errorf("license unit missing %q", want) }
	}
	if strings.Contains(unit, "/var/lib/otaadmin") || strings.Contains(unit, "/www/wwwroot") { t.Error("license unit references OTA/site paths") }

	ota := read(t, "deploy", "ota-admin", "superpan-ota-admin.service")
	if !strings.Contains(ota, "User=superpan-ota-admin") || !strings.Contains(ota, "Group=superpan-ota-admin") { t.Error("OTA unit identity changed unexpectedly") }

	nginx := read(t, "deploy", "license-service", "ota.fanfh.cn.license-api.nginx.conf")
	for _, want := range []string{"location = /license-api/v1/activate", "location = /license-api/v1/validate", "proxy_pass http://127.0.0.1:18092", "proxy_set_header X-SuperPan-Load-Test \"\"", "proxy_cache off"} {
		if !strings.Contains(nginx, want) { t.Errorf("license nginx template missing %q", want) }
	}
	if strings.Contains(nginx, "location /") || strings.Contains(nginx, "server {") || strings.Contains(nginx, "/internal/") { t.Error("license nginx template exposes broad/internal routes") }
}

func TestLicenseConfigExampleUsesDedicatedRoots(t *testing.T) {
	cfg := read(t, "deploy", "license-service", "config.example.json")
	for _, want := range []string{"127.0.0.1:18092", "/var/lib/superpan-license", "/etc/superpan-license"} {
		if !strings.Contains(cfg, want) { t.Errorf("license config missing %q", want) }
	}
}
