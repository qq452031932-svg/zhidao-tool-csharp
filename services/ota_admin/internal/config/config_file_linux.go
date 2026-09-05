//go:build linux

package config

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"golang.org/x/sys/unix"
)

func openTrustedConfig(path string) (*os.File, error) {
	clean := filepath.Clean(path)
	if !filepath.IsAbs(clean) {
		return nil, errors.New("config path must be absolute")
	}
	if err := validateConfigParents(filepath.Dir(clean)); err != nil {
		return nil, err
	}
	fd, err := unix.Open(clean, unix.O_RDONLY|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
	if err != nil {
		return nil, fmt.Errorf("open config without following symlinks: %w", err)
	}
	file := os.NewFile(uintptr(fd), clean)
	if file == nil {
		_ = unix.Close(fd)
		return nil, errors.New("open config file descriptor")
	}
	var stat unix.Stat_t
	if err := unix.Fstat(fd, &stat); err != nil {
		file.Close()
		return nil, fmt.Errorf("fstat config: %w", err)
	}
	if stat.Mode&unix.S_IFMT != unix.S_IFREG || stat.Nlink != 1 {
		file.Close()
		return nil, errors.New("config must be one regular non-hardlinked file")
	}
	if err := ValidateRootOnlyMetadata(stat.Uid, stat.Mode&0o777); err != nil {
		file.Close()
		return nil, err
	}
	return file, nil
}

func validateConfigParents(parent string) error {
	current := string(filepath.Separator)
	for _, component := range strings.Split(strings.TrimPrefix(filepath.Clean(parent), string(filepath.Separator)), string(filepath.Separator)) {
		if component != "" {
			current = filepath.Join(current, component)
		}
		info, err := os.Lstat(current)
		if err != nil {
			return fmt.Errorf("lstat config parent %q: %w", current, err)
		}
		if !info.IsDir() {
			return fmt.Errorf("config parent %q is not a directory", current)
		}
		if err := ValidateTrustedParentMetadata(info.Mode()&os.ModeSymlink != 0, uint32(info.Mode().Perm())); err != nil {
			return fmt.Errorf("unsafe config parent %q: %w", current, err)
		}
	}
	return nil
}
