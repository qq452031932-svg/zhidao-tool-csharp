package main

import (
	"database/sql"
	_ "modernc.org/sqlite"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func createDB(t *testing.T, root string) string {
	t.Helper()
	path := filepath.Join(root, "state.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	_, err = db.Exec(`CREATE TABLE license_schema_version(singleton_id INTEGER PRIMARY KEY,version INTEGER); INSERT INTO license_schema_version VALUES(1,3);
CREATE TABLE license_software(id TEXT,enabled INTEGER); INSERT INTO license_software VALUES('hidden',1);
CREATE TABLE license_packages(id TEXT); INSERT INTO license_packages VALUES('hidden');
CREATE TABLE license_package_members(package_id TEXT,software_id TEXT); INSERT INTO license_package_members VALUES('hidden','hidden');
CREATE TABLE license_cards(id TEXT,package_id TEXT,activated_at INTEGER,expires_at INTEGER,machine_hash TEXT,unactivated_total_days INTEGER); INSERT INTO license_cards VALUES('hidden','hidden',NULL,NULL,NULL,30);
CREATE TABLE license_activation_bindings(card_id TEXT,software_id TEXT,machine_hash TEXT,activated_at INTEGER,expires_at INTEGER);
CREATE TABLE license_activation_requests(request_id TEXT,card_id TEXT,software_id TEXT,machine_hash TEXT,activated_at INTEGER,expires_at INTEGER,lease_until INTEGER);
CREATE TABLE license_replay_nonces(nonce TEXT,expires_at INTEGER); CREATE TABLE license_leases(card_id TEXT,software_id TEXT,machine_hash TEXT,last_seen_at INTEGER,lease_until INTEGER);
CREATE TABLE license_admin_idempotency(key TEXT); CREATE TABLE license_admin_idempotency_cards(idempotency_key TEXT,card_id TEXT);`)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestValidateInputsRequireCanonicalDisposableStoppedInstance(t *testing.T) {
	root := filepath.Join(t.TempDir(), disposableBaseName)
	if err := os.Mkdir(root, 0700); err != nil {
		t.Fatal(err)
	}
	db := createDB(t, root)
	marker := filepath.Join(root, stoppedMarkerName)
	if err := os.WriteFile(marker, []byte("stopped\n"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := validateInputs(db, root, marker); err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct{ db, root, marker string }{{"relative", root, marker}, {db, filepath.Dir(root), marker}, {db, root, filepath.Join(root, "missing")}} {
		if err := validateInputs(tc.db, tc.root, tc.marker); err == nil {
			t.Fatalf("accepted %+v", tc)
		}
	}
}

func TestCheckDatabaseProducesStableApprovedAggregates(t *testing.T) {
	root := filepath.Join(t.TempDir(), disposableBaseName)
	_ = os.Mkdir(root, 0700)
	path := createDB(t, root)
	marker := filepath.Join(root, stoppedMarkerName)
	_ = os.WriteFile(marker, []byte("stopped\n"), 0600)
	s, err := CheckDatabase(path, root, marker)
	if err != nil {
		t.Fatal(err)
	}
	output := s.String()
	if !strings.HasPrefix(output, "quick_check=ok schema_version=3 count_license_software=1") {
		t.Fatalf("unstable summary: %s", output)
	}
	for _, forbidden := range []string{"hidden", "ciphertext", "machine_hash"} {
		if strings.Contains(output, forbidden) {
			t.Fatalf("unsafe summary: %s", output)
		}
	}
}

func TestCheckDatabaseRejectsSemanticInvariantViolation(t *testing.T) {
	root := filepath.Join(t.TempDir(), disposableBaseName)
	_ = os.Mkdir(root, 0700)
	path := createDB(t, root)
	marker := filepath.Join(root, stoppedMarkerName)
	_ = os.WriteFile(marker, []byte("stopped\n"), 0600)
	db, _ := sql.Open("sqlite", path)
	_, _ = db.Exec(`UPDATE license_cards SET activated_at=1,expires_at=NULL,machine_hash='hidden'`)
	_ = db.Close()
	if _, err := CheckDatabase(path, root, marker); err == nil {
		t.Fatal("semantic corruption accepted")
	}
}

func TestValidateInputsRejectsSymlinkDatabase(t *testing.T) {
	root := filepath.Join(t.TempDir(), disposableBaseName)
	_ = os.Mkdir(root, 0700)
	real := createDB(t, root)
	link := filepath.Join(root, "link.db")
	if err := os.Symlink(real, link); err != nil {
		t.Skip(err)
	}
	marker := filepath.Join(root, stoppedMarkerName)
	_ = os.WriteFile(marker, []byte("stopped\n"), 0600)
	if err := validateInputs(link, root, marker); err == nil {
		t.Fatal("symlink accepted")
	}
}
