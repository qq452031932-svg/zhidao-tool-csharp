package store

import (
	"bytes"
	"context"
	"database/sql"
	"errors"
	"fmt"
	"io"
	"path/filepath"
	"strings"
	"testing"
	"time"
	"unicode"

	"fanfh.cn/superpan/ota-admin/internal/audit"
)

const (
	sessionHash1 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
	sessionHash2 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
	candidateID1 = "11111111-1111-4111-8111-111111111111"
	operationID1 = "22222222-2222-4222-8222-222222222222"
	releaseID1   = "33333333-3333-4333-8333-333333333333"
	releaseID2   = "44444444-4444-4444-8444-444444444444"
	digest1      = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
)

func TestOpenMigratesRequiredTablesAndPragmas(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	for _, table := range []string{"administrators", "highest_committed_version", "releases", "release_assets", "candidates", "sessions", "operations", "audit_events"} {
		var count int
		if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?`, table).Scan(&count); err != nil || count != 1 {
			t.Fatalf("table %q count/error = %d/%v", table, count, err)
		}
	}
	assertConnectionPragmas(t, s.db)
}

func TestPragmasApplyAfterPhysicalConnectionReplacement(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	s.db.SetMaxIdleConns(0)
	for range 3 {
		assertConnectionPragmas(t, s.db)
	}
}

func TestOpenRejectsDSNMetacharactersBeforeCreatingDatabase(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "state.db?_pragma=foreign_keys(0)")
	if _, err := Open(context.Background(), path); err == nil {
		t.Fatal("Open() accepted a path containing injected DSN parameters")
	}
}

func TestStateMetadataRequiresPrivateServiceOwnedNonLinkedObjects(t *testing.T) {
	t.Parallel()
	if err := validateStateDirectoryMetadata(1000, 1000, 0o700, true, false); err != nil {
		t.Fatal(err)
	}
	if err := validateStateFileMetadata(1000, 1000, 0o600, true, false, 1); err != nil {
		t.Fatal(err)
	}
	for _, test := range []struct {
		name string
		err  error
	}{
		{"writable state directory", validateStateDirectoryMetadata(1000, 1000, 0o770, true, false)},
		{"foreign owner", validateStateDirectoryMetadata(0, 1000, 0o700, true, false)},
		{"symlink directory", validateStateDirectoryMetadata(1000, 1000, 0o700, true, true)},
		{"readable database", validateStateFileMetadata(1000, 1000, 0o640, true, false, 1)},
		{"hardlinked database", validateStateFileMetadata(1000, 1000, 0o600, true, false, 2)},
		{"symlink database", validateStateFileMetadata(1000, 1000, 0o600, true, true, 1)},
	} {
		if test.err == nil {
			t.Errorf("%s accepted", test.name)
		}
	}
}

func TestMigrationChainIsOrderedAndComplete(t *testing.T) {
	t.Parallel()
	if len(migrations) == 0 || schemaVersion != migrations[len(migrations)-1].version {
		t.Fatalf("schemaVersion/migrations = %d/%+v", schemaVersion, migrations)
	}
	for index, migration := range migrations {
		if migration.version != index+1 || strings.TrimSpace(migration.sql) == "" {
			t.Fatalf("migration %d = %+v, want contiguous nonempty migration", index, migration)
		}
	}
}

func TestInitializeAdminRequiresExactArgon2idPHC(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	for _, invalid := range []string{"hash", "$argon2id$v=19$m=8,t=1,p=1$AA$AA", "$argon2i$v=19$m=65536,t=3,p=2$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"} {
		if err := s.InitializeAdmin(context.Background(), invalid); err == nil {
			t.Fatalf("InitializeAdmin() accepted %q", invalid)
		}
	}
}

func TestGeneratedAdminPasswordIsHashedUniqueStrongAndForcedChange(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	var hashes []string
	for range 2 {
		s := openStore(t)
		var output bytes.Buffer
		if err := s.InitializeAdminWithGeneratedPassword(ctx, &output, nil); err != nil {
			t.Fatalf("InitializeAdminWithGeneratedPassword() error = %v", err)
		}
		if strings.Count(output.String(), "\n") != 1 {
			t.Fatalf("password output = %q, want one line", output.String())
		}
		password := strings.TrimSuffix(output.String(), "\n")
		if len(password) < 32 || !hasPasswordClasses(password) {
			t.Fatalf("weak generated password %q", password)
		}
		admin, err := s.Admin(ctx)
		if err != nil || admin.Username != AdministratorUsername || !admin.MustChangePassword || admin.PasswordHash == password {
			t.Fatalf("Admin() = %+v, %v", admin, err)
		}
		if !VerifyPasswordHash(password, admin.PasswordHash) {
			t.Fatal("stored Argon2id hash does not verify generated password")
		}
		hashes = append(hashes, admin.PasswordHash)
	}
	if hashes[0] == hashes[1] {
		t.Fatal("two initializations reused an Argon2id salt")
	}
}

func TestGeneratedAdminRollsBackWhenPasswordCannotBeWritten(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	err := s.InitializeAdminWithGeneratedPassword(context.Background(), failingWriter{}, nil)
	if err == nil {
		t.Fatal("InitializeAdminWithGeneratedPassword() succeeded with failing writer")
	}
	if _, err := s.Admin(context.Background()); !errors.Is(err, ErrNotFound) {
		t.Fatalf("Admin() after writer failure error = %v, want ErrNotFound", err)
	}
}

func TestGeneratedAdminDoesNotPrintOnAlreadyInitialized(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	var first bytes.Buffer
	if err := s.InitializeAdminWithGeneratedPassword(ctx, &first, nil); err != nil {
		t.Fatal(err)
	}
	var second bytes.Buffer
	if err := s.InitializeAdminWithGeneratedPassword(ctx, &second, nil); !errors.Is(err, ErrAdminAlreadyInitialized) {
		t.Fatalf("second initialization error = %v", err)
	}
	if second.Len() != 0 {
		t.Fatalf("failed initialization disclosed %q", second.String())
	}
}

func TestHighestCommittedVersionIsCanonicalNumericAndMonotonicAtDBBoundary(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	for _, version := range []string{"1.2.3", "1.10.0", "2.0.0"} {
		if err := s.AdvanceHighestCommittedVersion(ctx, version); err != nil {
			t.Fatalf("advance %q: %v", version, err)
		}
	}
	for _, invalid := range []string{"2.0.0", "1.99.99", "2.0.1-beta.1", "02.0.1", "invalid"} {
		if err := s.AdvanceHighestCommittedVersion(ctx, invalid); err == nil {
			t.Errorf("AdvanceHighestCommittedVersion(%q) succeeded", invalid)
		}
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE highest_committed_version SET version='1.0.0', major=1, minor=0, patch=0`); err == nil {
		t.Fatal("raw SQL lowered the permanent version")
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE highest_committed_version SET version='9.9.9', major=1, minor=0, patch=0`); err == nil {
		t.Fatal("raw SQL stored an inconsistent version tuple")
	}
	got, ok, err := s.HighestCommittedVersion(ctx)
	if err != nil || !ok || got != "2.0.0" {
		t.Fatalf("HighestCommittedVersion() = %q, %v, %v", got, ok, err)
	}
}

func TestHighestCommittedVersionReadRejectsPersistedTupleMismatch(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	if err := s.AdvanceHighestCommittedVersion(ctx, "1.2.3"); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `PRAGMA ignore_check_constraints=ON`); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `DROP TRIGGER highest_committed_version_monotonic`); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE highest_committed_version SET version='9.9.9'`); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.HighestCommittedVersion(ctx); err == nil {
		t.Fatal("HighestCommittedVersion() accepted corrupt persisted tuple")
	}
}

