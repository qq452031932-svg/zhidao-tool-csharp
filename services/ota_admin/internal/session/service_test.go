package session

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"errors"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

type oneShotGateReader struct {
	mu      sync.Mutex
	started bool
	entered chan struct{}
	release chan struct{}
}

func newOneShotGateReader() *oneShotGateReader {
	return &oneShotGateReader{entered: make(chan struct{}), release: make(chan struct{})}
}

func (reader *oneShotGateReader) Read(buffer []byte) (int, error) {
	reader.mu.Lock()
	first := !reader.started
	reader.started = true
	reader.mu.Unlock()
	if first {
		close(reader.entered)
		<-reader.release
	}
	return rand.Read(buffer)
}

func TestFormalSessionPersistsOnlyTokenHashAndHonorsStoreRevocation(t *testing.T) {
	database, err := store.Open(context.Background(), filepath.Join(privateStateTestDir(t), "state.db"))
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
	if _, err := database.Session(context.Background(), idHash); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("externally revoked database session was not pruned: %v", err)
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
	var firstSource string
	for index := range maximumPreauthorizations {
		source := "203.0." + string(rune(index/256)) + "." + string(rune(index%256))
		token, csrf, _, _, err := service.Bootstrap(source)
		if err != nil {
			t.Fatalf("bootstrap %d error=%v", index, err)
		}
		if index == 0 {
			firstToken, firstCSRF = token, csrf
			firstSource = source
		}
	}
	if _, _, _, retryAfter, err := service.Bootstrap("198.51.100.1"); !errors.Is(err, ErrBootstrapLimited) || retryAfter != 5*time.Minute {
		t.Fatalf("capacity overflow error=%v retry-after=%v", err, retryAfter)
	}
	service.ConsumePreauthorization(firstToken, firstCSRF)
	if _, _, _, _, err := service.Bootstrap(firstSource); err != nil {
		t.Fatalf("bootstrap after consumption error=%v", err)
	}
	if len(service.preauth) != maximumPreauthorizations || service.preauthOrder.Len() != maximumPreauthorizations {
		t.Fatalf("preauthorization bounds map=%d queue=%d", len(service.preauth), service.preauthOrder.Len())
	}
}

func TestBootstrapLimiterCapacityReturnsConservativeRetryDelay(t *testing.T) {
	_, service, _ := newTestService(t, 5)
	now := time.Unix(2_000_000_000, 0).UTC()
	for index := range maximumPreauthorizations {
		if !service.bootstrapLimiter.RecordFailureKey("filler-"+string(rune(index)), now) {
			t.Fatalf("limiter filled early at %d", index)
		}
	}
	if _, _, _, retryAfter, err := service.Bootstrap("198.51.100.2"); !errors.Is(err, ErrBootstrapLimited) || retryAfter != 5*time.Minute {
		t.Fatalf("limiter capacity error=%v retry-after=%v", err, retryAfter)
	}
}

func TestPasswordRotationSerializesAgainstOldPasswordLogin(t *testing.T) {
	_, service, password := newTestService(t, 5)
	preauth, csrf, _, _, err := service.Bootstrap("203.0.113.10")
	if err != nil {
		t.Fatal(err)
	}
	first, _, err := service.Login(context.Background(), service.ConsumePreauthorization(preauth, csrf), "admin", password, "203.0.113.10")
	if err != nil {
		t.Fatal(err)
	}
	principal, err := service.Authenticate(context.Background(), first.Token, "", false)
	if err != nil {
		t.Fatal(err)
	}
	preauth, csrf, _, _, err = service.Bootstrap("203.0.113.11")
	if err != nil {
		t.Fatal(err)
	}
	oldPasswordGrant := service.ConsumePreauthorization(preauth, csrf)

	gate := newOneShotGateReader()
	service.random = gate
	changeDone := make(chan error, 1)
	go func() {
		_, err := service.ChangePassword(context.Background(), principal, password, "a replacement administrator password")
		changeDone <- err
	}()
	<-gate.entered
	if service.loginMu.TryLock() {
		service.loginMu.Unlock()
		close(gate.release)
		t.Fatal("password rotation did not hold the login serialization lock")
	}
	loginDone := make(chan error, 1)
	go func() {
		_, _, err := service.Login(context.Background(), oldPasswordGrant, "admin", password, "203.0.113.11")
		loginDone <- err
	}()
	close(gate.release)
	if err := <-changeDone; err != nil {
		t.Fatalf("password change error=%v", err)
	}
	if err := <-loginDone; !errors.Is(err, ErrUnauthorized) {
		t.Fatalf("old-password login after rotation error=%v", err)
	}
}

