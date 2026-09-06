//go:build linux

package candidate

import (
	"errors"
	"fmt"
	"io"
	"os"

	"golang.org/x/sys/unix"
)

type cleanupIdentity struct {
	name     string
	device   uint64
	inode    uint64
	isDir    bool
	present  bool
	children []cleanupIdentity
}

func deleteCandidatePaths(root, id string) error {
	archiveName, extractionName, err := candidatePathNames(id)
	if err != nil {
		return err
	}
	rootFD, err := openCandidateRoot(root)
	if err != nil {
		return fmt.Errorf("open staging root: %w", err)
	}
	defer unix.Close(rootFD)
	archive, err := preflightAt(rootFD, archiveName, false)
	if err != nil {
		return err
	}
	extraction, err := preflightAt(rootFD, extractionName, true)
	if err != nil {
		return err
	}
	if extraction.present {
		directoryFD, err := unix.Openat(rootFD, extraction.name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
		if err != nil {
			return err
		}
		directory := os.NewFile(uintptr(directoryFD), extraction.name)
		entries, err := directory.ReadDir(257)
		if err != nil && !errors.Is(err, io.EOF) || len(entries) > 256 {
			directory.Close()
			return err
		}
		for _, entry := range entries {
			child, err := preflightAt(directoryFD, entry.Name(), false)
			if err != nil || !child.present {
				directory.Close()
				return errors.New("candidate extraction contains an unsafe member")
			}
			extraction.children = append(extraction.children, child)
		}
		for _, child := range extraction.children {
			if err := verifyAt(directoryFD, child); err != nil {
				directory.Close()
				return err
			}
			if err := unix.Unlinkat(directoryFD, child.name, 0); err != nil {
				directory.Close()
				return err
			}
		}
		if err := directory.Close(); err != nil {
			return err
		}
		if err := verifyAt(rootFD, extraction); err != nil {
			return err
		}
		if err := unix.Unlinkat(rootFD, extraction.name, unix.AT_REMOVEDIR); err != nil {
			return err
		}
	}
	if archive.present {
		if err := verifyAt(rootFD, archive); err != nil {
			return err
		}
		if err := unix.Unlinkat(rootFD, archive.name, 0); err != nil {
			return err
		}
	}
	return unix.Fsync(rootFD)
}

func openCandidateRoot(root string) (int, error) {
	rootFD, err := unix.Open(root, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return -1, err
	}
	var rootStat unix.Stat_t
	if err := unix.Fstat(rootFD, &rootStat); err != nil || rootStat.Mode&unix.S_IFMT != unix.S_IFDIR || rootStat.Mode&0o777 != 0o700 || rootStat.Uid != uint32(os.Geteuid()) {
		unix.Close(rootFD)
		return -1, errors.New("staging root identity or permissions changed")
	}
	return rootFD, nil
}

func preflightAt(parentFD int, name string, directory bool) (cleanupIdentity, error) {
	if name == "" || name == "." || name == ".." {
		return cleanupIdentity{}, errors.New("candidate cleanup name is invalid")
	}
	var stat unix.Stat_t
	err := unix.Fstatat(parentFD, name, &stat, unix.AT_SYMLINK_NOFOLLOW)
	if errors.Is(err, unix.ENOENT) {
		return cleanupIdentity{name: name}, nil
	}
	if err != nil {
		return cleanupIdentity{}, err
	}
	want := uint32(unix.S_IFREG)
	if directory {
		want = unix.S_IFDIR
	}
	if stat.Mode&unix.S_IFMT != want || !safeLinkCount(directory, stat.Nlink) {
		return cleanupIdentity{}, errors.New("candidate cleanup path has an unsafe type or link count")
	}
	flags := unix.O_RDONLY | unix.O_NOFOLLOW | unix.O_CLOEXEC
	if directory {
		flags |= unix.O_DIRECTORY
	}
	fd, err := unix.Openat(parentFD, name, flags, 0)
	if err != nil {
		return cleanupIdentity{}, err
	}
	defer unix.Close(fd)
	var opened unix.Stat_t
	if err := unix.Fstat(fd, &opened); err != nil || opened.Dev != stat.Dev || opened.Ino != stat.Ino || opened.Mode&unix.S_IFMT != want || !safeLinkCount(directory, opened.Nlink) {
		return cleanupIdentity{}, errors.New("candidate cleanup path identity changed")
	}
	return cleanupIdentity{name: name, device: uint64(stat.Dev), inode: stat.Ino, isDir: directory, present: true}, nil
}

func verifyAt(parentFD int, identity cleanupIdentity) error {
	var stat unix.Stat_t
	if err := unix.Fstatat(parentFD, identity.name, &stat, unix.AT_SYMLINK_NOFOLLOW); err != nil || uint64(stat.Dev) != identity.device || stat.Ino != identity.inode || !safeLinkCount(identity.isDir, stat.Nlink) {
		return errors.New("candidate cleanup path identity changed")
	}
	want := uint32(unix.S_IFREG)
	if identity.isDir {
		want = unix.S_IFDIR
	}
	if stat.Mode&unix.S_IFMT != want {
		return errors.New("candidate cleanup path type changed")
	}
	return nil
}

func safeLinkCount(directory bool, count uint64) bool {
	if directory {
		return count >= 2
	}
	return count == 1
}