func TestInitialStatesAndForeignKeysAreEnforcedOnAPIsAndRawInserts(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	now := time.Now().UTC()
	if err := s.CreateSession(ctx, Session{IDHash: sessionHash1, State: SessionRevoked, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(time.Minute), AbsoluteExpiresAt: now.Add(time.Hour)}); err == nil {
		t.Fatal("CreateSession() accepted non-active initial state")
	}
	createActiveSession(t, s, sessionHash1)
	if _, err := s.db.ExecContext(ctx, `INSERT INTO sessions(id_hash,state,created_at,last_seen_at,idle_expires_at,absolute_expires_at) VALUES(?, 'expired', ?, ?, ?, ?)`, sessionHash2, stamp(now), stamp(now), stamp(now.Add(time.Minute)), stamp(now.Add(time.Hour))); err == nil {
		t.Fatal("raw INSERT accepted non-active session")
	}
	badCandidate := Candidate{ID: candidateID1, SessionID: sessionHash2, State: CandidateUploading, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}
	if err := s.CreateCandidate(ctx, badCandidate); err == nil {
		t.Fatal("candidate accepted a nonexistent session foreign key")
	}
	if _, err := s.db.ExecContext(ctx, `INSERT INTO operations(id,kind,state,created_at,finished_at) VALUES(?, 'publish', 'succeeded', ?, ?)`, operationID1, stamp(now), stamp(now)); err == nil {
		t.Fatal("raw INSERT accepted a terminal initial operation")
	}
	if _, err := s.db.ExecContext(ctx, `INSERT INTO candidates(id,session_id,state,created_at,expires_at,upload_digest,validation_report) VALUES(?,?, 'validated', ?, ?, ?, '{"valid":true}')`, candidateID1, sessionHash1, stamp(now), stamp(now.Add(time.Hour)), digest1); err == nil {
		t.Fatal("raw INSERT accepted a validated initial candidate")
	}
	if _, err := s.db.ExecContext(ctx, `INSERT INTO releases(id,version,generation_name,state,committed_at) VALUES(?, '1.0.0', 'release-1', 'retained', ?)`, releaseID1, stamp(now)); err == nil {
		t.Fatal("raw INSERT accepted a retained initial release")
	}
}

