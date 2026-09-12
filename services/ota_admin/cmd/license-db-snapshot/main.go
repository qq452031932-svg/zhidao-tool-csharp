package main

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"flag"
	"fmt"
	"net/url"
	"os"
	"os/exec"
	"os/signal"
	"path"
	"path/filepath"
	"sort"
	"strings"
	"syscall"

	_ "modernc.org/sqlite"
)

const (
	requiredService  = "superpan-license.service"
	liveDatabasePath = "/var/lib/superpan-license/state.db"
	backupRoot       = "/var/backups/superpan-license"
)

type ServiceState uint8

const (
	ServiceUnknown ServiceState = iota
	ServiceStopped
	ServiceActive
)

type SourceKind uint8

const (
	SourceLive SourceKind = iota
	SourceBackup
)

type FileIdentity struct{ Device, Inode uint64 }

type SourceBinding interface {
	Identity() FileIdentity
	Close() error
}
type SourceBinder interface {
	Bind(string, FileIdentity) (SourceBinding, error)
}
type DirectoryGuardLease interface {
	Validate() error
	Restore() error
}
type DirectoryGuard interface {
	Acquire(string, SourceKind) (DirectoryGuardLease, error)
}
type DatabaseOpener func(string) (*sql.DB, FileIdentity, error)

type ServiceStateProbe interface {
	State(context.Context, string) (ServiceState, error)
}
type TrustProbe interface {
	ValidateSource(string, SourceKind) (FileIdentity, error)
	ValidateBackupDirectory(string) (FileIdentity, error)
	ValidateSidecar(string, SourceKind) (FileIdentity, bool, error)
	SecureOutput(string) error
}

type SnapshotOptions struct {
	ServiceName            string
	Source                 string
	Destination            string
	ServiceProbe           ServiceStateProbe
	TrustProbe             TrustProbe
	PathClassifier         func(string, string) (SourceKind, error)
	Checkpoint             func(context.Context, *sql.Conn) (busy, logFrames, checkpointed int, err error)
	SourceBinder           SourceBinder
	DirectoryGuard         DirectoryGuard
	DatabaseOpener         DatabaseOpener
	RemovePublished        func(string) error
	SyncPublishedDirectory func(string) error
	AfterPublish           func()
}

type Summary struct {
	SchemaVersion int
	QuickCheck    string
	TableCounts   map[string]int64
	SHA256        string
}

