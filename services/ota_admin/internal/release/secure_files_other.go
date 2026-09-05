//go:build !linux

package release

import "os"

func openDirectoryNoFollow(path string) (*os.File, error) {
	return os.Open(path)
}

func openChildNoFollow(_ *os.File, path, _ string) (*os.File, error) {
	before, err := os.Lstat(path)
	if err != nil || before.Mode()&os.ModeSymlink != 0 {
		return nil, err
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	after, err := file.Stat()
	if err != nil || !os.SameFile(before, after) {
		file.Close()
		if err != nil {
			return nil, err
		}
		return nil, os.ErrInvalid
	}
	return file, nil
}
