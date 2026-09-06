package store

import (
	"context"
	"database/sql"
	"errors"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

// deployedSchemaV4Snapshot is the exact v4 migration shipped before v5. Its
// backfill used candidate_cleanups.updated_at for started_at; v5 must repair it.
const deployedSchemaV4Snapshot = `
CREATE TABLE candidate_upload_failures (
    operation_id TEXT PRIMARY KEY REFERENCES operations(id) ON DELETE RESTRICT,
    candidate_id TEXT NOT NULL UNIQUE REFERENCES candidates(id) ON DELETE RESTRICT,
    public_code TEXT NOT NULL CHECK (public_code IN ('signature_mismatch','version_floor','release_validation_failed','invalid_zip_envelope','upload_too_large','insufficient_storage','operation_failed','candidate_expired'))
);
CREATE TABLE candidate_cleanup_attempts (
    operation_id TEXT PRIMARY KEY REFERENCES operations(id) ON DELETE RESTRICT,
    candidate_id TEXT NOT NULL REFERENCES candidates(id) ON DELETE RESTRICT,
    attempt_number INTEGER NOT NULL CHECK (typeof(attempt_number) = 'integer' AND attempt_number >= 0 AND attempt_number > 0),
    started_at INTEGER NOT NULL CHECK (typeof(started_at) = 'integer' AND started_at >= 0),
    finished_at INTEGER CHECK (finished_at IS NULL OR (typeof(finished_at) = 'integer' AND finished_at >= 0)),
    outcome TEXT NOT NULL DEFAULT '' CHECK (outcome IN ('','success','failure')),
    CHECK ((outcome='' AND finished_at IS NULL) OR (outcome<>'' AND finished_at IS NOT NULL)),
    UNIQUE(candidate_id,attempt_number)
);
CREATE INDEX candidate_cleanup_attempts_candidate ON candidate_cleanup_attempts(candidate_id,attempt_number);
INSERT INTO candidate_cleanup_attempts(operation_id,candidate_id,attempt_number,started_at,finished_at,outcome)
SELECT x.operation_id,x.candidate_id,x.attempt_count,x.updated_at,
       CASE WHEN o.state IN ('succeeded','failed') THEN o.finished_at ELSE NULL END,
       CASE WHEN o.state='succeeded' THEN 'success' WHEN o.state='failed' THEN 'failure' ELSE '' END
FROM candidate_cleanups x JOIN operations o ON o.id=x.operation_id
WHERE x.operation_id IS NOT NULL AND x.attempt_count>0;
`

func TestV3MigrationAddsConstrainedCandidateCleanupLedger(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(time.Hour))
	if err := s.CreateCandidate(context.Background(), Candidate{ID: candidateID1, SessionID: sessionHash1, State: CandidateUploading, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`INSERT INTO candidate_cleanups(candidate_id,state,attempt_count,updated_at) VALUES(?,'bogus',0,?)`, candidateID1, timestamp(now)); err == nil {
		t.Fatal("invalid cleanup state accepted")
	}
	if _, err := s.db.Exec(`INSERT INTO candidate_cleanups(candidate_id,state,attempt_count,updated_at) VALUES(?,'pending',-1,?)`, candidateID1, timestamp(now)); err == nil {
		t.Fatal("negative attempt count accepted")
	}
}

func TestV2ToCurrentMigrationIsIncrementalAndIdempotent(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "state.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(schemaV2 + `PRAGMA user_version=2;`); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	for range 2 {
		s, err := Open(context.Background(), path)
		if err != nil {
			t.Fatal(err)
		}
		var version, count int
		if err := s.db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != schemaVersion {
			t.Fatalf("version=%d err=%v", version, err)
		}
		if err := s.db.QueryRow(`SELECT count(*) FROM sqlite_master WHERE type='table' AND name='candidate_cleanups'`).Scan(&count); err != nil || count != 1 {
			t.Fatalf("table count=%d err=%v", count, err)
		}
		if err := s.Close(); err != nil {
			t.Fatal(err)
		}
	}
}

