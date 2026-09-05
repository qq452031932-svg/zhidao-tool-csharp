//go:build linux

package upload

import (
	"errors"
	"fmt"
	"os"
	"testing"

	"golang.org/x/sys/unix"
)

func TestWrappedLinuxSpaceAndQuotaErrorsAreNormalized(t *testing.T) {
	for _, diskError := range []error{unix.ENOSPC, unix.EDQUOT} {
		wrapped := fmt.Errorf("outer: %w", &os.PathError{Op: "write", Path: "/srv/staging/candidate", Err: diskError})
		writer := &diskGuardWriter{
			writer:         linuxFailingWriter{err: wrapped},
			root:           "/srv/staging",
			availableBytes: func(string) (uint64, error) { return 1 << 20, nil },
		}
		if _, err := writer.Write([]byte("zip")); !errors.Is(err, ErrInsufficientDisk) {
			t.Fatalf("staging error=%v want ErrInsufficientDisk", err)
		}
		if err := classifyArchiveDataError(wrapped); !errors.Is(err, ErrInsufficientDisk) {
			t.Fatalf("extraction error=%v want ErrInsufficientDisk", err)
		}
	}
}

type linuxFailingWriter struct{ err error }

func (writer linuxFailingWriter) Write([]byte) (int, error) { return 0, writer.err }
