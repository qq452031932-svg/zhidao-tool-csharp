//go:build linux

package main

import (
	"errors"
	"fmt"
	"os"
	"os/user"
	"path/filepath"
	"strconv"
	"syscall"
)

type platformTrustProbe struct{}

type platformSourceBinding struct {
	file     *os.File
	identity FileIdentity
}

func (b *platformSourceBinding) Identity() FileIdentity { return b.identity }
func (b *platformSourceBinding) Close() error           { return b.file.Close() }

type platformSourceBinder struct{}

func (platformSourceBinder) Bind(path string, expected FileIdentity) (SourceBinding, error) {
	fd, err := syscall.Open(path, syscall.O_RDONLY|syscall.O_CLOEXEC|syscall.O_NOFOLLOW, 0)
	if err != nil {
		return nil, err
	}
	file := os.NewFile(uintptr(fd), path)
	var stat syscall.Stat_t
	if err := syscall.Fstat(fd, &stat); err != nil {
		file.Close()
		return nil, err
	}
	actual := FileIdentity{Device: uint64(stat.Dev), Inode: stat.Ino}
	if actual != expected {
		file.Close()
		return nil, errors.New("source identity changed before descriptor binding")
	}
	return &platformSourceBinding{file: file, identity: actual}, nil
}

type platformDirectoryGuard struct{}
type directoryGuardOps interface {
	Fchmod(int, uint32) error
	Fchown(int, int, int) error
	Sync(*os.File) error
	Metadata(int) (uint32, uint32, uint32, error)
}
type linuxDirectoryGuardOps struct{}

func (linuxDirectoryGuardOps) Fchmod(fd int, mode uint32) error { return syscall.Fchmod(fd, mode) }
func (linuxDirectoryGuardOps) Fchown(fd, uid, gid int) error    { return syscall.Fchown(fd, uid, gid) }
func (linuxDirectoryGuardOps) Sync(file *os.File) error         { return file.Sync() }
func (linuxDirectoryGuardOps) Metadata(fd int) (uint32, uint32, uint32, error) {
	var stat syscall.Stat_t
	if err := syscall.Fstat(fd, &stat); err != nil {
		return 0, 0, 0, err
	}
	return stat.Uid, stat.Gid, uint32(stat.Mode) & 07777, nil
}

type platformDirectoryGuardLease struct {
	file     *os.File
	path     string
	identity FileIdentity
	uid, gid uint32
	mode     uint32
	live     bool
	ops      directoryGuardOps
}

func (platformDirectoryGuard) Acquire(source string, kind SourceKind) (DirectoryGuardLease, error) {
	parent := filepath.Dir(source)
	if kind == SourceLive {
		if parent != filepath.Dir(liveDatabasePath) {
			return nil, errors.New("live source parent is not exact")
		}
		if err := validateRootAncestors(parent); err != nil {
			return nil, err
		}
	}
	expectedUID, expectedGID := uint32(0), uint32(0)
	var err error
	if kind == SourceLive {
		expectedUID, expectedGID, err = serviceIDs()
		if err != nil {
			return nil, err
		}
	}
	return acquirePlatformDirectoryGuard(parent, kind == SourceLive, expectedUID, expectedGID)
}

func acquirePlatformDirectoryGuard(parent string, live bool, expectedUID, expectedGID uint32) (DirectoryGuardLease, error) {
	fd, err := syscall.Open(parent, syscall.O_RDONLY|syscall.O_DIRECTORY|syscall.O_CLOEXEC|syscall.O_NOFOLLOW, 0)
	if err != nil {
		return nil, err
	}
	file := os.NewFile(uintptr(fd), parent)
	var stat syscall.Stat_t
	if err := syscall.Fstat(fd, &stat); err != nil {
		file.Close()
		return nil, err
	}
	if stat.Uid != expectedUID || stat.Gid != expectedGID || uint32(stat.Mode)&07777 != 0700 {
		file.Close()
		return nil, errors.New("source directory has untrusted original metadata")
	}
	lease := &platformDirectoryGuardLease{file: file, path: parent, identity: FileIdentity{Device: uint64(stat.Dev), Inode: stat.Ino}, uid: stat.Uid, gid: stat.Gid, mode: uint32(stat.Mode) & 07777, live: live, ops: linuxDirectoryGuardOps{}}
	if !lease.live {
		if err := lease.Validate(); err != nil {
			file.Close()
			return nil, err
		}
		return lease, nil
	}
	// 0700 is established before ownership changes, so this controlled mutation
	// never widens access. Once root owns the same inode, stale service-UID
	// directory descriptors cannot create or rename entries under Linux DAC.
	if err := syscall.Fchmod(fd, 0700); err != nil {
		file.Close()
		return nil, err
	}
	if err := syscall.Fchown(fd, 0, 0); err != nil {
		file.Close()
		return nil, err
	}
	if err := lease.finishPartialAcquire(); err != nil {
		file.Close()
		return nil, err
	}
	return lease, nil
}

