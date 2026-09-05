//go:build linux

package store

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"golang.org/x/sys/unix"
)

func prepareStatePath(path string) (*statePathHandle, error) {
	dirFD, err := openStateDirectory(filepath.Dir(path))
	if err != nil {
		return nil, err
	}
	base := filepath.Base(path)
	existed := map[string]bool{}
	var databaseIdentity unix.Stat_t
	for _, name := range []string{base, base + "-wal", base + "-shm"} {
		fd, openErr := unix.Openat(dirFD, name, unix.O_RDWR|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
		if errors.Is(openErr, unix.ENOENT) {
			continue
		}
		if openErr != nil {
			unix.Close(dirFD)
			return nil, fmt.Errorf("inspect preexisting state file: %w", openErr)
		}
		if err := validateOpenedStateFile(fd); err != nil {
			unix.Close(fd)
			unix.Close(dirFD)
			return nil, err
		}
		if name == base {
			if err := unix.Fstat(fd, &databaseIdentity); err != nil {
				unix.Close(fd)
				unix.Close(dirFD)
				return nil, err
			}
		}
		unix.Close(fd)
		existed[name] = true
	}
	if !existed[base] {
		fd, createErr := unix.Openat(dirFD, base, unix.O_RDWR|unix.O_CLOEXEC|unix.O_NOFOLLOW|unix.O_CREAT|unix.O_EXCL, 0o600)
		if createErr != nil {
			unix.Close(dirFD)
			return nil, createErr
		}
		if err := validateOpenedStateFile(fd); err != nil {
			unix.Close(fd)
			unix.Close(dirFD)
			return nil, err
		}
		if err := unix.Fstat(fd, &databaseIdentity); err != nil {
			unix.Close(fd)
			unix.Close(dirFD)
			return nil, err
		}
		unix.Close(fd)
		existed[base] = true
	}
	h := &statePathHandle{sqlitePath: fmt.Sprintf("/proc/self/fd/%d/%s", dirFD, base), close: func() error { return unix.Close(dirFD) }}
	h.verify = func() error {
		return verifyPinnedStateFiles(dirFD, base, existed, databaseIdentity.Dev, databaseIdentity.Ino)
	}
	return h, nil
}

func openStateDirectory(path string) (int, error) {
	current, err := unix.Open("/", unix.O_RDONLY|unix.O_CLOEXEC|unix.O_DIRECTORY|unix.O_NOFOLLOW, 0)
	if err != nil {
		return -1, err
	}
	components := strings.Split(strings.TrimPrefix(filepath.Clean(path), "/"), "/")
	for _, component := range components {
		if component == "" {
			continue
		}
		next, openErr := unix.Openat(current, component, unix.O_RDONLY|unix.O_CLOEXEC|unix.O_DIRECTORY|unix.O_NOFOLLOW, 0)
		unix.Close(current)
		if openErr != nil {
			return -1, openErr
		}
		current = next
	}
	var stat unix.Stat_t
	if err := unix.Fstat(current, &stat); err != nil {
		unix.Close(current)
		return -1, err
	}
	if err := validateStateDirectoryMetadata(stat.Uid, uint32(os.Geteuid()), os.FileMode(stat.Mode), stat.Mode&unix.S_IFMT == unix.S_IFDIR, false); err != nil {
		unix.Close(current)
		return -1, err
	}
	return current, nil
}

func validateOpenedStateFile(fd int) error {
	var stat unix.Stat_t
	if err := unix.Fstat(fd, &stat); err != nil {
		return err
	}
	return validateStateFileMetadata(stat.Uid, uint32(os.Geteuid()), os.FileMode(stat.Mode), stat.Mode&unix.S_IFMT == unix.S_IFREG, false, uint64(stat.Nlink))
}

func verifyPinnedStateFiles(dirFD int, base string, existed map[string]bool, databaseDevice uint64, databaseInode uint64) error {
	for _, name := range []string{base, base + "-wal", base + "-shm"} {
		fd, err := unix.Openat(dirFD, name, unix.O_RDWR|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
		if errors.Is(err, unix.ENOENT) {
			if name == base {
				return errors.New("main database disappeared after secure open")
			}
			continue
		}
		if err != nil {
			return err
		}
		var stat unix.Stat_t
		if err := unix.Fstat(fd, &stat); err != nil {
			unix.Close(fd)
			return err
		}
		if !existed[name] && stat.Uid == uint32(os.Geteuid()) && stat.Mode&unix.S_IFMT == unix.S_IFREG && stat.Nlink == 1 {
			if err := unix.Fchmod(fd, 0o600); err != nil {
				unix.Close(fd)
				return err
			}
		}
		if err := validateOpenedStateFile(fd); err != nil {
			unix.Close(fd)
			return err
		}
		if name == base && (uint64(stat.Dev) != databaseDevice || stat.Ino != databaseInode) {
			unix.Close(fd)
			return errors.New("database identity changed after secure open")
		}
		unix.Close(fd)
	}
	return nil
}
