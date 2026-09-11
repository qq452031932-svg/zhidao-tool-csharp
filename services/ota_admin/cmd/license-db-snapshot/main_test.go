package main

import (
	"context"
	"database/sql"
	"os"
	"path/filepath"
	"strings"
	"testing"

	_ "modernc.org/sqlite"
)

func TestSnapshotRejectsNonAbsoluteSource(t *testing.T) {
	root := t.TempDir()
	if _, err := Snapshot(context.Background(), "db.sqlite", filepath.Join(root, "out.sqlite"), root); err == nil {
		t.Fatal("expected relative source to be rejected")
	}
}

func TestSnapshotRejectsDestinationOutsideMaintenanceRoot(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "db.sqlite")
	createTestDB(t, src)
	outside := filepath.Join(t.TempDir(), "out.sqlite")
	if _, err := Snapshot(context.Background(), src, outside, root); err == nil {
		t.Fatal("expected destination outside root to be rejected")
	}
}

func TestSnapshotRejectsSymlinkSourceAndExistingDestination(t *testing.T) {
	root := t.TempDir()
	realDB := filepath.Join(root, "real.sqlite")
	createTestDB(t, realDB)
	link := filepath.Join(root, "link.sqlite")
	if err := os.Symlink(realDB, link); err == nil {
		if _, err := Snapshot(context.Background(), link, filepath.Join(root, "out.sqlite"), root); err == nil {
			t.Fatal("expected symlink source to be rejected")
		}
	}
	out := filepath.Join(root, "existing.sqlite")
	if err := os.WriteFile(out, []byte("x"), 0o600); err != nil { t.Fatal(err) }
	if _, err := Snapshot(context.Background(), realDB, out, root); err == nil {
		t.Fatal("expected existing destination to be rejected")
	}
}

func TestSnapshotCreatesVerifiedSummaryWithoutSensitiveRows(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "db.sqlite")
	createTestDB(t, src)
	out := filepath.Join(root, "snap.sqlite")
	summary, err := Snapshot(context.Background(), src, out, root)
	if err != nil { t.Fatal(err) }
	if summary.QuickCheck != "ok" || summary.SchemaVersion != 3 || summary.TableCounts["license_cards"] != 1 {
		t.Fatalf("unexpected summary: %#v", summary)
	}
	if strings.Contains(summary.String(), "secret-card") || strings.Contains(summary.String(), "ciphertext") {
		t.Fatal("summary leaked sensitive row data")
	}
}

func createTestDB(t *testing.T, path string) {
	db, err := sql.Open("sqlite", path)
	if err != nil { t.Fatal(err) }
	defer db.Close()
	if _, err := db.Exec(`CREATE TABLE license_schema_version(singleton_id INTEGER PRIMARY KEY, version INTEGER); INSERT INTO license_schema_version VALUES(1,3); CREATE TABLE license_cards(id TEXT, ciphertext BLOB); INSERT INTO license_cards VALUES('secret-card', X'0102');`); err != nil { t.Fatal(err) }
}
