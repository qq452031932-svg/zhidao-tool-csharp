package store_test

import (
	"bytes"
	"context"
	"database/sql"
	"errors"
	"io"
	"path/filepath"
	"strings"
	"testing"
	"time"
	"unicode"

	"fanfh.cn/superpan/ota-admin/internal/audit"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func TestOpenMigratesRequiredTablesAndPragmas(t *testing.T) {
	t.Parallel()

	db := openStore(t)
	ctx := context.Background()
	for _, table := range []string{
		"administrators", "highest_committed_version", "releases", "release_assets",
		"candidates", "sessions", "operations", "audit_events",
	} {
		var count int
		if err := db.DB().QueryRowContext(ctx, `SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?`, table).Scan(&count); err != nil {
			t.Fatalf("query table %q: %v", table, err)
		}
		if count != 1 {
			t.Errorf("table %q count = %d, want 1", table, count)
		}
	}

	var journalMode string
	if err := db.DB().QueryRowContext(ctx, `PRAGMA journal_mode`).Scan(&journalMode); err != nil {
		t.Fatalf("query journal mode: %v", err)
	}
	if journalMode != "wal" {
		t.Fatalf("journal_mode = %q, want wal", journalMode)
	}
	var busyTimeout int
	if err := db.DB().QueryRowContext(ctx, `PRAGMA busy_timeout`).Scan(&busyTimeout); err != nil {
		t.Fatalf("query busy timeout: %v", err)
	}
	if busyTimeout < 5000 {
		t.Fatalf("busy_timeout = %d, want at least 5000", busyTimeout)
	}
}

func TestInitializeAdminCreatesExactlyOneFixedForcedChangeAdministrator(t *testing.T) {
	t.Parallel()

	db := openStore(t)
	ctx := context.Background()
	if err := db.InitializeAdmin(ctx, "$argon2id$first"); err != nil {
		t.Fatalf("InitializeAdmin() error = %v", err)
	}
	admin, err := db.Admin(ctx)
	if err != nil {
		t.Fatalf("Admin() error = %v", err)
	}
	if admin.Username != store.AdministratorUsername || admin.PasswordHash != "$argon2id$first" || !admin.MustChangePassword {
		t.Fatalf("Admin() = %+v", admin)
	}
	if err := db.InitializeAdmin(ctx, "$argon2id$second"); !errors.Is(err, store.ErrAdminAlreadyInitialized) {
		t.Fatalf("second InitializeAdmin() error = %v, want ErrAdminAlreadyInitialized", err)
	}
	if _, err := db.DB().ExecContext(ctx, `INSERT INTO administrators(singleton_id, username, password_hash, must_change_password) VALUES(2, 'other', 'hash', 1)`); err == nil {
		t.Fatal("database accepted a second administrator")
	}
}

func TestInitializeAdminWithGeneratedPasswordPrintsOneStrongPasswordOnce(t *testing.T) {
	t.Parallel()

	db := openStore(t)
	ctx := context.Background()
	var output bytes.Buffer
	if err := db.InitializeAdminWithGeneratedPassword(ctx, &output, zeroReader{}); err != nil {
		t.Fatalf("InitializeAdminWithGeneratedPassword() error = %v", err)
	}
	if strings.Count(output.String(), "\n") != 1 {
		t.Fatalf("initial password output = %q, want exactly one line", output.String())
	}
	password := strings.TrimSuffix(output.String(), "\n")
	if len(password) < 32 || !hasPasswordClasses(password) {
		t.Fatalf("generated password does not meet strength contract: %q", password)
	}
	admin, err := db.Admin(ctx)
	if err != nil {
		t.Fatalf("Admin() error = %v", err)
	}
	if admin.PasswordHash == password || !strings.HasPrefix(admin.PasswordHash, "$argon2id$v=19$") || !admin.MustChangePassword {
		t.Fatalf("stored administrator does not contain a forced-change Argon2id hash: %+v", admin)
	}

	var secondOutput bytes.Buffer
	if err := db.InitializeAdminWithGeneratedPassword(ctx, &secondOutput, zeroReader{}); !errors.Is(err, store.ErrAdminAlreadyInitialized) {
		t.Fatalf("second initialization error = %v, want ErrAdminAlreadyInitialized", err)
	}
	if secondOutput.Len() != 0 {
		t.Fatalf("failed initialization disclosed a password: %q", secondOutput.String())
	}
}

func TestHighestCommittedVersionIsPermanentAndStrictlyMonotonic(t *testing.T) {
	t.Parallel()

	db := openStore(t)
	ctx := context.Background()
	for _, version := range []string{"1.2.3", "1.10.0", "2.0.0"} {
		if err := db.AdvanceHighestCommittedVersion(ctx, version); err != nil {
			t.Fatalf("AdvanceHighestCommittedVersion(%q) error = %v", version, err)
		}
	}
	for _, version := range []string{"2.0.0", "1.99.99", "invalid"} {
		if err := db.AdvanceHighestCommittedVersion(ctx, version); err == nil {
			t.Errorf("AdvanceHighestCommittedVersion(%q) succeeded", version)
		}
	}
	got, ok, err := db.HighestCommittedVersion(ctx)
	if err != nil || !ok || got != "2.0.0" {
		t.Fatalf("HighestCommittedVersion() = (%q, %v, %v), want (2.0.0, true, nil)", got, ok, err)
	}

	if _, err := db.DB().ExecContext(ctx, `UPDATE highest_committed_version SET sequence = sequence - 1`); err == nil {
		t.Fatal("database accepted a decreasing permanent version sequence")
	}
	if _, err := db.DB().ExecContext(ctx, `UPDATE highest_committed_version SET version='1.0.0', major=1, minor=0, patch=0, sequence=sequence+1`); err == nil {
		t.Fatal("database accepted a lower permanent semantic version with a larger sequence")
	}
}

func TestHighestCommittedVersionUsesSemanticPrereleaseOrdering(t *testing.T) {
	t.Parallel()

	db := openStore(t)
	ctx := context.Background()
	for _, version := range []string{"1.0.0-beta.2", "1.0.0-beta.10", "1.0.0"} {
		if err := db.AdvanceHighestCommittedVersion(ctx, version); err != nil {
			t.Fatalf("AdvanceHighestCommittedVersion(%q) error = %v", version, err)
		}
	}
}

func TestCandidateSessionAndOperationStateMachinesRejectBackwardTransitions(t *testing.T) {
	t.Parallel()

	db := openStore(t)
	ctx := context.Background()
	now := time.Unix(1_800_000_000, 0).UTC()
	if err := db.CreateCandidate(ctx, store.Candidate{ID: "cand_1", SessionID: "sess_1", Fingerprint: "abc123", State: store.CandidateUploading, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}); err != nil {
		t.Fatalf("CreateCandidate() error = %v", err)
	}
	if err := db.TransitionCandidate(ctx, "cand_1", store.CandidateValidated); err != nil {
		t.Fatalf("TransitionCandidate(validated) error = %v", err)
	}
	if err := db.TransitionCandidate(ctx, "cand_1", store.CandidateUploading); !errors.Is(err, store.ErrInvalidTransition) {
		t.Fatalf("backward candidate transition error = %v, want ErrInvalidTransition", err)
	}

	if err := db.CreateSession(ctx, store.Session{IDHash: "session_hash", State: store.SessionActive, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(30 * time.Minute), AbsoluteExpiresAt: now.Add(8 * time.Hour)}); err != nil {
		t.Fatalf("CreateSession() error = %v", err)
	}
	if err := db.TransitionSession(ctx, "session_hash", store.SessionRevoked); err != nil {
		t.Fatalf("TransitionSession(revoked) error = %v", err)
	}
	if err := db.TransitionSession(ctx, "session_hash", store.SessionActive); !errors.Is(err, store.ErrInvalidTransition) {
		t.Fatalf("backward session transition error = %v, want ErrInvalidTransition", err)
	}

	if err := db.CreateOperation(ctx, store.Operation{ID: "op_1", Kind: store.OperationPublish, State: store.OperationPending, CreatedAt: now}); err != nil {
		t.Fatalf("CreateOperation() error = %v", err)
	}
	if err := db.TransitionOperation(ctx, "op_1", store.OperationRunning, ""); err != nil {
		t.Fatalf("TransitionOperation(running) error = %v", err)
	}
	if err := db.TransitionOperation(ctx, "op_1", store.OperationSucceeded, ""); err != nil {
		t.Fatalf("TransitionOperation(succeeded) error = %v", err)
	}
	if err := db.TransitionOperation(ctx, "op_1", store.OperationRunning, ""); !errors.Is(err, store.ErrInvalidTransition) {
		t.Fatalf("backward operation transition error = %v, want ErrInvalidTransition", err)
	}
}

func TestWithTxRollsBackAllStateIncludingAudit(t *testing.T) {
	t.Parallel()

	db := openStore(t)
	ctx := context.Background()
	wantErr := errors.New("injected failure")
	err := db.WithTx(ctx, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, `INSERT INTO operations(id, kind, state, created_at) VALUES('op_rollback', 'publish', 'pending', ?)`, time.Now().UTC()); err != nil {
			return err
		}
		if err := store.InsertAudit(ctx, tx, audit.Event{Type: audit.UploadStarted, Outcome: audit.OutcomeSuccess, OperationID: "op_rollback", Code: "accepted", At: time.Now().UTC()}); err != nil {
			return err
		}
		return wantErr
	})
	if !errors.Is(err, wantErr) {
		t.Fatalf("WithTx() error = %v, want injected failure", err)
	}
	for _, table := range []string{"operations", "audit_events"} {
		var count int
		if err := db.DB().QueryRowContext(ctx, `SELECT count(*) FROM `+table).Scan(&count); err != nil {
			t.Fatalf("count %s: %v", table, err)
		}
		if count != 0 {
			t.Errorf("%s row count = %d after rollback, want 0", table, count)
		}
	}
}

