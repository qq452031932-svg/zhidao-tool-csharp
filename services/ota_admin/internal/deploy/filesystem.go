package deploy

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/upload"
)

const maxGenerationEntries = 256

var canonicalReleaseID = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)

// ErrCrashSimulation is accepted only through the in-process fault hook. It
// leaves durable intermediate artifacts in place so recovery tests and the
// acceptance-only harness can model an abrupt process exit.
var ErrCrashSimulation = errors.New("simulated process crash")

type Boundary string

const (
	BoundaryFileSynced        Boundary = "file_synced"
	BoundaryCandidateSynced   Boundary = "candidate_dir_synced"
	BoundaryGenerationNamed   Boundary = "generation_renamed"
	BoundaryGenerationsSynced Boundary = "generations_dir_synced"
	BoundaryStableTemp        Boundary = "stable_temp_created"
	BoundaryStableReplaced    Boundary = "stable_replaced"
	BoundaryParentSynced      Boundary = "win_x64_parent_synced"
	BoundaryMigrationRenamed  Boundary = "migration_stable_renamed"
	BoundaryMigrationSynced   Boundary = "migration_parent_synced"
	BoundaryMigrationRemoved  Boundary = "migration_backup_removed"
)

type FaultHook func(Boundary) error

type Disk struct {
	root, generations string
	hook              FaultHook
	sameDevice        func(os.FileInfo, os.FileInfo) bool
}

func NewDisk(root string, hook FaultHook) (*Disk, error) {
	if root == "" || !filepath.IsAbs(root) {
		return nil, errors.New("publication root must be absolute")
	}
	root = filepath.Clean(root)
	resolvedRoot, err := filepath.EvalSymlinks(root)
	if err != nil {
		return nil, errors.New("publication root cannot be resolved")
	}
	root = filepath.Clean(resolvedRoot)
	info, err := os.Lstat(root)
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 || runtime.GOOS == "linux" && info.Mode().Perm()&0o022 != 0 {
		return nil, errors.New("publication root must be a non-linked non-writable directory")
	}
	generations := filepath.Join(root, "generations")
	if err := os.Mkdir(generations, 0o755); err != nil && !errors.Is(err, os.ErrExist) {
		return nil, err
	}
	info, err = os.Lstat(generations)
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 || runtime.GOOS == "linux" && info.Mode().Perm()&0o022 != 0 {
		return nil, errors.New("generations root is unsafe")
	}
	return &Disk{root: root, generations: generations, hook: hook, sameDevice: sameFilesystem}, nil
}