func TestV3ToV4MigrationPreservesCleanupLedgerAndIsIdempotent(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "state.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(schemaV2 + schemaV3 + `PRAGMA user_version=3;`); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	for range 2 {
		s, err := Open(context.Background(), path)
		if err != nil {
			t.Fatal(err)
		}
		var version, ledgers, attempts, failures int
		if err := s.db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != schemaVersion {
			t.Fatalf("version=%d err=%v", version, err)
		}
		if err := s.db.QueryRow(`SELECT count(*) FROM sqlite_master WHERE type='table' AND name='candidate_cleanups'`).Scan(&ledgers); err != nil || ledgers != 1 {
			t.Fatalf("cleanup ledgers=%d err=%v", ledgers, err)
		}
		if err := s.db.QueryRow(`SELECT count(*) FROM sqlite_master WHERE type='table' AND name='candidate_cleanup_attempts'`).Scan(&attempts); err != nil || attempts != 1 {
			t.Fatalf("attempt tables=%d err=%v", attempts, err)
		}
		if err := s.db.QueryRow(`SELECT count(*) FROM sqlite_master WHERE type='table' AND name='candidate_upload_failures'`).Scan(&failures); err != nil || failures != 1 {
			t.Fatalf("upload failure tables=%d err=%v", failures, err)
		}
		if err := s.Close(); err != nil {
			t.Fatal(err)
		}
	}
}

func TestV4ToV5MigrationAddsConstrainedUploadLeaseLedgerIdempotently(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(schemaV2 + schemaV3 + schemaV4 + `PRAGMA user_version=4;`); err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	for range 2 {
		s, err := Open(context.Background(), path)
		if err != nil {
			t.Fatal(err)
		}
		var version, tables int
		if err := s.db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != schemaVersion {
			t.Fatalf("version=%d err=%v", version, err)
		}
		if err := s.db.QueryRow(`SELECT count(*) FROM sqlite_master WHERE type='table' AND name='candidate_upload_leases'`).Scan(&tables); err != nil || tables != 1 {
			t.Fatalf("lease tables=%d err=%v", tables, err)
		}
		if err := s.Close(); err != nil {
			t.Fatal(err)
		}
	}
}

func TestV5ToV6MigrationAddsReleaseNotesAndRollbackFailureAuditIdempotently(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(schemaV2 + schemaV3 + schemaV4 + schemaV5 + `PRAGMA user_version=5;`); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Exec(`INSERT INTO audit_events(occurred_at,event_type,outcome,code,source_pseudonym) VALUES(?,'login_result','success','login_accepted',?)`, timestamp(time.Now().UTC()), "hmac-sha256:"+strings.Repeat("a", 64)); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	for range 2 {
		s, err := Open(context.Background(), path)
		if err != nil {
			t.Fatal(err)
		}
		var version, notesColumns int
		if err := s.db.QueryRow(`PRAGMA user_version`).Scan(&version); err != nil || version != schemaVersion {
			t.Fatalf("version=%d err=%v", version, err)
		}
		if err := s.db.QueryRow(`SELECT count(*) FROM pragma_table_info('releases') WHERE name='notes' AND type='TEXT'`).Scan(&notesColumns); err != nil || notesColumns != 1 {
			t.Fatalf("notes columns=%d err=%v", notesColumns, err)
		}
		if _, err := s.db.Exec(`INSERT INTO audit_events(occurred_at,event_type,outcome,operation_id,release_id,code) VALUES(?,'rollback_failed','failure',?,?,'internal_failure')`, timestamp(time.Now().UTC()), rollbackOperationID, releaseID3); err != nil {
			t.Fatalf("rollback failure audit rejected: %v", err)
		}
		if err := s.Close(); err != nil {
			t.Fatal(err)
		}
		break
	}
	s, err := Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	var rollbackCount, loginCount int
	if err := s.db.QueryRow(`SELECT count(*) FROM audit_events WHERE event_type='rollback_failed'`).Scan(&rollbackCount); err != nil || rollbackCount != 1 {
		t.Fatalf("rollback audit count=%d err=%v", rollbackCount, err)
	}
	if err := s.db.QueryRow(`SELECT count(*) FROM audit_events WHERE event_type='login_result'`).Scan(&loginCount); err != nil || loginCount != 1 {
		t.Fatalf("preserved login audit count=%d err=%v", loginCount, err)
	}
}

