//go:build linux

package main

import (
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"testing"
)

type fakeDirectoryOps struct {
	uid, gid, mode   uint32
	failServiceChown bool
	failNextSync     error
}

func (o *fakeDirectoryOps) Fchmod(_ int, mode uint32) error { o.mode = mode; return nil }
func (o *fakeDirectoryOps) Fchown(_ int, uid, gid int) error {
	if o.failServiceChown && uid != 0 {
		return errors.New("fixture restore owner failure")
	}
	o.uid, o.gid = uint32(uid), uint32(gid)
	return nil
}
func (o *fakeDirectoryOps) Sync(*os.File) error {
	if o.failNextSync != nil {
		err := o.failNextSync
		o.failNextSync = nil
		return err
	}
	return nil
}
func (o *fakeDirectoryOps) Metadata(int) (uint32, uint32, uint32, error) {
	return o.uid, o.gid, o.mode, nil
}

func TestPartialGuardAcquireRollbackSuccessRestoresMetadata(t *testing.T) {
	original := errors.New("fixture acquire validation failure")
	ops := &fakeDirectoryOps{uid: 0, gid: 0, mode: 0700, failNextSync: original}
	lease := &platformDirectoryGuardLease{file: os.NewFile(0, "fixture"), uid: 123, gid: 456, mode: 0700, live: true, ops: ops}
	err := lease.finishPartialAcquire()
	if !errors.Is(err, original) || errors.Is(err, ErrManualRepairRequired) {
		t.Fatalf("unexpected rollback result: %v", err)
	}
	if ops.uid != 123 || ops.gid != 456 || ops.mode != 0700 {
		t.Fatalf("metadata not restored: %#v", ops)
	}
}

func TestPartialGuardAcquireRollbackFailureRequiresManualRepair(t *testing.T) {
	original := errors.New("fixture acquire sync failure")
	ops := &fakeDirectoryOps{uid: 0, gid: 0, mode: 0700, failServiceChown: true, failNextSync: original}
	lease := &platformDirectoryGuardLease{file: os.NewFile(0, "fixture"), uid: 123, gid: 456, mode: 0700, live: true, ops: ops}
	err := lease.finishPartialAcquire()
	if !errors.Is(err, ErrManualRepairRequired) || !errors.Is(err, original) || !strings.Contains(err.Error(), "keep superpan-license.service stopped; manually restore directory ownership") {
		t.Fatalf("manual repair error missing: %v", err)
	}
	if ops.uid != 0 || ops.gid != 0 || ops.mode != 0700 {
		t.Fatalf("root control was not re-seized: %#v", ops)
	}
}

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
