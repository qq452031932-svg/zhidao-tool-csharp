//go:build linux

package deploy

import (
	"errors"
	"os"
	"syscall"
)

func validateImmutableGeneration(info os.FileInfo, directory bool) error {
	if info == nil || info.Mode()&os.ModeSymlink != 0 || info.Mode().Perm()&0o222 != 0 {
		return errors.New("generation permissions are mutable")
	}
	if directory != info.IsDir() {
		return errors.New("generation member type is invalid")
	}
	if !directory && !info.Mode().IsRegular() {
		return errors.New("generation member is not regular")
	}
	if !directory {
		stat, ok := info.Sys().(*syscall.Stat_t)
		if !ok || stat.Nlink != 1 {
			return errors.New("generation member must have one link")
		}
	}
	return nil
}
