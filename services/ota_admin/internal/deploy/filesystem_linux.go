//go:build linux

package deploy

import (
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"syscall"

	"fanfh.cn/superpan/ota-admin/internal/upload"
	"golang.org/x/sys/unix"
)

type boundRoots struct {
	rootFD, generationsFD          int
	rootDev, rootIno               uint64
	generationsDev, generationsIno uint64
	rootPath, generationsPath      string
}

func bindRoots(root, generations string) (*boundRoots, error) {
	rootFD, err := unix.Open(root, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return nil, err
	}
	if err := unix.Mkdirat(rootFD, filepath.Base(generations), 0o755); err != nil && !errors.Is(err, unix.EEXIST) {
		unix.Close(rootFD)
		return nil, err
	}
	generationsFD, err := unix.Openat(rootFD, filepath.Base(generations), unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		unix.Close(rootFD)
		return nil, err
	}
	var rootStat, generationsStat unix.Stat_t
	if err := unix.Fstat(rootFD, &rootStat); err != nil {
		unix.Close(generationsFD)
		unix.Close(rootFD)
		return nil, err
	}
	if err := unix.Fstat(generationsFD, &generationsStat); err != nil {
		unix.Close(generationsFD)
		unix.Close(rootFD)
		return nil, err
	}
	if rootStat.Mode&unix.S_IFMT != unix.S_IFDIR || rootStat.Mode&0o022 != 0 || generationsStat.Mode&unix.S_IFMT != unix.S_IFDIR || generationsStat.Mode&0o022 != 0 {
		unix.Close(generationsFD)
		unix.Close(rootFD)
		return nil, errors.New("generations root is unsafe")
	}
	bound := &boundRoots{rootFD: rootFD, generationsFD: generationsFD, rootDev: uint64(rootStat.Dev), rootIno: rootStat.Ino, generationsDev: uint64(generationsStat.Dev), generationsIno: generationsStat.Ino, rootPath: root, generationsPath: generations}
	runtime.SetFinalizer(bound, func(value *boundRoots) {
		_ = unix.Close(value.generationsFD)
		_ = unix.Close(value.rootFD)
	})
	if err := bound.verify(); err != nil {
		runtime.SetFinalizer(bound, nil)
		unix.Close(generationsFD)
		unix.Close(rootFD)
		return nil, err
	}
	return bound, nil
}

func (bound *boundRoots) verify() error {
	if bound == nil {
		return errors.New("publication roots are not bound")
	}
	var openedRoot, openedGenerations, namedRoot, namedGenerations unix.Stat_t
	if unix.Fstat(bound.rootFD, &openedRoot) != nil || unix.Fstat(bound.generationsFD, &openedGenerations) != nil || unix.Lstat(bound.rootPath, &namedRoot) != nil || unix.Lstat(bound.generationsPath, &namedGenerations) != nil {
		return errors.New("publication root identity cannot be verified")
	}
	if openedRoot.Mode&unix.S_IFMT != unix.S_IFDIR || openedRoot.Mode&0o022 != 0 || openedGenerations.Mode&unix.S_IFMT != unix.S_IFDIR || openedGenerations.Mode&0o022 != 0 || namedRoot.Mode&unix.S_IFMT != unix.S_IFDIR || namedRoot.Mode&0o022 != 0 || namedGenerations.Mode&unix.S_IFMT != unix.S_IFDIR || namedGenerations.Mode&0o022 != 0 || uint64(openedRoot.Dev) != bound.rootDev || openedRoot.Ino != bound.rootIno || uint64(openedGenerations.Dev) != bound.generationsDev || openedGenerations.Ino != bound.generationsIno || uint64(namedRoot.Dev) != bound.rootDev || namedRoot.Ino != bound.rootIno || uint64(namedGenerations.Dev) != bound.generationsDev || namedGenerations.Ino != bound.generationsIno {
		return errors.New("publication root identity changed")
	}
	return nil
}

