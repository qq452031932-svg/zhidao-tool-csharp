//go:build !linux

package deploy

import (
	"errors"
	"io"
	"os"
	"path/filepath"

	"fanfh.cn/superpan/ota-admin/internal/upload"
)

type boundRoots struct {
	root, generations string
}

func bindRoots(root, generations string) (*boundRoots, error) {
	if err := os.Mkdir(generations, 0o755); err != nil && !errors.Is(err, os.ErrExist) {
		return nil, err
	}
	info, err := os.Lstat(generations)
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("generations root is unsafe")
	}
	return &boundRoots{root: root, generations: generations}, nil
}
func (bound *boundRoots) verify() error { return nil }
func (bound *boundRoots) generationExists(name string) (bool, error) {
	info, err := os.Lstat(filepath.Join(bound.generations, name))
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	return err == nil && info.IsDir() && info.Mode()&os.ModeSymlink == 0, err
}
func (bound *boundRoots) mkdirGeneration(name string, mode uint32) error {
	return os.Mkdir(filepath.Join(bound.generations, name), os.FileMode(mode))
}
func (bound *boundRoots) openGenerationOutput(directory, name string, mode uint32) (*os.File, error) {
	return os.OpenFile(filepath.Join(bound.generations, directory, name), os.O_WRONLY|os.O_CREATE|os.O_EXCL, os.FileMode(mode))
}
func (bound *boundRoots) chmodGeneration(name string, mode uint32) error {
	return os.Chmod(filepath.Join(bound.generations, name), os.FileMode(mode))
}
func (bound *boundRoots) syncGeneration(name string) error {
	return syncDirectory(filepath.Join(bound.generations, name))
}
func (bound *boundRoots) renameGeneration(oldName, newName string) error {
	return os.Rename(filepath.Join(bound.generations, oldName), filepath.Join(bound.generations, newName))
}
func (bound *boundRoots) syncGenerations() error { return syncDirectory(bound.generations) }
func (bound *boundRoots) syncRoot() error        { return syncDirectory(bound.root) }
func (bound *boundRoots) symlinkRoot(target, name string) error {
	return os.Symlink(target, filepath.Join(bound.root, name))
}
func (bound *boundRoots) renameRoot(oldName, newName string) error {
	return os.Rename(filepath.Join(bound.root, oldName), filepath.Join(bound.root, newName))
}
func (bound *boundRoots) readRootLink(name string) (string, error) {
	return os.Readlink(filepath.Join(bound.root, name))
}
func (bound *boundRoots) unlinkRoot(name string) error {
	return os.Remove(filepath.Join(bound.root, name))
}
func (bound *boundRoots) rootEntry(name string) (rootEntry, error) {
	info, err := os.Lstat(filepath.Join(bound.root, name))
	if errors.Is(err, os.ErrNotExist) {
		return rootEntry{}, nil
	}
	if err != nil {
		return rootEntry{}, err
	}
	return rootEntry{exists: true, directory: info.IsDir(), symlink: info.Mode()&os.ModeSymlink != 0, sameDevice: true, mode: info.Mode().Perm()}, nil
}
func (bound *boundRoots) rootDirectoryEmpty(name string) (bool, error) {
	handle, err := os.Open(filepath.Join(bound.root, name))
	if err != nil {
		return false, err
	}
	entries, readErr := handle.ReadDir(1)
	closeErr := handle.Close()
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return false, readErr
	}
	if closeErr != nil {
		return false, closeErr
	}
	return len(entries) == 0, nil
}
func (bound *boundRoots) removeRootDirectory(name string) error {
	return os.Remove(filepath.Join(bound.root, name))
}
func (bound *boundRoots) mkdirRoot(name string, mode uint32) error {
	return os.Mkdir(filepath.Join(bound.root, name), os.FileMode(mode))
}
func (bound *boundRoots) removeGeneration(name string) error {
	return removeGeneration(bound.generations, name)
}

func (disk *Disk) openGenerationEnvelope(name string) (*upload.Envelope, func() error, error) {
	envelope, err := generationEnvelope(filepath.Join(disk.generations, name))
	if err != nil {
		return nil, nil, err
	}
	return envelope, func() error { return disk.bound.verify() }, nil
}

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