func (s Summary) String() string {
	keys := make([]string, 0, len(s.TableCounts))
	for k := range s.TableCounts {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	parts := []string{fmt.Sprintf("schema_version=%d", s.SchemaVersion), "quick_check=" + s.QuickCheck}
	for _, k := range keys {
		parts = append(parts, fmt.Sprintf("count_%s=%d", k, s.TableCounts[k]))
	}
	parts = append(parts, "sha256="+s.SHA256)
	return strings.Join(parts, " ")
}

func classifyPaths(source, destination string) (SourceKind, error) {
	if source == "" || destination == "" || !path.IsAbs(source) || !path.IsAbs(destination) || path.Clean(source) != source || path.Clean(destination) != destination {
		return 0, errors.New("source and destination must be canonical absolute paths")
	}
	insideBackup := func(p string) bool { return strings.HasPrefix(p, backupRoot+"/") }
	if !insideBackup(destination) {
		return 0, errors.New("destination must be below the fixed backup root")
	}
	if source == liveDatabasePath {
		return SourceLive, nil
	}
	if insideBackup(source) {
		return SourceBackup, nil
	}
	return 0, errors.New("source is not the live database or a trusted backup path")
}

func Snapshot(ctx context.Context, options SnapshotOptions) (summary Summary, err error) {
	if options.ServiceName != requiredService {
		return summary, errors.New("invalid service name")
	}
	if options.ServiceProbe == nil || options.TrustProbe == nil {
		return summary, errors.New("service and trust probes are required")
	}
	if err = validateSQLitePath(options.Source); err != nil {
		return summary, fmt.Errorf("source path: %w", err)
	}
	if err = validateSQLitePath(options.Destination); err != nil {
		return summary, fmt.Errorf("destination path: %w", err)
	}
	state, probeErr := options.ServiceProbe.State(ctx, options.ServiceName)
	if probeErr != nil || state != ServiceStopped {
		return summary, errors.New("service stopped state was not proven")
	}
	binder := options.SourceBinder
	if binder == nil {
		binder = platformSourceBinder{}
	}
	directoryGuard := options.DirectoryGuard
	if directoryGuard == nil {
		directoryGuard = platformDirectoryGuard{}
	}
	databaseOpener := options.DatabaseOpener
	if databaseOpener == nil {
		databaseOpener = openVerifiedDatabase
	}
	classifier := options.PathClassifier
	if classifier == nil {
		classifier = classifyPaths
	}
	kind, err := classifier(options.Source, options.Destination)
	if err != nil {
		return summary, err
	}
	guardLease, err := directoryGuard.Acquire(options.Source, kind)
	if err != nil {
		return summary, fmt.Errorf("acquire source directory guard: %w", err)
	}
	removePublished := options.RemovePublished
	if removePublished == nil {
		removePublished = os.Remove
	}
	syncPublishedDirectory := options.SyncPublishedDirectory
	if syncPublishedDirectory == nil {
		syncPublishedDirectory = syncDirectory
	}
	published := false
	temporary := ""
	temporaryPresent := false
	defer func() {
		panicValue := recover()
		restoreErr := guardLease.Restore()
		var cleanupErr error
		if temporaryPresent {
			if removeErr := os.Remove(temporary); removeErr != nil && !errors.Is(removeErr, os.ErrNotExist) {
				cleanupErr = errors.Join(cleanupErr, fmt.Errorf("remove temporary: %w", removeErr))
			}
		}
		if (err != nil || restoreErr != nil || panicValue != nil) && published {
			if removeErr := removePublished(options.Destination); removeErr != nil {
				cleanupErr = errors.Join(cleanupErr, fmt.Errorf("remove published destination: %w", removeErr))
			} else if syncErr := syncPublishedDirectory(filepath.Dir(options.Destination)); syncErr != nil {
				cleanupErr = errors.Join(cleanupErr, fmt.Errorf("sync destination cleanup: %w", syncErr))
			}
		}
		if restoreErr != nil {
			summary = Summary{}
			err = errors.Join(err, fmt.Errorf("source directory restore failed; keep service stopped and repair ownership manually: %w", restoreErr), cleanupErr)
		} else if cleanupErr != nil {
			summary = Summary{}
			err = errors.Join(err, cleanupErr)
		}
		if panicValue != nil {
			if err != nil {
				panic(errors.Join(fmt.Errorf("snapshot panic: %v", panicValue), err))
			}
			panic(panicValue)
		}
	}()
	if err = guardLease.Validate(); err != nil {
		return summary, fmt.Errorf("source directory guard: %w", err)
	}
	sourceIdentity, err := options.TrustProbe.ValidateSource(options.Source, kind)
	if err != nil {
		return summary, fmt.Errorf("source trust: %w", err)
	}
	binding, err := binder.Bind(options.Source, sourceIdentity)
	if err != nil {
		return summary, fmt.Errorf("bind verified source: %w", err)
	}
	defer binding.Close()
	if binding.Identity() != sourceIdentity {
		return summary, errors.New("bound source identity mismatch")
	}
	if err = guardLease.Validate(); err != nil {
		return summary, fmt.Errorf("source directory guard: %w", err)
	}
	parent := filepath.Dir(options.Destination)
	parentIdentity, err := options.TrustProbe.ValidateBackupDirectory(parent)
	if err != nil {
		return summary, fmt.Errorf("destination parent trust: %w", err)
	}
	if _, statErr := os.Lstat(options.Destination); statErr == nil {
		return summary, errors.New("destination already exists")
	} else if !errors.Is(statErr, os.ErrNotExist) {
		return summary, statErr
	}

	initialSidecars, err := captureSidecars(options.TrustProbe, options.Source, kind)
	if err != nil {
		return summary, err
	}
	db, sqliteIdentity, err := databaseOpener(options.Source)
	if err != nil {
		return summary, err
	}
	defer db.Close()
	if sqliteIdentity != binding.Identity() {
		return summary, errors.New("SQLite target identity does not match verified source")
	}
	if err = guardLease.Validate(); err != nil {
		return summary, fmt.Errorf("source directory guard: %w", err)
	}
	db.SetMaxOpenConns(1)
	conn, err := db.Conn(ctx)
	if err != nil {
		return summary, err
	}
	defer conn.Close()
	if _, err = conn.ExecContext(ctx, "PRAGMA locking_mode=EXCLUSIVE"); err != nil {
		return summary, fmt.Errorf("exclusive locking mode: %w", err)
	}
	if _, err = conn.ExecContext(ctx, "BEGIN EXCLUSIVE"); err != nil {
		return summary, fmt.Errorf("exclusive lock: %w", err)
	}
	if _, err = conn.ExecContext(ctx, "COMMIT"); err != nil {
		return summary, fmt.Errorf("exclusive lock commit: %w", err)
	}
	if err = compareSource(options.TrustProbe, options.Source, kind, sourceIdentity); err != nil {
		return summary, err
	}
	if err = compareBackupDirectory(options.TrustProbe, parent, parentIdentity); err != nil {
		return summary, err
	}
	if err = guardLease.Validate(); err != nil {
		return summary, fmt.Errorf("source directory guard: %w", err)
	}
	var busy, logFrames, checkpointed int
	checkpoint := options.Checkpoint
	if checkpoint == nil {
		checkpoint = sqliteCheckpoint
	}
	if busy, logFrames, checkpointed, err = checkpoint(ctx, conn); err != nil {
		return summary, fmt.Errorf("checkpoint: %w", err)
	}
	if busy != 0 || logFrames != checkpointed {
		return summary, fmt.Errorf("checkpoint incomplete: busy=%d log=%d checkpointed=%d", busy, logFrames, checkpointed)
	}
	if err = compareExistingSidecars(options.TrustProbe, options.Source, kind, initialSidecars); err != nil {
		return summary, err
	}
	lockedSidecars, err := captureSidecars(options.TrustProbe, options.Source, kind)
	if err != nil {
		return summary, err
	}

	temporary, err = unusedTemporaryName(parent)
	if err != nil {
		return summary, err
	}
	temporaryPresent = true
	if _, err = conn.ExecContext(ctx, "VACUUM INTO ?", temporary); err != nil {
		return summary, fmt.Errorf("vacuum into temporary: %w", err)
	}
	if err = options.TrustProbe.SecureOutput(temporary); err != nil {
		return summary, fmt.Errorf("secure temporary: %w", err)
	}
	if err = compareSidecars(options.TrustProbe, options.Source, kind, lockedSidecars); err != nil {
		return summary, err
	}
	if err = compareSource(options.TrustProbe, options.Source, kind, sourceIdentity); err != nil {
		return summary, err
	}
	if err = guardLease.Validate(); err != nil {
		return summary, fmt.Errorf("source directory guard: %w", err)
	}
	if summary, err = verify(ctx, temporary); err != nil {
		return Summary{}, err
	}
	if err = syncFile(temporary); err != nil {
		return Summary{}, err
	}
	if err = compareBackupDirectory(options.TrustProbe, parent, parentIdentity); err != nil {
		return Summary{}, err
	}
	if err = guardLease.Validate(); err != nil {
		return Summary{}, fmt.Errorf("source directory guard: %w", err)
	}
	if err = os.Link(temporary, options.Destination); err != nil {
		return Summary{}, fmt.Errorf("publish without replacement: %w", err)
	}
	published = true
	if options.AfterPublish != nil {
		options.AfterPublish()
	}
	if err = os.Remove(temporary); err != nil {
		return Summary{}, fmt.Errorf("remove temporary link: %w", err)
	}
	temporaryPresent = false
	if err = options.TrustProbe.SecureOutput(options.Destination); err != nil {
		return Summary{}, fmt.Errorf("secure published output: %w", err)
	}
	if err = syncFile(options.Destination); err != nil {
		return Summary{}, err
	}
	if err = syncDirectory(parent); err != nil {
		return Summary{}, err
	}
	return summary, nil
}

func buildSQLiteDSN(filePath string, readOnly bool) string {
	slashed := filepath.ToSlash(filePath)
	query := url.Values{}
	if readOnly {
		query.Set("mode", "ro")
	} else {
		query.Set("mode", "rw")
		query.Add("_pragma", "busy_timeout(0)")
	}
	escapedPath := (&url.URL{Path: slashed}).EscapedPath()
	return "file:" + escapedPath + "?" + query.Encode()
}

func validateSQLitePath(filePath string) error {
	if strings.ContainsAny(filepath.Base(filePath), "?#%") {
		return errors.New("SQLite basename contains forbidden URI control characters")
	}
	return nil
}

func openVerifiedDatabase(filePath string) (*sql.DB, FileIdentity, error) {
	db, err := sql.Open("sqlite", buildSQLiteDSN(filePath, false))
	if err != nil {
		return nil, FileIdentity{}, err
	}
	if err = db.Ping(); err != nil {
		db.Close()
		return nil, FileIdentity{}, err
	}
	identity, err := platformPathIdentity(filePath)
	if err != nil {
		db.Close()
		return nil, FileIdentity{}, err
	}
	return db, identity, nil
}

func sqliteCheckpoint(ctx context.Context, conn *sql.Conn) (busy, logFrames, checkpointed int, err error) {
	err = conn.QueryRowContext(ctx, "PRAGMA wal_checkpoint(TRUNCATE)").Scan(&busy, &logFrames, &checkpointed)
	return
}

func compareSource(probe TrustProbe, source string, kind SourceKind, expected FileIdentity) error {
	actual, err := probe.ValidateSource(source, kind)
	if err != nil {
		return fmt.Errorf("source trust: %w", err)
	}
	if actual != expected {
		return errors.New("source identity changed")
	}
	return nil
}

func compareBackupDirectory(probe TrustProbe, directory string, expected FileIdentity) error {
	actual, err := probe.ValidateBackupDirectory(directory)
	if err != nil {
		return fmt.Errorf("destination parent trust: %w", err)
	}
	if actual != expected {
		return errors.New("destination parent identity changed")
	}
	return nil
}

func unusedTemporaryName(parent string) (string, error) {
	f, err := os.CreateTemp(parent, ".license-db-snapshot-*.db")
	if err != nil {
		return "", err
	}
	name := f.Name()
	closeErr := f.Close()
	removeErr := os.Remove(name)
	if closeErr != nil {
		return "", closeErr
	}
	if removeErr != nil {
		return "", removeErr
	}
	return name, nil
}

func captureSidecars(probe TrustProbe, source string, kind SourceKind) (map[string]FileIdentity, error) {
	result := make(map[string]FileIdentity)
	for _, suffix := range []string{"-wal", "-shm"} {
		identity, exists, err := probe.ValidateSidecar(source+suffix, kind)
		if err != nil {
			return nil, fmt.Errorf("sidecar trust: %w", err)
		}
		if exists {
			result[suffix] = identity
		}
	}
	return result, nil
}

func compareSidecars(probe TrustProbe, source string, kind SourceKind, expected map[string]FileIdentity) error {
	for _, suffix := range []string{"-wal", "-shm"} {
		identity, exists, err := probe.ValidateSidecar(source+suffix, kind)
		if err != nil {
			return fmt.Errorf("sidecar trust: %w", err)
		}
		previous, wasPresent := expected[suffix]
		if exists != wasPresent || (exists && identity != previous) {
			return fmt.Errorf("sidecar identity changed: %s", suffix)
		}
	}
	return nil
}

func compareExistingSidecars(probe TrustProbe, source string, kind SourceKind, expected map[string]FileIdentity) error {
	for suffix, previous := range expected {
		identity, exists, err := probe.ValidateSidecar(source+suffix, kind)
		if err != nil {
			return fmt.Errorf("sidecar trust: %w", err)
		}
		if !exists || identity != previous {
			return fmt.Errorf("sidecar identity changed: %s", suffix)
		}
	}
	return nil
}

func syncFile(path string) error {
	f, err := os.OpenFile(path, os.O_RDWR, 0)
	if err != nil {
		return err
	}
	defer f.Close()
	return f.Sync()
}

func verify(ctx context.Context, path string) (Summary, error) {
	db, err := sql.Open("sqlite", buildSQLiteDSN(path, true))
	if err != nil {
		return Summary{}, err
	}
	defer db.Close()
	var qc string
	if err = db.QueryRowContext(ctx, "PRAGMA quick_check").Scan(&qc); err != nil {
		return Summary{}, err
	}
	if qc != "ok" {
		return Summary{}, fmt.Errorf("quick_check=%s", qc)
	}
	var version int
	if err = db.QueryRowContext(ctx, "SELECT version FROM license_schema_version WHERE singleton_id=1").Scan(&version); err != nil {
		return Summary{}, err
	}
	counts := map[string]int64{}
	rows, err := db.QueryContext(ctx, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
	if err != nil {
		return Summary{}, err
	}
	names, err := collectTableNames(rows)
	if err != nil {
		return Summary{}, err
	}
	for _, name := range names {
		var n int64
		if err := db.QueryRowContext(ctx, "SELECT count(*) FROM \""+strings.ReplaceAll(name, "\"", "\"\"")+"\"").Scan(&n); err != nil {
			return Summary{}, err
		}
		counts[name] = n
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return Summary{}, err
	}
	h := sha256.Sum256(b)
	return Summary{SchemaVersion: version, QuickCheck: "ok", TableCounts: counts, SHA256: hex.EncodeToString(h[:])}, nil
}

type tableRows interface {
	Next() bool
	Scan(...any) error
	Err() error
	Close() error
}

func collectTableNames(rows tableRows) ([]string, error) {
	var names []string
	for rows.Next() {
		var name string
		if err := rows.Scan(&name); err != nil {
			_ = rows.Close()
			return nil, err
		}
		names = append(names, name)
	}
	if err := rows.Err(); err != nil {
		_ = rows.Close()
		return nil, err
	}
	if err := rows.Close(); err != nil {
		return nil, err
	}
	return names, nil
}

type systemdProbe struct{}

func (systemdProbe) State(ctx context.Context, service string) (ServiceState, error) {
	if service != requiredService {
		return ServiceUnknown, errors.New("invalid service")
	}
	out, err := exec.CommandContext(ctx, "systemctl", "show", "--property=ActiveState", "--value", service).Output()
	if err != nil {
		return ServiceUnknown, err
	}
	switch strings.TrimSpace(string(out)) {
	case "inactive":
		return ServiceStopped, nil
	case "active":
		return ServiceActive, nil
	default:
		return ServiceUnknown, nil
	}
}

func main() {
	service := flag.String("service", "", "service that must be stopped")
	source := flag.String("source", "", "absolute source database")
	destination := flag.String("destination", "", "new snapshot path below the fixed backup root")
	flag.Parse()
	if *service == "" || *source == "" || *destination == "" {
		fmt.Fprintln(os.Stderr, "service, source and destination are required")
		os.Exit(2)
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	s, err := Snapshot(ctx, SnapshotOptions{ServiceName: *service, Source: *source, Destination: *destination, ServiceProbe: systemdProbe{}, TrustProbe: platformTrustProbe{}, SourceBinder: platformSourceBinder{}, DirectoryGuard: platformDirectoryGuard{}})
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println(s.String())
}
