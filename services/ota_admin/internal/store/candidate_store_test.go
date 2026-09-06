package store

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"
	"time"
)

const candidateID2 = "55555555-5555-4555-8555-555555555555"

func TestAcceptCandidateForActiveSessionBindsEvidenceAndUploadOperationAtomically(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(time.Hour))

	record, created, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"schemaVersion":1,"verified":true,"version":"1.2.3"}`,
		Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), CommittedAt: now,
	})
	if err != nil || !created {
		t.Fatalf("accept created=%v err=%v", created, err)
	}
	if record.ID != candidateID1 || record.SessionID != sessionHash1 || record.State != CandidateValidated ||
		record.UploadDigest != digest1 || record.OperationID != operationID1 || record.Version != "1.2.3" {
		t.Fatalf("record=%+v", record)
	}
	var operationState, operationCode string
	if err := s.db.QueryRow(`SELECT state,result_code FROM operations WHERE id=?`, operationID1).Scan(&operationState, &operationCode); err != nil || operationState != "succeeded" || operationCode != "completed" {
		t.Fatalf("operation=%s/%s err=%v", operationState, operationCode, err)
	}
	var audits int
	if err := s.db.QueryRow(`SELECT count(*) FROM audit_events WHERE operation_id=?`, operationID1).Scan(&audits); err != nil || audits != 3 {
		t.Fatalf("audits=%d err=%v", audits, err)
	}
}

func TestAcceptCandidateForActiveSessionRejectsRevokedAndExpiredSessionsWithoutPartialRows(t *testing.T) {
	for _, test := range []struct {
		name      string
		state     SessionState
		idleUntil time.Duration
		absUntil  time.Duration
	}{
		{"revoked", SessionRevoked, time.Hour, 2 * time.Hour},
		{"idle expired", SessionActive, 0, 2 * time.Hour},
		{"absolute expired", SessionActive, time.Hour, 0},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := openStore(t)
			now := time.Unix(2_000_000_000, 0).UTC()
			createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
			if test.state == SessionRevoked {
				if err := s.TransitionSession(context.Background(), sessionHash1, SessionRevoked); err != nil {
					t.Fatal(err)
				}
			} else {
				idleExpires := now.Add(test.idleUntil)
				absoluteExpires := now.Add(test.absUntil)
				if idleExpires.After(absoluteExpires) {
					idleExpires = absoluteExpires
				}
				_, err := s.db.Exec(`UPDATE sessions SET idle_expires_at=?,absolute_expires_at=? WHERE id_hash=?`, timestamp(idleExpires), timestamp(absoluteExpires), sessionHash1)
				if err != nil {
					t.Fatal(err)
				}
			}
			_, _, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{
				CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
				UploadDigest: digest1, ValidationReport: `{"schemaVersion":1,"verified":true,"version":"1.2.3"}`,
				Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), CommittedAt: now,
			})
			if !errors.Is(err, ErrSessionInactive) {
				t.Fatalf("err=%v, want %v", err, ErrSessionInactive)
			}
			for _, table := range []string{"candidates", "operations", "audit_events"} {
				var count int
				if err := s.db.QueryRow(`SELECT count(*) FROM ` + table).Scan(&count); err != nil || count != 0 {
					t.Fatalf("%s count=%d err=%v", table, count, err)
				}
			}
		})
	}
}

func TestAcceptCandidateUsesFreshCommitTimeForNewAndReservedOperations(t *testing.T) {
	for _, reserved := range []bool{false, true} {
		t.Run(map[bool]string{false: "new", true: "reserved"}[reserved], func(t *testing.T) {
			s := openStore(t)
			now := time.Unix(2_000_000_000, 0).UTC()
			createCandidateSession(t, s, sessionHash1, now, now.Add(time.Minute))
			if reserved {
				if _, _, err := s.ReserveCandidateForActiveSession(context.Background(), CandidateReservation{CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1, CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour)}); err != nil {
					t.Fatal(err)
				}
			}
			_, _, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1, UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), CommittedAt: now.Add(time.Minute)})
			if !errors.Is(err, ErrSessionInactive) {
				t.Fatalf("err=%v", err)
			}
			var validated int
			if err := s.db.QueryRow(`SELECT count(*) FROM candidates WHERE state='validated'`).Scan(&validated); err != nil || validated != 0 {
				t.Fatalf("validated=%d err=%v", validated, err)
			}
		})
	}
}

func TestAcceptCandidateRejectsUnverifiedValidationReport(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	_, _, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":false,"version":"1.2.3"}`,
		Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), CommittedAt: now,
	})
	if err == nil {
		t.Fatal("unverified report accepted")
	}
}

