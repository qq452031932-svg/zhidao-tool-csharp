//go:build !linux

package candidate

import (
	"errors"
	"io"
	"os"
)

func scanCandidateArtifacts(root string, visit func(string) error) error {
	directory, err := os.Open(root)
	if err != nil {
		return err
	}
	defer directory.Close()
	for {
		entries, readErr := directory.ReadDir(256)
		if readErr != nil && !errors.Is(readErr, io.EOF) {
			return readErr
		}
		for _, entry := range entries {
			if id, ok := candidateIDFromArtifact(entry.Name()); ok {
				if err := visit(id); err != nil {
					return err
				}
			}
		}
		if errors.Is(readErr, io.EOF) {
			return nil
		}
	}
}
