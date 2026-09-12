package main

import (
	"context"
	"database/sql"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	_ "modernc.org/sqlite"
)

type fakeServiceProbe struct {
	state ServiceState
	err   error
}

func (p fakeServiceProbe) State(context.Context, string) (ServiceState, error) { return p.state, p.err }

type permissiveTrust struct{}

func (permissiveTrust) ValidateSource(string, SourceKind) (FileIdentity, error) {
	return FileIdentity{Device: 1, Inode: 1}, nil
}
func (permissiveTrust) ValidateBackupDirectory(string) (FileIdentity, error) {
	return FileIdentity{Device: 1, Inode: 2}, nil
}
func (permissiveTrust) ValidateSidecar(path string, _ SourceKind) (FileIdentity, bool, error) {
	if _, err := os.Lstat(path); errors.Is(err, os.ErrNotExist) {
		return FileIdentity{}, false, nil
	}
	return FileIdentity{Device: 1, Inode: 3}, true, nil
}
func (permissiveTrust) SecureOutput(string) error { return nil }

func testOptions(t *testing.T, source, destination string) SnapshotOptions {
	t.Helper()
	return SnapshotOptions{ServiceName: requiredService, Source: source, Destination: destination,
		ServiceProbe: fakeServiceProbe{state: ServiceStopped}, TrustProbe: permissiveTrust{}}
}

func TestSnapshotRejectsInvalidOrUnprovenServiceState(t *testing.T) {
	for _, tc := range []struct {
		name    string
		service string
		probe   fakeServiceProbe
	}{
		{"invalid service", "other.service", fakeServiceProbe{state: ServiceStopped}},
		{"active", requiredService, fakeServiceProbe{state: ServiceActive}},
		{"unknown", requiredService, fakeServiceProbe{state: ServiceUnknown}},
		{"probe failure", requiredService, fakeServiceProbe{err: errors.New("boom")}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			o := SnapshotOptions{ServiceName: tc.service, Source: liveDatabasePath, Destination: filepath.Join(backupRoot, "x.db"), ServiceProbe: tc.probe, TrustProbe: permissiveTrust{}}
			if _, err := Snapshot(context.Background(), o); err == nil {
				t.Fatal("expected fail-closed service rejection")
			}
		})
	}
}

func TestClassifyPathsAllowsOnlyLiveAndBackupSourcesAndBackupDestination(t *testing.T) {
	for _, tc := range []struct {
		source, destination string
		ok                  bool
	}{
		{liveDatabasePath, backupRoot + "/daily/a.db", true},
		{backupRoot + "/daily/a.db", backupRoot + "/restore/b.db", true},
		{"/tmp/state.db", backupRoot + "/a.db", false},
		{liveDatabasePath + "/child", backupRoot + "/a.db", false},
		{liveDatabasePath, "/tmp/a.db", false},
		{backupRoot + "/../escape.db", backupRoot + "/a.db", false},
	} {
		_, err := classifyPaths(tc.source, tc.destination)
		if (err == nil) != tc.ok {
			t.Fatalf("source=%q destination=%q ok=%v err=%v", tc.source, tc.destination, tc.ok, err)
		}
	}
}

func TestSnapshotCreatesAggregateOnlyVerifiedSnapshot(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "state.db")
	dst := filepath.Join(root, "snapshot.db")
	createTestDB(t, src)
	o := testOptions(t, src, dst)
	o.PathClassifier = func(string, string) (SourceKind, error) { return SourceLive, nil }
	summary, err := Snapshot(context.Background(), o)
	if err != nil {
		t.Fatal(err)
	}
	if summary.QuickCheck != "ok" || summary.SchemaVersion != 3 || summary.TableCounts["license_cards"] != 1 {
		t.Fatalf("unexpected summary: %#v", summary)
	}
	text := summary.String()
	if strings.Contains(text, "secret-card") || strings.Contains(text, "ciphertext") {
		t.Fatalf("sensitive row leaked: %s", text)
	}
	if !strings.Contains(text, "schema_version=3 quick_check=ok count_license_cards=1") {
		t.Fatalf("unstable output: %s", text)
	}
	if _, err := os.Stat(dst); err != nil {
		t.Fatal(err)
	}
}

