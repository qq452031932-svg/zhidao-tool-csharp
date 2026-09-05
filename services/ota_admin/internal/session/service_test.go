package session

import (
	"bytes"
	"context"
	"crypto/rand"
	"errors"
	"path/filepath"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func TestFormalSessionPersistsOnlyTokenHashAndHonorsStoreRevocation(t *testing.T) {
	database, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	password := "a long enough administrator password"
	passwordHash, err := security.HashPassword(password, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.InitializeAdmin(context.Background(), passwordHash); err != nil {
		t.Fatal(err)
	}
	now := time.Unix(2_000_000_000, 0).UTC()
	service, err := NewService(database, Options{
		Now:            func() time.Time { return now },
		Random:         rand.Reader,
		SourceHMACKey:  bytes.Repeat([]byte{7}, 32),
		PreauthTTL:     5 * time.Minute,
		IdleTimeout:    30 * time.Minute,
		AbsoluteExpiry: 8 * time.Hour,
		FailureLimit:   5,
		FailureWindow:  15 * time.Minute,
		FailureLock:    15 * time.Minute,
	})
	if err != nil {
		t.Fatal(err)
	}
	preauth, csrf, _, _, err := service.Bootstrap("203.0.113.9")
	if err != nil {
		t.Fatal(err)
	}
	result, _, err := service.Login(context.Background(), service.ConsumePreauthorization(preauth, csrf), "admin", password, "203.0.113.9")
	if err != nil {
		t.Fatal(err)
	}
	idHash := security.HashToken(result.Token)
	if _, err := database.Session(context.Background(), idHash); err != nil {
		t.Fatalf("hashed session missing: %v", err)
	}
	if _, err := database.Session(context.Background(), result.Token); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("raw session token was accepted as a persisted identifier: %v", err)
	}
	if err := database.TransitionSession(context.Background(), idHash, store.SessionRevoked); err != nil {
		t.Fatal(err)
	}
	if _, err := service.Authenticate(context.Background(), result.Token, "", false); !errors.Is(err, ErrUnauthorized) {
		t.Fatalf("externally revoked session authentication error=%v", err)
	}
}

func TestConsumedLoginGrantCannotBeReused(t *testing.T) {
	database, service, password := newTestService(t, 5)
	preauth, csrf, _, _, err := service.Bootstrap("203.0.113.9")
	if err != nil {
		t.Fatal(err)
	}
	grant := service.ConsumePreauthorization(preauth, csrf)
	if _, _, err := service.Login(context.Background(), grant, "admin", "wrong password", "203.0.113.9"); !errors.Is(err, ErrUnauthorized) {
		t.Fatalf("first login error=%v", err)
	}
	if _, _, err := service.Login(context.Background(), grant, "admin", password, "203.0.113.9"); !errors.Is(err, ErrUnauthorized) {
		t.Fatalf("reused grant error=%v", err)
	}
	_ = database
}

func TestConcurrentFailuresCannotPassLockThreshold(t *testing.T) {
	_, service, _ := newTestService(t, 1)
	grants := make([]LoginGrant, 2)
	for index := range grants {
		preauth, csrf, _, _, err := service.Bootstrap("203.0.113.9")
		if err != nil {
			t.Fatal(err)
		}
		grants[index] = service.ConsumePreauthorization(preauth, csrf)
	}
	start := make(chan struct{})
	errorsFound := make(chan error, len(grants))
	for _, grant := range grants {
		go func(grant LoginGrant) {
			<-start
			_, _, err := service.Login(context.Background(), grant, "admin", "wrong password", "203.0.113.9")
			errorsFound <- err
		}(grant)
	}
	close(start)
	var unauthorized, locked int
	for range grants {
		switch err := <-errorsFound; {
		case errors.Is(err, ErrUnauthorized):
			unauthorized++
		case errors.Is(err, ErrLocked):
			locked++
		default:
			t.Fatalf("unexpected concurrent login error=%v", err)
		}
	}
	if unauthorized != 1 || locked != 1 {
		t.Fatalf("concurrent outcomes unauthorized=%d locked=%d", unauthorized, locked)
	}
}

func TestPreauthorizationStorageHasAHardCapacityAndReleasesConsumedSlots(t *testing.T) {
	_, service, _ := newTestService(t, 5)
	var firstToken, firstCSRF string
	for index := range maximumPreauthorizations {
		token, csrf, _, _, err := service.Bootstrap("203.0." + string(rune(index/256)) + "." + string(rune(index%256)))
		if err != nil {
			t.Fatalf("bootstrap %d error=%v", index, err)
		}
		if index == 0 {
			firstToken, firstCSRF = token, csrf
		}
	}
	if _, _, _, _, err := service.Bootstrap("198.51.100.1"); !errors.Is(err, ErrBootstrapLimited) {
		t.Fatalf("capacity overflow error=%v", err)
	}
	service.ConsumePreauthorization(firstToken, firstCSRF)
	if _, _, _, _, err := service.Bootstrap("198.51.100.1"); err != nil {
		t.Fatalf("bootstrap after consumption error=%v", err)
	}
	if len(service.preauth) != maximumPreauthorizations || service.preauthOrder.Len() != maximumPreauthorizations {
		t.Fatalf("preauthorization bounds map=%d queue=%d", len(service.preauth), service.preauthOrder.Len())
	}
}

func newTestService(t *testing.T, failureLimit int) (*store.Store, *Service, string) {
	t.Helper()
	database, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	password := "a long enough administrator password"
	passwordHash, err := security.HashPassword(password, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.InitializeAdmin(context.Background(), passwordHash); err != nil {
		t.Fatal(err)
	}
	now := time.Unix(2_000_000_000, 0).UTC()
	service, err := NewService(database, Options{
		Now:            func() time.Time { return now },
		Random:         rand.Reader,
		SourceHMACKey:  bytes.Repeat([]byte{7}, 32),
		PreauthTTL:     5 * time.Minute,
		IdleTimeout:    30 * time.Minute,
		AbsoluteExpiry: 8 * time.Hour,
		FailureLimit:   failureLimit,
		FailureWindow:  15 * time.Minute,
		FailureLock:    15 * time.Minute,
	})
	if err != nil {
		t.Fatal(err)
	}
	return database, service, password
}
