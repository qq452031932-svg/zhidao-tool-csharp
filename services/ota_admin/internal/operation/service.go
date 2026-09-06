package operation

import (
	"context"
	"errors"

	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

type Publisher interface {
	PublishForActiveSession(context.Context, string, string, string) (deploy.PublishResult, error)
	RollbackForActiveSession(context.Context, string, string, string) (deploy.RollbackResult, error)
}

type PasswordVerifier interface {
	ConfirmPassword(context.Context, session.Principal, string) error
}

type HistoryStore interface {
	ReleaseHistory(context.Context, int) ([]store.ReleaseHistoryItem, error)
}

type Retainer interface {
	Trigger()
}

type Options struct {
	Publisher Publisher
	Passwords PasswordVerifier
	History   HistoryStore
	Retention Retainer
}

type Service struct {
	publisher Publisher
	passwords PasswordVerifier
	history   HistoryStore
	retention Retainer
}

func NewService(options Options) (*Service, error) {
	if options.Publisher == nil || options.Passwords == nil || options.History == nil || options.Retention == nil {
		return nil, errors.New("release operation dependencies are required")
	}
	return &Service{publisher: options.Publisher, passwords: options.Passwords, history: options.History, retention: options.Retention}, nil
}

func (service *Service) PublishForActiveSession(ctx context.Context, sessionID, candidateID, operationID string) (deploy.PublishResult, error) {
	result, err := service.publisher.PublishForActiveSession(ctx, sessionID, candidateID, operationID)
	if err != nil {
		return deploy.PublishResult{}, err
	}
	// Trigger is a non-blocking enqueue. Retention has its own durable operation
	// and audit record, and runs on its startup/periodic background worker.
	service.retention.Trigger()
	return result, nil
}

func (service *Service) RollbackForActiveSession(ctx context.Context, principal session.Principal, releaseID, operationID, password string) (deploy.RollbackResult, error) {
	if err := service.passwords.ConfirmPassword(ctx, principal, password); err != nil {
		return deploy.RollbackResult{}, err
	}
	return service.publisher.RollbackForActiveSession(ctx, principal.IDHash, releaseID, operationID)
}

func (service *Service) ReleaseHistory(ctx context.Context) ([]store.ReleaseHistoryItem, error) {
	return service.history.ReleaseHistory(ctx, 3)
}

func (service *Service) ReadOnlyError() error {
	if reporter, ok := service.publisher.(interface{ ReadOnlyError() error }); ok {
		return reporter.ReadOnlyError()
	}
	return nil
}
