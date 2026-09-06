package deploy

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"sync"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

const testReleaseID = "33333333-3333-4333-8333-333333333333"

func TestGenerationFaultsBeforeStableCommitLeaveCurrentUnchanged(t *testing.T) {
	for _, boundary := range []Boundary{BoundaryFileSynced, BoundaryCandidateSynced, BoundaryGenerationNamed, BoundaryGenerationsSynced, BoundaryStableTemp} {
		t.Run(string(boundary), func(t *testing.T) {
			root := publicationRoot(t)
			oldTarget := installOldStableIfSupported(t, root)
			disk, err := NewDisk(root, func(actual Boundary) error {
				if actual == boundary {
					return errors.New("injected")
				}
				return nil
			})
			if err != nil {
				t.Fatal(err)
			}
			name, cleanup, err := disk.stage(context.Background(), sourceEnvelope(t), testReleaseID)
			if boundary == BoundaryFileSynced || boundary == BoundaryCandidateSynced {
				if err == nil {
					t.Fatal("stage fault succeeded")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			_, err = disk.commit(name, cleanup)
			if err == nil {
				t.Fatal("commit fault succeeded")
			}
			if oldTarget != "" {
				target, readErr := os.Readlink(filepath.Join(root, "stable"))
				if readErr != nil || target != oldTarget {
					t.Fatalf("stable=%q err=%v", target, readErr)
				}
			}
		})
	}
}

func TestLinuxStableCommitUsesRelativeContainedLinkAndReadOnlyGeneration(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("real symlink durability path is Linux-only")
	}
	root := publicationRoot(t)
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	name, cleanup, err := disk.stage(context.Background(), sourceEnvelope(t), testReleaseID)
	if err != nil {
		t.Fatal(err)
	}
	switched, err := disk.commit(name, cleanup)
	if err != nil || !switched {
		t.Fatalf("switched=%v err=%v", switched, err)
	}
	if got, err := disk.StableTarget(); err != nil || got != name {
		t.Fatalf("target=%q err=%v", got, err)
	}
	info, err := os.Stat(filepath.Join(root, "generations", name))
	if err != nil || info.Mode().Perm() != 0o555 {
		t.Fatalf("mode=%v err=%v", info.Mode(), err)
	}
}

func TestLinuxRecoveryRejectsHardLinkedGenerationMembers(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("hard-link recovery policy is Linux-only")
	}
	root := publicationRoot(t)
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	name, cleanup, err := disk.stage(context.Background(), sourceEnvelope(t), testReleaseID)
	if err != nil {
		t.Fatal(err)
	}
	if switched, err := disk.commit(name, cleanup); err != nil || !switched {
		t.Fatalf("switched=%v err=%v", switched, err)
	}
	member := filepath.Join(root, "generations", name, "asset.nupkg")
	if err := os.Link(member, filepath.Join(t.TempDir(), "external-alias")); err != nil {
		t.Fatal(err)
	}
	if _, err := disk.RecoverGeneration(context.Background(), fixedValidator{report: release.Report{Verified: true}}); err == nil {
		t.Fatal("hard-linked generation member accepted")
	}
}

func TestGenerationDurabilityBoundariesHaveExactOrder(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("real symlink durability path is Linux-only")
	}
	root := publicationRoot(t)
	var got []Boundary
	disk, err := NewDisk(root, func(boundary Boundary) error {
		got = append(got, boundary)
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	envelope := sourceEnvelope(t)
	name, cleanup, err := disk.stage(context.Background(), envelope, testReleaseID)
	if err != nil {
		t.Fatal(err)
	}
	switched, err := disk.commit(name, cleanup)
	if err != nil || !switched {
		t.Fatalf("switched=%v err=%v", switched, err)
	}
	want := []Boundary{BoundaryFileSynced, BoundaryFileSynced, BoundaryCandidateSynced, BoundaryGenerationNamed, BoundaryGenerationsSynced, BoundaryStableTemp, BoundaryStableReplaced, BoundaryParentSynced}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("boundaries=%v want=%v", got, want)
	}
	final := filepath.Join(root, "generations", name)
	entries, err := os.ReadDir(final)
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		info, err := entry.Info()
		if err != nil {
			t.Fatal(err)
		}
		if !info.Mode().IsRegular() || info.Mode().Perm()&0o222 != 0 {
			t.Fatalf("generation member %q mode=%v", entry.Name(), info.Mode())
		}
	}
}

