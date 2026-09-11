package main

import (
 "database/sql"
 "path/filepath"
 "testing"
 _ "modernc.org/sqlite"
)

func TestValidateDatabasePathRequiresDisposableRoot(t *testing.T) {
	if err := validateDatabasePath("relative.db", "/tmp/root"); err == nil { t.Fatal("relative path accepted") }
	if err := validateDatabasePath("/var/lib/superpan-license/license.db", "/tmp/root"); err == nil { t.Fatal("path outside root accepted") }
}

func TestSummaryStringIsRedacted(t *testing.T) {
	s := Summary{QuickCheck: "ok", TableCounts: map[string]int64{"license_cards": 2}}
	if got := s.String(); got == "" || containsSensitive(got) { t.Fatalf("unsafe summary: %s", got) }
}

func TestCheckDatabaseRunsQuickCheckAndCounts(t *testing.T) {
	root := t.TempDir(); path := filepath.Join(root, "license.db")
	db, err := sql.Open("sqlite", path); if err != nil { t.Fatal(err) }
	if _, err = db.Exec(`CREATE TABLE license_schema_version(singleton_id INTEGER PRIMARY KEY, version INTEGER); INSERT INTO license_schema_version VALUES(1,3); CREATE TABLE license_cards(id TEXT); INSERT INTO license_cards VALUES('hidden');`); err != nil { t.Fatal(err) }; db.Close()
	s, err := CheckDatabase(path, root); if err != nil { t.Fatal(err) }
	if s.QuickCheck != "ok" || s.TableCounts["license_cards"] != 1 { t.Fatalf("unexpected summary: %#v", s) }
}

func containsSensitive(s string) bool { for _, w := range []string{"ciphertext", "secret-card", "machine_hash", "nonce"} { for i := 0; i+len(w)<=len(s); i++ { if s[i:i+len(w)]==w { return true } } }; return false }
