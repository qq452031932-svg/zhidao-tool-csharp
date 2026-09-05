//go:build linux

package config

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadUsesOneRootOwnedDescriptorAndAllowsServiceGroupRead(t *testing.T) {
	if os.Geteuid() != 0 {
		t.Skip("root ownership fixture requires root")
	}
	root := rootOwnedConfigTestDir(t, ".config-secure-")
	path := filepath.Join(root, "ota-admin.json")
	if err := os.WriteFile(path, []byte(`{"database_path":"/var/lib/superpan-ota-admin/state.db"}`), 0o640); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err != nil {
		t.Fatalf("Load(0640 root config) error = %v", err)
	}

	link := filepath.Join(root, "linked.json")
	if err := os.Symlink(path, link); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(link); err == nil {
		t.Fatal("Load() followed a config symlink")
	}
}

func TestLoadRejectsWritableParentComponent(t *testing.T) {
	if os.Geteuid() != 0 {
		t.Skip("root ownership fixture requires root")
	}
	root := rootOwnedConfigTestDir(t, ".config-parent-")
	unsafeParent := filepath.Join(root, "writable")
	if err := os.Mkdir(unsafeParent, 0o770); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(unsafeParent, 0o770); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(unsafeParent, "ota-admin.json")
	if err := os.WriteFile(path, []byte(`{"database_path":"/var/lib/superpan-ota-admin/state.db"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err == nil {
		t.Fatal("Load() accepted a group-writable parent")
	}
}

func TestLoadRejectsIntermediateSymlinkComponent(t *testing.T) {
	if os.Geteuid() != 0 {
		t.Skip("root ownership fixture requires root")
	}
	root := rootOwnedConfigTestDir(t, ".config-link-")
	realParent := filepath.Join(root, "real")
	if err := os.Mkdir(realParent, 0o700); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(realParent, "ota-admin.json")
	if err := os.WriteFile(path, []byte(`{"database_path":"/var/lib/superpan-ota-admin/state.db"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	linkedParent := filepath.Join(root, "linked")
	if err := os.Symlink(realParent, linkedParent); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(filepath.Join(linkedParent, "ota-admin.json")); err == nil {
		t.Fatal("Load followed intermediate symlink")
	}
}

func TestLoadRejectsNonRootOwnedParentComponent(t *testing.T) {
	if os.Geteuid() != 0 {
		t.Skip("root ownership fixture requires root")
	}
	root := rootOwnedConfigTestDir(t, ".config-owner-")
	parent := filepath.Join(root, "foreign")
	if err := os.Mkdir(parent, 0o700); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(parent, "ota-admin.json")
	if err := os.WriteFile(path, []byte(`{"database_path":"/var/lib/superpan-ota-admin/state.db"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Chown(parent, 1, -1); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err == nil {
		t.Fatal("Load accepted a non-root-owned parent")
	}
}

func rootOwnedConfigTestDir(t *testing.T, pattern string) string {
	t.Helper()
	root, err := os.MkdirTemp(".", pattern)
	if err != nil {
		t.Fatal(err)
	}
	absoluteRoot, err := filepath.Abs(root)
	if err != nil {
		_ = os.RemoveAll(root)
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(absoluteRoot) })
	if err := os.Chmod(absoluteRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	return absoluteRoot
}
