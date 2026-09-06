//go:build !linux

package candidate

import (
	"errors"
	"os"
	"path/filepath"
)

func deleteCandidatePaths(root, id string) error {
	archiveName, extractionName, err := candidatePathNames(id)
	if err != nil {
		return err
	}
	archive := filepath.Join(root, archiveName)
	extraction := filepath.Join(root, extractionName)
	archiveExists, extractionExists := false, false
	if info, statErr := os.Lstat(archive); statErr == nil {
		if !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
			return errors.New("candidate archive is not a regular non-linked file")
		}
		archiveExists = true
	} else if !errors.Is(statErr, os.ErrNotExist) {
		return statErr
	}
	entries, readErr := os.ReadDir(extraction)
	if readErr == nil {
		info, statErr := os.Lstat(extraction)
		if statErr != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
			return errors.New("candidate extraction is not a non-linked directory")
		}
		extractionExists = true
		for _, entry := range entries {
			path := filepath.Join(extraction, entry.Name())
			info, statErr := os.Lstat(path)
			if statErr != nil || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
				return errors.New("candidate extraction contains an unsafe member")
			}
		}
	} else if !errors.Is(readErr, os.ErrNotExist) {
		return readErr
	}
	if extractionExists {
		for _, entry := range entries {
			if err := os.Remove(filepath.Join(extraction, entry.Name())); err != nil {
				return err
			}
		}
		if err := os.Remove(extraction); err != nil {
			return err
		}
	}
	if archiveExists {
		return os.Remove(archive)
	}
	return nil
}
