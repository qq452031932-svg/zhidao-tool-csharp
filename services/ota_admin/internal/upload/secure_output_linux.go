//go:build linux

package upload

import (
	"os"

	"golang.org/x/sys/unix"
)

func createOutputFile(root, name string) (*os.File, string, error) {
	directory, err := unix.Open(root, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
	if err != nil {
		return nil, "", err
	}
	defer unix.Close(directory)
	descriptor, err := unix.Openat(directory, name, unix.O_WRONLY|unix.O_CREAT|unix.O_EXCL|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0o600)
	if err != nil {
		return nil, "", err
	}
	path := root + string(os.PathSeparator) + name
	return os.NewFile(uintptr(descriptor), path), path, nil
}
