//go:build windows

package upload

import (
	"errors"
	"fmt"
	"os"
	"testing"

	"golang.org/x/sys/windows"
)

func TestWrappedDiskSpaceErrorsAreNormalizedDuringStagingAndExtraction(t *testing.T) {
	for _, diskError := range []error{
		windows.ERROR_DISK_FULL,
		windows.ERROR_HANDLE_DISK_FULL,
		windows.ERROR_DISK_QUOTA_EXCEEDED,
	} {
		t.Run(diskError.Error(), func(t *testing.T) {
			wrapped := fmt.Errorf("outer: %w", &os.PathError{Op: "write", Path: `C:\staging\candidate`, Err: diskError})
			writer := &diskGuardWriter{
				writer:         failingWriter{err: wrapped},
				root:           `C:\staging`,
				availableBytes: func(string) (uint64, error) { return 1 << 20, nil },
			}
			if _, err := writer.Write([]byte("zip")); !errors.Is(err, ErrInsufficientDisk) {
				t.Fatalf("staging error=%v want ErrInsufficientDisk", err)
			}
			if err := classifyArchiveDataError(wrapped); !errors.Is(err, ErrInsufficientDisk) {
				t.Fatalf("extraction error=%v want ErrInsufficientDisk", err)
			}
		})
	}
}

func TestOtherWrappedFilesystemErrorsRemainInternal(t *testing.T) {
	failure := &os.PathError{Op: "write", Path: `C:\staging\candidate`, Err: windows.ERROR_ACCESS_DENIED}
	classified := classifyArchiveDataError(failure)
	if !errors.Is(classified, windows.ERROR_ACCESS_DENIED) || errors.Is(classified, ErrInsufficientDisk) {
		t.Fatalf("filesystem error was misclassified: %v", classified)
	}
}

type failingWriter struct{ err error }

func (writer failingWriter) Write([]byte) (int, error) { return 0, writer.err }