func TestConfirmPasswordRequiresCurrentHashAndStillActiveSession(t *testing.T) {
	database, service, password := newTestService(t, 5)
	preauth, csrf, _, _, err := service.Bootstrap("203.0.113.12")
	if err != nil {
		t.Fatal(err)
	}
	login, _, err := service.Login(context.Background(), service.ConsumePreauthorization(preauth, csrf), "admin", password, "203.0.113.12")
	if err != nil {
		t.Fatal(err)
	}
	principal, err := service.Authenticate(context.Background(), login.Token, login.CSRFToken, false)
	if err != nil {
		t.Fatal(err)
	}
	const replacement = "a replacement administrator password"
	rotated, err := service.ChangePassword(context.Background(), principal, password, replacement)
	if err != nil {
		t.Fatal(err)
	}
	principal, err = service.Authenticate(context.Background(), rotated.Token, rotated.CSRFToken, false)
	if err != nil {
		t.Fatal(err)
	}
	if err := service.ConfirmPassword(context.Background(), principal, "wrong"); !errors.Is(err, ErrPassword) {
		t.Fatalf("wrong password err=%v", err)
	}
	if err := service.ConfirmPassword(context.Background(), principal, replacement); err != nil {
		t.Fatalf("correct password err=%v", err)
	}
	if err := database.TransitionSession(context.Background(), principal.IDHash, store.SessionRevoked); err != nil {
		t.Fatal(err)
	}
	if err := service.ConfirmPassword(context.Background(), principal, replacement); !errors.Is(err, store.ErrSessionInactive) {
		t.Fatalf("revoked session err=%v", err)
	}
}

func TestInvalidPreauthorizationIsSourceLimitedAndAuditCoalesced(t *testing.T) {
	database, service, observer, password := newObservedTestService(t, 5)
	preauth, csrf, _, _, err := service.Bootstrap("203.0.113.20")
	if err != nil {
		t.Fatal(err)
	}
	goodGrant := service.ConsumePreauthorization(preauth, csrf)
	for range 1_000 {
		_, _, err := service.Login(context.Background(), LoginGrant{}, "admin", "wrong", "203.0.113.20")
		if !errors.Is(err, ErrUnauthorized) && !errors.Is(err, ErrLocked) {
			t.Fatalf("invalid grant error=%v", err)
		}
	}
	if got := auditCount(t, observer); got > 5 {
		t.Fatalf("invalid grants appended %d audit rows, want at most 5", got)
	}
	if _, _, err := service.Login(context.Background(), goodGrant, "admin", password, "203.0.113.20"); err != nil {
		t.Fatalf("fabricated grants affected valid administrator login: %v", err)
	}
	_ = database
}

func TestInvalidGrantLimiterCapacityFailsClosedWithoutAuditChurn(t *testing.T) {
	_, service, observer, _ := newObservedTestService(t, 5)
	now := time.Unix(2_000_000_000, 0).UTC()
	for index := range 3_000 {
		key := "filler-" + string(rune(index))
		service.invalidLimiter.RecordFailure(key, key, now)
	}
	for range 100 {
		_, _, _ = service.Login(context.Background(), LoginGrant{}, "admin", "wrong", "overflow-source")
	}
	if got := auditCount(t, observer); got != 0 {
		t.Fatalf("untrackable invalid grants appended %d audit rows", got)
	}
}

func TestLockedSourceCannotChurnBootstrapOrDurableAudit(t *testing.T) {
	_, service, observer, _ := newObservedTestService(t, 1)
	grants := make([]LoginGrant, 8)
	for index := range grants {
		preauth, csrf, _, _, err := service.Bootstrap("203.0.113.30")
		if err != nil {
			t.Fatal(err)
		}
		grants[index] = service.ConsumePreauthorization(preauth, csrf)
	}
	if _, _, err := service.Login(context.Background(), grants[0], "admin", "wrong", "203.0.113.30"); !errors.Is(err, ErrUnauthorized) && !errors.Is(err, ErrLocked) {
		t.Fatalf("initial failure error=%v", err)
	}
	for _, grant := range grants[1:] {
		_, _, _ = service.Login(context.Background(), grant, "admin", "wrong", "203.0.113.30")
	}
	for range 1_000 {
		_, _, _ = service.Login(context.Background(), LoginGrant{}, "admin", "wrong", "203.0.113.30")
	}
	if _, _, _, _, err := service.Bootstrap("203.0.113.30"); !errors.Is(err, ErrBootstrapLimited) {
		t.Fatalf("bootstrap while locked error=%v", err)
	}
	if got := auditCount(t, observer); got > 2 {
		t.Fatalf("locked churn appended %d audit rows, want at most 2", got)
	}
}