func TestV5RepairsDeployedV4CleanupAttemptStartTime(t *testing.T) {
	path := filepath.Join(t.TempDir(), "state.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Unix(2_000_000_000, 0).UTC()
	if _, err = db.Exec(schemaV2 + schemaV3); err != nil {
		t.Fatal(err)
	}
	statements := []struct {
		query string
		args  []any
	}{
		{`INSERT INTO sessions(id_hash,state,created_at,last_seen_at,idle_expires_at,absolute_expires_at) VALUES(?,'active',?,?,?,?)`, []any{sessionHash1, timestamp(now), timestamp(now), timestamp(now.Add(time.Hour)), timestamp(now.Add(2 * time.Hour))}},
		{`INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, []any{candidateID1, sessionHash1, timestamp(now), timestamp(now.Add(time.Hour))}},
		{`UPDATE candidates SET state='failed' WHERE id=?`, []any{candidateID1}},
		{`INSERT INTO operations(id,kind,state,created_at) VALUES(?,'cleanup','pending',?)`, []any{operationID1, timestamp(now)}},
		{`UPDATE operations SET state='running' WHERE id=?`, []any{operationID1}},
		{`INSERT INTO candidate_cleanups(candidate_id,operation_id,state,attempt_count,claim_token,claim_expires_at,updated_at) VALUES(?,?,'pending',1,?,?,?)`, []any{candidateID1, operationID1, candidateID2, timestamp(now.Add(time.Minute)), timestamp(now.Add(30 * time.Second))}},
	}
	for _, statement := range statements {
		if _, err := db.Exec(statement.query, statement.args...); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := db.Exec(deployedSchemaV4Snapshot + `PRAGMA user_version=4;`); err != nil {
		t.Fatal(err)
	}
	var wrongStarted int64
	if err := db.QueryRow(`SELECT started_at FROM candidate_cleanup_attempts WHERE operation_id=?`, operationID1).Scan(&wrongStarted); err != nil || wrongStarted != timestamp(now.Add(30*time.Second)) {
		t.Fatalf("old v4 started_at=%d err=%v", wrongStarted, err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	secureMigrationFixture(t, path)
	s, err := Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	var repaired int64
	if err := s.db.QueryRow(`SELECT started_at FROM candidate_cleanup_attempts WHERE operation_id=?`, operationID1).Scan(&repaired); err != nil || repaired != timestamp(now) {
		t.Fatalf("repaired started_at=%d err=%v", repaired, err)
	}
}

func TestV4MigrationBackfillsV3CleanupAttemptStates(t *testing.T) {
	tests := []struct {
		name           string
		operationState string
		cleanupState   string
		wantOutcome    string
	}{
		{name: "running", operationState: "running", cleanupState: "pending"},
		{name: "failed", operationState: "failed", cleanupState: "pending", wantOutcome: "failure"},
		{name: "completed", operationState: "succeeded", cleanupState: "completed", wantOutcome: "success"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "state.db")
			db, err := sql.Open("sqlite", path)
			if err != nil {
				t.Fatal(err)
			}
			now := time.Unix(2_000_000_000, 0).UTC()
			if _, err = db.Exec(schemaV2 + schemaV3 + `PRAGMA user_version=3;`); err != nil {
				t.Fatal(err)
			}
			statements := []struct {
				query string
				args  []any
			}{
				{`INSERT INTO sessions(id_hash,state,created_at,last_seen_at,idle_expires_at,absolute_expires_at) VALUES(?, 'active', ?, ?, ?, ?)`, []any{sessionHash1, timestamp(now), timestamp(now), timestamp(now.Add(time.Hour)), timestamp(now.Add(2 * time.Hour))}},
				{`INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, []any{candidateID1, sessionHash1, timestamp(now), timestamp(now.Add(time.Hour))}},
				{`UPDATE candidates SET state='failed' WHERE id=?`, []any{candidateID1}},
				{`INSERT INTO operations(id,kind,state,created_at) VALUES(?,'cleanup','pending',?)`, []any{operationID1, timestamp(now)}},
				{`UPDATE operations SET state='running' WHERE id=?`, []any{operationID1}},
			}
			for _, statement := range statements {
				if _, err := db.Exec(statement.query, statement.args...); err != nil {
					t.Fatal(err)
				}
			}
			if test.operationState != "running" {
				if _, err := db.Exec(`UPDATE operations SET state=?,result_code=?,finished_at=? WHERE id=?`, test.operationState, map[string]string{"failed": "internal_failure", "succeeded": "completed"}[test.operationState], timestamp(now.Add(time.Minute)), operationID1); err != nil {
					t.Fatal(err)
				}
			}
			cleanupUpdatedAt := now.Add(30 * time.Second)
			if test.cleanupState == "completed" {
				_, err = db.Exec(`INSERT INTO candidate_cleanups(candidate_id,operation_id,state,attempt_count,updated_at,completed_at) VALUES(?,?,'completed',1,?,?)`, candidateID1, operationID1, timestamp(cleanupUpdatedAt), timestamp(now.Add(time.Minute)))
			} else {
				_, err = db.Exec(`INSERT INTO candidate_cleanups(candidate_id,operation_id,state,attempt_count,claim_token,claim_expires_at,updated_at,last_error_code) VALUES(?,?,'pending',1,?,?,?,?)`, candidateID1, operationID1, candidateID2, timestamp(now.Add(time.Minute)), timestamp(cleanupUpdatedAt), map[string]string{"failed": "internal_failure"}[test.operationState])
			}
			if err != nil {
				t.Fatal(err)
			}
			if err := db.Close(); err != nil {
				t.Fatal(err)
			}
			secureMigrationFixture(t, path)
			s, err := Open(context.Background(), path)
			if err != nil {
				t.Fatal(err)
			}
			defer s.Close()
			var candidate string
			var attempt int
			var outcome string
			var started int64
			var finished sql.NullInt64
			if err := s.db.QueryRow(`SELECT candidate_id,attempt_number,outcome,started_at,finished_at FROM candidate_cleanup_attempts WHERE operation_id=?`, operationID1).Scan(&candidate, &attempt, &outcome, &started, &finished); err != nil {
				t.Fatal(err)
			}
			if candidate != candidateID1 || attempt != 1 || outcome != test.wantOutcome || started != timestamp(now) || finished.Valid != (test.wantOutcome != "") {
				t.Fatalf("backfilled candidate=%s attempt=%d outcome=%q started=%d finished=%v", candidate, attempt, outcome, started, finished)
			}
		})
	}
}

