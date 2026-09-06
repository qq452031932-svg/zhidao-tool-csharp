package deploy

import (
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
	if !strings.HasPrefix(string(mainSource), "//go:build linux && ota_admin_acceptance\n") {
		t.Fatal("acceptance harness is not protected by its Linux-only opt-in build tag")
	}
	acceptanceScript := readTask10Script(t, filepath.Join(moduleRoot, "scripts", "build_linux_acceptance.ps1"))
	for _, required := range []string{"-tags 'ota_admin_acceptance'", "./cmd/ota-admin-acceptance", "superpan-ota-admin-acceptance-linux-amd64"} {
		if !strings.Contains(acceptanceScript, required) {
			t.Fatalf("acceptance build script missing %q", required)
		}
	}
	productionScript := readTask10Script(t, filepath.Join(moduleRoot, "scripts", "build_linux_amd64.ps1"))
	for _, required := range []string{"Assert-AcceptanceHarnessExcluded", "./cmd/ota-admin", "superpan-ota-admin-linux-amd64"} {
		if !strings.Contains(productionScript, required) {
			t.Fatalf("production build script missing isolation proof %q", required)
		}
	}
}

func readTask10Script(t *testing.T, path string) string {
	t.Helper()
	content, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(content)
}