func (bound *boundRoots) generationExists(name string) (bool, error) {
	var stat unix.Stat_t
	err := unix.Fstatat(bound.generationsFD, name, &stat, unix.AT_SYMLINK_NOFOLLOW)
	if errors.Is(err, unix.ENOENT) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	if stat.Mode&unix.S_IFMT != unix.S_IFDIR || stat.Nlink < 1 {
		return false, errors.New("generation path is unsafe")
	}
	return true, nil
}

func (bound *boundRoots) mkdirGeneration(name string, mode uint32) error {
	return unix.Mkdirat(bound.generationsFD, name, mode)
}

func (bound *boundRoots) openGenerationOutput(directory, name string, mode uint32) (*os.File, error) {
	directoryFD, err := unix.Openat(bound.generationsFD, directory, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return nil, err
	}
	defer unix.Close(directoryFD)
	fd, err := unix.Openat(directoryFD, name, unix.O_WRONLY|unix.O_CREAT|unix.O_EXCL|unix.O_NOFOLLOW|unix.O_CLOEXEC, mode)
	if err != nil {
		return nil, err
	}
	return os.NewFile(uintptr(fd), name), nil
}

func (bound *boundRoots) chmodGeneration(name string, mode uint32) error {
	fd, err := unix.Openat(bound.generationsFD, name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return err
	}
	defer unix.Close(fd)
	return unix.Fchmod(fd, mode)
}

func (bound *boundRoots) syncGeneration(name string) error {
	fd, err := unix.Openat(bound.generationsFD, name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return err
	}
	defer unix.Close(fd)
	return unix.Fsync(fd)
}

func (bound *boundRoots) renameGeneration(oldName, newName string) error {
	return unix.Renameat(bound.generationsFD, oldName, bound.generationsFD, newName)
}

func (bound *boundRoots) syncGenerations() error { return unix.Fsync(bound.generationsFD) }
func (bound *boundRoots) syncRoot() error        { return unix.Fsync(bound.rootFD) }

func (bound *boundRoots) symlinkRoot(target, name string) error {
	return unix.Symlinkat(target, bound.rootFD, name)
}

func (bound *boundRoots) renameRoot(oldName, newName string) error {
	return unix.Renameat(bound.rootFD, oldName, bound.rootFD, newName)
}

func (bound *boundRoots) readRootLink(name string) (string, error) {
	buffer := make([]byte, 4096)
	n, err := unix.Readlinkat(bound.rootFD, name, buffer)
	if err != nil {
		return "", err
	}
	if n == len(buffer) {
		return "", errors.New("symbolic link target is too long")
	}
	return string(buffer[:n]), nil
}

func (bound *boundRoots) unlinkRoot(name string) error {
	return unix.Unlinkat(bound.rootFD, name, 0)
}

func (bound *boundRoots) rootEntry(name string) (rootEntry, error) {
	var stat unix.Stat_t
	err := unix.Fstatat(bound.rootFD, name, &stat, unix.AT_SYMLINK_NOFOLLOW)
	if errors.Is(err, unix.ENOENT) {
		return rootEntry{}, nil
	}
	if err != nil {
		return rootEntry{}, err
	}
	return rootEntry{exists: true, directory: stat.Mode&unix.S_IFMT == unix.S_IFDIR, symlink: stat.Mode&unix.S_IFMT == unix.S_IFLNK, sameDevice: uint64(stat.Dev) == bound.rootDev, mode: os.FileMode(stat.Mode & 0o777)}, nil
}

func (bound *boundRoots) rootDirectoryEmpty(name string) (bool, error) {
	fd, err := unix.Openat(bound.rootFD, name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return false, err
	}
	directory := os.NewFile(uintptr(fd), name)
	entries, readErr := directory.ReadDir(1)
	closeErr := directory.Close()
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return false, readErr
	}
	if closeErr != nil {
		return false, closeErr
	}
	return len(entries) == 0, nil
}