func TestCleanupClaimsAreConcurrentExclusiveAndMissingPathsCanComplete(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(time.Hour))
	if _, err := s.db.Exec(`INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, candidateID1, sessionHash1, timestamp(now.Add(-2*time.Hour)), timestamp(now.Add(-time.Hour))); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`INSERT INTO operations(id,kind,state,candidate_id,created_at) VALUES(?,'upload','pending',?,?)`, operationID1, candidateID1, timestamp(now.Add(-2*time.Hour))); err != nil {
		t.Fatal(err)
	}
	if _, err := s.QueueExpiredCandidateCleanups(context.Background(), now, 64); err != nil {
		t.Fatal(err)
	}
	var wg sync.WaitGroup
	claims := make(chan []CleanupClaim, 2)
	for _, token := range []string{operationID1, candidateID2} {
		wg.Add(1)
		go func(token string) {
			defer wg.Done()
			got, err := s.ClaimCandidateCleanups(context.Background(), now, time.Minute, 1, token, []string{releaseID1})
			if err != nil {
				t.Error(err)
				return
			}
			claims <- got
		}(token)
	}
	wg.Wait()
	close(claims)
	var all []CleanupClaim
	for batch := range claims {
		all = append(all, batch...)
	}
	if len(all) != 1 {
		t.Fatalf("claims=%+v", all)
	}
	if err := s.CompleteCandidateCleanup(context.Background(), all[0], now); err != nil {
		t.Fatal(err)
	}
	var state string
	if err := s.db.QueryRow(`SELECT state FROM candidate_cleanups WHERE candidate_id=?`, candidateID1).Scan(&state); err != nil || state != "completed" {
		t.Fatalf("state=%q err=%v", state, err)
	}
}

func TestExpiredCleanupLeaseIsFailedAndAuditedBeforeRetryClaim(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(time.Hour))
	if _, err := s.db.Exec(`INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, candidateID1, sessionHash1, timestamp(now.Add(-time.Hour)), timestamp(now.Add(time.Hour))); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`UPDATE candidates SET state='failed' WHERE id=?`, candidateID1); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`INSERT INTO operations(id,kind,state,candidate_id,created_at) VALUES(?,'upload','pending',?,?)`, operationID1, candidateID1, timestamp(now.Add(-time.Hour))); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`UPDATE operations SET state='failed',result_code='internal_failure',finished_at=? WHERE id=?`, timestamp(now.Add(-time.Hour)), operationID1); err != nil {
		t.Fatal(err)
	}
	if _, err := s.QueueExpiredCandidateCleanups(context.Background(), now, 1); err != nil {
		t.Fatal(err)
	}
	first, err := s.ClaimCandidateCleanups(context.Background(), now, time.Minute, 1, candidateID2, []string{releaseID1})
	if err != nil || len(first) != 1 {
		t.Fatalf("first=%+v err=%v", first, err)
	}
	second, err := s.ClaimCandidateCleanups(context.Background(), now.Add(2*time.Minute), time.Minute, 1, releaseID2, []string{candidateID1})
	if err != nil || len(second) != 1 {
		t.Fatalf("second=%+v err=%v", second, err)
	}
	var state, code string
	if err := s.db.QueryRow(`SELECT state,result_code FROM operations WHERE id=?`, releaseID1).Scan(&state, &code); err != nil || state != "failed" || code != "internal_failure" {
		t.Fatalf("old op=%s/%s err=%v", state, code, err)
	}
	var audits int
	if err := s.db.QueryRow(`SELECT count(*) FROM audit_events WHERE event_type='cleanup_result' AND operation_id=? AND outcome='failure'`, releaseID1).Scan(&audits); err != nil || audits != 1 {
		t.Fatalf("audits=%d err=%v", audits, err)
	}
	var firstCandidate, secondCandidate string
	if err := s.db.QueryRow(`SELECT candidate_id FROM candidate_cleanup_attempts WHERE operation_id=?`, releaseID1).Scan(&firstCandidate); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRow(`SELECT candidate_id FROM candidate_cleanup_attempts WHERE operation_id=?`, candidateID1).Scan(&secondCandidate); err != nil {
		t.Fatal(err)
	}
	if firstCandidate != candidateID1 || secondCandidate != candidateID1 {
		t.Fatalf("cleanup attempt association lost: first=%s second=%s", firstCandidate, secondCandidate)
	}
}