func (g *platformDirectoryGuardLease) finishPartialAcquire() error {
	fd := int(g.file.Fd())
	if err := g.ops.Fchmod(fd, 0700); err != nil {
		return g.recoverPartialAcquire(err)
	}
	if err := g.ops.Sync(g.file); err != nil {
		return g.recoverPartialAcquire(err)
	}
	if err := g.Validate(); err != nil {
		return g.recoverPartialAcquire(err)
	}
	return nil
}

func (g *platformDirectoryGuardLease) Validate() error {
	var stat syscall.Stat_t
	if err := syscall.Fstat(int(g.file.Fd()), &stat); err != nil {
		return err
	}
	if (FileIdentity{Device: uint64(stat.Dev), Inode: stat.Ino}) != g.identity {
		return errors.New("guard descriptor identity changed")
	}
	expectedUID, expectedGID := g.uid, g.gid
	if g.live {
		expectedUID, expectedGID = 0, 0
	}
	if stat.Uid != expectedUID || stat.Gid != expectedGID || uint32(stat.Mode)&07777 != 0700 {
		return errors.New("guard metadata changed")
	}
	info, err := os.Lstat(g.path)
	if err != nil {
		return err
	}
	pathStat, ok := info.Sys().(*syscall.Stat_t)
	if !ok || info.Mode()&os.ModeSymlink != 0 || (FileIdentity{Device: uint64(pathStat.Dev), Inode: pathStat.Ino}) != g.identity {
		return errors.New("guard path identity changed")
	}
	return nil
}

func (g *platformDirectoryGuardLease) Restore() error {
	defer g.file.Close()
	if !g.live {
		return g.Validate()
	}
	fd := int(g.file.Fd())
	if err := g.ops.Fchmod(fd, 0700); err != nil {
		return g.restoreFailure("prepare restore", err)
	}
	if err := g.ops.Sync(g.file); err != nil {
		return g.restoreFailure("sync guarded metadata", err)
	}
	if err := g.ops.Fchown(fd, int(g.uid), int(g.gid)); err != nil {
		return g.restoreFailure("restore owner", err)
	}
	if err := g.ops.Fchmod(fd, g.mode); err != nil {
		return g.restoreFailure("restore mode", err)
	}
	if err := g.ops.Sync(g.file); err != nil {
		return g.restoreFailure("sync restored metadata", err)
	}
	if err := g.validateMetadata(g.uid, g.gid, g.mode); err != nil {
		return g.restoreFailure("verify restored metadata", err)
	}
	return nil
}

func (g *platformDirectoryGuardLease) seizeRoot() error {
	fd := int(g.file.Fd())
	chownErr := g.ops.Fchown(fd, 0, 0)
	chmodErr := g.ops.Fchmod(fd, 0700)
	syncErr := g.ops.Sync(g.file)
	return errors.Join(chownErr, chmodErr, syncErr)
}

func (g *platformDirectoryGuardLease) restoreFailure(step string, stepErr error) error {
	seizeErr := g.seizeRoot()
	verifyErr := g.validateMetadata(0, 0, 0700)
	return errors.Join(fmt.Errorf("%s: %w", step, stepErr), wrapOptional("re-seize root control", seizeErr), wrapOptional("verify root control", verifyErr))
}

func wrapOptional(message string, err error) error {
	if err == nil {
		return nil
	}
	return fmt.Errorf("%s: %w", message, err)
}

