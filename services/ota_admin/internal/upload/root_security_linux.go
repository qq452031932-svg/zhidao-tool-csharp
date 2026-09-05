//go:build linux

package upload

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
	return validatePrivateStagingRootMetadata(metadata.Uid, uint32(os.Geteuid()), info.Mode())
}
