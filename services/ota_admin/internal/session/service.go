package session

import (
	"container/list"
	"context"
	"crypto/rand"
	"errors"
	"io"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/audit"
	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

var (
	ErrUnauthorized     = errors.New("authentication failed")
	ErrForbidden        = errors.New("request rejected")
	ErrLocked           = errors.New("authentication temporarily unavailable")
	ErrPassword         = errors.New("password rejected")
	ErrBootstrapLimited = errors.New("preauthorization temporarily unavailable")
)

const (
	maximumPreauthorizations          = 4096
	maximumPreauthorizationsPerSource = 8
	maximumActiveSessions             = 32
)

type Options struct {
	Now            func() time.Time
	Random         io.Reader
	SourceHMACKey  []byte
	PreauthTTL     time.Duration
	IdleTimeout    time.Duration
	AbsoluteExpiry time.Duration
	FailureLimit   int
	FailureWindow  time.Duration
	FailureLock    time.Duration
}

type Service struct {
	store            *store.Store
	now              func() time.Time
	random           io.Reader
	sourceHMACKey    []byte
	preauthTTL       time.Duration
	idleTimeout      time.Duration
	absoluteExpiry   time.Duration
	limiter          *security.RateLimiter
	invalidLimiter   *security.RateLimiter
	bootstrapLimiter *security.RateLimiter
	loginMu          sync.Mutex

	mu              sync.Mutex
	preauth         map[string]*preauthorization
	preauthOrder    *list.List
	preauthBySource map[string]int
	sessions        map[string]runtimeSession
}

type preauthorization struct {
	csrfHash string
	expires  time.Time
	source   string
	element  *list.Element
}

type runtimeSession struct {
	csrfHash  string
	createdAt time.Time
}

type LoginGrant struct{ state *loginGrantState }

type loginGrantState struct {
	mu    sync.Mutex
	valid bool
	used  bool
}

type LoginResult struct {
	Token              string
	CSRFToken          string
	ExpiresAt          time.Time
	CookieMaxAge       time.Duration
	MustChangePassword bool
}

type Principal struct {
	IDHash             string
	MustChangePassword bool
}

func NewService(database *store.Store, options Options) (*Service, error) {
	if database == nil {
		return nil, errors.New("session store is required")
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	if options.Random == nil {
		options.Random = rand.Reader
	}
	if len(options.SourceHMACKey) < 32 {
		return nil, errors.New("source HMAC key must contain at least 32 bytes")
	}
	if options.PreauthTTL <= 0 || options.IdleTimeout <= 0 || options.AbsoluteExpiry <= 0 || options.IdleTimeout > options.AbsoluteExpiry || options.FailureLimit <= 0 || options.FailureWindow <= 0 || options.FailureLock <= 0 {
		return nil, errors.New("session limits are invalid")
	}
	if err := database.RevokeAllActiveSessions(context.Background()); err != nil {
		return nil, err
	}
	if err := database.ExpireAndPruneSessions(context.Background(), options.Now().UTC()); err != nil {
		return nil, err
	}
	return &Service{
		store:            database,
		now:              options.Now,
		random:           options.Random,
		sourceHMACKey:    append([]byte(nil), options.SourceHMACKey...),
		preauthTTL:       options.PreauthTTL,
		idleTimeout:      options.IdleTimeout,
		absoluteExpiry:   options.AbsoluteExpiry,
		limiter:          security.NewRateLimiter(options.FailureLimit, options.FailureWindow, options.FailureLock),
		invalidLimiter:   security.NewRateLimiter(options.FailureLimit, options.FailureWindow, options.FailureLock),
		bootstrapLimiter: security.NewRateLimiter(maximumPreauthorizationsPerSource, options.PreauthTTL, options.PreauthTTL),
		preauth:          make(map[string]*preauthorization),
		preauthOrder:     list.New(),
		preauthBySource:  make(map[string]int),
		sessions:         make(map[string]runtimeSession),
	}, nil
}

func (service *Service) Bootstrap(sourceAddress string) (token, csrfToken string, expiresAt time.Time, maxAge time.Duration, err error) {
	source, err := audit.PseudonymizeSource(service.sourceHMACKey, sourceAddress)
	if err != nil {
		return "", "", time.Time{}, 0, err
	}
	service.loginMu.Lock()
	defer service.loginMu.Unlock()
	now := service.now().UTC()
	expiresAt = now.Add(service.preauthTTL)
	sourceKey := source.String()
	if locked, remaining := service.limiter.Locked(store.AdministratorUsername, sourceKey, now); locked {
		return "", "", time.Time{}, remaining, ErrBootstrapLimited
	}
	if locked, remaining := service.bootstrapLimiter.LockedKey(sourceKey, now); locked {
		return "", "", time.Time{}, remaining, ErrBootstrapLimited
	}
	service.mu.Lock()
	defer service.mu.Unlock()
	service.deleteExpiredPreauthLocked(now)
	if len(service.preauth) >= maximumPreauthorizations || service.preauthBySource[sourceKey] >= maximumPreauthorizationsPerSource {
		return "", "", time.Time{}, service.preauthTTL, ErrBootstrapLimited
	}
	if !service.bootstrapLimiter.RecordFailureKey(sourceKey, now) {
		return "", "", time.Time{}, service.preauthTTL, ErrBootstrapLimited
	}
	token, err = security.NewRandomToken(service.random)
	if err != nil {
		return "", "", time.Time{}, 0, err
	}
	csrfToken, err = security.NewRandomToken(service.random)
	if err != nil {
		return "", "", time.Time{}, 0, err
	}
	idHash := security.HashToken(token)
	if _, collision := service.preauth[idHash]; collision {
		return "", "", time.Time{}, 0, errors.New("preauthorization token collision")
	}
	record := &preauthorization{csrfHash: security.HashToken(csrfToken), expires: expiresAt, source: sourceKey}
	record.element = service.preauthOrder.PushBack(idHash)
	service.preauth[idHash] = record
	service.preauthBySource[sourceKey]++
	return token, csrfToken, expiresAt, service.preauthTTL, nil
}

// ConsumePreauthorization removes the server-side record before validating it.
// This makes every presented pre-auth token one-use, including failed attempts.
func (service *Service) ConsumePreauthorization(token, csrfToken string) LoginGrant {
	now := service.now().UTC()
	idHash := security.HashToken(token)
	service.mu.Lock()
	record, found := service.preauth[idHash]
	if found {
		service.removePreauthorizationLocked(idHash, record)
	}
	service.deleteExpiredPreauthLocked(now)
	service.mu.Unlock()
	valid := found && now.Before(record.expires) && security.TokenMatchesHash(csrfToken, record.csrfHash)
	return LoginGrant{state: &loginGrantState{valid: valid}}
}

func (service *Service) Login(ctx context.Context, grant LoginGrant, username, password, sourceAddress string) (LoginResult, time.Duration, error) {
	source, err := audit.PseudonymizeSource(service.sourceHMACKey, sourceAddress)
	if err != nil {
		return LoginResult{}, 0, err
	}
	service.loginMu.Lock()
	defer service.loginMu.Unlock()
	now := service.now().UTC()
	if !grant.claim() {
		return service.rejectInvalidGrant(ctx, source, now)
	}
	if locked, remaining := service.limiter.Locked(store.AdministratorUsername, source.String(), now); locked {
		return LoginResult{}, remaining, ErrLocked
	}
	administrator, adminErr := service.store.Admin(ctx)
	if adminErr != nil {
		return LoginResult{}, 0, adminErr
	}
	validUsername := constantUsernameMatch(username, administrator.Username)
	validPassword := security.VerifyPassword(password, administrator.PasswordHash)
	validCredentials := validUsername && validPassword
	if !validCredentials {
		service.limiter.RecordFailure(store.AdministratorUsername, source.String(), now)
		locked, _ := service.limiter.Locked(store.AdministratorUsername, source.String(), now)
		code := audit.CodeLoginRejected
		if locked {
			code = audit.CodeLoginLocked
		}
		if err := service.recordLogin(ctx, audit.OutcomeFailure, source, code, now); err != nil {
			return LoginResult{}, 0, err
		}
		return LoginResult{}, 0, ErrUnauthorized
	}
	service.limiter.RecordSuccess(store.AdministratorUsername, source.String())
	result, err := service.issueSession(ctx, administrator.MustChangePassword, now)
	if err != nil {
		return LoginResult{}, 0, err
	}
	if err := service.recordLogin(ctx, audit.OutcomeSuccess, source, audit.CodeLoginAccepted, now); err != nil {
		_ = service.store.TransitionSession(ctx, security.HashToken(result.Token), store.SessionRevoked)
		service.forgetSession(security.HashToken(result.Token))
		_ = service.store.ExpireAndPruneSessions(ctx, now)
		return LoginResult{}, 0, err
	}
	return result, 0, nil
}

func (service *Service) rejectInvalidGrant(ctx context.Context, source audit.SourcePseudonym, now time.Time) (LoginResult, time.Duration, error) {
	sourceKey := source.String()
	if locked, remaining := service.invalidLimiter.LockedKey(sourceKey, now); locked {
		return LoginResult{}, remaining, ErrLocked
	}
	if !service.invalidLimiter.RecordFailureKey(sourceKey, now) {
		return LoginResult{}, service.preauthTTL, ErrLocked
	}
	locked, remaining := service.invalidLimiter.LockedKey(sourceKey, now)
	code := audit.CodeLoginRejected
	if locked {
		code = audit.CodeLoginLocked
	}
	if err := service.recordLogin(ctx, audit.OutcomeFailure, source, code, now); err != nil {
		return LoginResult{}, 0, err
	}
	if locked {
		return LoginResult{}, remaining, ErrLocked
	}
	return LoginResult{}, 0, ErrUnauthorized
}

func (grant LoginGrant) claim() bool {
	if grant.state == nil {
		return false
	}
	grant.state.mu.Lock()
	defer grant.state.mu.Unlock()
	if grant.state.used {
		return false
	}
	grant.state.used = true
	return grant.state.valid
}

func (service *Service) Authenticate(ctx context.Context, token, csrfToken string, mutating bool) (Principal, error) {
	if token == "" {
		return Principal{}, ErrUnauthorized
	}
	idHash := security.HashToken(token)
	service.mu.Lock()
	runtime, found := service.sessions[idHash]
	service.mu.Unlock()
	if !found {
		return Principal{}, ErrUnauthorized
	}
	if mutating && !security.TokenMatchesHash(csrfToken, runtime.csrfHash) {
		return Principal{}, ErrForbidden
	}
	persisted, err := service.store.Session(ctx, idHash)
	if errors.Is(err, store.ErrNotFound) {
		service.forgetSession(idHash)
		return Principal{}, ErrUnauthorized
	}
	if err != nil {
		return Principal{}, err
	}
	if persisted.State != store.SessionActive {
		service.forgetSession(idHash)
		_ = service.store.ExpireAndPruneSessions(ctx, service.now().UTC())
		return Principal{}, ErrUnauthorized
	}
	now := service.now().UTC()
	if !now.Before(persisted.IdleExpiresAt) || !now.Before(persisted.AbsoluteExpiresAt) {
		_ = service.store.TransitionSession(ctx, idHash, store.SessionExpired)
		service.forgetSession(idHash)
		_ = service.store.ExpireAndPruneSessions(ctx, now)
		return Principal{}, ErrUnauthorized
	}
	idleExpiresAt := now.Add(service.idleTimeout)
	if idleExpiresAt.After(persisted.AbsoluteExpiresAt) {
		idleExpiresAt = persisted.AbsoluteExpiresAt
	}
	if err := service.store.TouchSession(ctx, idHash, now, idleExpiresAt); err != nil {
		service.forgetSession(idHash)
		if errors.Is(err, store.ErrNotFound) || errors.Is(err, store.ErrInvalidTransition) {
			return Principal{}, ErrUnauthorized
		}
		return Principal{}, err
	}
	administrator, err := service.store.Admin(ctx)
	if err != nil {
		return Principal{}, err
	}
	return Principal{IDHash: idHash, MustChangePassword: administrator.MustChangePassword}, nil
}

// RotateCSRF issues the authenticated browser a fresh mutation token after a
// page reload. The session cookie remains HttpOnly; only the CSRF secret is
// returned to same-origin script by the authenticated current-session API.
func (service *Service) RotateCSRF(principal Principal) (string, error) {
	service.loginMu.Lock()
	defer service.loginMu.Unlock()
	token, err := security.NewRandomToken(service.random)
	if err != nil {
		return "", err
	}
	service.mu.Lock()
	defer service.mu.Unlock()
	runtime, found := service.sessions[principal.IDHash]
	if !found {
		return "", ErrUnauthorized
	}
	runtime.csrfHash = security.HashToken(token)
	service.sessions[principal.IDHash] = runtime
	return token, nil
}

func (service *Service) Logout(ctx context.Context, principal Principal) error {
	err := service.store.TransitionSession(ctx, principal.IDHash, store.SessionRevoked)
	service.forgetSession(principal.IDHash)
	_ = service.store.ExpireAndPruneSessions(ctx, service.now().UTC())
	if errors.Is(err, store.ErrNotFound) || errors.Is(err, store.ErrInvalidTransition) {
		return ErrUnauthorized
	}
	return err
}

func (service *Service) ChangePassword(ctx context.Context, principal Principal, currentPassword, newPassword string) (LoginResult, error) {
	service.loginMu.Lock()
	defer service.loginMu.Unlock()
	administrator, err := service.store.Admin(ctx)
	if err != nil {
		return LoginResult{}, err
	}
	if !security.VerifyPassword(currentPassword, administrator.PasswordHash) || currentPassword == newPassword {
		return LoginResult{}, ErrPassword
	}
	newHash, err := security.HashPassword(newPassword, service.random)
	if err != nil {
		return LoginResult{}, ErrPassword
	}
	now := service.now().UTC()
	token, csrfToken, persisted, err := service.newSessionValues(now)
	if err != nil {
		return LoginResult{}, err
	}
	if err := service.store.ChangePasswordAndRotateSession(ctx, newHash, principal.IDHash, persisted); err != nil {
		return LoginResult{}, err
	}
	service.mu.Lock()
	service.sessions = map[string]runtimeSession{persisted.IDHash: {csrfHash: security.HashToken(csrfToken), createdAt: persisted.CreatedAt}}
	service.mu.Unlock()
	_ = service.store.ExpireAndPruneSessions(ctx, now)
	return LoginResult{Token: token, CSRFToken: csrfToken, ExpiresAt: persisted.AbsoluteExpiresAt, CookieMaxAge: service.absoluteExpiry, MustChangePassword: false}, nil
}

func (service *Service) ConfirmPassword(ctx context.Context, principal Principal, password string) error {
	service.loginMu.Lock()
	defer service.loginMu.Unlock()
	if password == "" || len(password) > security.MaximumPasswordBytes || principal.IDHash == "" || principal.MustChangePassword {
		return ErrPassword
	}
	administrator, err := service.store.Admin(ctx)
	if err != nil {
		return err
	}
	if !security.VerifyPassword(password, administrator.PasswordHash) {
		return ErrPassword
	}
	return service.store.RequireActiveSession(ctx, principal.IDHash, service.now().UTC())
}

func (service *Service) issueSession(ctx context.Context, mustChange bool, now time.Time) (LoginResult, error) {
	if err := service.cleanupSessions(ctx, now); err != nil {
		return LoginResult{}, err
	}
	if err := service.makeSessionCapacity(ctx, now); err != nil {
		return LoginResult{}, err
	}
	token, csrfToken, persisted, err := service.newSessionValues(now)
	if err != nil {
		return LoginResult{}, err
	}
	if err := service.store.CreateSession(ctx, persisted); err != nil {
		return LoginResult{}, err
	}
	service.mu.Lock()
	service.sessions[persisted.IDHash] = runtimeSession{csrfHash: security.HashToken(csrfToken), createdAt: persisted.CreatedAt}
	service.mu.Unlock()
	return LoginResult{Token: token, CSRFToken: csrfToken, ExpiresAt: persisted.AbsoluteExpiresAt, CookieMaxAge: service.absoluteExpiry, MustChangePassword: mustChange}, nil
}

func (service *Service) cleanupSessions(ctx context.Context, now time.Time) error {
	service.mu.Lock()
	identifiers := make([]string, 0, len(service.sessions))
	for idHash := range service.sessions {
		identifiers = append(identifiers, idHash)
	}
	service.mu.Unlock()
	for _, idHash := range identifiers {
		persisted, err := service.store.Session(ctx, idHash)
		if errors.Is(err, store.ErrNotFound) {
			service.forgetSession(idHash)
			continue
		}
		if err != nil {
			return err
		}
		if persisted.State != store.SessionActive {
			service.forgetSession(idHash)
			continue
		}
		if !now.Before(persisted.IdleExpiresAt) || !now.Before(persisted.AbsoluteExpiresAt) {
			if err := service.store.TransitionSession(ctx, idHash, store.SessionExpired); err != nil && !errors.Is(err, store.ErrInvalidTransition) && !errors.Is(err, store.ErrNotFound) {
				return err
			}
			service.forgetSession(idHash)
		}
	}
	return service.store.ExpireAndPruneSessions(ctx, now)
}

func (service *Service) makeSessionCapacity(ctx context.Context, now time.Time) error {
	for {
		service.mu.Lock()
		if len(service.sessions) < maximumActiveSessions {
			service.mu.Unlock()
			return nil
		}
		var oldestID string
		var oldestAt time.Time
		for idHash, runtime := range service.sessions {
			if oldestID == "" || runtime.createdAt.Before(oldestAt) || (runtime.createdAt.Equal(oldestAt) && idHash < oldestID) {
				oldestID = idHash
				oldestAt = runtime.createdAt
			}
		}
		service.mu.Unlock()
		if err := service.store.TransitionSession(ctx, oldestID, store.SessionRevoked); err != nil && !errors.Is(err, store.ErrInvalidTransition) && !errors.Is(err, store.ErrNotFound) {
			return err
		}
		service.forgetSession(oldestID)
		if err := service.store.ExpireAndPruneSessions(ctx, now); err != nil {
			return err
		}
	}
}

func (service *Service) newSessionValues(now time.Time) (string, string, store.Session, error) {
	token, err := security.NewRandomToken(service.random)
	if err != nil {
		return "", "", store.Session{}, err
	}
	csrfToken, err := security.NewRandomToken(service.random)
	if err != nil {
		return "", "", store.Session{}, err
	}
	absolute := now.Add(service.absoluteExpiry)
	idle := now.Add(service.idleTimeout)
	if idle.After(absolute) {
		idle = absolute
	}
	persisted := store.Session{
		IDHash:            security.HashToken(token),
		State:             store.SessionActive,
		CreatedAt:         now,
		LastSeenAt:        now,
		IdleExpiresAt:     idle,
		AbsoluteExpiresAt: absolute,
	}
	return token, csrfToken, persisted, nil
}

func (service *Service) recordLogin(ctx context.Context, outcome audit.Outcome, source audit.SourcePseudonym, code audit.Code, at time.Time) error {
	event, err := audit.NewLoginResult(outcome, source, code, at)
	if err != nil {
		return err
	}
	return service.store.RecordAudit(ctx, event)
}

func (service *Service) forgetSession(idHash string) {
	service.mu.Lock()
	delete(service.sessions, idHash)
	service.mu.Unlock()
}

func (service *Service) deleteExpiredPreauthLocked(now time.Time) {
	for element := service.preauthOrder.Front(); element != nil; {
		idHash, _ := element.Value.(string)
		record := service.preauth[idHash]
		if record == nil {
			next := element.Next()
			service.preauthOrder.Remove(element)
			element = next
			continue
		}
		if now.Before(record.expires) {
			return
		}
		next := element.Next()
		service.removePreauthorizationLocked(idHash, record)
		element = next
	}
}

func (service *Service) removePreauthorizationLocked(idHash string, record *preauthorization) {
	delete(service.preauth, idHash)
	if record.element != nil {
		service.preauthOrder.Remove(record.element)
		record.element = nil
	}
	service.preauthBySource[record.source]--
	if service.preauthBySource[record.source] <= 0 {
		delete(service.preauthBySource, record.source)
	}
}

func constantUsernameMatch(left, right string) bool {
	return security.ConstantTimeStringEqual(left, right)
}
