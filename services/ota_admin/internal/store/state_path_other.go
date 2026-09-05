//go:build !linux

package store

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
)

func prepareStatePath(path string) error {
	parent, err := os.Lstat(filepath.Dir(path))
	if err != nil {
		return fmt.Errorf("lstat state directory: %w", err)
	}
	if !parent.IsDir() || parent.Mode()&os.ModeSymlink != 0 {
		return errors.New("state parent must be a non-symlink directory")
	}
	file, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0o600)
	if errors.Is(err, os.ErrExist) {
		info, statErr := os.Lstat(path)
		if statErr != nil {
			return statErr
		}
		if !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
			return errors.New("state database must be a regular non-symlink file")
		}
		return nil
	}
	if err != nil {
		return fmt.Errorf("create state database: %w", err)
	}
	return file.Close()
}

func verifyStateFiles(path string) error {
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