func TestExpiredCleanupLeaseFailsClosedWhenAttemptLedgerIsMissing(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(time.Hour))
	if _, err := s.db.Exec(`INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, candidateID1, sessionHash1, timestamp(now.Add(-time.Hour)), timestamp(now.Add(time.Hour))); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`UPDATE candidates SET state='failed' WHERE id=?`, candidateID1); err != nil {
		t.Fatal(err)
	}
	if _, err := s.QueueExpiredCandidateCleanups(context.Background(), now, 1); err != nil {
		t.Fatal(err)
	}
	first, err := s.ClaimCandidateCleanups(context.Background(), now, time.Minute, 1, candidateID2, []string{operationID1})
	if err != nil || len(first) != 1 {
		t.Fatalf("first=%+v err=%v", first, err)
	}
	if _, err := s.db.Exec(`DELETE FROM candidate_cleanup_attempts WHERE operation_id=?`, operationID1); err != nil {
		t.Fatal(err)
	}
	if _, err := s.ClaimCandidateCleanups(context.Background(), now.Add(2*time.Minute), time.Minute, 1, releaseID1, []string{releaseID2}); !errors.Is(err, ErrInvalidTransition) {
		t.Fatalf("claim err=%v", err)
	}
	var operationState, ledgerOperation, claimToken string
	if err := s.db.QueryRow(`SELECT state FROM operations WHERE id=?`, operationID1).Scan(&operationState); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRow(`SELECT operation_id,claim_token FROM candidate_cleanups WHERE candidate_id=?`, candidateID1).Scan(&ledgerOperation, &claimToken); err != nil {
		t.Fatal(err)
	}
	if operationState != "running" || ledgerOperation != operationID1 || claimToken != candidateID2 {
		t.Fatalf("reclaim partially committed: operation=%q ledger=%q token=%q", operationState, ledgerOperation, claimToken)
	}
}

func TestExpiredCleanupLeaseAcceptsOnlyAConsistentPreviouslyFailedAttempt(t *testing.T) {
	for _, test := range []struct {
		name       string
		finishBoth bool
		wantError  bool
	}{
		{name: "consistent", finishBoth: true},
		{name: "operation only", wantError: true},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := openStore(t)
			now := time.Unix(2_000_000_000, 0).UTC()
			createCandidateSession(t, s, sessionHash1, now, now.Add(time.Hour))
			if _, err := s.db.Exec(`INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, candidateID1, sessionHash1, timestamp(now.Add(-time.Hour)), timestamp(now.Add(time.Hour))); err != nil {
				t.Fatal(err)
			}
			if _, err := s.db.Exec(`UPDATE candidates SET state='failed' WHERE id=?`, candidateID1); err != nil {
				t.Fatal(err)
			}
			if _, err := s.QueueExpiredCandidateCleanups(context.Background(), now, 1); err != nil {
				t.Fatal(err)
			}
			first, err := s.ClaimCandidateCleanups(context.Background(), now, time.Minute, 1, candidateID2, []string{operationID1})
			if err != nil || len(first) != 1 {
				t.Fatalf("first=%+v err=%v", first, err)
			}
			if _, err := s.db.Exec(`UPDATE operations SET state='failed',result_code='internal_failure',finished_at=? WHERE id=?`, timestamp(now.Add(time.Minute)), operationID1); err != nil {
				t.Fatal(err)
			}
			if test.finishBoth {
				if _, err := s.db.Exec(`UPDATE candidate_cleanup_attempts SET outcome='failure',finished_at=? WHERE operation_id=?`, timestamp(now.Add(time.Minute)), operationID1); err != nil {
					t.Fatal(err)
				}
			}
			second, err := s.ClaimCandidateCleanups(context.Background(), now.Add(2*time.Minute), time.Minute, 1, releaseID1, []string{releaseID2})
			if test.wantError {
				if !errors.Is(err, ErrInvalidTransition) || len(second) != 0 {
					t.Fatalf("second=%+v err=%v", second, err)
				}
				return
			}
			if err != nil || len(second) != 1 || second[0].CandidateID != candidateID1 {
				t.Fatalf("second=%+v err=%v", second, err)
			}
		})
	}
}

