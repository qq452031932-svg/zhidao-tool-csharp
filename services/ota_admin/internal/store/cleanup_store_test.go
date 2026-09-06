package store

import (
	"context"
	"database/sql"
	"path/filepath"
	"sync"
	"testing"
	"time"
)

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
