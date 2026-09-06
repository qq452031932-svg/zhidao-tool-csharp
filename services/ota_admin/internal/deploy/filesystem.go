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
var ErrCleanupIncomplete = errors.New("pre-commit cleanup could not be proven")

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
	remove            func(string, string) error
	bound             *boundRoots
	deviceOK          func(rootEntry) bool
}

type rootEntry struct {
	exists, directory, symlink, sameDevice bool
	mode                                   os.FileMode
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
	bound, err := bindRoots(root, generations)
	if err != nil {
		return nil, err
	}
	disk := &Disk{root: root, generations: generations, hook: hook, bound: bound, deviceOK: func(entry rootEntry) bool { return entry.sameDevice }}
	disk.remove = func(_ string, name string) error { return bound.removeGeneration(name) }
	return disk, nil
}

func (disk *Disk) stage(ctx context.Context, source *upload.Envelope, releaseID string) (string, func() error, error) {
	if disk == nil || source == nil || !canonicalReleaseID.MatchString(releaseID) {
		return "", nil, errors.New("generation request is invalid")
	}
	if err := disk.bound.verify(); err != nil {
		return "", nil, err
	}
	name := "release-" + releaseID
	temporaryName := "." + name + ".tmp"
	if exists, err := disk.bound.generationExists(temporaryName); err != nil || exists {
		return "", nil, errors.New("temporary generation already exists")
	}
	if exists, err := disk.bound.generationExists(name); err != nil || exists {
		return "", nil, errors.New("generation already exists")
	}
	if err := disk.bound.mkdirGeneration(temporaryName, 0o700); err != nil {
		return "", nil, err
	}
	cleanup := func() error {
		return errors.Join(disk.remove(disk.generations, temporaryName), disk.remove(disk.generations, name), disk.bound.verify())
	}
	fail := func(cause error) (string, func() error, error) {
		cleanupErr := cleanup()
		if cleanupErr != nil {
			cause = errors.Join(cause, ErrCleanupIncomplete, cleanupErr)
		}
		return "", nil, cause
	}
	entries := append([]upload.Entry(nil), source.Entries...)
	if len(entries) == 0 || len(entries) > maxGenerationEntries {
		return fail(errors.New("generation entry count is invalid"))
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
			return fail(err)
		}
		if entry.Name == "" || filepath.Base(entry.Name) != entry.Name || seen[strings.ToLower(entry.Name)] {
			return fail(errors.New("generation entry name is unsafe"))
		}
		seen[strings.ToLower(entry.Name)] = true
		sourceFile, sourceInfo, err := openStableRegular(entry.Path)
		if err != nil {
			return fail(err)
		}
		destination, err := disk.bound.openGenerationOutput(temporaryName, entry.Name, 0o600)
		if err != nil {
			sourceFile.Close()
			return fail(err)
		}
		_, copyErr := io.CopyBuffer(destination, sourceFile, make([]byte, 64<<10))
		chmodErr := destination.Chmod(0o444)
		syncErr := destination.Sync()
		closeDestinationErr := destination.Close()
		after, statErr := sourceFile.Stat()
		closeSourceErr := sourceFile.Close()
		if copyErr != nil || chmodErr != nil || syncErr != nil || closeDestinationErr != nil || statErr != nil || closeSourceErr != nil || !os.SameFile(sourceInfo, after) || sourceInfo.Size() != after.Size() || sourceInfo.ModTime() != after.ModTime() {
			return fail(errors.New("generation source changed while copying"))
		}
		if err := disk.boundary(BoundaryFileSynced); err != nil {
			if !errors.Is(err, ErrCrashSimulation) {
				_, _, err = fail(err)
			}
			return "", nil, err
		}
	}
	if err := disk.bound.chmodGeneration(temporaryName, 0o555); err != nil {
		return fail(err)
	}
	if err := disk.bound.syncGeneration(temporaryName); err != nil {
		return fail(err)
	}
	if err := disk.boundary(BoundaryCandidateSynced); err != nil {
		if !errors.Is(err, ErrCrashSimulation) {
			_, _, err = fail(err)
		}
		return "", nil, err
	}
	if err := disk.bound.verify(); err != nil {
		return fail(err)
	}
	return name, cleanup, nil
}

