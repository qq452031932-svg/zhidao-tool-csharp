//go:build linux

package store

import (
	"context"
	"os"
	"path/filepath"
	"syscall"
	"testing"
)

func TestOpenCreatesPrivateUnlinkedStateFiles(t *testing.T) {
	root := t.TempDir()
	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(root, "state.db")
	s, err := Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	for _, candidate := range []string{path, path + "-wal", path + "-shm"} {
		info, err := os.Lstat(candidate)
		if os.IsNotExist(err) {
			continue
		}
		if err != nil {
			t.Fatal(err)
		}
		stat := info.Sys().(*syscall.Stat_t)
		if err := validateStateFileMetadata(stat.Uid, uint32(os.Geteuid()), info.Mode(), info.Mode().IsRegular(), info.Mode()&os.ModeSymlink != 0, uint64(stat.Nlink)); err != nil {
			t.Fatalf("%s: %v", candidate, err)
		}
	}
}

func TestOpenRejectsInsecureStateParentAndLinkedDatabase(t *testing.T) {
	root := t.TempDir()
	if err := os.Chmod(root, 0o750); err != nil {
		t.Fatal(err)
	}
	if _, err := Open(context.Background(), filepath.Join(root, "state.db")); err == nil {
		t.Fatal("Open() accepted mode 0750 state parent")
	}

	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatal(err)
	}
	original := filepath.Join(root, "original.db")
	if err := os.WriteFile(original, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	hardlink := filepath.Join(root, "hardlink.db")
	if err := os.Link(original, hardlink); err != nil {
		t.Fatal(err)
	}
	if _, err := Open(context.Background(), hardlink); err == nil {
		t.Fatal("Open() accepted hardlinked database")
	}
	symlink := filepath.Join(root, "symlink.db")
	if err := os.Symlink(original, symlink); err != nil {
		t.Fatal(err)
	}
	if _, err := Open(context.Background(), symlink); err == nil {
		t.Fatal("Open() accepted symlink database")
	}
	badSidecar := filepath.Join(root, "sidecar.db-wal")
	if err := os.WriteFile(badSidecar, nil, 0o640); err != nil {
		t.Fatal(err)
	}
	if _, err := Open(context.Background(), filepath.Join(root, "sidecar.db")); err == nil {
		t.Fatal("Open accepted insecure preexisting WAL before SQLite use")
	}
}

func TestPinnedStatePathRejectsDatabaseIdentityReplacement(t *testing.T) {
	root := t.TempDir()
	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(root, "state.db")
	handle, err := prepareStatePath(path)
	if err != nil {
		t.Fatal(err)
	}
	defer handle.Close()
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := handle.Verify(); err == nil {
		t.Fatal("pinned state path accepted a replacement database inode")
	}
}