func TestAuditAcceptsSafeMetadataAndRejectsSensitiveValues(t *testing.T) {
	t.Parallel()

	db := openStore(t)
	ctx := context.Background()
	safe := audit.Event{Type: audit.PublishFailed, Outcome: audit.OutcomeFailure, OperationID: "op_123", CandidateID: "cand_123", ReleaseID: "rel_123", Code: "hash_mismatch", SourceHash: "sha256:0123456789abcdef", At: time.Now().UTC()}
	if err := db.RecordAudit(ctx, safe); err != nil {
		t.Fatalf("RecordAudit(safe) error = %v", err)
	}
	for name, event := range map[string]audit.Event{
		"password":      {Type: audit.LoginResult, Outcome: audit.OutcomeFailure, Code: "password=hunter2"},
		"cookie":        {Type: audit.LoginResult, Outcome: audit.OutcomeFailure, Code: "session=secret"},
		"raw source IP": {Type: audit.LoginResult, Outcome: audit.OutcomeFailure, SourceHash: "203.0.113.9"},
		"absolute path": {Type: audit.UploadCompleted, Outcome: audit.OutcomeFailure, Code: "/var/lib/private/file.zip"},
		"package data":  {Type: audit.UploadCompleted, Outcome: audit.OutcomeFailure, Code: "package contents"},
	} {
		t.Run(name, func(t *testing.T) {
			if err := db.RecordAudit(ctx, event); err == nil {
				t.Fatal("RecordAudit() accepted sensitive metadata")
			}
		})
	}
}

func openStore(t *testing.T) *store.Store {
	t.Helper()
	path := filepath.Join(t.TempDir(), "state.db")
	db, err := store.Open(context.Background(), path)
	if err != nil {
		t.Fatalf("Open() error = %v", err)
	}
	t.Cleanup(func() {
		if err := db.Close(); err != nil {
			t.Errorf("Close() error = %v", err)
		}
	})
	return db
}

type zeroReader struct{}

func (zeroReader) Read(buffer []byte) (int, error) {
	for index := range buffer {
		buffer[index] = 0
	}
	return len(buffer), nil
}

var _ io.Reader = zeroReader{}

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
