package main

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	_ "modernc.org/sqlite"
)

type Summary struct {
	SchemaVersion int
	QuickCheck string
	TableCounts map[string]int64
	SHA256 string
}

func (s Summary) String() string {
	keys := make([]string, 0, len(s.TableCounts)); for k := range s.TableCounts { keys = append(keys, k) }; sort.Strings(keys)
	parts := []string{fmt.Sprintf("schema_version=%d", s.SchemaVersion), "quick_check=" + s.QuickCheck, "sha256=" + s.SHA256}
	for _, k := range keys { parts = append(parts, fmt.Sprintf("count_%s=%d", k, s.TableCounts[k])) }
	return strings.Join(parts, " ")
}

func Snapshot(ctx context.Context, source, destination, maintenanceRoot string) (Summary, error) {
	var zero Summary
	src, err := filepath.Abs(source); if err != nil || !filepath.IsAbs(source) { return zero, errors.New("source must be absolute") }
	dst, err := filepath.Abs(destination); if err != nil || !filepath.IsAbs(destination) { return zero, errors.New("destination must be absolute") }
	root, err := filepath.Abs(maintenanceRoot); if err != nil { return zero, err }
	inside := func(p string) bool { rel, e := filepath.Rel(root, p); return e == nil && rel != ".." && !strings.HasPrefix(rel, ".."+string(os.PathSeparator)) && !filepath.IsAbs(rel) }
	if !inside(src) || !inside(dst) { return zero, errors.New("paths must stay under maintenance root") }
	st, err := os.Lstat(src); if err != nil { return zero, err }; if !st.Mode().IsRegular() { return zero, errors.New("source must be a regular file") }
	if _, err := os.Lstat(dst); err == nil { return zero, errors.New("destination already exists") } else if !os.IsNotExist(err) { return zero, err }
	db, err := sql.Open("sqlite", src); if err != nil { return zero, err }; defer db.Close()
	if _, err = db.ExecContext(ctx, "PRAGMA wal_checkpoint(TRUNCATE)"); err != nil { return zero, fmt.Errorf("checkpoint: %w", err) }
	if _, err = db.ExecContext(ctx, "VACUUM INTO ?", dst); err != nil { return zero, fmt.Errorf("vacuum: %w", err) }
	check, err := verify(ctx, dst); if err != nil { return zero, err }; return check, nil
}

func verify(ctx context.Context, path string) (Summary, error) {
	db, err := sql.Open("sqlite", path); if err != nil { return Summary{}, err }; defer db.Close()
	var qc string; if err = db.QueryRowContext(ctx, "PRAGMA quick_check").Scan(&qc); err != nil { return Summary{}, err }; if !strings.EqualFold(qc, "ok") { return Summary{}, fmt.Errorf("quick_check=%s", qc) }
	var version int; if err = db.QueryRowContext(ctx, "SELECT version FROM license_schema_version WHERE singleton_id=1").Scan(&version); err != nil { return Summary{}, err }
	counts := map[string]int64{}; rows, err := db.QueryContext(ctx, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"); if err != nil { return Summary{}, err }; defer rows.Close()
	for rows.Next() { var name string; if err := rows.Scan(&name); err != nil { return Summary{}, err }; var n int64; if err := db.QueryRowContext(ctx, "SELECT count(*) FROM \""+strings.ReplaceAll(name, "\"", "\"\"")+"\"").Scan(&n); err != nil { return Summary{}, err }; counts[name] = n }
	b, err := os.ReadFile(path); if err != nil { return Summary{}, err }; h := sha256.Sum256(b)
	return Summary{SchemaVersion: version, QuickCheck: "ok", TableCounts: counts, SHA256: hex.EncodeToString(h[:])}, nil
}

func main() {
	source := flag.String("source", "", "absolute source database")
	destination := flag.String("destination", "", "new snapshot path")
	root := flag.String("maintenance-root", "", "validated maintenance root")
	flag.Parse(); if *source == "" || *destination == "" || *root == "" { fmt.Fprintln(os.Stderr, "source, destination and maintenance-root are required"); os.Exit(2) }
	s, err := Snapshot(context.Background(), *source, *destination, *root); if err != nil { fmt.Fprintln(os.Stderr, err); os.Exit(1) }; fmt.Println(s.String())
}
