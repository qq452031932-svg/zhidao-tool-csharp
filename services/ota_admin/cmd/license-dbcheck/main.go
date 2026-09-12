package main

import (
	"context"
	"database/sql"
	"errors"
	"flag"
	"fmt"
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

func validateInputs(path, root, marker string) error {
	for _, value := range []string{path, root, marker} {
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
	if filepath.Clean(path) != filepath.Join(cleanRoot, "state.db") || filepath.Clean(marker) != filepath.Join(cleanRoot, stoppedMarkerName) {
		return errors.New("paths do not match disposable contract")
	}
	rootInfo, err := os.Lstat(cleanRoot)
	if err != nil || !rootInfo.IsDir() || rootInfo.Mode()&os.ModeSymlink != 0 || (runtime.GOOS != "windows" && rootInfo.Mode().Perm()&0077 != 0) {
		return errors.New("untrusted disposable root")
	}
	for _, value := range []string{path, marker} {
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

func CheckDatabase(path, root, marker string) (Summary, error) {
	if err := validateInputs(path, root, marker); err != nil {
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
	invariants := []string{"SELECT count(*) FROM license_cards WHERE (activated_at IS NULL)!=(expires_at IS NULL) OR (activated_at IS NULL)!=(machine_hash IS NULL) OR unactivated_total_days<0", "SELECT count(*) FROM license_activation_bindings WHERE expires_at<activated_at", "SELECT count(*) FROM license_activation_requests WHERE expires_at<activated_at OR lease_until<activated_at", "SELECT count(*) FROM license_package_members m LEFT JOIN license_packages p ON p.id=m.package_id LEFT JOIN license_software s ON s.id=m.software_id WHERE p.id IS NULL OR s.id IS NULL"}
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
	return Summary{"ok", version, counts}, nil
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
	if err := fs.Parse(args); err != nil {
		return err
	}
	if fs.NArg() != 0 {
		return errors.New("positional arguments forbidden")
	}
	summary, err := CheckDatabase(*path, *root, *marker)
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
