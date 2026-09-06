//go:build linux

package candidate

import (
	"errors"
	"os"
	"syscall"
)

func validateStagingRoot(info os.FileInfo) error {
	metadata, ok := info.Sys().(*syscall.Stat_t)
	if !ok {
		return errors.New("staging root ownership metadata is unavailable")
	}
	if metadata.Uid != uint32(os.Geteuid()) {
		return errors.New("staging root must be owned by the service user")
	}
	if info.Mode().Perm() != 0o700 {
		return errors.New("staging root permissions must be 0700")
	}
	return nil
}
