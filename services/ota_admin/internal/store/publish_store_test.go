package store

import (
	"context"
	"errors"
	"testing"
	"time"
)

func TestBeginPublishIsSessionBoundIdempotentAndAtomic(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	createCandidateSession(t, s, sessionHash2, now, now.Add(2*time.Hour))
	request := reserveCandidateAcceptance(t, s, CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3",
		CreatedAt: now, ExpiresAt: now.Add(time.Hour), CommittedAt: now,
	})
	_, _, err := s.AcceptCandidateForActiveSession(context.Background(), request)
	if err != nil {
		t.Fatal(err)
	}
	first, created, err := s.BeginPublishForActiveSession(context.Background(), sessionHash1, candidateID1, releaseID1, now)
	if err != nil || !created || first.State != OperationRunning || first.CandidateID != candidateID1 {
		t.Fatalf("first=%+v created=%v err=%v", first, created, err)
	}
	second, created, err := s.BeginPublishForActiveSession(context.Background(), sessionHash1, candidateID1, releaseID1, now.Add(time.Second))
	if err != nil || created || second != first {
		t.Fatalf("second=%+v created=%v err=%v", second, created, err)
	}
	if _, _, err := s.BeginPublishForActiveSession(context.Background(), sessionHash2, candidateID1, releaseID1, now.Add(time.Second)); !errors.Is(err, ErrSessionInactive) {
		t.Fatalf("cross-session replay err=%v", err)
	}
	var candidateState, operationState string
	if err := s.db.QueryRow(`SELECT state FROM candidates WHERE id=?`, candidateID1).Scan(&candidateState); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRow(`SELECT state FROM operations WHERE id=?`, releaseID1).Scan(&operationState); err != nil {
		t.Fatal(err)
	}
	if candidateState != "publishing" || operationState != "running" {
		t.Fatalf("candidate=%s operation=%s", candidateState, operationState)
	}
}

func TestPublishOperationLookupRejectsASameSessionIdempotencyKeyOfAnotherKind(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	request := reserveCandidateAcceptance(t, s, CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3",
		CreatedAt: now, ExpiresAt: now.Add(time.Hour), CommittedAt: now,
	})
	if _, _, err := s.AcceptCandidateForActiveSession(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	if _, found, err := s.PublishOperationForActiveSession(context.Background(), sessionHash1, operationID1, now); !errors.Is(err, ErrInvalidTransition) || found {
		t.Fatalf("found=%v err=%v", found, err)
	}
}

func TestCommitPublishedReleaseRequiresTheRecoveryIdentityConvention(t *testing.T) {
	for _, mutate := range []func(*PublishCommit){
		func(commit *PublishCommit) { commit.Release.ID = releaseID2 },
		func(commit *PublishCommit) { commit.Release.GenerationName = "other-generation" },
	} {
		s, request := preparedPublish(t)
		request.Release.ID = request.OperationID
		request.Release.GenerationName = "release-" + request.OperationID
		mutate(&request)
		if err := s.CommitPublishedRelease(context.Background(), request); !errors.Is(err, ErrInvalidTransition) {
			t.Fatalf("commit=%+v err=%v", request, err)
		}
	}
}

func TestFailPublishRollsBackCandidateOperationAndAuditTogether(t *testing.T) {
	s := openStore(t)
	now := time.Unix(2_000_000_000, 0).UTC()
	createCandidateSession(t, s, sessionHash1, now, now.Add(2*time.Hour))
	request := reserveCandidateAcceptance(t, s, CandidateAcceptance{
		CandidateID: candidateID1, SessionID: sessionHash1, OperationID: operationID1,
		UploadDigest: digest1, ValidationReport: `{"verified":true,"version":"1.2.3"}`, Version: "1.2.3",
		CreatedAt: now, ExpiresAt: now.Add(time.Hour), CommittedAt: now,
	})
	_, _, err := s.AcceptCandidateForActiveSession(context.Background(), request)
	if err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.BeginPublishForActiveSession(context.Background(), sessionHash1, candidateID1, releaseID1, now); err != nil || !created {
		t.Fatalf("created=%v err=%v", created, err)
	}
	if err := s.FailPublish(context.Background(), releaseID1, candidateID1, "hash_mismatch", now.Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	operation, found, err := s.PublishOperationForActiveSession(context.Background(), sessionHash1, releaseID1, now.Add(time.Second))
	if err != nil || !found || operation.State != OperationFailed || operation.ResultCode != "hash_mismatch" {
		t.Fatalf("operation=%+v found=%v err=%v", operation, found, err)
	}
	var state, code string
	if err := s.db.QueryRow(`SELECT state FROM candidates WHERE id=?`, candidateID1).Scan(&state); err != nil || state != "failed" {
		t.Fatalf("state=%s err=%v", state, err)
	}
	if err := s.db.QueryRow(`SELECT code FROM audit_events WHERE event_type='publish_failed' AND operation_id=?`, releaseID1).Scan(&code); err != nil || code != "hash_mismatch" {
		t.Fatalf("audit=%s err=%v", code, err)
	}
	if err := s.FailPublish(context.Background(), releaseID1, candidateID1, "internal_failure", now.Add(2*time.Second)); !errors.Is(err, ErrInvalidTransition) {
		t.Fatalf("second failure err=%v", err)
	}
}
