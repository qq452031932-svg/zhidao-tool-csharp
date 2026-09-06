//go:build linux

package candidate

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"os"

	"golang.org/x/sys/unix"
)

func hashCandidateArchive(root, name string) (string, int64, error) {
	rootFD, err := openCandidateRoot(root)
	if err != nil {
		return "", 0, err
	}
	defer unix.Close(rootFD)
	identity, err := preflightAt(rootFD, name, false)
	if err != nil || !identity.present {
		return "", 0, errors.New("candidate archive is not a regular file")
	}
	fd, err := unix.Openat(rootFD, name, unix.O_RDONLY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return "", 0, err
	}
	file := os.NewFile(uintptr(fd), name)
	defer file.Close()
	var before unix.Stat_t
	if err := unix.Fstat(fd, &before); err != nil || uint64(before.Dev) != identity.device || before.Ino != identity.inode || before.Nlink != 1 || before.Size < 0 {
		return "", 0, errors.New("candidate archive identity changed")
	}
	hash := sha256.New()
	if _, err := io.CopyBuffer(hash, file, make([]byte, 64<<10)); err != nil {
		return "", 0, err
	}
	var after unix.Stat_t
	if err := unix.Fstat(fd, &after); err != nil || after.Dev != before.Dev || after.Ino != before.Ino || after.Nlink != 1 || after.Size != before.Size || after.Mtim != before.Mtim {
		return "", 0, errors.New("candidate archive changed while hashing")
	}
	if err := verifyAt(rootFD, identity); err != nil {
		return "", 0, err
	}
	return hex.EncodeToString(hash.Sum(nil)), after.Size, nil
}

func adoptCandidatePaths(root, sourceArchive, sourceExtraction, destinationArchive, destinationExtraction string, entryNames []string) error {
	rootFD, err := openCandidateRoot(root)
	if err != nil {
		return err
	}
	defer unix.Close(rootFD)
	archive, err := preflightAt(rootFD, sourceArchive, false)
	if err != nil || !archive.present {
		return errors.New("candidate archive changed before adoption")
	}
	extraction, err := preflightAt(rootFD, sourceExtraction, true)
	if err != nil || !extraction.present {
		return errors.New("candidate extraction changed before adoption")
	}
	for _, target := range []string{destinationArchive, destinationExtraction} {
		var stat unix.Stat_t
		if err := unix.Fstatat(rootFD, target, &stat, unix.AT_SYMLINK_NOFOLLOW); !errors.Is(err, unix.ENOENT) {
			return errors.New("candidate destination already exists")
		}
	}
	directoryFD, err := unix.Openat(rootFD, sourceExtraction, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		return err
	}
	directory := os.NewFile(uintptr(directoryFD), sourceExtraction)
	entries, err := directory.ReadDir(257)
	if err != nil && !errors.Is(err, io.EOF) || len(entries) > 256 || len(entries) != len(entryNames) {
		directory.Close()
		return errors.New("candidate extraction closure changed")
	}
	wanted := make(map[string]bool, len(entryNames))
	for _, name := range entryNames {
		wanted[name] = true
	}
	for _, entry := range entries {
		child, err := preflightAt(directoryFD, entry.Name(), false)
		if err != nil || !child.present || !wanted[entry.Name()] {
			directory.Close()
			return errors.New("candidate extraction member changed")
		}
	}
	if err := directory.Close(); err != nil {
		return err
	}
	if err := verifyAt(rootFD, archive); err != nil {
		return err
	}
	if err := verifyAt(rootFD, extraction); err != nil {
		return err
	}
	if err := unix.Renameat(rootFD, sourceArchive, rootFD, destinationArchive); err != nil {
		return err
	}
	if err := unix.Renameat(rootFD, sourceExtraction, rootFD, destinationExtraction); err != nil {
		rollbackErr := unix.Renameat(rootFD, destinationArchive, rootFD, sourceArchive)
		return errors.Join(err, rollbackErr)
	}
	return nil
}