func TestAcceptCandidateOperationIDIsConcurrentAndSessionBoundIdempotent(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	createCandidateSession(t, s, sessionHash2, now, now.Add(time.Hour))
	requests := []CandidateAcceptance{
		{CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1, UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), CommittedAt: now},
		{CandidateID: candidateID2, SessionID: sessionHash1, OperationID: operationID1, UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour), CommittedAt: now},
	}
	var wait sync.WaitGroup
	results := make(chan CandidateRecord, len(requests))
	errorsSeen := make(chan error, len(requests))
	for _, request := range requests {
		wait.Add(1)
		go func(request CandidateAcceptance) {
			defer wait.Done()
			record, _, err := s.AcceptCandidateForActiveSession(context.Background(), request)
			if err != nil {
				errorsSeen <- err
				return
			}
			results <- record
		}(request)
	}
	wait.Wait()
	close(results)
	close(errorsSeen)
	for err := range errorsSeen {
		t.Fatal(err)
	}
	var acceptedID string
	for result := range results {
		if acceptedID == "" {
			acceptedID = result.ID
		}
		if result.ID != acceptedID || result.OperationID != operationID1 {
			t.Fatalf("non-idempotent result=%+v first=%s", result, acceptedID)
		}
	}
	if _, _, err := s.CandidateByOperationForActiveSession(context.Background(), sessionHash2, operationID1, now); !errors.Is(err, ErrSessionInactive) {
		t.Fatalf("cross-session lookup err=%v", err)
	}
}

func TestReserveCandidateAtomicallyClaimsIdempotencyKeyBeforeUpload(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	requests := []CandidateReservation{
		{CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1, CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour)},
		{CandidateID: candidateID2, SessionID: sessionHash1, OperationID: operationID1, CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour)},
	}
	var wait sync.WaitGroup
	created := make(chan bool, 2)
	ids := make(chan string, 2)
	for _, request := range requests {
		wait.Add(1)
		go func(request CandidateReservation) {
			defer wait.Done()
			record, fresh, err := s.ReserveCandidateForActiveSession(context.Background(), request)
			if err != nil {
				t.Error(err)
				return
			}
			created <- fresh
			ids <- record.ID
		}(request)
	}
	wait.Wait()
	close(created)
	close(ids)
	createdCount := 0
	for value := range created {
		if value {
			createdCount++
		}
	}
	if createdCount != 1 {
		t.Fatalf("created count=%d", createdCount)
	}
	var first string
	for id := range ids {
		if first == "" {
			first = id
		}
		if id != first {
			t.Fatalf("reservation IDs differ: %s/%s", first, id)
		}
	}
	var candidates, operations, audits int
	if err := s.db.QueryRow(`SELECT count(*) FROM candidates`).Scan(&candidates); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRow(`SELECT count(*) FROM operations`).Scan(&operations); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRow(`SELECT count(*) FROM audit_events WHERE event_type='upload_started'`).Scan(&audits); err != nil {
		t.Fatal(err)
	}
	if candidates != 1 || operations != 1 || audits != 1 {
		t.Fatalf("rows candidates=%d operations=%d audits=%d", candidates, operations, audits)
	}
}

func TestFailedReservationIsTerminalAndReplayable(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	if _, _, err := s.ReserveCandidateForActiveSession(context.Background(), CandidateReservation{CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1, CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour)}); err != nil {
		t.Fatal(err)
	}
	failed, err := s.FailReservedCandidate(context.Background(), sessionHash1, operationID1, "internal_failure", now.Add(time.Second))
	if err != nil || failed.State != CandidateFailed || failed.OperationState != OperationFailed {
		t.Fatalf("failed=%+v err=%v", failed, err)
	}
	replayed, created, err := s.ReserveCandidateForActiveSession(context.Background(), CandidateReservation{CandidateID: candidateID2, SessionID: sessionHash1, OperationID: operationID1, CreatedAt: now.Add(2 * time.Second), ExpiresAt: now.Add(24 * time.Hour)})
	if err != nil || created || replayed.ID != candidateID1 || replayed.ResultCode != "operation_failed" {
		t.Fatalf("replayed=%+v created=%v err=%v", replayed, created, err)
	}
}

func TestFailedValidationRecordsSeparateTruthfulGateAndUploadTerminalAudits(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	for index, code := range []string{"signature_mismatch", "version_floor", "hash_mismatch", "internal_failure"} {
		candidateID := fmt.Sprintf("%08d-1111-4111-8111-%012d", index+1, index+1)
		operationID := fmt.Sprintf("%08d-2222-4222-8222-%012d", index+1, index+1)
		if _, _, err := s.ReserveCandidateForActiveSession(context.Background(), CandidateReservation{CandidateID: candidateID, SessionID: sessionHash1, OperationID: operationID, CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour)}); err != nil {
			t.Fatal(err)
		}
		if _, err := s.FailReservedCandidate(context.Background(), sessionHash1, operationID, code, now.Add(time.Second)); err != nil {
			t.Fatal(err)
		}
		var gateCode, uploadCode string
		if err := s.db.QueryRow(`SELECT code FROM audit_events WHERE event_type='gate_result' AND operation_id=?`, operationID).Scan(&gateCode); err != nil || gateCode != code {
			t.Fatalf("code=%s gate=%s err=%v", code, gateCode, err)
		}
		if err := s.db.QueryRow(`SELECT code FROM audit_events WHERE event_type='upload_completed' AND operation_id=?`, operationID).Scan(&uploadCode); err != nil {
			t.Fatal(err)
		}
		wantUpload := "internal_failure"
		if code == "hash_mismatch" {
			wantUpload = code
		}
		if uploadCode != wantUpload {
			t.Fatalf("code=%s upload=%s want=%s", code, uploadCode, wantUpload)
		}
	}
}

