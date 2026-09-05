//go:build windows

package upload

import (
	"errors"

	"golang.org/x/sys/windows"
)

func isInsufficientDiskError(err error) bool {
	return errors.Is(err, windows.ERROR_DISK_FULL) ||
		errors.Is(err, windows.ERROR_HANDLE_DISK_FULL) ||
		errors.Is(err, windows.ERROR_DISK_QUOTA_EXCEEDED)
}
