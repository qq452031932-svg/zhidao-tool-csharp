package store

import (
	"context"
	"errors"
	"testing"
	"time"
)

const (
	rollbackOperationID = "88888888-8888-4888-8888-888888888888"
	cleanupOperationID  = "99999999-9999-4999-8999-999999999999"
	releaseID3          = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
	releaseID4          = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
)

func TestRollbackOperationIsSessionBoundIdempotentAndPreservesPermanentFloor(t *testing.T) {
	s := openStore(t)
	now := time.Now().UTC()
	createActiveSession(t, s, sessionHash1)
	seedReleaseHistory(t, s, now)

	first, created, err := s.BeginRollbackForActiveSession(context.Background(), sessionHash1, releaseID3, rollbackOperationID, now)
	if err != nil || !created || first.OperationID != rollbackOperationID || first.ReleaseID != releaseID3 || first.State != OperationRunning {
		t.Fatalf("first=%+v created=%v err=%v", first, created, err)
	}
	second, created, err := s.BeginRollbackForActiveSession(context.Background(), sessionHash1, releaseID3, rollbackOperationID, now.Add(time.Second))
	if err != nil || created || second != first {
		t.Fatalf("second=%+v created=%v err=%v", second, created, err)
	}
	if _, _, err := s.BeginRollbackForActiveSession(context.Background(), sessionHash1, releaseID2, rollbackOperationID, now.Add(time.Second)); !errors.Is(err, ErrInvalidTransition) {
		t.Fatalf("conflicting idempotency replay err=%v", err)
	}

	if err := s.CommitRollback(context.Background(), rollbackOperationID, releaseID3, now.Add(2*time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := s.CommitRollback(context.Background(), rollbackOperationID, releaseID3, now.Add(3*time.Second)); err != nil {
		t.Fatalf("terminal replay error=%v", err)
	}
	current, _, found, err := s.CurrentRelease(context.Background())
	if err != nil || !found || current.ID != releaseID3 {
		t.Fatalf("current=%+v found=%v err=%v", current, found, err)
	}
	if floor, found, err := s.HighestCommittedVersion(context.Background()); err != nil || !found || floor != "1.0.4" {
		t.Fatalf("floor=%q found=%v err=%v", floor, found, err)
	}
	var auditCount int
	if err := s.db.QueryRow(`SELECT count(*) FROM audit_events WHERE event_type='rollback_completed' AND operation_id=? AND release_id=? AND outcome='success' AND code='completed'`, rollbackOperationID, releaseID3).Scan(&auditCount); err != nil || auditCount != 1 {
		t.Fatalf("audit count=%d err=%v", auditCount, err)
	}
}

func TestRollbackAndPublishAreMutuallyExclusive(t *testing.T) {
	s := openStore(t)
	now := time.Now().UTC()
	createActiveSession(t, s, sessionHash1)
	seedReleaseHistory(t, s, now)
	if _, created, err := s.BeginRollbackForActiveSession(context.Background(), sessionHash1, releaseID3, rollbackOperationID, now); err != nil || !created {
		t.Fatalf("begin rollback created=%v err=%v", created, err)
	}
	if err := s.CreateCandidate(context.Background(), Candidate{ID: candidateID1, SessionID: sessionHash1, State: CandidateUploading, CreatedAt: now, ExpiresAt: now.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := s.ValidateCandidate(context.Background(), candidateID1, digest1, `{"verified":true}`); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.BeginPublishForActiveSession(context.Background(), sessionHash1, candidateID1, operationID1, now); !errors.Is(err, ErrInvalidTransition) {
		t.Fatalf("concurrent publish err=%v", err)
	}
}

func TestReleaseHistoryAndRetentionKeepCurrentPlusTwoPrevious(t *testing.T) {
	s := openStore(t)
	now := time.Now().UTC()
	seedReleaseHistory(t, s, now)

	history, err := s.ReleaseHistory(context.Background(), 3)
	if err != nil {
		t.Fatal(err)
	}
	if len(history) != 3 || history[0].ID != releaseID4 || history[1].ID != releaseID3 || history[2].ID != releaseID2 {
		t.Fatalf("history=%+v", history)
	}

	target, created, err := s.BeginRetentionCleanup(context.Background(), cleanupOperationID, now.Add(time.Minute))
	if err != nil || !created || target.ID != releaseID1 || target.State != "removed" {
		t.Fatalf("target=%+v created=%v err=%v", target, created, err)
	}
	if err := s.FinishRetentionCleanup(context.Background(), cleanupOperationID, releaseID1, false, now.Add(2*time.Minute)); err != nil {
		t.Fatal(err)
	}
	var state string
	if err := s.db.QueryRow(`SELECT state FROM releases WHERE id=?`, releaseID1).Scan(&state); err != nil || state != "removed" {
		t.Fatalf("cleanup failure restored release state=%q err=%v", state, err)
	}
}

func TestRunningRetentionCleanupIsRecoverableAfterRestart(t *testing.T) {
	s := openStore(t)
	now := time.Now().UTC()
	seedReleaseHistory(t, s, now)
	target, created, err := s.BeginRetentionCleanup(context.Background(), cleanupOperationID, now.Add(time.Minute))
	if err != nil || !created {
		t.Fatalf("target=%+v created=%v err=%v", target, created, err)
	}
	recovered, operationID, found, err := s.InFlightRetentionCleanup(context.Background())
	if err != nil || !found || operationID != cleanupOperationID || recovered.ID != target.ID || recovered.State != "removed" {
		t.Fatalf("recovered=%+v operation=%q found=%v err=%v", recovered, operationID, found, err)
	}
}

func TestFailedRollbackPersistsTypedReplayAndAudit(t *testing.T) {
	s := openStore(t)
	now := time.Now().UTC()
	createActiveSession(t, s, sessionHash1)
	seedReleaseHistory(t, s, now)
	if _, created, err := s.BeginRollbackForActiveSession(context.Background(), sessionHash1, releaseID3, rollbackOperationID, now); err != nil || !created {
		t.Fatalf("begin created=%v err=%v", created, err)
	}
	if err := s.FailRollback(context.Background(), rollbackOperationID, releaseID3, "signature_mismatch", now.Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	replay, created, err := s.BeginRollbackForActiveSession(context.Background(), sessionHash1, releaseID3, rollbackOperationID, now.Add(2*time.Second))
	if err != nil || created || replay.State != OperationFailed || replay.ResultCode != "signature_mismatch" {
		t.Fatalf("replay=%+v created=%v err=%v", replay, created, err)
	}
	var count int
	if err := s.db.QueryRow(`SELECT count(*) FROM audit_events WHERE event_type='rollback_failed' AND operation_id=? AND release_id=? AND outcome='failure' AND code='signature_mismatch'`, rollbackOperationID, releaseID3).Scan(&count); err != nil || count != 1 {
		t.Fatalf("audit count=%d err=%v", count, err)
	}
}

func TestRetentionExcludesRunningRollbackTarget(t *testing.T) {
	s := openStore(t)
	now := time.Now().UTC()
	createActiveSession(t, s, sessionHash1)
	seedReleaseHistory(t, s, now)
	if _, created, err := s.BeginRollbackForActiveSession(context.Background(), sessionHash1, releaseID1, rollbackOperationID, now); err != nil || !created {
		t.Fatalf("begin rollback created=%v err=%v", created, err)
	}
	target, created, err := s.BeginRetentionCleanup(context.Background(), cleanupOperationID, now.Add(time.Second))
	if err != nil || created || target.ID != "" {
		t.Fatalf("retention target=%+v created=%v err=%v", target, created, err)
	}
	var state string
	if err := s.db.QueryRow(`SELECT state FROM releases WHERE id=?`, releaseID1).Scan(&state); err != nil || state != "retained" {
		t.Fatalf("rollback target state=%q err=%v", state, err)
	}
}

func TestReleaseHistoryReturnsPersistedSignedNotes(t *testing.T) {
	s := openStore(t)
	now := time.Now().UTC()
	seedReleaseHistory(t, s, now)
	if _, err := s.db.Exec(`UPDATE releases SET notes='修复批量转存。' WHERE id=?`, releaseID4); err != nil {
		t.Fatal(err)
	}
	history, err := s.ReleaseHistory(context.Background(), 3)
	if err != nil {
		t.Fatal(err)
	}
	if len(history) == 0 || history[0].Notes != "修复批量转存。" {
		t.Fatalf("history=%+v", history)
	}
}

func seedReleaseHistory(t *testing.T, s *Store, now time.Time) {
	t.Helper()
	items := []struct {
		id, version string
	}{
		{releaseID1, "1.0.1"},
		{releaseID2, "1.0.2"},
		{releaseID3, "1.0.3"},
		{releaseID4, "1.0.4"},
	}
	for index, item := range items {
		if index > 0 {
			if _, err := s.db.Exec(`UPDATE releases SET state='retained' WHERE state='current'`); err != nil {
				t.Fatal(err)
			}
		}
		if _, err := s.db.Exec(`INSERT INTO releases(id,version,generation_name,state,committed_at) VALUES(?,?,?,'current',?)`, item.id, item.version, "release-"+item.id, stamp(now.Add(time.Duration(index)*time.Minute))); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := s.db.Exec(`INSERT INTO highest_committed_version(singleton_id,version,major,minor,patch,committed_at) VALUES(1,'1.0.4',1,0,4,?)`, stamp(now.Add(3*time.Minute))); err != nil {
		t.Fatal(err)
	}
}