func TestSnapshotRejectsExistingDestinationAndCleansTemporaryFiles(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "state.db")
	dst := filepath.Join(root, "snapshot.db")
	createTestDB(t, src)
	if err := os.WriteFile(dst, []byte("existing"), 0600); err != nil {
		t.Fatal(err)
	}
	o := testOptions(t, src, dst)
	o.PathClassifier = func(string, string) (SourceKind, error) { return SourceLive, nil }
	if _, err := Snapshot(context.Background(), o); err == nil {
		t.Fatal("expected existing destination rejection")
	}
	b, _ := os.ReadFile(dst)
	if string(b) != "existing" {
		t.Fatal("existing destination changed")
	}
	matches, _ := filepath.Glob(filepath.Join(root, ".license-db-snapshot-*"))
	if len(matches) != 0 {
		t.Fatalf("temporary files leaked: %v", matches)
	}
}

func TestSnapshotFailsWhenExclusiveLockIsBusy(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "state.db")
	dst := filepath.Join(root, "snapshot.db")
	createTestDB(t, src)
	writer, err := sql.Open("sqlite", src)
	if err != nil {
		t.Fatal(err)
	}
	defer writer.Close()
	if _, err := writer.Exec("BEGIN IMMEDIATE"); err != nil {
		t.Fatal(err)
	}
	defer writer.Exec("ROLLBACK")
	o := testOptions(t, src, dst)
	o.PathClassifier = func(string, string) (SourceKind, error) { return SourceLive, nil }
	if _, err := Snapshot(context.Background(), o); err == nil || !strings.Contains(strings.ToLower(err.Error()), "exclusive") {
		t.Fatalf("expected exclusive lock failure, got %v", err)
	}
	if _, err := os.Stat(dst); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("destination leaked: %v", err)
	}
}

func TestSnapshotRejectsBusyOrIncompleteCheckpoint(t *testing.T) {
	for _, tc := range []struct {
		name                    string
		busy, log, checkpointed int
	}{
		{"busy", 1, 4, 0}, {"incomplete", 0, 4, 3},
	} {
		t.Run(tc.name, func(t *testing.T) {
			root := t.TempDir()
			src := filepath.Join(root, "state.db")
			dst := filepath.Join(root, "snapshot.db")
			createTestDB(t, src)
			o := testOptions(t, src, dst)
			o.PathClassifier = func(string, string) (SourceKind, error) { return SourceLive, nil }
			o.Checkpoint = func(context.Context, *sql.Conn) (int, int, int, error) { return tc.busy, tc.log, tc.checkpointed, nil }
			if _, err := Snapshot(context.Background(), o); err == nil || !strings.Contains(err.Error(), "checkpoint incomplete") {
				t.Fatalf("expected checkpoint rejection, got %v", err)
			}
			if _, err := os.Stat(dst); !errors.Is(err, os.ErrNotExist) {
				t.Fatalf("destination leaked: %v", err)
			}
		})
	}
}

type replacingTrust struct {
	permissiveTrust
	calls int
}

func (p *replacingTrust) ValidateSidecar(string, SourceKind) (FileIdentity, bool, error) {
	p.calls++
	return FileIdentity{Device: 1, Inode: uint64(p.calls)}, true, nil
}

func TestSnapshotRejectsSidecarIdentityReplacementAndCleans(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "state.db")
	dst := filepath.Join(root, "snapshot.db")
	createTestDB(t, src)
	trust := &replacingTrust{}
	o := testOptions(t, src, dst)
	o.TrustProbe = trust
	o.PathClassifier = func(string, string) (SourceKind, error) { return SourceLive, nil }
	if _, err := Snapshot(context.Background(), o); err == nil || !strings.Contains(err.Error(), "identity") {
		t.Fatalf("expected identity failure, got %v", err)
	}
	if _, err := os.Stat(dst); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("destination leaked: %v", err)
	}
	matches, _ := filepath.Glob(filepath.Join(root, ".license-db-snapshot-*"))
	if len(matches) != 0 {
		t.Fatalf("temporary files leaked: %v", matches)
	}
}

func createTestDB(t *testing.T, path string) {
	t.Helper()
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if _, err := db.Exec(`PRAGMA journal_mode=WAL; CREATE TABLE license_schema_version(singleton_id INTEGER PRIMARY KEY, version INTEGER); INSERT INTO license_schema_version VALUES(1,3); CREATE TABLE license_cards(id TEXT, ciphertext BLOB); INSERT INTO license_cards VALUES('secret-card', X'0102');`); err != nil {
		t.Fatal(err)
	}
}