func TestStableTargetRejectsUnsafeOrEscapingLinks(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("real symlink validation path is Linux-only")
	}
	tests := []struct {
		name   string
		target string
		setup  func(string)
	}{
		{name: "absolute", target: "/tmp/release-33333333-3333-4333-8333-333333333333"},
		{name: "traversal", target: "generations/../release-33333333-3333-4333-8333-333333333333"},
		{name: "wrong prefix", target: "other/release-33333333-3333-4333-8333-333333333333"},
		{name: "linked generation", target: "generations/release-33333333-3333-4333-8333-333333333333", setup: func(root string) {
			if err := os.Symlink(t.TempDir(), filepath.Join(root, "generations", "release-33333333-3333-4333-8333-333333333333")); err != nil {
				t.Fatal(err)
			}
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			root := publicationRoot(t)
			disk, err := NewDisk(root, nil)
			if err != nil {
				t.Fatal(err)
			}
			if test.setup != nil {
				test.setup(root)
			}
			if err := os.Symlink(test.target, filepath.Join(root, "stable")); err != nil {
				t.Fatal(err)
			}
			if _, err := disk.StableTarget(); err == nil {
				t.Fatal("unsafe stable target accepted")
			}
		})
	}
}

func TestMigrateEmptyStableRejectsContentAndRemovesOnlyEmptyDirectory(t *testing.T) {
	root := publicationRoot(t)
	stable := filepath.Join(root, "stable")
	if err := os.Mkdir(stable, 0o755); err != nil {
		t.Fatal(err)
	}
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(stable, "keep"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := disk.MigrateEmptyStable(); err == nil {
		t.Fatal("non-empty stable migrated")
	}
	if err := os.Remove(filepath.Join(stable, "keep")); err != nil {
		t.Fatal(err)
	}
	if err := disk.MigrateEmptyStable(); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(stable); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("stable remains: %v", err)
	}
}

func TestMigrateMissingStableIsAnIdempotentNoOp(t *testing.T) {
	root := publicationRoot(t)
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	for range 2 {
		if err := disk.MigrateEmptyStable(); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := os.Lstat(filepath.Join(root, "stable")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("missing stable changed: %v", err)
	}
}

func TestMigrateEmptyStableRecoversAnInterruptedBackupBeforeRetrying(t *testing.T) {
	root := publicationRoot(t)
	backup := filepath.Join(root, ".stable-empty-migration")
	if err := os.Mkdir(backup, 0o755); err != nil {
		t.Fatal(err)
	}
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := disk.MigrateEmptyStable(); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(backup); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("interrupted migration backup remains: %v", err)
	}
	if _, err := os.Lstat(filepath.Join(root, "stable")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("stable should finish migrated after recovery: %v", err)
	}
}

func TestNewDiskCanonicalizesAliasedRootsForOneProcessLock(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("filesystem alias behavior is Linux-only")
	}
	parent := t.TempDir()
	realRoot := filepath.Join(parent, "real")
	if err := os.Mkdir(realRoot, 0o755); err != nil {
		t.Fatal(err)
	}
	aliasParent := filepath.Join(t.TempDir(), "alias")
	if err := os.Symlink(parent, aliasParent); err != nil {
		t.Fatal(err)
	}
	first, err := NewDisk(realRoot, nil)
	if err != nil {
		t.Fatal(err)
	}
	second, err := NewDisk(filepath.Join(aliasParent, "real"), nil)
	if err != nil {
		t.Fatal(err)
	}
	if first.root != second.root {
		t.Fatalf("roots did not canonicalize: %q != %q", first.root, second.root)
	}
	firstLock, _ := processLocks.LoadOrStore(first.root, &sync.Mutex{})
	secondLock, _ := processLocks.LoadOrStore(second.root, &sync.Mutex{})
	if firstLock != secondLock {
		t.Fatal("aliased publication roots received different process locks")
	}
}

