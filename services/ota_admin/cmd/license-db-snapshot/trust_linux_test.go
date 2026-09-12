//go:build linux

package main

import (
	"os"
	"os/exec"
	"path/filepath"
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