func (disk *Disk) stage(ctx context.Context, source *upload.Envelope, releaseID string) (string, func(), error) {
	if disk == nil || source == nil || !canonicalReleaseID.MatchString(releaseID) {
		return "", nil, errors.New("generation request is invalid")
	}
	name := "release-" + releaseID
	temporary := filepath.Join(disk.generations, "."+name+".tmp")
	final := filepath.Join(disk.generations, name)
	if _, err := os.Lstat(temporary); !errors.Is(err, os.ErrNotExist) {
		return "", nil, errors.New("temporary generation already exists")
	}
	if _, err := os.Lstat(final); !errors.Is(err, os.ErrNotExist) {
		return "", nil, errors.New("generation already exists")
	}
	if err := os.Mkdir(temporary, 0o700); err != nil {
		return "", nil, err
	}
	cleanup := func() {
		_ = removeGeneration(disk.generations, filepath.Base(temporary))
		_ = removeGeneration(disk.generations, name)
	}
	entries := append([]upload.Entry(nil), source.Entries...)
	if len(entries) == 0 || len(entries) > maxGenerationEntries {
		cleanup()
		return "", nil, errors.New("generation entry count is invalid")
	}
	sort.Slice(entries, func(i, j int) bool {
		leftRelease := strings.HasPrefix(entries[i].Name, "releases.") && strings.HasSuffix(entries[i].Name, ".json")
		rightRelease := strings.HasPrefix(entries[j].Name, "releases.") && strings.HasSuffix(entries[j].Name, ".json")
		if leftRelease != rightRelease {
			return !leftRelease
		}
		return entries[i].Name < entries[j].Name
	})
	seen := map[string]bool{}
	for _, entry := range entries {
		if err := ctx.Err(); err != nil {
			cleanup()
			return "", nil, err
		}
		if entry.Name == "" || filepath.Base(entry.Name) != entry.Name || seen[strings.ToLower(entry.Name)] {
			cleanup()
			return "", nil, errors.New("generation entry name is unsafe")
		}
		seen[strings.ToLower(entry.Name)] = true
		sourceFile, sourceInfo, err := openStableRegular(entry.Path)
		if err != nil {
			cleanup()
			return "", nil, err
		}
		destinationPath := filepath.Join(temporary, entry.Name)
		destination, err := os.OpenFile(destinationPath, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
		if err != nil {
			sourceFile.Close()
			cleanup()
			return "", nil, err
		}
		_, copyErr := io.CopyBuffer(destination, sourceFile, make([]byte, 64<<10))
		chmodErr := destination.Chmod(0o444)
		syncErr := destination.Sync()
		closeDestinationErr := destination.Close()
		after, statErr := sourceFile.Stat()
		closeSourceErr := sourceFile.Close()
		if copyErr != nil || chmodErr != nil || syncErr != nil || closeDestinationErr != nil || statErr != nil || closeSourceErr != nil || !os.SameFile(sourceInfo, after) || sourceInfo.Size() != after.Size() || sourceInfo.ModTime() != after.ModTime() {
			cleanup()
			return "", nil, errors.New("generation source changed while copying")
		}
		if err := disk.boundary(BoundaryFileSynced); err != nil {
			if !errors.Is(err, ErrCrashSimulation) {
				cleanup()
			}
			return "", nil, err
		}
	}
	if err := os.Chmod(temporary, 0o555); err != nil {
		cleanup()
		return "", nil, err
	}
	if err := syncDirectory(temporary); err != nil {
		cleanup()
		return "", nil, err
	}
	if err := disk.boundary(BoundaryCandidateSynced); err != nil {
		if !errors.Is(err, ErrCrashSimulation) {
			cleanup()
		}
		return "", nil, err
	}
	return name, cleanup, nil
}

func (disk *Disk) commit(name string, cleanup func()) (bool, error) {
	temporary := filepath.Join(disk.generations, "."+name+".tmp")
	final := filepath.Join(disk.generations, name)
	if err := os.Rename(temporary, final); err != nil {
		cleanup()
		return false, err
	}
	if err := disk.boundary(BoundaryGenerationNamed); err != nil {
		if !errors.Is(err, ErrCrashSimulation) {
			cleanup()
		}
		return false, err
	}
	if err := syncDirectory(disk.generations); err != nil {
		cleanup()
		return false, err
	}
	if err := disk.boundary(BoundaryGenerationsSynced); err != nil {
		if !errors.Is(err, ErrCrashSimulation) {
			cleanup()
		}
		return false, err
	}
	target := filepath.ToSlash(filepath.Join("generations", name))
	tempLink := filepath.Join(disk.root, ".stable-"+strings.TrimPrefix(name, "release-")+".tmp")
	if err := os.Symlink(target, tempLink); err != nil {
		cleanup()
		return false, err
	}
	if err := disk.boundary(BoundaryStableTemp); err != nil {
		if !errors.Is(err, ErrCrashSimulation) {
			_ = os.Remove(tempLink)
			cleanup()
		}
		return false, err
	}
	if err := os.Rename(tempLink, filepath.Join(disk.root, "stable")); err != nil {
		_ = os.Remove(tempLink)
		cleanup()
		return false, err
	}
	if err := disk.boundary(BoundaryStableReplaced); err != nil {
		return true, err
	}
	if err := syncDirectory(disk.root); err != nil {
		return true, err
	}
	if err := disk.boundary(BoundaryParentSynced); err != nil {
		return true, err
	}
	return true, nil
}

func (disk *Disk) boundary(boundary Boundary) error {
	if disk.hook != nil {
		return disk.hook(boundary)
	}
	return nil
}

func (disk *Disk) StableTarget() (string, error) {
	target, err := os.Readlink(filepath.Join(disk.root, "stable"))
	if err != nil {
		return "", err
	}
	target = filepath.ToSlash(target)
	if filepath.IsAbs(target) || strings.Contains(target, "\\") || !strings.HasPrefix(target, "generations/release-") || strings.Contains(target, "..") {
		return "", errors.New("stable link target is unsafe")
	}
	name := strings.TrimPrefix(target, "generations/")
	if !canonicalReleaseID.MatchString(strings.TrimPrefix(name, "release-")) {
		return "", errors.New("stable link target is invalid")
	}
	info, err := os.Lstat(filepath.Join(disk.generations, name))
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return "", errors.New("stable generation is invalid")
	}
	return name, nil
}

