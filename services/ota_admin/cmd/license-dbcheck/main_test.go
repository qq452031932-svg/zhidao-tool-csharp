package main

import "testing"

func TestValidateDatabasePathRequiresDisposableRoot(t *testing.T) {
	if err := validateDatabasePath("relative.db", "/tmp/root"); err == nil { t.Fatal("relative path accepted") }
	if err := validateDatabasePath("/var/lib/superpan-license/license.db", "/tmp/root"); err == nil { t.Fatal("path outside root accepted") }
}

func TestSummaryStringIsRedacted(t *testing.T) {
	s := Summary{QuickCheck: "ok", TableCounts: map[string]int64{"license_cards": 2}}
	if got := s.String(); got == "" || containsSensitive(got) { t.Fatalf("unsafe summary: %s", got) }
}

func containsSensitive(s string) bool { for _, w := range []string{"ciphertext", "secret-card", "machine_hash", "nonce"} { for i := 0; i+len(w)<=len(s); i++ { if s[i:i+len(w)]==w { return true } } }; return false }