func TestCleanupClaimFailsClosedWhenLedgerCASChangesNoRows(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(time.Hour))
	if _, err := s.db.Exec(`INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, candidateID1, sessionHash1, timestamp(now.Add(-time.Hour)), timestamp(now.Add(time.Hour))); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`UPDATE candidates SET state='failed' WHERE id=?`, candidateID1); err != nil {
		t.Fatal(err)
	}
	if _, err := s.QueueExpiredCandidateCleanups(context.Background(), now, 1); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`CREATE TRIGGER ignore_cleanup_claim BEFORE UPDATE ON candidate_cleanups WHEN NEW.claim_token<>'' BEGIN SELECT RAISE(IGNORE); END;`); err != nil {
		t.Fatal(err)
	}
	claims, err := s.ClaimCandidateCleanups(context.Background(), now, time.Minute, 1, candidateID2, []string{operationID1})
	if !errors.Is(err, ErrInvalidTransition) || len(claims) != 0 {
		t.Fatalf("claims=%+v err=%v", claims, err)
	}
	var attempts, operations int
	if err := s.db.QueryRow(`SELECT attempt_count FROM candidate_cleanups WHERE candidate_id=?`, candidateID1).Scan(&attempts); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRow(`SELECT count(*) FROM operations WHERE id=?`, operationID1).Scan(&operations); err != nil {
		t.Fatal(err)
	}
	if attempts != 0 || operations != 0 {
		t.Fatalf("claim partially committed: attempts=%d operations=%d", attempts, operations)
	}
}
