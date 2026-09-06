package deploy

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestAcceptanceHarnessIsBuildTaggedAndIsolatedFromProduction(t *testing.T) {
	moduleRoot := filepath.Clean(filepath.Join("..", ".."))
	mainPath := filepath.Join(moduleRoot, "cmd", "ota-admin-acceptance", "main_linux.go")
	mainSource, err := os.ReadFile(mainPath)
	if err != nil {
		t.Fatal(err)
	}
	firstLine, _, hasNewline := strings.Cut(string(mainSource), "\n")
	if !hasNewline || strings.TrimSuffix(firstLine, "\r") != "//go:build linux && ota_admin_acceptance" {
		t.Fatal("acceptance harness is not protected by its Linux-only opt-in build tag")
	}
	for _, required := range []string{
		"//go:embed testdata/valid-upload.zip",
		"//go:embed testdata/publisher.cer",
		"x509.ParseCertificate",
		"release.NewValidator",
		"upload.NewReceiver",
		"candidate.NewService",
		"RevalidateForConfirm",
		"/proc/self/cgroup",
		"/proc/1/comm",
	} {
		if !strings.Contains(string(mainSource), required) {
			t.Fatalf("acceptance harness missing real-chain/systemd contract %q", required)
		}
	}
	acceptanceScript := readTask10Script(t, filepath.Join(moduleRoot, "scripts", "build_linux_acceptance.ps1"))
	for _, required := range []string{"-tags 'ota_admin_acceptance'", "./cmd/ota-admin-acceptance", "superpan-ota-admin-acceptance-linux-amd64", "Get-AcceptanceSourcePaths", "cmd/ota-admin-acceptance/testdata/valid-upload.zip", "cmd/ota-admin-acceptance/testdata/publisher.cer", "b01dd17f87ade73bcefacedf55fe688fc8dd7bc222bb4bb7c264790633afff95", "54250feb9125e34f5f48ffaa2210f7c6d251a6475bf09cef34b0aee9e57f3d53"} {
		if !strings.Contains(acceptanceScript, required) {
			t.Fatalf("acceptance build script missing %q", required)
		}
	}
	productionScript := readTask10Script(t, filepath.Join(moduleRoot, "scripts", "build_linux_amd64.ps1"))
	for _, required := range []string{"Assert-AcceptanceHarnessExcluded", "./cmd/ota-admin", "superpan-ota-admin-linux-amd64", "cmd/ota-admin-acceptance/testdata/valid-upload.zip", "cmd/ota-admin-acceptance/testdata/publisher.cer"} {
		if !strings.Contains(productionScript, required) {
			t.Fatalf("production build script missing isolation proof %q", required)
		}
	}
	if strings.Contains(productionScript, "EndsWith('.zip'") || strings.Contains(productionScript, "EndsWith('.cer'") {
		t.Fatal("production source allowlist was broadened to binary acceptance fixtures")
	}
}

func TestAcceptanceHarnessFixturesExactlyMatchTrackedPowerShellGolden(t *testing.T) {
	moduleRoot := filepath.Clean(filepath.Join("..", ".."))
	acceptanceFixtureRoot := filepath.Join(moduleRoot, "cmd", "ota-admin-acceptance", "testdata")
	for name, want := range map[string]string{
		"valid-upload.zip": "b01dd17f87ade73bcefacedf55fe688fc8dd7bc222bb4bb7c264790633afff95",
		"publisher.cer":    "54250feb9125e34f5f48ffaa2210f7c6d251a6475bf09cef34b0aee9e57f3d53",
	} {
		got := fixtureSHA256(t, filepath.Join(acceptanceFixtureRoot, name))
		if got != want {
			t.Fatalf("acceptance fixture %s hash = %s, want audited golden %s", name, got, want)
		}
	}
	entries, err := os.ReadDir(acceptanceFixtureRoot)
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		lower := strings.ToLower(entry.Name())
		if strings.Contains(lower, "private") || strings.HasSuffix(lower, ".pfx") || strings.HasSuffix(lower, ".p12") || strings.HasSuffix(lower, ".key") || strings.HasSuffix(lower, ".pem") {
			t.Fatalf("acceptance fixture directory contains prohibited key material: %s", entry.Name())
		}
	}
}

func fixtureSHA256(t *testing.T, path string) string {
	t.Helper()
	content, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha256.Sum256(content)
	return hex.EncodeToString(digest[:])
}

func readTask10Script(t *testing.T, path string) string {
	t.Helper()
	content, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(content)
}
