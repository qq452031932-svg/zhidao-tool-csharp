//go:build !linux

package upload

import "os"

func validateStagingRoot(os.FileInfo) error {
	return nil
}