func TestCandidateBindsSessionDigestAndValidationReport(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	createActiveSession(t, s, sessionHash1)
	now := time.Now().UTC()
	candidate := Candidate{ID: candidateID1, SessionID: sessionHash1, State: CandidateUploading, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}
	if err := s.CreateCandidate(ctx, candidate); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE candidates SET upload_digest=? WHERE id=?`, digest1, candidateID1); err == nil {
		t.Fatal("raw SQL populated candidate evidence outside the validating transition")
	}
	if err := s.ValidateCandidate(ctx, candidateID1, digest1, `{"valid":true}`); err != nil {
		t.Fatalf("ValidateCandidate() error = %v", err)
	}
	for _, statement := range []string{
		`UPDATE candidates SET session_id='` + sessionHash2 + `' WHERE id='` + candidateID1 + `'`,
		`UPDATE candidates SET upload_digest='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' WHERE id='` + candidateID1 + `'`,
		`UPDATE candidates SET validation_report='{}' WHERE id='` + candidateID1 + `'`,
	} {
		if _, err := s.db.ExecContext(ctx, statement); err == nil {
			t.Fatalf("raw SQL changed immutable candidate binding: %s", statement)
		}
	}
	if err := s.TransitionCandidate(ctx, candidateID1, CandidateUploading); !errors.Is(err, ErrInvalidTransition) {
		t.Fatalf("backward transition error = %v", err)
	}
}

func TestOperationTerminalStateRequiresFinishedAtAndCannotMoveBackward(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	now := time.Now().UTC()
	if err := s.CreateOperation(ctx, Operation{ID: operationID1, Kind: OperationPublish, State: OperationPending, CreatedAt: now}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE operations SET state='succeeded' WHERE id=?`, operationID1); err == nil {
		t.Fatal("terminal operation accepted NULL finished_at")
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationRunning, ""); err != nil {
		t.Fatal(err)
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationSucceeded, "completed"); err != nil {
		t.Fatal(err)
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationRunning, ""); !errors.Is(err, ErrInvalidTransition) {
		t.Fatalf("backward operation transition error = %v", err)
	}
}

func TestReleaseCurrentUniquenessTransitionsAndFlatGenerationNames(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	now := time.Now().UTC()
	if err := s.CommitRelease(ctx, Release{ID: releaseID1, Version: "1.0.0", GenerationName: "release-1", CommittedAt: now}); err != nil {
		t.Fatal(err)
	}
	if err := s.CommitRelease(ctx, Release{ID: releaseID2, Version: "1.0.1", GenerationName: "release-2", CommittedAt: now.Add(time.Minute)}); err != nil {
		t.Fatal(err)
	}
	var currentCount int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM releases WHERE state='current'`).Scan(&currentCount); err != nil || currentCount != 1 {
		t.Fatalf("current release count = %d, %v", currentCount, err)
	}
	if err := s.SetCurrentRelease(ctx, releaseID1); err != nil {
		t.Fatalf("SetCurrentRelease() error = %v", err)
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE releases SET state='removed' WHERE id=?`, releaseID1); err == nil {
		t.Fatal("raw SQL removed the current release")
	}
	for index, name := range []string{"../release", "nested/release", `C:\release`, ".", "bad name"} {
		id := fmt.Sprintf("55555555-5555-4555-8555-%012d", index)
		if err := s.CommitRelease(ctx, Release{ID: id, Version: fmt.Sprintf("2.0.%d", index), GenerationName: name, CommittedAt: now}); err == nil {
			t.Errorf("CommitRelease() accepted generation name %q", name)
		}
	}
}

