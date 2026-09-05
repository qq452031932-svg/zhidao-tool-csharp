//go:build !linux && !windows

package upload

import "errors"

func filesystemAvailableBytes(string) (uint64, error) {
	return 0, errors.New("disk-space preflight is unsupported on this platform")
}
