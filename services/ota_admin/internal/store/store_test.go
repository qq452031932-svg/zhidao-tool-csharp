package store

import (
	"bytes"
	"context"
	"database/sql"
	"errors"
	"fmt"
	"io"
	"os"
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
	for _, table := range []string{"administrators", "highest_committed_version", "releases", "release_assets", "candidates", "sessions", "operations", "audit_events", "candidate_cleanups", "candidate_upload_failures", "candidate_cleanup_attempts"} {
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
		hasSQL := strings.TrimSpace(migration.sql) != ""
		if migration.version != index+1 || hasSQL == (migration.apply != nil) {
			t.Fatalf("migration %d = %+v, want contiguous migration with exactly one implementation", index, migration)
		}
	}
}

func TestFreshAndLegacyDatabasesEndAtSchemaV3(t *testing.T) {
	t.Parallel()
	fresh := openStore(t)
	var version int
	if err := fresh.db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != schemaVersion {
		t.Fatalf("fresh schema=%d err=%v", version, err)
	}
	assertIntegerTimestampColumns(t, fresh.db)

	path := filepath.Join(t.TempDir(), "legacy.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	if _, err = db.Exec(originalV1FixtureDDL); err != nil {
		t.Fatal(err)
	}
	hash, err := hashPassword("Strong!Password123", nilReaderRand{})
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now().UTC().Format(time.RFC3339Nano)
	if _, err = db.Exec(`INSERT INTO administrators VALUES(1,'admin',?,1,?)`, hash, now); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO highest_committed_version VALUES(1,'1.2.3',1,2,3,'',x'01',1,?)`, now); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO releases VALUES(?, '1.2.3','release-1','committed',?,'admin')`, releaseID1, now); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO release_assets VALUES(?,?,12,?)`, releaseID1, "asset.nupkg", digest1); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	upgraded, err := Open(context.Background(), path)
	if err != nil {
		t.Fatalf("upgrade: %v", err)
	}
	defer upgraded.Close()
	if err = upgraded.db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != schemaVersion {
		t.Fatalf("upgraded schema=%d err=%v", version, err)
	}
	assertIntegerTimestampColumns(t, upgraded.db)
	var state string
	if err = upgraded.db.QueryRow(`SELECT state FROM releases WHERE id=?`, releaseID1).Scan(&state); err != nil || state != "current" {
		t.Fatalf("release state=%q err=%v", state, err)
	}
	if got, ok, err := upgraded.HighestCommittedVersion(context.Background()); err != nil || !ok || got != "1.2.3" {
		t.Fatalf("floor=%q/%v/%v", got, ok, err)
	}
	admin, err := upgraded.Admin(context.Background())
	if err != nil || admin.PasswordHash != hash || !admin.MustChangePassword {
		t.Fatalf("admin not preserved: %+v/%v", admin, err)
	}
	var assetCount int
	if err := upgraded.db.QueryRow(`SELECT count(*) FROM release_assets WHERE release_id=? AND file_name='asset.nupkg'`, releaseID1).Scan(&assetCount); err != nil || assetCount != 1 {
		t.Fatalf("asset not preserved: %d/%v", assetCount, err)
	}
}

func TestHardenedCommittedV1DatabaseMigratesToV2(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "hardened-v1.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	if _, err = db.Exec(hardenedV1FixtureDDL); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`PRAGMA user_version=1`); err != nil {
		t.Fatal(err)
	}
	hash, err := hashPassword("Strong!Password123", nilReaderRand{})
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now().UTC().Format(time.RFC3339Nano)
	if _, err = db.Exec(`INSERT INTO administrators VALUES(1,'admin',?,1,?)`, hash, now); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO highest_committed_version VALUES(1,'1.2.3',1,2,3,?)`, now); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO releases VALUES(?, '1.2.3','release-1','current',?,'admin')`, releaseID1, now); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO release_assets VALUES(?,?,12,?)`, releaseID1, "asset.nupkg", digest1); err != nil {
		t.Fatal(err)
	}
	expires := time.Now().UTC().Add(time.Hour).Format(time.RFC3339Nano)
	if _, err = db.Exec(`INSERT INTO sessions VALUES(?,'active',?,?,?,?)`, sessionHash1, now, now, expires, expires); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, candidateID1, sessionHash1, now, expires); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`UPDATE candidates SET upload_digest=?,validation_report='{"valid":true}',state='validated' WHERE id=?`, digest1, candidateID1); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO operations(id,kind,state,candidate_id,created_at) VALUES(?,'upload','pending',?,?)`, operationID1, candidateID1, now); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO audit_events(occurred_at,event_type,outcome,operation_id,candidate_id,code) VALUES(?,'upload_started','success',?,?,'accepted')`, now, operationID1, candidateID1); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	upgraded, err := Open(context.Background(), path)
	if err != nil {
		t.Fatalf("upgrade hardened V1: %v", err)
	}
	defer upgraded.Close()
	var version int
	if err := upgraded.db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != schemaVersion {
		t.Fatalf("upgraded schema=%d err=%v", version, err)
	}
	if got, ok, err := upgraded.HighestCommittedVersion(context.Background()); err != nil || !ok || got != "1.2.3" {
		t.Fatalf("floor=%q/%v/%v", got, ok, err)
	}
	for table, want := range map[string]int{"sessions": 1, "candidates": 1, "operations": 1, "audit_events": 1} {
		var count int
		if err := upgraded.db.QueryRow(`SELECT count(*) FROM ` + table).Scan(&count); err != nil || count != want {
			t.Fatalf("preserved %s count=%d err=%v", table, count, err)
		}
	}
}