func (disk *Disk) commit(name string, cleanup func() error) (bool, error) {
	return disk.commitAuthorized(name, cleanup, nil)
}

func (disk *Disk) commitAuthorized(name string, cleanup func() error, authorize func() error) (bool, error) {
	fail := func(cause error) (bool, error) {
		if cleanupErr := cleanup(); cleanupErr != nil {
			return false, errors.Join(cause, ErrCleanupIncomplete, cleanupErr)
		}
		return false, cause
	}
	if err := disk.bound.verify(); err != nil {
		return fail(err)
	}
	temporaryName := "." + name + ".tmp"
	if err := disk.bound.renameGeneration(temporaryName, name); err != nil {
		return fail(err)
	}
	if err := disk.boundary(BoundaryGenerationNamed); err != nil {
		if !errors.Is(err, ErrCrashSimulation) {
			return fail(err)
		}
		return false, err
	}
	if err := disk.bound.syncGenerations(); err != nil {
		return fail(err)
	}
	if err := disk.boundary(BoundaryGenerationsSynced); err != nil {
		if !errors.Is(err, ErrCrashSimulation) {
			return fail(err)
		}
		return false, err
	}
	if authorize != nil {
		if err := authorize(); err != nil {
			return fail(err)
		}
	}
	if err := disk.bound.verify(); err != nil {
		return fail(err)
	}
	target := filepath.ToSlash(filepath.Join("generations", name))
	tempLinkName := ".stable-" + strings.TrimPrefix(name, "release-") + ".tmp"
	if err := disk.bound.symlinkRoot(target, tempLinkName); err != nil {
		return fail(err)
	}
	if err := disk.boundary(BoundaryStableTemp); err != nil {
		if !errors.Is(err, ErrCrashSimulation) {
			if removeErr := disk.bound.unlinkRoot(tempLinkName); removeErr != nil {
				return false, errors.Join(err, ErrCleanupIncomplete, removeErr)
			}
			if syncErr := disk.bound.syncRoot(); syncErr != nil {
				return false, errors.Join(err, ErrCleanupIncomplete, syncErr)
			}
			return fail(err)
		}
		return false, err
	}
	if err := disk.bound.renameRoot(tempLinkName, "stable"); err != nil {
		removeErr := disk.bound.unlinkRoot(tempLinkName)
		syncErr := disk.bound.syncRoot()
		if removeErr != nil && !errors.Is(removeErr, os.ErrNotExist) || syncErr != nil {
			return false, errors.Join(err, ErrCleanupIncomplete, removeErr, syncErr)
		}
		return fail(err)
	}
	if err := disk.boundary(BoundaryStableReplaced); err != nil {
		return true, err
	}
	if err := disk.bound.syncRoot(); err != nil {
		return true, err
	}
	if err := disk.boundary(BoundaryParentSynced); err != nil {
		return true, err
	}
	if err := disk.bound.verify(); err != nil {
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
	if err := disk.bound.verify(); err != nil {
		return "", err
	}
	target, err := disk.bound.readRootLink("stable")
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
	exists, err := disk.bound.generationExists(name)
	if err != nil || !exists {
		return "", errors.New("stable generation is invalid")
	}
	if err := disk.bound.verify(); err != nil {
		return "", err
	}
	return name, nil
}

func (disk *Disk) MigrateEmptyStable() error {
	const stable = "stable"
	const backup = ".stable-empty-migration"
	if err := disk.recoverInterruptedEmptyStableMigration(stable, backup); err != nil {
		return err
	}
	info, err := disk.bound.rootEntry(stable)
	if err != nil {
		return err
	}
	if !info.exists {
		return nil
	}
	if info.symlink {
		return nil
	}
	if !info.directory {
		return errors.New("stable path is not a directory")
	}
	if !disk.deviceOK(info) {
		return errors.New("stable directory must be on the publication filesystem")
	}
	empty, err := disk.bound.rootDirectoryEmpty(stable)
	if err != nil {
		return err
	}
	if !empty {
		return errors.New("stable directory is not empty")
	}
	backupInfo, err := disk.bound.rootEntry(backup)
	if err != nil || backupInfo.exists {
		return errors.New("stable migration backup already exists")
	}
	if err := disk.bound.renameRoot(stable, backup); err != nil {
		return err
	}
	rollback := func(cause error) error {
		backupInfo, err := disk.bound.rootEntry(backup)
		if err != nil {
			return errors.Join(cause, err)
		}
		if backupInfo.exists {
			if restoreErr := disk.bound.renameRoot(backup, stable); restoreErr != nil {
				return errors.Join(cause, restoreErr)
			}
		} else {
			if restoreErr := disk.bound.mkdirRoot(stable, uint32(info.mode.Perm())); restoreErr != nil && !errors.Is(restoreErr, os.ErrExist) {
				return errors.Join(cause, restoreErr)
			}
		}
		return errors.Join(cause, disk.bound.syncRoot(), disk.bound.verify())
	}
	if err := disk.boundary(BoundaryMigrationRenamed); err != nil {
		return rollback(err)
	}
	if err := disk.bound.syncRoot(); err != nil {
		return rollback(err)
	}
	if err := disk.boundary(BoundaryMigrationSynced); err != nil {
		return rollback(err)
	}
	if err := disk.bound.removeRootDirectory(backup); err != nil {
		return rollback(err)
	}
	if err := disk.boundary(BoundaryMigrationRemoved); err != nil {
		return rollback(err)
	}
	if err := disk.bound.syncRoot(); err != nil {
		return rollback(err)
	}
	return disk.bound.verify()
}

func (disk *Disk) recoverInterruptedEmptyStableMigration(stable, backup string) error {
	backupInfo, err := disk.bound.rootEntry(backup)
	if err != nil {
		return err
	}
	if !backupInfo.exists {
		return nil
	}
	if !backupInfo.directory || backupInfo.symlink {
		return errors.New("stable migration backup is unsafe")
	}
	stableInfo, err := disk.bound.rootEntry(stable)
	if err != nil {
		return err
	}
	if stableInfo.exists {
		return errors.New("stable and its migration backup both exist")
	}
	if !disk.deviceOK(backupInfo) {
		return errors.New("stable migration backup is on another filesystem")
	}
	empty, err := disk.bound.rootDirectoryEmpty(backup)
	if err != nil {
		return err
	}
	if !empty {
		return errors.New("stable migration backup is not empty")
	}
	if err := disk.bound.renameRoot(backup, stable); err != nil {
		return err
	}
	return errors.Join(disk.bound.syncRoot(), disk.bound.verify())
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
	tempLinkName := ".stable-" + releaseID + ".tmp"
	if target, err := disk.bound.readRootLink(tempLinkName); err == nil {
		if filepath.ToSlash(target) != filepath.ToSlash(filepath.Join("generations", name)) {
			return errors.New("temporary stable target is unsafe")
		}
		if err := disk.bound.unlinkRoot(tempLinkName); err != nil {
			return err
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	if err := disk.remove(disk.generations, "."+name+".tmp"); err != nil {
		return err
	}
	if err := disk.remove(disk.generations, name); err != nil {
		return err
	}
	return errors.Join(disk.bound.syncGenerations(), disk.bound.syncRoot(), disk.bound.verify())
}

func (disk *Disk) String() string { return fmt.Sprintf("deploy disk %s", disk.root) }
