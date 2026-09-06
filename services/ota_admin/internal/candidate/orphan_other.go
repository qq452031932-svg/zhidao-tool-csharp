//go:build !linux

package candidate

import (
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
)

func cleanupStaleUploadArtifacts(root string, cutoff time.Time) error {
	handle, err := os.Open(root)
	if err != nil {
		return err
	}
	defer handle.Close()
	for {
		entries, readErr := handle.ReadDir(256)
		if readErr != nil && !errors.Is(readErr, io.EOF) {
			return readErr
		}
		for _, entry := range entries {
			name := entry.Name()
			isArchive, isExtraction := strings.HasPrefix(name, ".upload-"), strings.HasPrefix(name, ".extract-")
			if !isArchive && !isExtraction {
				continue
			}
			path := filepath.Join(root, name)
			info, err := os.Lstat(path)
			if err != nil {
				if errors.Is(err, os.ErrNotExist) {
					continue
				}
				return err
			}
			if !info.ModTime().Before(cutoff) || info.Mode()&os.ModeSymlink != 0 || isArchive && !info.Mode().IsRegular() || isExtraction && !info.IsDir() {
				continue
			}
			if isExtraction {
				dir, err := os.Open(path)
				if err != nil {
					return err
				}
				children, readErr := dir.ReadDir(257)
				closeErr := dir.Close()
				if readErr != nil && !errors.Is(readErr, io.EOF) {
					return readErr
				}
				if closeErr != nil {
					return closeErr
				}
				if len(children) > 256 {
					return errors.New("stale extraction contains too many members")
				}
				for _, child := range children {
					childPath := filepath.Join(path, child.Name())
					childInfo, err := os.Lstat(childPath)
					if err != nil || !childInfo.Mode().IsRegular() || childInfo.Mode()&os.ModeSymlink != 0 {
						return errors.New("stale extraction contains unsafe member")
					}
					if err := os.Remove(childPath); err != nil {
						return err
					}
				}
			}
			if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
				return err
			}
		}
		if errors.Is(readErr, io.EOF) {
			break
		}
	}
	return nil
}
