package store

import (
	"context"
	"errors"
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
		Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour),
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
				Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour),
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

func TestAcceptCandidateRejectsUnverifiedValidationReport(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	_, _, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":false,"version":"1.2.3"}`,
		Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour),
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
		{CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1, UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour)},
		{CandidateID: candidateID2, SessionID: sessionHash1, OperationID: operationID1, UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour)},
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

func TestCandidateConfirmReadAndExpiryAreSessionBoundTransactions(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	createCandidateSession(t, s, sessionHash2, now, now.Add(time.Hour))
	_, _, err := s.AcceptCandidateForActiveSession(context.Background(), CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(30 * time.Minute),
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
		UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3", CreatedAt: now, ExpiresAt: now.Add(time.Minute),
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
