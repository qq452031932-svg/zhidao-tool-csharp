//go:build linux

package deploy

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/release"
)

func TestBoundPublicationDescriptorsFailClosedWhenNamedRootIsReplaced(t *testing.T) {
	root := publicationRoot(t)
	moved := root + ".moved"
	var swapped bool
	disk, err := NewDisk(root, func(boundary Boundary) error {
		if boundary != BoundaryGenerationsSynced || swapped {
			return nil
		}
		swapped = true
		if err := os.Rename(root, moved); err != nil {
			return err
		}
		if err := os.Mkdir(root, 0o755); err != nil {
			return err
		}
		return os.Mkdir(filepath.Join(root, "generations"), 0o755)
	})
	if err != nil {
		t.Fatal(err)
	}
	report := linuxTestReport()
	_, switched, err := disk.PublishGeneration(context.Background(), sourceEnvelope(t), testReleaseID, report, fixedValidator{report: report})
	if err == nil || switched || !errors.Is(err, ErrCleanupIncomplete) {
		t.Fatalf("publish switched=%v err=%v", switched, err)
	}
	if _, err := os.Lstat(filepath.Join(root, "stable")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("replacement root received stable link: %v", err)
	}
}

func TestBoundGenerationDescriptorFailsClosedWhenNamedDirectoryIsReplaced(t *testing.T) {
	root := publicationRoot(t)
	generations := filepath.Join(root, "generations")
	moved := generations + ".moved"
	var swapped bool
	disk, err := NewDisk(root, func(boundary Boundary) error {
		if boundary != BoundaryCandidateSynced || swapped {
			return nil
		}
		swapped = true
		if err := os.Rename(generations, moved); err != nil {
			return err
		}
		return os.Mkdir(generations, 0o755)
	})
	if err != nil {
		t.Fatal(err)
	}
	report := linuxTestReport()
	_, switched, err := disk.PublishGeneration(context.Background(), sourceEnvelope(t), testReleaseID, report, fixedValidator{report: report})
	if err == nil || switched || !errors.Is(err, ErrCleanupIncomplete) {
		t.Fatalf("publish switched=%v err=%v", switched, err)
	}
	if entries, err := os.ReadDir(generations); err != nil || len(entries) != 0 {
		t.Fatalf("replacement generations mutated: entries=%v err=%v", entries, err)
	}
}

func linuxTestReport() release.Report {
	return release.Report{SchemaVersion: 1, Verified: true, PackageID: "SuperPan.BaiduBatchTransfer", Channel: "win-x64-stable", Version: "1.2.3", FileCount: 2, Files: []release.ReportFile{{Name: "asset.nupkg", Size: 12, SHA256: strings.Repeat("a", 64)}, {Name: "releases.win-x64-stable.json", Size: 31, SHA256: strings.Repeat("b", 64)}}}
}
