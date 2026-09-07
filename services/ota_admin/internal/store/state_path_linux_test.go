//go:build linux

package store

import (
	"context"
	"database/sql"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"testing"

	"golang.org/x/sys/unix"
)

const siblingSQLitePathEnvironment = "OTA_ADMIN_SIBLING_SQLITE_PATH"

func TestMain(m *testing.M) {
	previous := unix.Umask(0o077)
	code := m.Run()
	unix.Umask(previous)
	os.Exit(code)
}

func TestOpenKeepsSQLitePOSIXLockAcrossStatePathVerification(t *testing.T) {
	root := t.TempDir()
	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatal(err)
	}
	databasePath := filepath.Join(root, "state.db")
	database, err := Open(context.Background(), databasePath)
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	if err := database.statePath.Verify(); err != nil {
		t.Fatalf("verify live state path: %v", err)
	}

	command := exec.Command(os.Args[0], "-test.run=^TestSQLiteExclusiveLockHelper$")
	command.Env = append(os.Environ(), siblingSQLitePathEnvironment+"="+databasePath)
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("SQLite shared lock was lost after state-path verification: %v\n%s", err, output)
	}
}

func TestSQLiteExclusiveLockHelper(t *testing.T) {
	databasePath := os.Getenv(siblingSQLitePathEnvironment)
	if databasePath == "" {
		t.Skip("SQLite lock helper")
	}
	fd, err := unix.Open(databasePath, unix.O_RDWR|unix.O_CLOEXEC, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer unix.Close(fd)
	lock := unix.Flock_t{Type: unix.F_WRLCK, Whence: 0, Start: 0x40000000, Len: 512}
	if err := unix.FcntlFlock(uintptr(fd), unix.F_SETLK, &lock); errors.Is(err, unix.EACCES) || errors.Is(err, unix.EAGAIN) {
		return
	} else if err != nil {
		t.Fatalf("probe SQLite lock: %v", err)
	}
	t.Fatal("acquired an exclusive SQLite lock while Store remained open")
}

func TestOpenKeepsSQLiteWALSidecarsPinnedAcrossSiblingConnectionClose(t *testing.T) {
	root := t.TempDir()
	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatal(err)
	}
	databasePath := filepath.Join(root, "state.db")
	database, err := Open(context.Background(), databasePath)
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	if err := database.statePath.Verify(); err != nil {
		t.Fatalf("verify live state path: %v", err)
	}

	type identity struct{ device, inode uint64 }
	before := make(map[string]identity)
	for _, path := range []string{databasePath + "-wal", databasePath + "-shm"} {
		info, err := os.Lstat(path)
		if err != nil {
			t.Fatalf("stat live SQLite sidecar %s: %v", path, err)
		}
		stat := info.Sys().(*syscall.Stat_t)
		before[path] = identity{device: uint64(stat.Dev), inode: stat.Ino}
	}

	command := exec.Command(os.Args[0], "-test.run=^TestSiblingSQLiteOpenCloseHelper$")
	command.Env = append(os.Environ(), siblingSQLitePathEnvironment+"="+databasePath)
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("sibling SQLite connection failed: %v\n%s", err, output)
	}

	for path, want := range before {
		info, err := os.Lstat(path)
		if err != nil {
			t.Fatalf("live SQLite sidecar was removed after sibling close: %s: %v", path, err)
		}
		stat := info.Sys().(*syscall.Stat_t)
		if got := (identity{device: uint64(stat.Dev), inode: stat.Ino}); got != want {
			t.Fatalf("live SQLite sidecar identity changed after sibling close: %s: got %+v, want %+v", path, got, want)
		}
	}
}

func TestSiblingSQLiteOpenCloseHelper(t *testing.T) {
	databasePath := os.Getenv(siblingSQLitePathEnvironment)
	if databasePath == "" {
		t.Skip("sibling SQLite helper")
	}
	database, err := sql.Open("sqlite", databaseURI(databasePath))
	if err != nil {
		t.Fatal(err)
	}
	var version int
	if err := database.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil {
		_ = database.Close()
		t.Fatal(err)
	}
	if version != schemaVersion {
		_ = database.Close()
		t.Fatalf("schema version = %d, want %d", version, schemaVersion)
	}
	if err := database.Close(); err != nil {
		t.Fatal(err)
	}
}

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
	// Make the fixture independent of a restrictive service/root umask. On
	// hardened CentOS hosts an umask of 0077 otherwise turns 0640 into 0600
	// and the test no longer creates the insecure file it claims to test.
	if err := os.Chmod(badSidecar, 0o640); err != nil {
		t.Fatal(err)
	}
	if _, err := Open(context.Background(), filepath.Join(root, "sidecar.db")); err == nil {
		t.Fatal("Open accepted insecure preexisting WAL before SQLite use")
	}
}

func TestOpenRejectsWorldReadableStateParent(t *testing.T) {
	root := t.TempDir()
	if err := os.Chmod(root, 0o755); err != nil {
		t.Fatal(err)
	}
	if _, err := Open(context.Background(), filepath.Join(root, "state.db")); err == nil {
		t.Fatal("Open() accepted mode 0755 state parent")
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

func TestPinnedStatePathRejectsMissingMainDatabase(t *testing.T) {
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
	if err := handle.Verify(); err == nil {
		t.Fatal("pinned state path accepted a missing main database")
	}
}
