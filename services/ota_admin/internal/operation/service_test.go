package operation

import (
	"context"
	"errors"
	"testing"

	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

const (
	testSessionID   = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	testCandidateID = "11111111-1111-4111-8111-111111111111"
	testReleaseID   = "22222222-2222-4222-8222-222222222222"
	testOperationID = "33333333-3333-4333-8333-333333333333"
)

type fakePublisher struct {
	publishResult               deploy.PublishResult
	rollbackResult              deploy.RollbackResult
	publishErr, rollbackErr     error
	publishCalls, rollbackCalls int
}

func (publisher *fakePublisher) PublishForActiveSession(context.Context, string, string, string) (deploy.PublishResult, error) {
	publisher.publishCalls++
	return publisher.publishResult, publisher.publishErr
}

func (publisher *fakePublisher) RollbackForActiveSession(context.Context, string, string, string) (deploy.RollbackResult, error) {
	publisher.rollbackCalls++
	return publisher.rollbackResult, publisher.rollbackErr
}

type fakePasswordVerifier struct {
	err   error
	calls int
}

func (verifier *fakePasswordVerifier) ConfirmPassword(context.Context, session.Principal, string) error {
	verifier.calls++
	return verifier.err
}

type fakeRetainer struct {
	calls int
	err   error
}

func (retainer *fakeRetainer) Trigger() { retainer.calls++ }

type fakeHistory struct {
	items []store.ReleaseHistoryItem
	limit int
}

func (history *fakeHistory) ReleaseHistory(_ context.Context, limit int) ([]store.ReleaseHistoryItem, error) {
	history.limit = limit
	return history.items, nil
}

func TestRollbackRequiresCurrentPasswordBeforeCallingPublisher(t *testing.T) {
	publisher := &fakePublisher{}
	passwords := &fakePasswordVerifier{err: session.ErrPassword}
	service, err := NewService(Options{Publisher: publisher, Passwords: passwords, History: &fakeHistory{}, Retention: &fakeRetainer{}})
	if err != nil {
		t.Fatal(err)
	}
	_, err = service.RollbackForActiveSession(context.Background(), session.Principal{IDHash: testSessionID}, testReleaseID, testOperationID, "wrong")
	if !errors.Is(err, session.ErrPassword) || passwords.calls != 1 || publisher.rollbackCalls != 0 {
		t.Fatalf("err=%v password calls=%d rollback calls=%d", err, passwords.calls, publisher.rollbackCalls)
	}
}

func TestSuccessfulPublishOnlyEnqueuesBackgroundRetention(t *testing.T) {
	want := deploy.PublishResult{OperationID: testOperationID, CandidateID: testCandidateID, ReleaseID: testOperationID, Version: "1.0.4", State: "succeeded"}
	publisher := &fakePublisher{publishResult: want}
	retention := &fakeRetainer{err: errors.New("cleanup failed")}
	service, err := NewService(Options{Publisher: publisher, Passwords: &fakePasswordVerifier{}, History: &fakeHistory{}, Retention: retention})
	if err != nil {
		t.Fatal(err)
	}
	got, err := service.PublishForActiveSession(context.Background(), testSessionID, testCandidateID, testOperationID)
	if err != nil || got != want || retention.calls != 1 {
		t.Fatalf("got=%+v err=%v retention calls=%d", got, err, retention.calls)
	}
}

func TestHistoryIsCappedAtCurrentPlusTwoPrevious(t *testing.T) {
	history := &fakeHistory{items: []store.ReleaseHistoryItem{{Release: store.Release{ID: testReleaseID}, State: "current"}}}
	service, err := NewService(Options{Publisher: &fakePublisher{}, Passwords: &fakePasswordVerifier{}, History: history, Retention: &fakeRetainer{}})
	if err != nil {
		t.Fatal(err)
	}
	items, err := service.ReleaseHistory(context.Background())
	if err != nil || len(items) != 1 || history.limit != 3 {
		t.Fatalf("items=%+v limit=%d err=%v", items, history.limit, err)
	}
}
