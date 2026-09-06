//go:build !linux

package candidate

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"os"
	"path/filepath"
)

func hashCandidateArchive(root, name string) (string, int64, error) {
	path := filepath.Join(root, name)
	before, err := os.Lstat(path)
	if err != nil || !before.Mode().IsRegular() || before.Mode()&os.ModeSymlink != 0 {
		return "", 0, errors.New("candidate archive is not a regular file")
	}
	file, err := os.Open(path)
	if err != nil {
		return "", 0, err
	}
	defer file.Close()
	opened, err := file.Stat()
	if err != nil || !os.SameFile(before, opened) {
		return "", 0, errors.New("candidate archive identity changed")
	}
	hash := sha256.New()
	if _, err := io.CopyBuffer(hash, file, make([]byte, 64<<10)); err != nil {
		return "", 0, err
	}
	after, statErr := file.Stat()
	pathAfter, pathErr := os.Lstat(path)
	if statErr != nil || pathErr != nil || !os.SameFile(opened, after) || !os.SameFile(after, pathAfter) || after.Size() != opened.Size() || after.ModTime() != opened.ModTime() {
		return "", 0, errors.New("candidate archive changed while hashing")
	}
	return hex.EncodeToString(hash.Sum(nil)), after.Size(), nil
}

func adoptCandidatePaths(root, sourceArchive, sourceExtraction, destinationArchive, destinationExtraction string, entryNames []string) error {
	for _, target := range []string{destinationArchive, destinationExtraction} {
		if _, err := os.Lstat(filepath.Join(root, target)); !errors.Is(err, os.ErrNotExist) {
			return errors.New("candidate destination already exists")
		}
	}
	extraction := filepath.Join(root, sourceExtraction)
	handle, err := os.Open(extraction)
	if err != nil {
		return errors.New("candidate extraction closure changed")
	}
	entries, err := handle.ReadDir(257)
	closeErr := handle.Close()
	if err != nil && !errors.Is(err, io.EOF) || len(entries) > 256 || len(entries) != len(entryNames) {
		return errors.New("candidate extraction closure changed")
	}
	if closeErr != nil {
		return closeErr
	}
	wanted := make(map[string]bool, len(entryNames))
	for _, name := range entryNames {
		wanted[name] = true
	}
	for _, entry := range entries {
		info, err := os.Lstat(filepath.Join(extraction, entry.Name()))
		if err != nil || !wanted[entry.Name()] || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
			return errors.New("candidate extraction member changed")
		}
	}
	if err := os.Rename(filepath.Join(root, sourceArchive), filepath.Join(root, destinationArchive)); err != nil {
		return err
	}
	if err := os.Rename(extraction, filepath.Join(root, destinationExtraction)); err != nil {
		rollbackErr := os.Rename(filepath.Join(root, destinationArchive), filepath.Join(root, sourceArchive))
		return errors.Join(err, rollbackErr)
	}
	return nil
}
