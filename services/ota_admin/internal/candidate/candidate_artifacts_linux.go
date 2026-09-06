//go:build linux

package candidate

import (
	"errors"
	"io"
	"os"

	"golang.org/x/sys/unix"
)

func scanCandidateArtifacts(root string, visit func(string) error) error {
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
