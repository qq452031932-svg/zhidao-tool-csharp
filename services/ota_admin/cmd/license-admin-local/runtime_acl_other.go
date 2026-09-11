//go:build license_local_acceptance && !windows

package main

import "os"

func secureRuntimeDirectory(path string) error {
	return os.Chmod(path, 0o700)
}
