package main

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	_ "modernc.org/sqlite"
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

const disposableBaseName = "license-capacity-18093"
const stoppedMarkerName = "instance.stopped"

var approvedTables = []string{"license_software", "license_packages", "license_package_members", "license_cards", "license_activation_bindings", "license_activation_requests", "license_replay_nonces", "license_leases", "license_admin_idempotency", "license_admin_idempotency_cards"}

type Summary struct {
	QuickCheck    string
	SchemaVersion int
	Counts        map[string]int64
}

func (s Summary) String() string {
	parts := []string{fmt.Sprintf("quick_check=%s schema_version=%d", s.QuickCheck, s.SchemaVersion)}
	for _, name := range approvedTables {
		parts = append(parts, fmt.Sprintf("count_%s=%d", name, s.Counts[name]))
	}
	return strings.Join(parts, " ")
}

func validateInputs(path, root, marker, expected string) error {
	for _, value := range []string{path, root, marker, expected} {
		if !filepath.IsAbs(value) {
			return errors.New("absolute paths required")
		}
	}
	cleanRoot := filepath.Clean(root)
	if filepath.Base(cleanRoot) != disposableBaseName {
		return errors.New("root is not named disposable instance")
	}
	if runtime.GOOS == "linux" && cleanRoot != "/var/lib/"+disposableBaseName {
		return errors.New("disposable root is not the fixed Linux path")
	}
	if filepath.Clean(path) != filepath.Join(cleanRoot, "state.db") || filepath.Clean(marker) != filepath.Join(cleanRoot, stoppedMarkerName) || filepath.Clean(expected) != filepath.Join(cleanRoot, "expected-counts.json") {
		return errors.New("paths do not match disposable contract")
	}
	rootInfo, err := os.Lstat(cleanRoot)
	if err != nil || !rootInfo.IsDir() || rootInfo.Mode()&os.ModeSymlink != 0 || (runtime.GOOS != "windows" && rootInfo.Mode().Perm()&0077 != 0) {
		return errors.New("untrusted disposable root")
	}
	for _, value := range []string{path, marker, expected} {
		info, e := os.Lstat(value)
		if e != nil || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 || (runtime.GOOS != "windows" && info.Mode().Perm()&0077 != 0) {
			return errors.New("untrusted disposable file")
		}
	}
	b, err := os.ReadFile(marker)
	if err != nil || string(b) != "stopped\n" {
		return errors.New("stopped-instance proof missing")
	}
	return nil
}

func CheckDatabase(path, root, marker, expectedPath string) (Summary, error) {
	if err := validateInputs(path, root, marker, expectedPath); err != nil {
		return Summary{}, err
	}
	expected, err := readExpectedCounts(expectedPath)
	if err != nil {
		return Summary{}, err
	}
	db, anchor, err := openAnchoredReadOnly(path)
	if err != nil {
		return Summary{}, err
	}
	defer anchor.Close()
	defer db.Close()
	ctx := context.Background()
	var quick string
	if err = db.QueryRowContext(ctx, "PRAGMA quick_check").Scan(&quick); err != nil || quick != "ok" {
		return Summary{}, errors.New("quick check failed")
	}
	var version int
	if err = db.QueryRowContext(ctx, "SELECT version FROM license_schema_version WHERE singleton_id=1").Scan(&version); err != nil || version != 3 {
		return Summary{}, errors.New("schema version invariant failed")
	}
	var unknown int
	if err = db.QueryRowContext(ctx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('license_schema_version','license_software','license_packages','license_package_members','license_cards','license_activation_bindings','license_activation_requests','license_replay_nonces','license_leases','license_admin_idempotency','license_admin_idempotency_cards')").Scan(&unknown); err != nil || unknown != 0 {
		return Summary{}, errors.New("schema allowlist invariant failed")
	}
	invariants := []string{"SELECT count(*) FROM license_cards c LEFT JOIN license_packages p ON p.id=c.package_id WHERE p.id IS NULL OR (c.activated_at IS NULL)!=(c.expires_at IS NULL) OR (c.activated_at IS NULL)!=(c.machine_hash IS NULL) OR c.unactivated_total_days<0", "SELECT count(*) FROM license_package_members m LEFT JOIN license_packages p ON p.id=m.package_id LEFT JOIN license_software s ON s.id=m.software_id WHERE p.id IS NULL OR s.id IS NULL", "SELECT count(*) FROM license_activation_bindings b LEFT JOIN license_cards c ON c.id=b.card_id LEFT JOIN license_software s ON s.id=b.software_id WHERE c.id IS NULL OR s.id IS NULL OR b.expires_at<b.activated_at", "SELECT count(*) FROM license_activation_requests r LEFT JOIN license_cards c ON c.id=r.card_id LEFT JOIN license_software s ON s.id=r.software_id WHERE c.id IS NULL OR s.id IS NULL OR r.expires_at<r.activated_at OR r.lease_until<r.activated_at", "SELECT count(*) FROM license_leases l LEFT JOIN license_cards c ON c.id=l.card_id LEFT JOIN license_software s ON s.id=l.software_id WHERE c.id IS NULL OR s.id IS NULL", "SELECT count(*) FROM license_admin_idempotency_cards x LEFT JOIN license_admin_idempotency i ON i.key=x.idempotency_key LEFT JOIN license_cards c ON c.id=x.card_id WHERE i.key IS NULL OR c.id IS NULL"}
	for _, query := range invariants {
		var invalid int64
		if err = db.QueryRowContext(ctx, query).Scan(&invalid); err != nil || invalid != 0 {
			return Summary{}, errors.New("semantic aggregate invariant failed")
		}
	}
	counts := map[string]int64{}
	for _, name := range approvedTables {
		var count int64
		if err = db.QueryRowContext(ctx, "SELECT count(*) FROM "+name).Scan(&count); err != nil {
			return Summary{}, errors.New("approved aggregate unavailable")
		}
		counts[name] = count
	}
	for name, want := range expected.Minimum {
		if counts[name] < want {
			return Summary{}, errors.New("minimum aggregate invariant failed")
		}
	}
	for name, want := range expected.Exact {
		if counts[name] != want {
			return Summary{}, errors.New("exact aggregate invariant failed")
		}
	}
	return Summary{"ok", version, counts}, nil
}