func assertIntegerTimestampColumns(t *testing.T, db *sql.DB) {
	t.Helper()
	var declaredType string
	if err := db.QueryRow(`SELECT type FROM pragma_table_info('administrators') WHERE name='created_at'`).Scan(&declaredType); err != nil {
		t.Fatal(err)
	}
	if declaredType != "INTEGER" {
		t.Fatalf("administrators.created_at type=%q, want INTEGER", declaredType)
	}
}

func TestUnsafeLegacyRowsFailMigrationWithoutPartialChanges(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "unsafe-legacy.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(originalV1FixtureDDL); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO audit_events(occurred_at,event_type,outcome,code) VALUES('2026-01-01T00:00:00Z','unknown','success','password=secret')`); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	if s, err := Open(context.Background(), path); err == nil {
		_ = s.Close()
		t.Fatal("unsafe V1 migrated")
	} else if !strings.Contains(err.Error(), "legacy audit_events rows cannot be safely transformed") {
		t.Fatalf("migration failed for the wrong reason: %v", err)
	}
	db, err = sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	var version int
	if err = db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != 1 {
		t.Fatalf("schema=%d err=%v", version, err)
	}
	var sourceHashColumn int
	if err = db.QueryRow(`SELECT count(*) FROM pragma_table_info('audit_events') WHERE name='source_hash'`).Scan(&sourceHashColumn); err != nil || sourceHashColumn != 1 {
		t.Fatalf("legacy schema partially changed")
	}
}

func TestUnsafeHardenedV1FailsMigrationWithoutPartialChanges(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "unsafe-hardened-v1.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	if _, err = db.Exec(hardenedV1FixtureDDL); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`PRAGMA user_version=1`); err != nil {
		t.Fatal(err)
	}
	now := time.Now().UTC().Format(time.RFC3339Nano)
	if _, err = db.Exec(`INSERT INTO operations(id,kind,state,created_at) VALUES(?,'publish','pending',?)`, operationID1, now); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	if s, err := Open(context.Background(), path); err == nil {
		_ = s.Close()
		t.Fatal("unsafe hardened V1 migrated")
	} else if !strings.Contains(err.Error(), "migrate hardened V1 data") {
		t.Fatalf("migration failed for the wrong reason: %v", err)
	}
	db, err = sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	var version int
	if err = db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != 1 {
		t.Fatalf("schema=%d err=%v", version, err)
	}
	var sourcePseudonymColumn int
	if err = db.QueryRow(`SELECT count(*) FROM pragma_table_info('audit_events') WHERE name='source_pseudonym'`).Scan(&sourcePseudonymColumn); err != nil || sourcePseudonymColumn != 1 {
		t.Fatal("hardened V1 schema partially changed")
	}
}

