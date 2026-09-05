//go:build linux

package store

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"

	"golang.org/x/sys/unix"
)

func prepareStatePath(path string) error {
	parent := filepath.Dir(path)
	parentInfo, err := os.Lstat(parent)
	if err != nil {
		return fmt.Errorf("lstat state directory: %w", err)
	}
	parentStat, ok := parentInfo.Sys().(*unix.Stat_t)
	if !ok {
		return errors.New("cannot inspect state directory ownership")
	}
	if err := validateStateDirectoryMetadata(parentStat.Uid, uint32(os.Geteuid()), parentInfo.Mode(), parentInfo.IsDir(), parentInfo.Mode()&os.ModeSymlink != 0); err != nil {
		return err
	}
	fd, err := unix.Open(path, unix.O_RDWR|unix.O_CLOEXEC|unix.O_NOFOLLOW|unix.O_CREAT|unix.O_EXCL, 0o600)
	if errors.Is(err, unix.EEXIST) {
		fd, err = unix.Open(path, unix.O_RDWR|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
	}
	if err != nil {
		return fmt.Errorf("open state database securely: %w", err)
	}
	defer unix.Close(fd)
	var stat unix.Stat_t
	if err := unix.Fstat(fd, &stat); err != nil {
		return fmt.Errorf("fstat state database: %w", err)
	}
	if err := validateStateFileMetadata(stat.Uid, uint32(os.Geteuid()), os.FileMode(stat.Mode), stat.Mode&unix.S_IFMT == unix.S_IFREG, false, uint64(stat.Nlink)); err != nil {
		return err
	}
	return nil
}

func verifyStateFiles(path string) error {
	for _, candidate := range []string{path, path + "-wal", path + "-shm"} {
		fd, err := unix.Open(candidate, unix.O_RDWR|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
		if errors.Is(err, unix.ENOENT) {
			continue
		}
		if err != nil {
			return fmt.Errorf("open state file without following symlinks: %w", err)
		}
		if err := unix.Fchmod(fd, 0o600); err != nil {
			_ = unix.Close(fd)
			return fmt.Errorf("chmod state file descriptor: %w", err)
		}
		var stat unix.Stat_t
		if err := unix.Fstat(fd, &stat); err != nil {
			_ = unix.Close(fd)
			return fmt.Errorf("fstat state file: %w", err)
		}
		if err := validateStateFileMetadata(stat.Uid, uint32(os.Geteuid()), os.FileMode(stat.Mode), stat.Mode&unix.S_IFMT == unix.S_IFREG, false, uint64(stat.Nlink)); err != nil {
			_ = unix.Close(fd)
			return fmt.Errorf("unsafe state file %q: %w", candidate, err)
		}
		if err := unix.Close(fd); err != nil {
			return fmt.Errorf("close state file: %w", err)
		}
	}
	return nil
}
