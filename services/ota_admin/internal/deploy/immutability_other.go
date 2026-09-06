//go:build !linux

package deploy

import (
	"errors"
	"os"
)

func validateImmutableGeneration(info os.FileInfo, directory bool) error {
	if info == nil || info.Mode()&os.ModeSymlink != 0 || directory != info.IsDir() {
		return errors.New("generation member type is invalid")
	}
	if !directory && !info.Mode().IsRegular() {
		return errors.New("generation member is not regular")
	}
	return nil
}