func secureMigrationFixture(t *testing.T, path string) {
	t.Helper()
	if err := os.Chmod(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(path, 0o600); err != nil {
		t.Fatal(err)
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

func TestPasswordRotationRequiresTheCurrentSessionToRemainActive(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	oldHash, err := hashPassword("Strong!Password123", nilReaderRand{})
	if err != nil {
		t.Fatal(err)
	}
	if err := s.InitializeAdmin(ctx, oldHash); err != nil {
		t.Fatal(err)
	}
	createActiveSession(t, s, sessionHash1)
	if err := s.TransitionSession(ctx, sessionHash1, SessionRevoked); err != nil {
		t.Fatal(err)
	}
	newHash, err := hashPassword("Different!Password456", nilReaderRand{n: 3})
	if err != nil {
		t.Fatal(err)
	}
	now := time.Now().UTC()
	next := Session{IDHash: sessionHash2, State: SessionActive, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(time.Minute), AbsoluteExpiresAt: now.Add(time.Hour)}
	if err := s.ChangePasswordAndRotateSession(ctx, newHash, sessionHash1, next); !errors.Is(err, ErrNotFound) {
		t.Fatalf("ChangePasswordAndRotateSession() error=%v, want ErrNotFound", err)
	}
	admin, err := s.Admin(ctx)
	if err != nil || admin.PasswordHash != oldHash || !admin.MustChangePassword {
		t.Fatalf("administrator changed after revoked-session rotation: %+v, %v", admin, err)
	}
	if _, err := s.Session(ctx, sessionHash2); !errors.Is(err, ErrNotFound) {
		t.Fatalf("replacement session created after rejected rotation: %v", err)
	}
}

func TestTouchSessionNeverMovesActivityOrIdleExpiryBackward(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	base := time.Now().UTC().Truncate(time.Second)
	if err := s.CreateSession(ctx, Session{IDHash: sessionHash1, State: SessionActive, CreatedAt: base, LastSeenAt: base, IdleExpiresAt: base.Add(time.Minute), AbsoluteExpiresAt: base.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := s.TouchSession(ctx, sessionHash1, base.Add(20*time.Minute), base.Add(50*time.Minute)); err != nil {
		t.Fatal(err)
	}
	if err := s.TouchSession(ctx, sessionHash1, base.Add(10*time.Minute), base.Add(40*time.Minute)); err != nil {
		t.Fatal(err)
	}
	got, err := s.Session(ctx, sessionHash1)
	if err != nil {
		t.Fatal(err)
	}
	if !got.LastSeenAt.Equal(base.Add(20*time.Minute)) || !got.IdleExpiresAt.Equal(base.Add(50*time.Minute)) {
		t.Fatalf("session touch regressed: %+v", got)
	}
}

func TestHighestCommittedVersionIsCanonicalNumericAndMonotonicAtDBBoundary(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, request := preparedPublish(t)
	if err := s.CommitPublishedRelease(ctx, request); err != nil {
		t.Fatal(err)
	}
	for _, invalid := range []string{"2.0.0", "1.99.99", "2.0.1-beta.1", "02.0.1", "invalid"} {
		if _, err := parseVersion(invalid); err == nil && invalid != "2.0.0" && invalid != "1.99.99" {
			t.Errorf("parseVersion(%q) succeeded", invalid)
		}
	}
	if _, err := parseVersion("65535.0.0"); err == nil {
		t.Fatal("parseVersion accepted a component outside the client range")
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE highest_committed_version SET version='1.0.0', major=1, minor=0, patch=0`); err == nil {
		t.Fatal("raw SQL lowered the permanent version")
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE highest_committed_version SET version='9.9.9', major=1, minor=0, patch=0`); err == nil {
		t.Fatal("raw SQL stored an inconsistent version tuple")
	}
	got, ok, err := s.HighestCommittedVersion(ctx)
	if err != nil || !ok || got != "1.2.3" {
		t.Fatalf("HighestCommittedVersion() = %q, %v, %v", got, ok, err)
	}
}

func TestHighestCommittedVersionReadRejectsPersistedTupleMismatch(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	_, err := s.db.ExecContext(ctx, `INSERT INTO highest_committed_version VALUES(1,'1.2.3',1,2,3,?)`, stamp(time.Now()))
	if err != nil {
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
	if _, err := s.db.ExecContext(ctx, `INSERT INTO candidates(id,session_id,state,created_at,expires_at,upload_digest,validation_report) VALUES(?,?, 'uploading', ?, ?, ?, '')`, candidateID1, sessionHash1, stamp(now), stamp(now.Add(time.Hour)), digest1); err == nil {
		t.Fatal("raw INSERT accepted uploading candidate evidence")
	}
	if _, err := s.db.ExecContext(ctx, `INSERT INTO releases(id,version,generation_name,state,committed_at) VALUES(?, '1.0.0', 'release-1', 'retained', ?)`, releaseID1, stamp(now)); err == nil {
		t.Fatal("raw INSERT accepted a retained initial release")
	}
	if _, err := s.db.ExecContext(ctx, `INSERT INTO releases(id,version,generation_name,state,committed_at) VALUES(?, '65535.0.0', 'release-1', 'current', ?)`, releaseID1, stamp(now)); err == nil {
		t.Fatal("raw INSERT accepted a release version outside the client range")
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
	if _, err := s.db.ExecContext(ctx, `UPDATE candidates SET release_version='1.2.3' WHERE id=?`, candidateID1); err == nil {
		t.Fatal("raw SQL bound a release version before publication")
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
	if err := s.CreateOperation(ctx, Operation{ID: operationID1, Kind: OperationCleanup, State: OperationPending, CreatedAt: now}); err != nil {
		t.Fatal(err)
	}
	for _, statement := range []string{
		`UPDATE operations SET state='running',result_code='completed' WHERE id='` + operationID1 + `'`,
		`UPDATE operations SET state='succeeded',result_code='hash_mismatch',finished_at=1 WHERE id='` + operationID1 + `'`,
		`UPDATE operations SET state='failed',result_code='',finished_at=1 WHERE id='` + operationID1 + `'`,
	} {
		if _, err := s.db.ExecContext(ctx, statement); err == nil {
			t.Fatalf("raw SQL bypassed operation result invariant: %s", statement)
		}
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE operations SET state='succeeded' WHERE id=?`, operationID1); err == nil {
		t.Fatal("terminal operation accepted NULL finished_at")
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationRunning, ""); err != nil {
		t.Fatal(err)
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationRunning, "completed"); err == nil {
		t.Fatal("running operation accepted completed result")
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationSucceeded, "hash_mismatch"); err == nil {
		t.Fatal("succeeded operation accepted failure result")
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationFailed, ""); err == nil {
		t.Fatal("failed operation accepted empty result")
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationSucceeded, "completed"); err != nil {
		t.Fatal(err)
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationRunning, ""); !errors.Is(err, ErrInvalidTransition) {
		t.Fatalf("backward operation transition error = %v", err)
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE operations SET result_code='internal_failure' WHERE id=?`, operationID1); err == nil {
		t.Fatal("terminal operation result was mutable")
	}
}

func TestOperationAssociationsAreKindSpecificAndImmutable(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	now := time.Now().UTC()
	createActiveSession(t, s, sessionHash1)
	if err := s.CreateCandidate(ctx, Candidate{ID: candidateID1, SessionID: sessionHash1, State: CandidateUploading, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `INSERT INTO releases(id,version,generation_name,state,committed_at) VALUES(?, '1.0.0', 'release-1', 'current', ?)`, releaseID1, stamp(now)); err != nil {
		t.Fatal(err)
	}
	if err := s.CreateOperation(ctx, Operation{ID: operationID1, Kind: OperationPublish, State: OperationPending, CreatedAt: now}); err == nil {
		t.Fatal("publish operation accepted no candidate")
	}
	if err := s.CreateOperation(ctx, Operation{ID: operationID1, Kind: OperationCleanup, State: OperationPending, CandidateID: candidateID1, CreatedAt: now}); err == nil {
		t.Fatal("cleanup operation accepted a candidate")
	}
	if err := s.CreateOperation(ctx, Operation{ID: operationID1, Kind: OperationPublish, State: OperationPending, CandidateID: candidateID1, CreatedAt: now}); err != nil {
		t.Fatal(err)
	}
	for _, statement := range []string{
		`UPDATE operations SET kind='upload' WHERE id='` + operationID1 + `'`,
		`UPDATE operations SET candidate_id=NULL WHERE id='` + operationID1 + `'`,
		`UPDATE operations SET release_id='` + releaseID1 + `' WHERE id='` + operationID1 + `'`,
	} {
		if _, err := s.db.ExecContext(ctx, statement); err == nil {
			t.Fatalf("raw SQL changed an operation association: %s", statement)
		}
	}
}

func TestTimestampColumnsRejectNonIntegerStorage(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	if _, err := s.db.Exec(`INSERT INTO audit_events(occurred_at,event_type,outcome,operation_id,candidate_id,code) VALUES('not-a-timestamp','upload_started','success',?,?, 'accepted')`, operationID1, candidateID1); err == nil {
		t.Fatal("audit timestamp accepted non-integer storage")
	}
}

func TestCommitPublishedReleaseIsAtomicAcrossEveryBoundary(t *testing.T) {
	t.Parallel()
	stages := []string{"release", "assets", "floor", "candidate", "operation", "audit"}
	for _, failStage := range stages {
		t.Run(failStage, func(t *testing.T) {
			s, request := preparedPublish(t)
			s.commitPublishHook = func(stage string) error {
				if stage == failStage {
					return errors.New("injected")
				}
				return nil
			}
			if err := s.CommitPublishedRelease(context.Background(), request); err == nil {
				t.Fatal("commit succeeded")
			}
			for _, table := range []string{"releases", "release_assets", "highest_committed_version", "audit_events"} {
				var count int
				if err := s.db.QueryRow(`SELECT count(*) FROM ` + table).Scan(&count); err != nil || count != 0 {
					t.Fatalf("%s count=%d err=%v", table, count, err)
				}
			}
			var operationState, candidateState string
			_ = s.db.QueryRow(`SELECT state FROM operations WHERE id=?`, operationID1).Scan(&operationState)
			_ = s.db.QueryRow(`SELECT state FROM candidates WHERE id=?`, candidateID1).Scan(&candidateState)
			if operationState != "running" || candidateState != "publishing" {
				t.Fatalf("states=%s/%s", operationState, candidateState)
			}
		})
	}
}

func TestCommitPublishedReleaseRequiresAssetClosure(t *testing.T) {
	t.Parallel()
	s, request := preparedPublish(t)
	request.Assets = nil
	if err := s.CommitPublishedRelease(context.Background(), request); err == nil {
		t.Fatal("publish committed without assets")
	}
	if _, ok, err := s.HighestCommittedVersion(context.Background()); err != nil || ok {
		t.Fatalf("empty publish changed floor: %v/%v", ok, err)
	}
}

func TestCommitPublishedReleaseReturnsStableIdempotentResult(t *testing.T) {
	t.Parallel()
	s, request := preparedPublish(t)
	ctx := context.Background()
	if err := s.CommitPublishedRelease(ctx, request); err != nil {
		t.Fatal(err)
	}
	if err := s.CommitPublishedRelease(ctx, request); err != nil {
		t.Fatalf("identical publish retry = %v", err)
	}
	for table, want := range map[string]int{"releases": 1, "release_assets": 1, "highest_committed_version": 1, "audit_events": 1} {
		var count int
		if err := s.db.QueryRow(`SELECT count(*) FROM ` + table).Scan(&count); err != nil || count != want {
			t.Fatalf("%s count=%d err=%v, want %d", table, count, err, want)
		}
	}
	conflict := request
	conflict.Release.GenerationName = "release-conflict"
	if err := s.CommitPublishedRelease(ctx, conflict); !errors.Is(err, ErrInvalidTransition) {
		t.Fatalf("conflicting publish retry = %v, want ErrInvalidTransition", err)
	}
}

func preparedPublish(t *testing.T) (*Store, PublishCommit) {
	t.Helper()
	s := openStore(t)
	ctx := context.Background()
	createActiveSession(t, s, sessionHash1)
	now := time.Now().UTC()
	if err := s.CreateCandidate(ctx, Candidate{ID: candidateID1, SessionID: sessionHash1, State: CandidateUploading, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := s.ValidateCandidate(ctx, candidateID1, digest1, `{"valid":true}`); err != nil {
		t.Fatal(err)
	}
	if err := s.TransitionCandidate(ctx, candidateID1, CandidatePublishing); err != nil {
		t.Fatal(err)
	}
	if err := s.CreateOperation(ctx, Operation{ID: operationID1, Kind: OperationPublish, State: OperationPending, CandidateID: candidateID1, CreatedAt: now}); err != nil {
		t.Fatal(err)
	}
	if err := s.TransitionOperation(ctx, operationID1, OperationRunning, ""); err != nil {
		t.Fatal(err)
	}
	return s, PublishCommit{Release: Release{ID: releaseID1, Version: "1.2.3", GenerationName: "release-1", CommittedAt: now}, OperationID: operationID1, CandidateID: candidateID1, Assets: []ReleaseAsset{{FileName: "asset.nupkg", SizeBytes: 12, SHA256: digest1}}}
}

func TestReleaseCurrentUniquenessTransitionsAndFlatGenerationNames(t *testing.T) {
	t.Parallel()
	s := openStore(t)
	ctx := context.Background()
	now := time.Now().UTC()
	if _, err := s.db.ExecContext(ctx, `INSERT INTO releases VALUES(?, '1.0.0','release-1','current',?,'admin')`, releaseID1, stamp(now)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE releases SET state='retained' WHERE id=?`, releaseID1); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `INSERT INTO releases VALUES(?, '1.0.1','release-2','current',?,'admin')`, releaseID2, stamp(now)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `INSERT INTO highest_committed_version VALUES(1,'1.0.1',1,0,1,?)`, stamp(now)); err != nil {
		t.Fatal(err)
	}
	var currentCount int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM releases WHERE state='current'`).Scan(&currentCount); err != nil || currentCount != 1 {
		t.Fatalf("current release count = %d, %v", currentCount, err)
	}
	if err := s.SetCurrentRelease(ctx, releaseID1); err != nil {
		t.Fatalf("SetCurrentRelease() error = %v", err)
	}
	if got, ok, err := s.HighestCommittedVersion(ctx); err != nil || !ok || got != "1.0.1" {
		t.Fatalf("rollback changed floor: %q/%v/%v", got, ok, err)
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE releases SET state='removed' WHERE id=?`, releaseID1); err == nil {
		t.Fatal("raw SQL removed the current release")
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE releases SET state='retained' WHERE id=?`, releaseID1); err != nil {
		t.Fatal(err)
	}
	for index, name := range []string{"../release", "nested/release", `C:\release`, ".", ".hidden", "bad name"} {
		id := fmt.Sprintf("55555555-5555-4555-8555-%012d", index)
		if _, err := s.db.ExecContext(ctx, `INSERT INTO releases VALUES(?,?,?,'current',?,'admin')`, id, fmt.Sprintf("2.0.%d", index), name, stamp(now)); err == nil {
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
		if _, err := tx.ExecContext(ctx, `INSERT INTO operations(id,kind,state,created_at) VALUES(?, 'cleanup', 'pending', ?)`, operationID1, stamp(time.Now())); err != nil {
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
	s, err := Open(context.Background(), filepath.Join(privateStateTestDir(t), "state.db"))
	if err != nil {
		t.Fatalf("Open() error = %v", err)
	}
	t.Cleanup(func() { _ = s.Close() })
	return s
}

func privateStateTestDir(t *testing.T) string {
	t.Helper()
	root := t.TempDir()
	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatalf("make state test directory private: %v", err)
	}
	return root
}

func createActiveSession(t *testing.T, s *Store, hash string) {
	t.Helper()
	now := time.Now().UTC()
	if err := s.CreateSession(context.Background(), Session{IDHash: hash, State: SessionActive, CreatedAt: now, LastSeenAt: now, IdleExpiresAt: now.Add(time.Minute), AbsoluteExpiresAt: now.Add(time.Hour)}); err != nil {
		t.Fatalf("CreateSession() error = %v", err)
	}
}

func stamp(value time.Time) int64 { return value.UTC().Unix() }

type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) { return 0, io.ErrClosedPipe }

type nilReaderRand struct{ n byte }

func (r nilReaderRand) Read(p []byte) (int, error) {
	for i := range p {
		p[i] = byte(i + 1)
	}
	return len(p), nil
}

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
