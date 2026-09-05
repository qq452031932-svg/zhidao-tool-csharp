//go:build !linux

package upload

import (
	"os"
	"path/filepath"
)

func createOutputFile(root, name string) (*os.File, string, error) {
	path := filepath.Join(root, name)
	file, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	return file, path, err
}
