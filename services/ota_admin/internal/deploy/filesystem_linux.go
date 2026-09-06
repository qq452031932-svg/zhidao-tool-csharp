//go:build linux

package deploy

import (
	"errors"
	"io"
	"os"

	"golang.org/x/sys/unix"
)

func sameFilesystem(left, right os.FileInfo) bool {
	leftStat, leftOK := left.Sys().(*unix.Stat_t)
	rightStat, rightOK := right.Sys().(*unix.Stat_t)
	return leftOK && rightOK && leftStat.Dev == rightStat.Dev
}

func openStableRegular(path string) (*os.File, os.FileInfo, error) {
	fd, err := unix.Open(path, unix.O_RDONLY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return nil, nil, err
	}
	file := os.NewFile(uintptr(fd), path)
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() {
		file.Close()
		return nil, nil, errors.New("generation source is not regular")
	}
	return file, info, nil
}
func syncDirectory(path string) error {
	fd, err := unix.Open(path, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return err
	}
	defer unix.Close(fd)
	return unix.Fsync(fd)
}
func removeGeneration(root, name string) error {
	if name == "" || name == "." || name == ".." {
		return errors.New("generation removal name invalid")
	}
	rootFD, err := unix.Open(root, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return err
	}
	defer unix.Close(rootFD)
	dirFD, err := unix.Openat(rootFD, name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if errors.Is(err, unix.ENOENT) {
		return nil
	}
	if err != nil {
		return err
	}
	dir := os.NewFile(uintptr(dirFD), name)
	entries, readErr := dir.ReadDir(257)
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		dir.Close()
		return readErr
	}
	if len(entries) > 256 {
		dir.Close()
		return errors.New("generation contains too many files")
	}
	for _, entry := range entries {
		var stat unix.Stat_t
		if err := unix.Fstatat(dirFD, entry.Name(), &stat, unix.AT_SYMLINK_NOFOLLOW); err != nil || stat.Mode&unix.S_IFMT != unix.S_IFREG || stat.Nlink != 1 {
			dir.Close()
			return errors.New("generation member unsafe")
		}
	}
	if err := unix.Fchmod(dirFD, 0o700); err != nil {
		dir.Close()
		return err
	}
	for _, entry := range entries {
		if err := unix.Unlinkat(dirFD, entry.Name(), 0); err != nil {
			dir.Close()
			return err
		}
	}
	if err := dir.Close(); err != nil {
		return err
	}
	if err := unix.Unlinkat(rootFD, name, unix.AT_REMOVEDIR); err != nil {
		return err
	}
	return unix.Fsync(rootFD)
}