func (g *platformDirectoryGuardLease) validateMetadata(uid, gid, mode uint32) error {
	actualUID, actualGID, actualMode, err := g.ops.Metadata(int(g.file.Fd()))
	if err != nil {
		return err
	}
	if actualUID != uid || actualGID != gid || actualMode != mode {
		return fmt.Errorf("unexpected metadata uid=%d gid=%d mode=%04o", actualUID, actualGID, actualMode)
	}
	return nil
}

func (g *platformDirectoryGuardLease) recoverPartialAcquire(acquireErr error) error {
	fd := int(g.file.Fd())
	restoreErr := errors.Join(g.ops.Fchmod(fd, 0700), g.ops.Sync(g.file), g.ops.Fchown(fd, int(g.uid), int(g.gid)), g.ops.Fchmod(fd, g.mode), g.ops.Sync(g.file))
	if restoreErr == nil {
		restoreErr = g.validateMetadata(g.uid, g.gid, g.mode)
	}
	if restoreErr == nil {
		return acquireErr
	}
	seizeErr := g.seizeRoot()
	verifyErr := g.validateMetadata(0, 0, 0700)
	return errors.Join(ErrManualRepairRequired, acquireErr, fmt.Errorf("partial guard rollback: %w", restoreErr), wrapOptional("re-seize root control", seizeErr), wrapOptional("verify root control", verifyErr))
}

func validateRootAncestors(parent string) error {
	for _, ancestor := range []string{"/", "/var", "/var/lib"} {
		info, err := os.Lstat(ancestor)
		if err != nil {
			return err
		}
		stat, ok := info.Sys().(*syscall.Stat_t)
		if !ok || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 || stat.Uid != 0 || stat.Gid != 0 || info.Mode().Perm()&0022 != 0 {
			return fmt.Errorf("untrusted live ancestor: %s", ancestor)
		}
	}
	return nil
}

func platformPathIdentity(path string) (FileIdentity, error) {
	info, err := os.Lstat(path)
	if err != nil {
		return FileIdentity{}, err
	}
	if !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
		return FileIdentity{}, errors.New("SQLite target is not a regular no-follow path")
	}
	stat, ok := info.Sys().(*syscall.Stat_t)
	if !ok {
		return FileIdentity{}, errors.New("SQLite target stat unavailable")
	}
	return FileIdentity{Device: uint64(stat.Dev), Inode: stat.Ino}, nil
}

