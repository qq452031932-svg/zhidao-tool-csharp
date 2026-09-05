//go:build !linux

package config

import (
	"errors"
	"os"
)

func openTrustedConfig(string) (*os.File, error) {
	return nil, errors.New("secure root-owned config loading is supported only on Linux")
}