func TestWithTransactionRollsBackStateAndAudit(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	want := errors.New("injected")
	event, err := audit.NewUploadStarted(operationID1, candidateID1, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	err = s.withTx(ctx, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, `INSERT INTO operations(id,kind,state,created_at) VALUES(?, 'publish', 'pending', ?)`, operationID1, stamp(time.Now())); err != nil {
			return err
		}
		if err := insertAudit(ctx, tx, event); err != nil {
			return err
		}
		return want
	})
	if !errors.Is(err, want) {
		t.Fatalf("withTx() error = %v", err)
	}
	for _, table := range []string{"operations", "audit_events"} {
		var count int
		if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM `+table).Scan(&count); err != nil || count != 0 {
			t.Fatalf("%s count = %d, %v", table, count, err)
		}
	}
}

func TestAuditIsAppendOnlyIncludingReplace(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	event, err := audit.NewPublishFailed(operationID1, candidateID1, audit.CodeHashMismatch, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if err := s.RecordAudit(context.Background(), event); err != nil {
		t.Fatal(err)
	}
	for _, statement := range []string{
		`UPDATE audit_events SET code='internal_failure' WHERE sequence=1`,
		`DELETE FROM audit_events WHERE sequence=1`,
		`INSERT OR REPLACE INTO audit_events(sequence,occurred_at,event_type,outcome,operation_id,candidate_id,release_id,code,source_pseudonym) SELECT sequence,occurred_at,event_type,outcome,operation_id,candidate_id,release_id,code,source_pseudonym FROM audit_events WHERE sequence=1`,
	} {
		if _, err := s.db.ExecContext(context.Background(), statement); err == nil {
			t.Fatalf("audit mutation succeeded: %s", statement)
		}
	}
}

func TestAuditDatabaseRejectsRawIPShortHashAndArbitraryCode(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	base := `INSERT INTO audit_events(occurred_at,event_type,outcome,operation_id,candidate_id,release_id,code,source_pseudonym) VALUES(?, 'login_result', 'failure', '', '', '', ?, ?)`
	for _, test := range []struct{ code, source string }{
		{"login_rejected", "203.0.113.9"},
		{"login_rejected", "hmac-sha256:0123456789abcdef"},
		{"password=hunter2", "hmac-sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"},
	} {
		if _, err := s.db.ExecContext(context.Background(), base, stamp(time.Now()), test.code, test.source); err == nil {
			t.Errorf("raw audit insert accepted code/source %q/%q", test.code, test.source)
		}
	}
}

func assertConnectionPragmas(t *testing.T, db *sql.DB) {
	t.Helper()
	expectations := map[string]string{"foreign_keys": "1", "busy_timeout": "5000", "synchronous": "2", "recursive_triggers": "1", "journal_mode": "wal"}
	for pragma, want := range expectations {
		var got string
		if err := db.QueryRowContext(context.Background(), `PRAGMA `+pragma).Scan(&got); err != nil || strings.ToLower(got) != want {
			t.Fatalf("PRAGMA %s = %q, %v; want %q", pragma, got, err, want)
		}
	}
}

func openStore(t *testing.T) *Store {
	t.Helper()
	s, err := Open(context.Background(), filepath.Join(t.TempDir(), "state.db"))
	if err != nil {
		t.Fatalf("Open() error = %v", err)
	}
	t.Cleanup(func() { _ = s.Close() })
	return s
}

func createActiveSession(t *testing.T, s *Store, hash string) {
	t.Helper()
	now := time.Now().UTC()
	if err := s.CreateSession(context.Background(), Session{IDHash: hash, State: SessionActive, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(time.Minute), AbsoluteExpiresAt: now.Add(time.Hour)}); err != nil {
		t.Fatalf("CreateSession() error = %v", err)
	}
}

func stamp(value time.Time) string { return value.UTC().Format(time.RFC3339Nano) }

type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) { return 0, io.ErrClosedPipe }

func hasPasswordClasses(password string) bool {
	var upper, lower, digit, symbol bool
	for _, character := range password {
		upper = upper || unicode.IsUpper(character)
		lower = lower || unicode.IsLower(character)
		digit = digit || unicode.IsDigit(character)
		symbol = symbol || strings.ContainsRune("!@#$%^&*-_=+", character)
	}
	return upper && lower && digit && symbol
}
