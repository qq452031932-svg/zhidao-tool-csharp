//go:build !linux

package candidate

import "os"

func validateStagingRoot(os.FileInfo) error { return nil }
