//go:build linux

package upload

import (
	"errors"

	"golang.org/x/sys/unix"
)

func isInsufficientDiskError(err error) bool {
	return errors.Is(err, unix.ENOSPC) || errors.Is(err, unix.EDQUOT)
}