func TestUploadReplayRemainsValidatedAfterLaterPublishFailure(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	_, _, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(time.Hour), CommittedAt: now,
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.BeginPublishForActiveSession(context.Background(), sessionHash1, candidateID1, releaseID1, now); err != nil || !created {
		t.Fatalf("begin created=%v err=%v", created, err)
	}
	if err := s.FailPublish(context.Background(), releaseID1, candidateID1, "internal_failure", now.Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	replayed, created, err := s.ReserveCandidateForActiveSession(context.Background(), CandidateReservation{CandidateID: candidateID2, SessionID: sessionHash1, OperationID: operationID1, CreatedAt: now.Add(time.Minute), ExpiresAt: now.Add(2 * time.Hour)})
	if err != nil || created || replayed.State != CandidateValidated || replayed.OperationState != OperationSucceeded || replayed.ResultCode != "completed" {
		t.Fatalf("replayed=%+v created=%v err=%v", replayed, created, err)
	}
}

func TestCandidateConfirmReadAndExpiryAreSessionBoundTransactions(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	createCandidateSession(t, s, sessionHash2, now, now.Add(time.Hour))
	_, _, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(30 * time.Minute), CommittedAt: now,
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CandidateForConfirmForActiveSession(context.Background(), sessionHash2, candidateID1, now); !errors.Is(err, ErrSessionInactive) {
		t.Fatalf("cross-session confirm err=%v", err)
	}
	if _, err := s.CandidateForConfirmForActiveSession(context.Background(), sessionHash1, candidateID1, now.Add(30*time.Minute)); !errors.Is(err, ErrCandidateExpired) {
		t.Fatalf("expired confirm err=%v", err)
	}
	expired, err := s.ExpireCandidates(context.Background(), now.Add(30*time.Minute))
	if err != nil || len(expired) != 1 || expired[0] != candidateID1 {
		t.Fatalf("expired=%v err=%v", expired, err)
	}
}

func TestCandidateStatusReadIsSessionBoundButAllowsExpiredState(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	createCandidateSession(t, s, sessionHash2, now, now.Add(2*time.Hour))
	_, _, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(time.Minute), CommittedAt: now,
	})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CandidateForActiveSession(context.Background(), sessionHash2, candidateID1, now); !errors.Is(err, ErrSessionInactive) {
		t.Fatalf("cross-session status err=%v", err)
	}
	if _, err := s.ExpireCandidates(context.Background(), now.Add(time.Minute)); err != nil {
		t.Fatal(err)
	}
	record, err := s.CandidateForActiveSession(context.Background(), sessionHash1, candidateID1, now)
	if err != nil || record.State != CandidateExpired {
		t.Fatalf("record=%+v err=%v", record, err)
	}
}

func TestRecordCleanupResultCreatesTerminalOperationAndAuditAtomically(t *testing.T) {
	for _, test := range []struct {
		name, state, code, outcome string
		success                    bool
	}{
		{name: "success", state: "succeeded", code: "completed", outcome: "success", success: true},
		{name: "failure", state: "failed", code: "internal_failure", outcome: "failure"},
	} {
		t.Run(test.name, func(t *testing.T) {
			s := openStore(t)
			now := time.Unix(2_000_000_000, 0).UTC()
			if err := s.RecordCleanupResult(context.Background(), operationID1, test.success, now); err != nil {
				t.Fatal(err)
			}
			var state, code string
			if err := s.db.QueryRow(`SELECT state,result_code FROM operations WHERE id=?`, operationID1).Scan(&state, &code); err != nil || state != test.state || code != test.code {
				t.Fatalf("operation=%s/%s err=%v", state, code, err)
			}
			var outcome string
			if err := s.db.QueryRow(`SELECT outcome FROM audit_events WHERE event_type='cleanup_result' AND operation_id=?`, operationID1).Scan(&outcome); err != nil || outcome != test.outcome {
				t.Fatalf("outcome=%q err=%v", outcome, err)
			}
		})
	}
}

func createCandidateSession(t *testing.T, s *Store, id string, now, absolute time.Time) {
	t.Helper()
	idle := now.Add(time.Hour)
	if idle.After(absolute) {
		idle = absolute
	}
	if err := s.CreateSession(context.Background(), Session{IDHash: id, State: SessionActive, CreatedAt: now.Add(-time.Minute), LastSeenAt: now.Add(-time.Minute), IdleExpiresAt: idle, AbsoluteExpiresAt: absolute}); err != nil {
		t.Fatal(err)
	}
}
