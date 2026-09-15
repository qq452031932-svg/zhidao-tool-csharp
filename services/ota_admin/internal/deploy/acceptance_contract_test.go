package deploy

import (
	"crypto/sha1"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"fmt"
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
		"unix.Umask(0o077)",
		"/proc/self/cgroup",
		"/proc/1/comm",
	} {
		if !strings.Contains(string(mainSource), required) {
			t.Fatalf("acceptance harness missing real-chain/systemd contract %q", required)
		}
	}
	acceptanceScript := readTask10Script(t, filepath.Join(moduleRoot, "scripts", "build_linux_acceptance.ps1"))
	for _, required := range []string{"-tags 'ota_admin_acceptance'", "./cmd/ota-admin-acceptance", "superpan-ota-admin-acceptance-linux-amd64", "Get-AcceptanceSourcePaths", "cmd/ota-admin-acceptance/testdata/valid-upload.zip", "cmd/ota-admin-acceptance/testdata/publisher.cer", "30b96617dfb888b5ce6daf0e8d3b7bd2b1a94c09d37021919d0b313720148916", "eed6657913e5bf86caac9b8f23928648b83a50b9b06f1e2c738cbb2a13dbc2d5"} {
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
	acceptanceScript := readTask10Script(t, filepath.Join(moduleRoot, "scripts", "build_linux_acceptance.ps1"))
	type fixtureIdentity struct {
		gitBlobSHA1           string
		fileSHA256            string
		certificateThumbprint string
	}
	for name, want := range map[string]fixtureIdentity{
		"valid-upload.zip": {
			gitBlobSHA1: "007430324b90fff97b835b46e4b51c5a9e31502f",
			fileSHA256:  "30b96617dfb888b5ce6daf0e8d3b7bd2b1a94c09d37021919d0b313720148916",
		},
		"publisher.cer": {
			gitBlobSHA1:           "86d490d04699624c2f5504acbed7fe499a20ae10",
			fileSHA256:            "eed6657913e5bf86caac9b8f23928648b83a50b9b06f1e2c738cbb2a13dbc2d5",
			certificateThumbprint: "96f6d3166172998f62621f93a1f12932b245a1a9",
		},
	} {
		path := filepath.Join(acceptanceFixtureRoot, name)
		if got := fixtureGitBlobSHA1(t, path); got != want.gitBlobSHA1 {
			t.Fatalf("acceptance fixture %s Git blob SHA-1 = %s, want %s", name, got, want.gitBlobSHA1)
		}
		if got := fixtureSHA256(t, path); got != want.fileSHA256 {
			t.Fatalf("acceptance fixture %s file SHA-256 = %s, want audited golden %s", name, got, want.fileSHA256)
		}
		pin := fmt.Sprintf("@('%s', '%s')", want.gitBlobSHA1, want.fileSHA256)
		if !strings.Contains(acceptanceScript, pin) {
			t.Fatalf("acceptance build script does not distinguish Git blob and file hashes for %s", name)
		}
		if want.certificateThumbprint != "" {
			if got := certificateThumbprint(t, path); got != want.certificateThumbprint {
				t.Fatalf("acceptance certificate thumbprint = %s, want %s", got, want.certificateThumbprint)
			}
			if want.certificateThumbprint == want.gitBlobSHA1 {
				t.Fatal("certificate thumbprint was confused with its Git blob SHA-1")
			}
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

func fixtureGitBlobSHA1(t *testing.T, path string) string {
	t.Helper()
	content, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha1.New()
	_, _ = fmt.Fprintf(digest, "blob %d%c", len(content), 0)
	_, _ = digest.Write(content)
	return hex.EncodeToString(digest.Sum(nil))
}

func certificateThumbprint(t *testing.T, path string) string {
	t.Helper()
	content, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	certificate, err := x509.ParseCertificate(content)
	if err != nil {
		t.Fatal(err)
	}
	digest := sha1.Sum(certificate.Raw)
	return hex.EncodeToString(digest[:])
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