func (platformTrustProbe) ValidateSource(path string, kind SourceKind) (FileIdentity, error) {
	uid, gid := uint32(0), uint32(0)
	if kind == SourceLive {
		var err error
		uid, gid, err = serviceIDs()
		if err != nil {
			return FileIdentity{}, err
		}
	}
	if kind == SourceBackup {
		if err := validateBackupChain(filepath.Dir(path)); err != nil {
			return FileIdentity{}, fmt.Errorf("source parent: %w", err)
		}
	} else if _, err := validateNode(filepath.Dir(path), true, 0, 0, 0700); err != nil {
		return FileIdentity{}, fmt.Errorf("source parent: %w", err)
	}
	return validateNode(path, false, uid, gid, 0600)
}
func (platformTrustProbe) ValidateBackupDirectory(path string) (FileIdentity, error) {
	if err := validateBackupChain(path); err != nil {
		return FileIdentity{}, err
	}
	return validateNode(path, true, 0, 0, 0700)
}
func (platformTrustProbe) ValidateSidecar(path string, kind SourceKind) (FileIdentity, bool, error) {
	if _, err := os.Lstat(path); errors.Is(err, os.ErrNotExist) {
		return FileIdentity{}, false, nil
	} else if err != nil {
		return FileIdentity{}, false, err
	}
	uid, gid := uint32(0), uint32(0)
	if kind == SourceLive {
		var err error
		uid, gid, err = serviceIDs()
		if err != nil {
			return FileIdentity{}, false, err
		}
	}
	id, err := validateNode(path, false, uid, gid, 0600)
	return id, true, err
}
func (platformTrustProbe) SecureOutput(path string) error {
	if err := os.Chmod(path, 0600); err != nil {
		return err
	}
	if err := os.Chown(path, 0, 0); err != nil {
		return err
	}
	_, err := validateNode(path, false, 0, 0, 0600)
	return err
}
func serviceIDs() (uint32, uint32, error) {
	u, err := user.Lookup("superpan-license")
	if err != nil {
		return 0, 0, err
	}
	uid, err := strconv.ParseUint(u.Uid, 10, 32)
	if err != nil {
		return 0, 0, err
	}
	g, err := user.LookupGroup("superpan-license")
	if err != nil {
		return 0, 0, err
	}
	gid, err := strconv.ParseUint(g.Gid, 10, 32)
	if err != nil {
		return 0, 0, err
	}
	return uint32(uid), uint32(gid), nil
}
func validateNode(path string, directory bool, uid, gid uint32, mode os.FileMode) (FileIdentity, error) {
	info, err := os.Lstat(path)
	if err != nil {
		return FileIdentity{}, err
	}
	if info.Mode()&os.ModeSymlink != 0 || (directory && !info.IsDir()) || (!directory && !info.Mode().IsRegular()) {
		return FileIdentity{}, errors.New("wrong node type or symlink")
	}
	stat, ok := info.Sys().(*syscall.Stat_t)
	if !ok {
		return FileIdentity{}, errors.New("stat metadata unavailable")
	}
	if (!directory && stat.Nlink != 1) || stat.Uid != uid || stat.Gid != gid || info.Mode().Perm() != mode {
		return FileIdentity{}, fmt.Errorf("untrusted metadata uid=%d gid=%d mode=%04o nlink=%d", stat.Uid, stat.Gid, info.Mode().Perm(), stat.Nlink)
	}
	flags := syscall.O_RDONLY | syscall.O_CLOEXEC | syscall.O_NOFOLLOW
	if directory {
		flags |= syscall.O_DIRECTORY
	}
	fd, openErr := syscall.Open(path, flags, 0)
	if openErr != nil {
		return FileIdentity{}, fmt.Errorf("no-follow open: %w", openErr)
	}
	var opened syscall.Stat_t
	fstatErr := syscall.Fstat(fd, &opened)
	closeErr := syscall.Close(fd)
	if fstatErr != nil {
		return FileIdentity{}, fmt.Errorf("descriptor stat: %w", fstatErr)
	}
	if closeErr != nil {
		return FileIdentity{}, fmt.Errorf("descriptor close: %w", closeErr)
	}
	if opened.Dev != stat.Dev || opened.Ino != stat.Ino || opened.Nlink != stat.Nlink {
		return FileIdentity{}, errors.New("path identity changed during no-follow validation")
	}
	buf := make([]byte, 1)
	n, aclErr := syscall.Getxattr(path, "system.posix_acl_access", buf)
	if aclErr == nil || (aclErr == syscall.ERANGE && n >= 0) {
		return FileIdentity{}, errors.New("POSIX ACL is not permitted")
	}
	if aclErr != syscall.ENODATA && aclErr != syscall.ENOTSUP {
		return FileIdentity{}, fmt.Errorf("ACL check: %w", aclErr)
	}
	return FileIdentity{Device: uint64(stat.Dev), Inode: stat.Ino}, nil
}

func validateBackupChain(candidate string) error {
	candidate = filepath.Clean(candidate)
	root := filepath.Clean(backupRoot)
	rel, err := filepath.Rel(root, candidate)
	if err != nil || rel == ".." || filepath.IsAbs(rel) || (len(rel) > 3 && rel[:3] == ".."+string(os.PathSeparator)) {
		return errors.New("backup directory escapes fixed root")
	}
	current := root
	if _, err := validateNode(current, true, 0, 0, 0700); err != nil {
		return err
	}
	if rel == "." {
		return nil
	}
	for _, component := range splitPath(rel) {
		current = filepath.Join(current, component)
		if _, err := validateNode(current, true, 0, 0, 0700); err != nil {
			return err
		}
	}
	return nil
}

func splitPath(value string) []string {
	var result []string
	for value != "." && value != "" {
		dir, base := filepath.Split(value)
		result = append([]string{base}, result...)
		value = filepath.Clean(dir)
	}
	return result
}
