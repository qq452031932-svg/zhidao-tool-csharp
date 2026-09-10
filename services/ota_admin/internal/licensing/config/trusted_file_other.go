//go:build !linux

package config

import (
	"errors"
	"io"
	"os"
	"runtime"
)

type trustedFilePolicy struct {
	allowedGIDs  map[uint32]struct{}
	exactSize    int64
	maximumSize  int64
	allowedModes map[os.FileMode]struct{}
}

func readLicenseFile(path string, exactSize, maximumSize int64) ([]byte, error) {
	return readLocalFile(path, exactSize, maximumSize, true)
}

func readSharedFile(path string, exactSize, maximumSize int64) ([]byte, error) {
	return readLocalFile(path, exactSize, maximumSize, true)
}

func readConfigFile(path string, maximumSize int64) ([]byte, error) {
	return readLocalFile(path, 0, maximumSize, false)
}

func readTrustedFile(path string, policy trustedFilePolicy, afterOpen func()) ([]byte, error) {
	return readLocalFile(path, policy.exactSize, policy.maximumSize, true, afterOpen)
}

func readLocalFile(path string, exactSize, maximumSize int64, secureMode bool, afterOpen ...func()) ([]byte, error) {
	if maximumSize < 1 || exactSize < 0 || exactSize > maximumSize {
		return nil, errors.New("trusted file request is invalid")
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, errors.New("open local file")
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() || info.Size() < 0 || info.Size() > maximumSize || (exactSize > 0 && info.Size() != exactSize) {
		return nil, errors.New("local file is invalid")
	}
	if secureMode && runtime.GOOS != "windows" && info.Mode().Perm() != 0o600 && info.Mode().Perm() != 0o640 {
		return nil, errors.New("local file permissions are too broad")
	}
	if len(afterOpen) == 1 && afterOpen[0] != nil {
		afterOpen[0]()
	}
	data, err := io.ReadAll(io.LimitReader(file, maximumSize+1))
	if err != nil || int64(len(data)) > maximumSize || (exactSize > 0 && int64(len(data)) != exactSize) {
		clear(data)
		return nil, errors.New("read local file")
	}
	return data, nil
}