func TestAdministratorReadFailureIsNotCountedAsBadCredentials(t *testing.T) {
	path := filepath.Join(privateStateTestDir(t), "state.db")
	database, err := store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	service := newServiceForTest(t, database, 1)
	preauth, csrf, _, _, err := service.Bootstrap("203.0.113.40")
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.Login(context.Background(), service.ConsumePreauthorization(preauth, csrf), "admin", "anything", "203.0.113.40"); errors.Is(err, ErrUnauthorized) || errors.Is(err, ErrLocked) || err == nil {
		t.Fatalf("administrator read error was normalized as credentials: %v", err)
	}
	password := "a long enough administrator password"
	hash, err := security.HashPassword(password, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.InitializeAdmin(context.Background(), hash); err != nil {
		t.Fatal(err)
	}
	preauth, csrf, _, _, err = service.Bootstrap("203.0.113.40")
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.Login(context.Background(), service.ConsumePreauthorization(preauth, csrf), "admin", password, "203.0.113.40"); err != nil {
		t.Fatalf("store recovery remained rate-limited: %v", err)
	}
}

func TestSessionIssuanceBoundsRuntimeAndPrunesInactiveDatabaseRows(t *testing.T) {
	database, service, _ := newTestService(t, 5)
	now := time.Unix(2_000_000_000, 0).UTC()
	results := make([]LoginResult, 33)
	for index := range results {
		result, err := service.issueSession(context.Background(), false, now.Add(time.Duration(index)*time.Second))
		if err != nil {
			t.Fatal(err)
		}
		results[index] = result
	}
	if len(service.sessions) > 32 {
		t.Fatalf("runtime sessions=%d, want at most 32", len(service.sessions))
	}
	if _, err := database.Session(context.Background(), security.HashToken(results[0].Token)); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("oldest database session was not pruned: %v", err)
	}

	revokedHash := security.HashToken(results[1].Token)
	if err := database.TransitionSession(context.Background(), revokedHash, store.SessionRevoked); err != nil {
		t.Fatal(err)
	}
	if _, err := service.issueSession(context.Background(), false, now.Add(time.Minute)); err != nil {
		t.Fatal(err)
	}
	if _, present := service.sessions[revokedHash]; present {
		t.Fatal("revoked session remained in runtime state")
	}
	if _, err := database.Session(context.Background(), revokedHash); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("revoked database session was not pruned: %v", err)
	}

	expired := results[2]
	if _, err := service.issueSession(context.Background(), false, now.Add(9*time.Hour)); err != nil {
		t.Fatal(err)
	}
	if _, present := service.sessions[security.HashToken(expired.Token)]; present {
		t.Fatal("expired session remained in runtime state")
	}
	if _, err := database.Session(context.Background(), security.HashToken(expired.Token)); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("expired database session was not pruned: %v", err)
	}
}

func newObservedTestService(t *testing.T, failureLimit int) (*store.Store, *Service, *sql.DB, string) {
	t.Helper()
	path := filepath.Join(privateStateTestDir(t), "state.db")
	database, err := store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	password := "a long enough administrator password"
	hash, err := security.HashPassword(password, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	if err := database.InitializeAdmin(context.Background(), hash); err != nil {
		t.Fatal(err)
	}
	observer, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = observer.Close() })
	return database, newServiceForTest(t, database, failureLimit), observer, password
}

func auditCount(t *testing.T, observer *sql.DB) int {
	t.Helper()
	var count int
	if err := observer.QueryRow(`SELECT count(*) FROM audit_events`).Scan(&count); err != nil {
		t.Fatal(err)
	}
	return count
}

func newTestService(t *testing.T, failureLimit int) (*store.Store, *Service, string) {
	t.Helper()
	database, err := store.Open(context.Background(), filepath.Join(privateStateTestDir(t), "state.db"))
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
	return database, newServiceForTest(t, database, failureLimit), password
}

func privateStateTestDir(t *testing.T) string {
	t.Helper()
	root := t.TempDir()
	if err := os.Chmod(root, 0o700); err != nil {
		t.Fatalf("make state test directory private: %v", err)
	}
	return root
}

func newServiceForTest(t *testing.T, database *store.Store, failureLimit int) *Service {
	t.Helper()
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
	return service
}