func TestAbortUnpublishedCanRemoveItsReadOnlyGeneration(t *testing.T) {
	root := publicationRoot(t)
	disk, err := NewDisk(root, nil)
	if err != nil {
		t.Fatal(err)
	}
	name, _, err := disk.stage(context.Background(), sourceEnvelope(t), testReleaseID)
	if err != nil {
		t.Fatal(err)
	}
	temporary := filepath.Join(root, "generations", "."+name+".tmp")
	final := filepath.Join(root, "generations", name)
	if err := os.Rename(temporary, final); err != nil {
		t.Fatal(err)
	}
	if err := disk.abortUnpublished(testReleaseID); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(final); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("unpublished generation remains: %v", err)
	}
}

func TestMigrateEmptyStableRejectsCrossFilesystemAndRollsBackEveryFault(t *testing.T) {
	t.Run("cross filesystem", func(t *testing.T) {
		root := publicationRoot(t)
		stable := filepath.Join(root, "stable")
		if err := os.Mkdir(stable, 0o755); err != nil {
			t.Fatal(err)
		}
		disk, err := NewDisk(root, nil)
		if err != nil {
			t.Fatal(err)
		}
		disk.deviceOK = func(rootEntry) bool { return false }
		if err := disk.MigrateEmptyStable(); err == nil {
			t.Fatal("cross-filesystem stable directory migrated")
		}
		if info, err := os.Stat(stable); err != nil || !info.IsDir() {
			t.Fatalf("stable was changed: %v", err)
		}
	})
	for _, boundary := range []Boundary{BoundaryMigrationRenamed, BoundaryMigrationSynced, BoundaryMigrationRemoved} {
		t.Run(string(boundary), func(t *testing.T) {
			root := publicationRoot(t)
			stable := filepath.Join(root, "stable")
			if err := os.Mkdir(stable, 0o755); err != nil {
				t.Fatal(err)
			}
			disk, err := NewDisk(root, func(actual Boundary) error {
				if actual == boundary {
					return errors.New("injected migration failure")
				}
				return nil
			})
			if err != nil {
				t.Fatal(err)
			}
			if err := disk.MigrateEmptyStable(); err == nil {
				t.Fatal("migration fault succeeded")
			}
			info, err := os.Stat(stable)
			if err != nil || !info.IsDir() {
				t.Fatalf("empty stable was not restored: %v", err)
			}
			entries, err := os.ReadDir(stable)
			if err != nil || len(entries) != 0 {
				t.Fatalf("restored stable is not empty: %v %v", entries, err)
			}
		})
	}
}

func publicationRoot(t *testing.T) string {
	t.Helper()
	root := t.TempDir()
	if err := os.Chmod(root, 0o755); err != nil {
		t.Fatal(err)
	}
	return root
}

func sourceEnvelope(t *testing.T) *upload.Envelope {
	t.Helper()
	root := t.TempDir()
	names := []string{"asset.nupkg", "releases.win-x64-stable.json"}
	entries := make([]upload.Entry, 0, len(names))
	for _, name := range names {
		path := filepath.Join(root, name)
		content := []byte(name)
		if err := os.WriteFile(path, content, 0o600); err != nil {
			t.Fatal(err)
		}
		entries = append(entries, upload.Entry{Name: name, Path: path, Size: int64(len(content))})
	}
	return &upload.Envelope{ExtractionRoot: root, Entries: entries}
}

func installOldStableIfSupported(t *testing.T, root string) string {
	t.Helper()
	old := filepath.Join(root, "generations", "release-11111111-1111-4111-8111-111111111111")
	if err := os.MkdirAll(old, 0o755); err != nil {
		t.Fatal(err)
	}
	target := "generations/release-11111111-1111-4111-8111-111111111111"
	if err := os.Symlink(target, filepath.Join(root, "stable")); err != nil {
		return ""
	}
	return target
}
