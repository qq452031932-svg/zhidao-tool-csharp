//go:build !linux

package store

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
)

func prepareStatePath(path string) (*statePathHandle, error) {
	parent, err := os.Lstat(filepath.Dir(path))
	if err != nil {
		return nil, fmt.Errorf("lstat state directory: %w", err)
	}
	if !parent.IsDir() || parent.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("state parent must be a non-symlink directory")
	}
	file, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0o600)
	if errors.Is(err, os.ErrExist) {
		info, statErr := os.Lstat(path)
		if statErr != nil {
			return nil, statErr
		}
		if !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
			return nil, errors.New("state database must be regular")
		}
	}
	if err != nil && !errors.Is(err, os.ErrExist) {
		return nil, err
	}
	if file != nil {
		_ = file.Close()
	}
	h := &statePathHandle{sqlitePath: path, close: func() error { return nil }}
	h.verify = func() error {
		for _, candidate := range []string{path, path + "-wal", path + "-shm"} {
			info, err := os.Lstat(candidate)
			if errors.Is(err, os.ErrNotExist) {
				continue
			}
			if err != nil {
				return err
			}
			if !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
				return errors.New("state file is not regular")
			}
			if err := os.Chmod(candidate, 0o600); err != nil {
				return err
			}
		}
		return nil
	}
	return h, nil
}