func (bound *boundRoots) removeRootDirectory(name string) error {
	return unix.Unlinkat(bound.rootFD, name, unix.AT_REMOVEDIR)
}

func (bound *boundRoots) mkdirRoot(name string, mode uint32) error {
	return unix.Mkdirat(bound.rootFD, name, mode)
}

func (bound *boundRoots) removeGeneration(name string) error {
	if name == "" || name == "." || name == ".." {
		return errors.New("generation removal name invalid")
	}
	dirFD, err := unix.Openat(bound.generationsFD, name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
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
	if err := unix.Unlinkat(bound.generationsFD, name, unix.AT_REMOVEDIR); err != nil {
		return err
	}
	return unix.Fsync(bound.generationsFD)
}

func (disk *Disk) openGenerationEnvelope(name string) (*upload.Envelope, *os.File, func() error, error) {
	if err := disk.bound.verify(); err != nil {
		return nil, nil, nil, err
	}
	fd, err := unix.Openat(disk.bound.generationsFD, name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return nil, nil, nil, err
	}
	directory := os.NewFile(uintptr(fd), name)
	fail := func(cause error) (*upload.Envelope, *os.File, func() error, error) {
		return nil, nil, nil, errors.Join(cause, directory.Close())
	}
	info, err := directory.Stat()
	if err != nil || validateImmutableGeneration(info, true) != nil {
		return fail(errors.New("generation root is unsafe"))
	}
	entries, readErr := directory.ReadDir(maxGenerationEntries + 1)
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return fail(readErr)
	}
	if len(entries) == 0 || len(entries) > maxGenerationEntries {
		return fail(errors.New("generation closure is invalid"))
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Name() < entries[j].Name() })
	envelope := &upload.Envelope{ExtractionRoot: fmt.Sprintf("/proc/self/fd/%d", fd)}
	var expanded int64
	for _, entry := range entries {
		var stat unix.Stat_t
		if err := unix.Fstatat(fd, entry.Name(), &stat, unix.AT_SYMLINK_NOFOLLOW); err != nil || stat.Mode&unix.S_IFMT != unix.S_IFREG || stat.Nlink != 1 || stat.Mode&0o222 != 0 || stat.Size < 0 || stat.Size > math.MaxInt64-expanded {
			return fail(errors.New("generation member is unsafe"))
		}
		expanded += stat.Size
		envelope.Entries = append(envelope.Entries, upload.Entry{Name: entry.Name(), Path: filepath.Join(envelope.ExtractionRoot, entry.Name()), Size: stat.Size})
	}
	envelope.ExpandedBytes = expanded
	closeEnvelope := func() error {
		return errors.Join(directory.Close(), disk.bound.verify())
	}
	return envelope, directory, closeEnvelope, nil
}

func (disk *Disk) verifyOpenedGeneration(name string, descriptor *os.File) error {
	if descriptor == nil {
		return errors.New("opened generation descriptor is missing")
	}
	opened, err := descriptor.Stat()
	if err != nil {
		return err
	}
	return errors.Join(disk.bound.verify(), disk.bound.verifyGenerationIdentity(name, opened))
}

func (bound *boundRoots) verifyGenerationIdentity(name string, opened os.FileInfo) error {
	openedStat, ok := opened.Sys().(*syscall.Stat_t)
	if !ok {
		return errors.New("opened generation identity unavailable")
	}
	var named unix.Stat_t
	if err := unix.Fstatat(bound.generationsFD, name, &named, unix.AT_SYMLINK_NOFOLLOW); err != nil || named.Mode&unix.S_IFMT != unix.S_IFDIR || named.Dev != openedStat.Dev || named.Ino != openedStat.Ino {
		return errors.New("generation identity changed during validation")
	}
	return nil
}

func sameFilesystem(left, right os.FileInfo) bool {
	leftStat, leftOK := left.Sys().(*syscall.Stat_t)
	rightStat, rightOK := right.Sys().(*syscall.Stat_t)
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