func (disk *Disk) MigrateEmptyStable() error {
	path := filepath.Join(disk.root, "stable")
	backup := filepath.Join(disk.root, ".stable-empty-migration")
	if err := disk.recoverInterruptedEmptyStableMigration(path, backup); err != nil {
		return err
	}
	info, err := os.Lstat(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	if info.Mode()&os.ModeSymlink != 0 {
		return nil
	}
	if !info.IsDir() {
		return errors.New("stable path is not a directory")
	}
	rootInfo, err := os.Stat(disk.root)
	if err != nil || !disk.sameDevice(rootInfo, info) {
		return errors.New("stable directory must be on the publication filesystem")
	}
	handle, err := os.Open(path)
	if err != nil {
		return err
	}
	entries, readErr := handle.ReadDir(1)
	closeErr := handle.Close()
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return readErr
	}
	if closeErr != nil {
		return closeErr
	}
	if len(entries) != 0 {
		return errors.New("stable directory is not empty")
	}
	if _, err := os.Lstat(backup); !errors.Is(err, os.ErrNotExist) {
		return errors.New("stable migration backup already exists")
	}
	if err := os.Rename(path, backup); err != nil {
		return err
	}
	rollback := func(cause error) error {
		if _, err := os.Lstat(backup); err == nil {
			if restoreErr := os.Rename(backup, path); restoreErr != nil {
				return errors.Join(cause, restoreErr)
			}
		} else if errors.Is(err, os.ErrNotExist) {
			if restoreErr := os.Mkdir(path, info.Mode().Perm()); restoreErr != nil && !errors.Is(restoreErr, os.ErrExist) {
				return errors.Join(cause, restoreErr)
			}
		} else {
			return errors.Join(cause, err)
		}
		return errors.Join(cause, syncDirectory(disk.root))
	}
	if err := disk.boundary(BoundaryMigrationRenamed); err != nil {
		return rollback(err)
	}
	if err := syncDirectory(disk.root); err != nil {
		return rollback(err)
	}
	if err := disk.boundary(BoundaryMigrationSynced); err != nil {
		return rollback(err)
	}
	if err := os.Remove(backup); err != nil {
		return rollback(err)
	}
	if err := disk.boundary(BoundaryMigrationRemoved); err != nil {
		return rollback(err)
	}
	if err := syncDirectory(disk.root); err != nil {
		return rollback(err)
	}
	return nil
}

func (disk *Disk) recoverInterruptedEmptyStableMigration(stable, backup string) error {
	backupInfo, err := os.Lstat(backup)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil || !backupInfo.IsDir() || backupInfo.Mode()&os.ModeSymlink != 0 {
		return errors.New("stable migration backup is unsafe")
	}
	if _, err := os.Lstat(stable); !errors.Is(err, os.ErrNotExist) {
		if err != nil {
			return err
		}
		return errors.New("stable and its migration backup both exist")
	}
	rootInfo, err := os.Stat(disk.root)
	if err != nil || !disk.sameDevice(rootInfo, backupInfo) {
		return errors.New("stable migration backup is on another filesystem")
	}
	handle, err := os.Open(backup)
	if err != nil {
		return err
	}
	entries, readErr := handle.ReadDir(1)
	closeErr := handle.Close()
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return readErr
	}
	if closeErr != nil {
		return closeErr
	}
	if len(entries) != 0 {
		return errors.New("stable migration backup is not empty")
	}
	if err := os.Rename(backup, stable); err != nil {
		return err
	}
	return syncDirectory(disk.root)
}

func (disk *Disk) GenerationPath(name string) (string, error) {
	if !strings.HasPrefix(name, "release-") || !canonicalReleaseID.MatchString(strings.TrimPrefix(name, "release-")) {
		return "", errors.New("generation name invalid")
	}
	return filepath.Join(disk.generations, name), nil
}

func (disk *Disk) abortUnpublished(releaseID string) error {
	if disk == nil || !canonicalReleaseID.MatchString(releaseID) {
		return errors.New("unpublished release ID is invalid")
	}
	name := "release-" + releaseID
	tempLink := filepath.Join(disk.root, ".stable-"+releaseID+".tmp")
	if info, err := os.Lstat(tempLink); err == nil {
		if info.Mode()&os.ModeSymlink == 0 {
			return errors.New("temporary stable path is unsafe")
		}
		target, err := os.Readlink(tempLink)
		if err != nil || filepath.ToSlash(target) != filepath.ToSlash(filepath.Join("generations", name)) {
			return errors.New("temporary stable target is unsafe")
		}
		if err := os.Remove(tempLink); err != nil {
			return err
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	if err := removeGeneration(disk.generations, "."+name+".tmp"); err != nil {
		return err
	}
	if err := removeGeneration(disk.generations, name); err != nil {
		return err
	}
	return errors.Join(syncDirectory(disk.generations), syncDirectory(disk.root))
}

func (disk *Disk) String() string { return fmt.Sprintf("deploy disk %s", disk.root) }
