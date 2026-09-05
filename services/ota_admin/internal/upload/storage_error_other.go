//go:build !linux && !windows

package upload

func isInsufficientDiskError(error) bool { return false }