type expectedCounts struct {
	Minimum map[string]int64 `json:"minimum"`
	Exact   map[string]int64 `json:"exact"`
}

func readExpectedCounts(path string) (expectedCounts, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return expectedCounts{}, errors.New("expected aggregates unavailable")
	}
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	var value expectedCounts
	if decoder.Decode(&value) != nil {
		return expectedCounts{}, errors.New("invalid expected aggregates")
	}
	var trailing any
	if decoder.Decode(&trailing) != io.EOF {
		return expectedCounts{}, errors.New("invalid expected aggregates")
	}
	allowed := map[string]bool{}
	for _, name := range approvedTables {
		allowed[name] = true
	}
	core := approvedTables[:8]
	for _, name := range core {
		if value.Minimum[name] < 1 {
			return expectedCounts{}, errors.New("core load aggregate minimum missing")
		}
	}
	for name, count := range value.Minimum {
		if !allowed[name] || count < 0 {
			return expectedCounts{}, errors.New("invalid minimum aggregate")
		}
		if _, duplicate := value.Exact[name]; duplicate {
			return expectedCounts{}, errors.New("duplicate expected aggregate")
		}
	}
	for name, count := range value.Exact {
		if !allowed[name] || count < 0 {
			return expectedCounts{}, errors.New("invalid exact aggregate")
		}
	}
	return value, nil
}

func openAnchoredReadOnly(path string) (*sql.DB, *os.File, error) {
	before, err := os.Lstat(path)
	if err != nil || !before.Mode().IsRegular() || before.Mode()&os.ModeSymlink != 0 {
		return nil, nil, errors.New("database identity is untrusted")
	}
	anchor, err := os.Open(path)
	if err != nil {
		return nil, nil, errors.New("open database descriptor")
	}
	after, err := anchor.Stat()
	current, currentErr := os.Lstat(path)
	if err != nil || currentErr != nil || current.Mode()&os.ModeSymlink != 0 || !os.SameFile(before, after) || !os.SameFile(after, current) {
		anchor.Close()
		return nil, nil, errors.New("database identity changed")
	}
	openPath := path
	if runtime.GOOS == "linux" {
		openPath = fmt.Sprintf("/proc/self/fd/%d", anchor.Fd())
	}
	db, err := sql.Open("sqlite", "file:"+filepath.ToSlash(openPath)+"?mode=ro&immutable=1")
	if err != nil {
		anchor.Close()
		return nil, nil, errors.New("open immutable database")
	}
	if err = db.Ping(); err != nil {
		db.Close()
		anchor.Close()
		return nil, nil, errors.New("open immutable database")
	}
	return db, anchor, nil
}
func runCLI(args []string) error {
	fs := flag.NewFlagSet("license-dbcheck", flag.ContinueOnError)
	path := fs.String("database", "", "stopped database")
	root := fs.String("root", "", "disposable root")
	marker := fs.String("stopped-marker", "", "stopped proof")
	expected := fs.String("expected-counts", "", "protected expected aggregates")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if fs.NArg() != 0 {
		return errors.New("positional arguments forbidden")
	}
	summary, err := CheckDatabase(*path, *root, *marker, *expected)
	if err != nil {
		return err
	}
	fmt.Println(summary.String())
	return nil
}
func main() {
	if err := runCLI(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "database check failed")
		os.Exit(1)
	}
}
