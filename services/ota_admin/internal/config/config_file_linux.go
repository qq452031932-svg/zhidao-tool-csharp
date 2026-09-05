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
	parentFD, err := openConfigParentChain(filepath.Dir(clean))
	if err != nil {
		return nil, err
	}
	defer unix.Close(parentFD)
	fd, err := unix.Openat(parentFD, filepath.Base(clean), unix.O_RDONLY|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
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
	if err := ValidateRootOnlyMetadata(stat.Uid, stat.Gid, uint32(os.Getegid()), stat.Mode&0o777); err != nil {
		file.Close()
		return nil, err
	}
	return file, nil
}

func openConfigParentChain(parent string) (int, error) {
	current, err := unix.Open(string(filepath.Separator), unix.O_RDONLY|unix.O_CLOEXEC|unix.O_DIRECTORY|unix.O_NOFOLLOW, 0)
	if err != nil {
		return -1, fmt.Errorf("open root config ancestor: %w", err)
	}
	var rootStat unix.Stat_t
	if err := unix.Fstat(current, &rootStat); err != nil {
		unix.Close(current)
		return -1, err
	}
	if err := ValidateTrustedParentMetadata(rootStat.Uid, false, rootStat.Mode&0o777); err != nil {
		unix.Close(current)
		return -1, err
	}
	for _, component := range strings.Split(strings.TrimPrefix(filepath.Clean(parent), string(filepath.Separator)), string(filepath.Separator)) {
		if component == "" {
			continue
		}
		next, openErr := unix.Openat(current, component, unix.O_RDONLY|unix.O_CLOEXEC|unix.O_DIRECTORY|unix.O_NOFOLLOW, 0)
		if openErr != nil {
			unix.Close(current)
			return -1, fmt.Errorf("open config ancestor %q: %w", component, openErr)
		}
		var stat unix.Stat_t
		if statErr := unix.Fstat(next, &stat); statErr != nil {
			unix.Close(next)
			unix.Close(current)
			return -1, statErr
		}
		if validateErr := ValidateTrustedParentMetadata(stat.Uid, false, stat.Mode&0o777); validateErr != nil {
			unix.Close(next)
			unix.Close(current)
			return -1, fmt.Errorf("unsafe config ancestor %q: %w", component, validateErr)
		}
		unix.Close(current)
		current = next
	}
	return current, nil
}
