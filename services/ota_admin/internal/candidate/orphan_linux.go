//go:build linux

package candidate

import (
	"errors"
	"io"
	"os"
	"strings"
	"time"

	"golang.org/x/sys/unix"
)

func cleanupStaleUploadArtifacts(root string, cutoff time.Time) error {
	rootFD, err := openCandidateRoot(root)
	if err != nil {
		return err
	}
	defer unix.Close(rootFD)
	dupFD, err := unix.Openat(rootFD, ".", unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return err
	}
	directory := os.NewFile(uintptr(dupFD), root)
	defer directory.Close()
	for {
		entries, readErr := directory.ReadDir(256)
		if readErr != nil && !errors.Is(readErr, io.EOF) {
			return readErr
		}
		for _, entry := range entries {
			name := entry.Name()
			isArchive, isExtraction := strings.HasPrefix(name, ".upload-"), strings.HasPrefix(name, ".extract-")
			if !isArchive && !isExtraction {
				continue
			}
			identity, err := preflightAt(rootFD, name, isExtraction)
			if err != nil || !identity.present {
				if err != nil {
					return err
				}
				continue
			}
			var stat unix.Stat_t
			if err := unix.Fstatat(rootFD, name, &stat, unix.AT_SYMLINK_NOFOLLOW); err != nil {
				return err
			}
			if !time.Unix(stat.Mtim.Sec, stat.Mtim.Nsec).Before(cutoff) {
				continue
			}
			if isExtraction {
				dirFD, err := unix.Openat(rootFD, name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
				if err != nil {
					return err
				}
				dir := os.NewFile(uintptr(dirFD), name)
				children, readErr := dir.ReadDir(257)
				if readErr != nil && !errors.Is(readErr, io.EOF) {
					dir.Close()
					return readErr
				}
				if len(children) > 256 {
					dir.Close()
					return errors.New("stale extraction contains too many members")
				}
				for _, child := range children {
					childIdentity, err := preflightAt(dirFD, child.Name(), false)
					if err != nil || !childIdentity.present {
						dir.Close()
						return errors.New("stale extraction contains unsafe member")
					}
					if err := verifyAt(dirFD, childIdentity); err != nil {
						dir.Close()
						return err
					}
					if err := unix.Unlinkat(dirFD, child.Name(), 0); err != nil {
						dir.Close()
						return err
					}
				}
				if err := unix.Fsync(dirFD); err != nil {
					dir.Close()
					return err
				}
				if err := dir.Close(); err != nil {
					return err
				}
			}
			if err := verifyAt(rootFD, identity); err != nil {
				return err
			}
			flags := 0
			if isExtraction {
				flags = unix.AT_REMOVEDIR
			}
			if err := unix.Unlinkat(rootFD, name, flags); err != nil && !errors.Is(err, unix.ENOENT) {
				return err
			}
		}
		if errors.Is(readErr, io.EOF) {
			break
		}
	}
	return unix.Fsync(rootFD)
}
