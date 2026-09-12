//go:build linux

package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"testing"
)

func TestValidateNodeRejectsSymlinkHardlinkAndWrongMode(t *testing.T) {
	if os.Geteuid() != 0 {
		t.Skip("root-owned production metadata fixture requires root")
	}
	root := t.TempDir()
	file := filepath.Join(root, "state.db")
	if err := os.WriteFile(file, []byte("db"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := validateNode(file, false, 0, 0, 0600); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(root, "link.db")
	if err := os.Symlink(file, link); err != nil {
		t.Fatal(err)
	}
	if _, err := validateNode(link, false, 0, 0, 0600); err == nil {
		t.Fatal("symlink accepted")
	}
	hard := filepath.Join(root, "hard.db")
	if err := os.Link(file, hard); err != nil {
		t.Fatal(err)
	}
	if _, err := validateNode(file, false, 0, 0, 0600); err == nil {
		t.Fatal("hard-linked file accepted")
	}
	if err := os.Remove(hard); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(file, 0640); err != nil {
		t.Fatal(err)
	}
	if _, err := validateNode(file, false, 0, 0, 0600); err == nil {
		t.Fatal("wrong mode accepted")
	}
}

func TestValidateNodeRejectsPOSIXACL(t *testing.T) {
	if os.Geteuid() != 0 {
		t.Skip("root-owned production metadata fixture requires root")
	}
	setfacl, err := exec.LookPath("setfacl")
	if err != nil {
		t.Skip("setfacl unavailable")
	}
	file := filepath.Join(t.TempDir(), "state.db")
	if err := os.WriteFile(file, []byte("db"), 0600); err != nil {
		t.Fatal(err)
	}
	if output, err := exec.Command(setfacl, "-m", "u:nobody:---", file).CombinedOutput(); err != nil {
		t.Skipf("ACL fixture unavailable: %v: %s", err, output)
	}
	if info, err := os.Stat(file); err != nil || info.Mode().Perm() != 0600 {
		t.Fatalf("ACL fixture changed ordinary mode: info=%v err=%v", info, err)
	}
	if _, err := validateNode(file, false, 0, 0, 0600); err == nil {
		t.Fatal("POSIX ACL accepted")
	}
}

func TestDirectoryGuardSeizesAndRestoresOwnership(t *testing.T) {
	if os.Geteuid() != 0 {
		t.Skip("fchown guard fixture requires root")
	}
	directory := filepath.Join(t.TempDir(), "live")
	if err := os.Mkdir(directory, 0700); err != nil {
		t.Fatal(err)
	}
	const fixtureUID, fixtureGID = 65534, 65534
	if err := os.Chown(directory, fixtureUID, fixtureGID); err != nil {
		t.Skipf("fixture chown unavailable: %v", err)
	}
	lease, err := acquirePlatformDirectoryGuard(directory, true, fixtureUID, fixtureGID)
	if err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(directory)
	if err != nil {
		t.Fatal(err)
	}
	stat := info.Sys().(*syscall.Stat_t)
	if stat.Uid != 0 || stat.Gid != 0 || info.Mode().Perm() != 0700 {
		t.Fatalf("guard did not seize directory: uid=%d gid=%d mode=%04o", stat.Uid, stat.Gid, info.Mode().Perm())
	}
	if err := lease.Restore(); err != nil {
		t.Fatal(err)
	}
	info, err = os.Stat(directory)
	if err != nil {
		t.Fatal(err)
	}
	stat = info.Sys().(*syscall.Stat_t)
	if stat.Uid != fixtureUID || stat.Gid != fixtureGID || info.Mode().Perm() != 0700 {
		t.Fatalf("guard did not restore directory: uid=%d gid=%d mode=%04o", stat.Uid, stat.Gid, info.Mode().Perm())
	}
}
