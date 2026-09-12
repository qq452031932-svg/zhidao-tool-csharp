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
	} else if _, err := validateNode(filepath.Dir(path), true, uid, gid, 0700); err != nil {
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
