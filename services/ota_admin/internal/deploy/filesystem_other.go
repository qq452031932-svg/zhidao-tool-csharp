//go:build !linux

package deploy

import (
	"errors"
	"io"
	"os"
)

func sameFilesystem(os.FileInfo, os.FileInfo) bool { return true }

func openStableRegular(path string) (*os.File, os.FileInfo, error) {
	before, err := os.Lstat(path)
	if err != nil || !before.Mode().IsRegular() || before.Mode()&os.ModeSymlink != 0 {
		return nil, nil, errors.New("generation source is not a regular file")
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, nil, err
	}
	opened, err := file.Stat()
	if err != nil || !os.SameFile(before, opened) {
		file.Close()
		return nil, nil, errors.New("generation source identity changed")
	}
	return file, opened, nil
}
func syncDirectory(string) error { return nil }

func removeGeneration(root, name string) error {
	if name == "" || name == "." || name == ".." {
		return errors.New("generation removal name invalid")
	}
	path := root + string(os.PathSeparator) + name
	info, err := os.Lstat(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return errors.New("generation removal target unsafe")
	}
	h, err := os.Open(path)
	if err != nil {
		return err
	}
	entries, readErr := h.ReadDir(257)
	closeErr := h.Close()
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return readErr
	}
	if closeErr != nil {
		return closeErr
	}
	if len(entries) > 256 {
		return errors.New("generation contains too many files")
	}
	for _, entry := range entries {
		child := path + string(os.PathSeparator) + entry.Name()
		childInfo, err := os.Lstat(child)
		if err != nil || !childInfo.Mode().IsRegular() || childInfo.Mode()&os.ModeSymlink != 0 {
			return errors.New("generation member unsafe")
		}
	}
	if err := os.Chmod(path, 0o700); err != nil {
		return err
	}
	for _, entry := range entries {
		child := path + string(os.PathSeparator) + entry.Name()
		if err := os.Chmod(child, 0o600); err != nil {
			return err
		}
		if err := os.Remove(child); err != nil {
			return err
		}
	}
	return os.Remove(path)
}
