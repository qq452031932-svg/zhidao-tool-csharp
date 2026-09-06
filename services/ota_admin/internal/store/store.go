package store

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/audit"
	"golang.org/x/crypto/argon2"
	_ "modernc.org/sqlite"
)

const AdministratorUsername = "admin"

var (
	ErrAdminAlreadyInitialized     = errors.New("administrator already initialized")
	ErrInvalidTransition           = errors.New("invalid state transition")
	ErrVersionNotIncreasing        = errors.New("version does not exceed historical maximum")
	ErrNotFound                    = errors.New("record not found")
	ErrSessionInactive             = errors.New("session is inactive or expired")
	ErrCandidateExpired            = errors.New("candidate expired")
	ErrUploadLeaseRecoveryRequired = errors.New("upload reservation requires legacy lease recovery")
)

type Store struct {
	db                *sql.DB
	statePath         *statePathHandle
	commitPublishHook func(string) error
	candidateMu       sync.Mutex
}

type statePathHandle struct {
	sqlitePath string
	verify     func() error
	close      func() error
}

func (h *statePathHandle) Verify() error { return h.verify() }
func (h *statePathHandle) Close() error {
	if h == nil || h.close == nil {
		return nil
	}
	err := h.close()
	h.close = nil
	return err
}

type Administrator struct {
	Username           string
	PasswordHash       string
	MustChangePassword bool
}

type CandidateState string

const (
	CandidateUploading  CandidateState = "uploading"
	CandidateValidated  CandidateState = "validated"
	CandidatePublishing CandidateState = "publishing"
	CandidatePublished  CandidateState = "published"
	CandidateFailed     CandidateState = "failed"
	CandidateExpired    CandidateState = "expired"
)

type Candidate struct {
	ID, SessionID        string
	State                CandidateState
	CreatedAt, ExpiresAt time.Time
}

type CandidateRecord struct {
	Candidate
	UploadDigest, ValidationReport, Version, OperationID string
	OperationState                                       OperationState
	ResultCode                                           string
	LeaseOwner                                           string
}

type CandidateAcceptance struct {
	CandidateID, SessionID, OperationID string
	UploadDigest, ValidationReport      string
	Version                             string
	CreatedAt, ExpiresAt                time.Time
	CommittedAt                         time.Time
	LeaseOwner                          string
}

type CandidateReservation struct {
	CandidateID, SessionID, OperationID, LeaseOwner string
	CreatedAt, ExpiresAt, LeaseExpiresAt            time.Time
}

type CleanupClaim struct {
	CandidateID, OperationID, ClaimToken string
	AttemptCount                         int
}

type SessionState string

const (
	SessionActive  SessionState = "active"
	SessionRevoked SessionState = "revoked"
	SessionExpired SessionState = "expired"
)

type Session struct {
	IDHash                                                  string
	State                                                   SessionState
	CreatedAt, LastSeenAt, IdleExpiresAt, AbsoluteExpiresAt time.Time
}

type OperationKind string
type OperationState string

const (
	OperationUpload    OperationKind  = "upload"
	OperationPublish   OperationKind  = "publish"
	OperationRollback  OperationKind  = "rollback"
	OperationCleanup   OperationKind  = "cleanup"
	OperationPending   OperationState = "pending"
	OperationRunning   OperationState = "running"
	OperationSucceeded OperationState = "succeeded"
	OperationFailed    OperationState = "failed"
)

type Operation struct {
	ID                     string
	Kind                   OperationKind
	State                  OperationState
	CandidateID, ReleaseID string
	CreatedAt              time.Time
}

type Release struct {
	ID, Version, GenerationName string
	CommittedAt                 time.Time
}

type ReleaseAsset struct {
	FileName  string
	SizeBytes int64
	SHA256    string
}
type PublishCommit struct {
	Release                  Release
	Assets                   []ReleaseAsset
	OperationID, CandidateID string
}

type PublishOperation struct {
	OperationID, CandidateID, ReleaseID, ResultCode string
	State                                           OperationState
}

func Open(ctx context.Context, path string) (*Store, error) {
	if err := validateDatabasePath(path); err != nil {
		return nil, err
	}
	securedPath, err := prepareStatePath(path)
	if err != nil {
		return nil, err
	}
	db, err := sql.Open("sqlite", databaseURI(securedPath.sqlitePath))
	if err != nil {
		_ = securedPath.Close()
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	db.SetMaxOpenConns(4)
	db.SetMaxIdleConns(4)
	s := &Store{db: db, statePath: securedPath}
	if err := db.PingContext(ctx); err != nil {
		_ = db.Close()
		_ = securedPath.Close()
		return nil, fmt.Errorf("connect sqlite: %w", err)
	}
	if err := securedPath.Verify(); err != nil {
		_ = db.Close()
		_ = securedPath.Close()
		return nil, err
	}
	if err := s.verifyPragmas(ctx); err != nil {
		_ = db.Close()
		_ = securedPath.Close()
		return nil, err
	}
	if err := s.migrate(ctx); err != nil {
		_ = db.Close()
		_ = securedPath.Close()
		return nil, err
	}
	if err := securedPath.Verify(); err != nil {
		_ = db.Close()
		_ = securedPath.Close()
		return nil, err
	}
	return s, nil
}

func validateDatabasePath(path string) error {
	if !filepath.IsAbs(path) {
		return errors.New("database path must be absolute")
	}
	if strings.HasPrefix(strings.ToLower(path), "file:") || strings.ContainsAny(path, "?#\x00") {
		return errors.New("database path contains URI or DSN metacharacters")
	}
	return nil
}

func databaseURI(path string) string {
	uriPath := filepath.ToSlash(filepath.Clean(path))
	if volume := filepath.VolumeName(path); volume != "" && !strings.HasPrefix(uriPath, "/") {
		uriPath = "/" + uriPath
	}
	u := &url.URL{Scheme: "file", Path: uriPath}
	query := url.Values{}
	for _, pragma := range []string{"foreign_keys(1)", "busy_timeout(5000)", "synchronous(FULL)", "recursive_triggers(1)", "journal_mode(WAL)"} {
		query.Add("_pragma", pragma)
	}
	u.RawQuery = query.Encode()
	return u.String()
}

func (s *Store) verifyPragmas(ctx context.Context) error {
	expected := map[string]string{"foreign_keys": "1", "busy_timeout": "5000", "synchronous": "2", "recursive_triggers": "1", "journal_mode": "wal"}
	for pragma, want := range expected {
		var got string
		if err := s.db.QueryRowContext(ctx, `PRAGMA `+pragma).Scan(&got); err != nil {
			return fmt.Errorf("verify PRAGMA %s: %w", pragma, err)
		}
		if strings.ToLower(got) != want {
			return fmt.Errorf("PRAGMA %s is %q, want %q", pragma, got, want)
		}
	}
	return nil
}

func (s *Store) migrate(ctx context.Context) error {
	var current int
	if err := s.db.QueryRowContext(ctx, `PRAGMA user_version`).Scan(&current); err != nil {
		return fmt.Errorf("read schema version: %w", err)
	}
	if current > schemaVersion {
		return fmt.Errorf("database schema %d is newer than supported schema %d", current, schemaVersion)
	}
	for _, next := range migrations {
		if next.version <= current {
			continue
		}
		if next.version != current+1 {
			return fmt.Errorf("missing migration from schema %d", current)
		}
		if err := s.withTx(ctx, func(tx *sql.Tx) error {
			if next.apply != nil {
				if err := next.apply(ctx, tx); err != nil {
					return err
				}
			} else {
				if next.validate != nil {
					if err := next.validate(tx); err != nil {
						return err
					}
				}
				if _, err := tx.ExecContext(ctx, next.sql); err != nil {
					return err
				}
			}
			_, err := tx.ExecContext(ctx, fmt.Sprintf(`PRAGMA user_version=%d`, next.version))
			return err
		}); err != nil {
			return fmt.Errorf("apply migration %d: %w", next.version, err)
		}
		current = next.version
	}
	return nil
}

func (s *Store) Close() error {
	dbErr := s.db.Close()
	pathErr := s.statePath.Close()
	if dbErr != nil {
		return dbErr
	}
	return pathErr
}

func (s *Store) withTx(ctx context.Context, work func(*sql.Tx) error) error {
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("begin transaction: %w", err)
	}
	defer tx.Rollback()
	if err := work(tx); err != nil {
		return err
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("commit transaction: %w", err)
	}
	return nil
}

func (s *Store) InitializeAdmin(ctx context.Context, encodedHash string) error {
	if _, _, err := decodePasswordHash(encodedHash); err != nil {
		return err
	}
	_, err := s.db.ExecContext(ctx, `INSERT INTO administrators(singleton_id,username,password_hash,must_change_password,created_at) VALUES(1,?,?,1,?)`, AdministratorUsername, encodedHash, timestamp(time.Now()))
	return s.adminInsertError(ctx, err)
}

func (s *Store) InitializeAdminWithGeneratedPassword(ctx context.Context, output io.Writer, random io.Reader) error {
	if random == nil {
		random = rand.Reader
	}
	return s.withTx(ctx, func(tx *sql.Tx) error {
		var count int
		if err := tx.QueryRowContext(ctx, `SELECT count(*) FROM administrators`).Scan(&count); err != nil {
			return err
		}
		if count != 0 {
			return ErrAdminAlreadyInitialized
		}
		password, err := generatePassword(random, 32)
		if err != nil {
			return fmt.Errorf("generate initial password: %w", err)
		}
		encodedHash, err := hashPassword(password, random)
		if err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO administrators(singleton_id,username,password_hash,must_change_password,created_at) VALUES(1,?,?,1,?)`, AdministratorUsername, encodedHash, timestamp(time.Now())); err != nil {
			return err
		}
		// Write before commit: an output failure rolls the administrator row back.
		if _, err := fmt.Fprintln(output, password); err != nil {
			return fmt.Errorf("print initial password: %w", err)
		}
		return nil
	})
}

func hashPassword(password string, random io.Reader) (string, error) {
	salt := make([]byte, 16)
	if _, err := io.ReadFull(random, salt); err != nil {
		return "", fmt.Errorf("generate password salt: %w", err)
	}
	hash := argon2.IDKey([]byte(password), salt, 3, 64*1024, 2, 32)
	return fmt.Sprintf("$argon2id$v=19$m=65536,t=3,p=2$%s$%s", base64.RawStdEncoding.EncodeToString(salt), base64.RawStdEncoding.EncodeToString(hash)), nil
}

func VerifyPasswordHash(password, encoded string) bool {
	salt, expected, err := decodePasswordHash(encoded)
	if err != nil {
		return false
	}
	actual := argon2.IDKey([]byte(password), salt, 3, 64*1024, 2, 32)
	return subtle.ConstantTimeCompare(actual, expected) == 1
}

func decodePasswordHash(encoded string) ([]byte, []byte, error) {
	parts := strings.Split(encoded, "$")
	if len(parts) != 6 || parts[0] != "" || parts[1] != "argon2id" || parts[2] != "v=19" || parts[3] != "m=65536,t=3,p=2" {
		return nil, nil, errors.New("password hash must use the exact Argon2id profile")
	}
	salt, err := base64.RawStdEncoding.Strict().DecodeString(parts[4])
	if err != nil || len(salt) != 16 {
		return nil, nil, errors.New("password hash has invalid Argon2id salt")
	}
	hash, err := base64.RawStdEncoding.Strict().DecodeString(parts[5])
	if err != nil || len(hash) != 32 {
		return nil, nil, errors.New("password hash has invalid Argon2id output")
	}
	return salt, hash, nil
}

func (s *Store) adminInsertError(ctx context.Context, err error) error {
	if err == nil {
		return nil
	}
	var count int
	if queryErr := s.db.QueryRowContext(ctx, `SELECT count(*) FROM administrators`).Scan(&count); queryErr == nil && count > 0 {
		return ErrAdminAlreadyInitialized
	}
	return fmt.Errorf("initialize administrator: %w", err)
}

func (s *Store) Admin(ctx context.Context) (Administrator, error) {
	var admin Administrator
	var mustChange int
	err := s.db.QueryRowContext(ctx, `SELECT username,password_hash,must_change_password FROM administrators WHERE singleton_id=1`).Scan(&admin.Username, &admin.PasswordHash, &mustChange)
	if errors.Is(err, sql.ErrNoRows) {
		return Administrator{}, ErrNotFound
	}
	if err != nil {
		return Administrator{}, fmt.Errorf("read administrator: %w", err)
	}
	if _, _, err := decodePasswordHash(admin.PasswordHash); err != nil {
		return Administrator{}, fmt.Errorf("stored password hash: %w", err)
	}
	admin.MustChangePassword = mustChange == 1
	return admin, nil
}

func (s *Store) HighestCommittedVersion(ctx context.Context) (string, bool, error) {
	var version string
	var major, minor, patch int64
	err := s.db.QueryRowContext(ctx, `SELECT version,major,minor,patch FROM highest_committed_version WHERE singleton_id=1`).Scan(&version, &major, &minor, &patch)
	if errors.Is(err, sql.ErrNoRows) {
		return "", false, nil
	}
	if err != nil {
		return "", false, err
	}
	parsed, err := parseVersion(version)
	if err != nil || parsed != (numericVersion{major, minor, patch}) {
		return "", false, errors.New("stored highest version tuple is inconsistent")
	}
	return version, true, nil
}

func (s *Store) CreateSession(ctx context.Context, session Session) error {
	if session.State != SessionActive {
		return errors.New("session must start active")
	}
	_, err := s.db.ExecContext(ctx, `INSERT INTO sessions(id_hash,state,created_at,last_seen_at,idle_expires_at,absolute_expires_at) VALUES(?,?,?,?,?,?)`, session.IDHash, session.State, timestamp(session.CreatedAt), timestamp(session.LastSeenAt), timestamp(session.IdleExpiresAt), timestamp(session.AbsoluteExpiresAt))
	return wrapWrite("create session", err)
}

func (s *Store) Session(ctx context.Context, idHash string) (Session, error) {
	var value Session
	var createdAt, lastSeenAt, idleExpiresAt, absoluteExpiresAt int64
	err := s.db.QueryRowContext(ctx, `SELECT id_hash,state,created_at,last_seen_at,idle_expires_at,absolute_expires_at FROM sessions WHERE id_hash=?`, idHash).
		Scan(&value.IDHash, &value.State, &createdAt, &lastSeenAt, &idleExpiresAt, &absoluteExpiresAt)
	if errors.Is(err, sql.ErrNoRows) {
		return Session{}, ErrNotFound
	}
	if err != nil {
		return Session{}, fmt.Errorf("read session: %w", err)
	}
	value.CreatedAt = time.Unix(createdAt, 0).UTC()
	value.LastSeenAt = time.Unix(lastSeenAt, 0).UTC()
	value.IdleExpiresAt = time.Unix(idleExpiresAt, 0).UTC()
	value.AbsoluteExpiresAt = time.Unix(absoluteExpiresAt, 0).UTC()
	return value, nil
}

func (s *Store) TouchSession(ctx context.Context, idHash string, lastSeenAt, idleExpiresAt time.Time) error {
	result, err := s.db.ExecContext(ctx, `UPDATE sessions SET last_seen_at=max(last_seen_at,?),idle_expires_at=max(idle_expires_at,?) WHERE id_hash=? AND state='active'`, timestamp(lastSeenAt), timestamp(idleExpiresAt), idHash)
	if err != nil {
		return fmt.Errorf("touch session: %w", err)
	}
	rows, err := result.RowsAffected()
	if err != nil {
		return err
	}
	if rows != 1 {
		return ErrNotFound
	}
	return nil
}

func (s *Store) RevokeAllActiveSessions(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, `UPDATE sessions SET state='revoked' WHERE state='active'`)
	return wrapWrite("revoke active sessions", err)
}

func (s *Store) ExpireAndPruneSessions(ctx context.Context, now time.Time) error {
	return s.withTx(ctx, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, `UPDATE sessions SET state='expired' WHERE state='active' AND (idle_expires_at<=? OR absolute_expires_at<=?)`, timestamp(now), timestamp(now)); err != nil {
			return err
		}
		_, err := tx.ExecContext(ctx, `DELETE FROM sessions WHERE state IN ('revoked','expired') AND NOT EXISTS (SELECT 1 FROM candidates WHERE candidates.session_id=sessions.id_hash)`)
		return err
	})
}

func (s *Store) ChangePasswordAndRotateSession(ctx context.Context, encodedHash, currentIDHash string, next Session) error {
	if _, _, err := decodePasswordHash(encodedHash); err != nil {
		return err
	}
	if next.State != SessionActive {
		return errors.New("replacement session must start active")
	}
	return s.withTx(ctx, func(tx *sql.Tx) error {
		var currentState SessionState
		if err := tx.QueryRowContext(ctx, `SELECT state FROM sessions WHERE id_hash=?`, currentIDHash).Scan(&currentState); errors.Is(err, sql.ErrNoRows) {
			return ErrNotFound
		} else if err != nil {
			return err
		}
		if currentState != SessionActive {
			return ErrNotFound
		}
		result, err := tx.ExecContext(ctx, `UPDATE administrators SET password_hash=?,must_change_password=0 WHERE singleton_id=1`, encodedHash)
		if err != nil {
			return err
		}
		rows, err := result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrNotFound
		}
		if _, err := tx.ExecContext(ctx, `UPDATE sessions SET state='revoked' WHERE state='active'`); err != nil {
			return err
		}
		_, err = tx.ExecContext(ctx, `INSERT INTO sessions(id_hash,state,created_at,last_seen_at,idle_expires_at,absolute_expires_at) VALUES(?,?,?,?,?,?)`, next.IDHash, next.State, timestamp(next.CreatedAt), timestamp(next.LastSeenAt), timestamp(next.IdleExpiresAt), timestamp(next.AbsoluteExpiresAt))
		return err
	})
}

func (s *Store) TransitionSession(ctx context.Context, id string, state SessionState) error {
	result, err := s.db.ExecContext(ctx, `UPDATE sessions SET state=? WHERE id_hash=?`, state, id)
	return transitionResult(result, err)
}

func (s *Store) CreateCandidate(ctx context.Context, candidate Candidate) error {
	if candidate.State != CandidateUploading {
		return errors.New("candidate must start uploading")
	}
	_, err := s.db.ExecContext(ctx, `INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,?,?,?)`, candidate.ID, candidate.SessionID, candidate.State, timestamp(candidate.CreatedAt), timestamp(candidate.ExpiresAt))
	return wrapWrite("create candidate", err)
}

func (s *Store) ReserveCandidateForActiveSession(ctx context.Context, request CandidateReservation) (CandidateRecord, bool, error) {
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	if request.CreatedAt.IsZero() || !request.ExpiresAt.After(request.CreatedAt) || request.ExpiresAt.Sub(request.CreatedAt) > 24*time.Hour {
		return CandidateRecord{}, false, errors.New("candidate lifetime is invalid")
	}
	if !uuidPatternStore.MatchString(request.LeaseOwner) || !request.LeaseExpiresAt.After(request.CreatedAt) || request.LeaseExpiresAt.Sub(request.CreatedAt) > 30*time.Minute {
		return CandidateRecord{}, false, errors.New("candidate upload lease is invalid")
	}
	started, err := audit.NewUploadStarted(request.OperationID, request.CandidateID, request.CreatedAt)
	if err != nil {
		return CandidateRecord{}, false, err
	}
	var record CandidateRecord
	created := false
	err = s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, request.SessionID, request.CreatedAt); err != nil {
			return err
		}
		existing, found, err := candidateByOperationTx(ctx, tx, request.OperationID)
		if err != nil {
			return err
		}
		if found {
			if existing.SessionID != request.SessionID {
				return ErrSessionInactive
			}
			if existing.State == CandidateUploading && existing.OperationState == OperationPending {
				var leaseExpiry int64
				err := tx.QueryRowContext(ctx, `SELECT lease_expires_at FROM candidate_upload_leases WHERE operation_id=? AND candidate_id=?`, request.OperationID, existing.ID).Scan(&leaseExpiry)
				if err != nil && !errors.Is(err, sql.ErrNoRows) {
					return err
				}
				if errors.Is(err, sql.ErrNoRows) {
					return ErrUploadLeaseRecoveryRequired
				}
				if leaseExpiry <= timestamp(request.CreatedAt) {
					failed, err := failReservedCandidateTx(ctx, tx, existing, "operation_failed", request.CreatedAt)
					if err != nil {
						return err
					}
					record = failed
					return nil
				}
			}
			record = existing
			return nil
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO candidates(id,session_id,state,created_at,expires_at) VALUES(?,?,'uploading',?,?)`, request.CandidateID, request.SessionID, timestamp(request.CreatedAt), timestamp(request.ExpiresAt)); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO operations(id,kind,state,candidate_id,created_at) VALUES(?,'upload','pending',?,?)`, request.OperationID, request.CandidateID, timestamp(request.CreatedAt)); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO candidate_upload_leases(operation_id,candidate_id,owner_token,lease_expires_at,updated_at) VALUES(?,?,?,?,?)`, request.OperationID, request.CandidateID, request.LeaseOwner, timestamp(request.LeaseExpiresAt), timestamp(request.CreatedAt)); err != nil {
			return err
		}
		if err := insertAudit(ctx, tx, started); err != nil {
			return err
		}
		record = CandidateRecord{Candidate: Candidate{ID: request.CandidateID, SessionID: request.SessionID, State: CandidateUploading, CreatedAt: request.CreatedAt.UTC(), ExpiresAt: request.ExpiresAt.UTC()}, OperationID: request.OperationID, OperationState: OperationPending, LeaseOwner: request.LeaseOwner}
		created = true
		return nil
	})
	return record, created, err
}

func (s *Store) FailReservedCandidate(ctx context.Context, sessionID, operationID, resultCode string, at time.Time, leaseOwner string) (CandidateRecord, error) {
	if !uuidPatternStore.MatchString(leaseOwner) {
		return CandidateRecord{}, ErrInvalidTransition
	}
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	var record CandidateRecord
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		var found bool
		var err error
		record, found, err = candidateByOperationTx(ctx, tx, operationID)
		if err != nil {
			return err
		}
		if !found || record.SessionID != sessionID {
			return ErrSessionInactive
		}
		if record.State != CandidateUploading || record.OperationState != OperationPending {
			return nil
		}
		var matches int
		if err := tx.QueryRowContext(ctx, `SELECT count(*) FROM candidate_upload_leases WHERE operation_id=? AND candidate_id=? AND owner_token=? AND lease_expires_at>?`, operationID, record.ID, leaseOwner, timestamp(at)).Scan(&matches); err != nil {
			return err
		}
		if matches != 1 {
			return ErrInvalidTransition
		}
		failed, err := failReservedCandidateTx(ctx, tx, record, resultCode, at)
		if err != nil {
			return err
		}
		record = failed
		return nil
	})
	return record, err
}

func (s *Store) RecoverUnleasedCandidateForActiveSession(ctx context.Context, sessionID, operationID string, at time.Time) (CandidateRecord, error) {
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	var record CandidateRecord
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, sessionID, at); err != nil {
			return err
		}
		var found bool
		var err error
		record, found, err = candidateByOperationTx(ctx, tx, operationID)
		if err != nil {
			return err
		}
		if !found || record.SessionID != sessionID {
			return ErrSessionInactive
		}
		if record.State != CandidateUploading || record.OperationState != OperationPending {
			return ErrInvalidTransition
		}
		var leases int
		if err := tx.QueryRowContext(ctx, `SELECT count(*) FROM candidate_upload_leases WHERE operation_id=? AND candidate_id=?`, operationID, record.ID).Scan(&leases); err != nil {
			return err
		}
		if leases != 0 {
			return ErrInvalidTransition
		}
		record, err = failReservedCandidateTx(ctx, tx, record, "operation_failed", at)
		return err
	})
	return record, err
}

func failReservedCandidateTx(ctx context.Context, tx *sql.Tx, record CandidateRecord, resultCode string, at time.Time) (CandidateRecord, error) {
	operationCode, publicCode, gateCode, valid := candidateFailureCodes(resultCode)
	if !valid {
		return CandidateRecord{}, errors.New("candidate failure code is invalid")
	}
	uploadCode := audit.CodeInternalFailure
	if operationCode == "hash_mismatch" {
		uploadCode = audit.CodeHashMismatch
	}
	if operationCode == "expired" {
		uploadCode = audit.CodeExpired
	}
	result, err := tx.ExecContext(ctx, `UPDATE candidates SET state='failed' WHERE id=? AND state='uploading'`, record.ID)
	if err != nil {
		return CandidateRecord{}, err
	}
	rows, err := result.RowsAffected()
	if err != nil {
		return CandidateRecord{}, err
	}
	if rows != 1 {
		return CandidateRecord{}, ErrInvalidTransition
	}
	if _, err := tx.ExecContext(ctx, `INSERT INTO candidate_upload_failures(operation_id,candidate_id,public_code) VALUES(?,?,?)`, record.OperationID, record.ID, publicCode); err != nil {
		return CandidateRecord{}, err
	}
	result, err = tx.ExecContext(ctx, `UPDATE operations SET state='failed',result_code=?,finished_at=? WHERE id=? AND state='pending'`, operationCode, timestamp(at), record.OperationID)
	if err != nil {
		return CandidateRecord{}, err
	}
	rows, err = result.RowsAffected()
	if err != nil {
		return CandidateRecord{}, err
	}
	if rows != 1 {
		return CandidateRecord{}, ErrInvalidTransition
	}
	if operationCode != "expired" {
		gateEvent, err := audit.NewGateResult(audit.OutcomeFailure, record.OperationID, record.ID, gateCode, at)
		if err != nil {
			return CandidateRecord{}, err
		}
		if err := insertAudit(ctx, tx, gateEvent); err != nil {
			return CandidateRecord{}, err
		}
	}
	event, err := audit.NewUploadCompleted(audit.OutcomeFailure, record.OperationID, record.ID, uploadCode, at)
	if err != nil {
		return CandidateRecord{}, err
	}
	if err := insertAudit(ctx, tx, event); err != nil {
		return CandidateRecord{}, err
	}
	if _, err := tx.ExecContext(ctx, `DELETE FROM candidate_upload_leases WHERE operation_id=? AND candidate_id=?`, record.OperationID, record.ID); err != nil {
		return CandidateRecord{}, err
	}
	record.State, record.OperationState, record.ResultCode, record.LeaseOwner = CandidateFailed, OperationFailed, publicCode, ""
	return record, nil
}

func (s *Store) RenewCandidateUploadLease(ctx context.Context, sessionID, operationID, leaseOwner string, now, expiresAt time.Time) error {
	if !uuidPatternStore.MatchString(leaseOwner) || !expiresAt.After(now) || expiresAt.Sub(now) > 30*time.Minute {
		return errors.New("candidate upload lease renewal is invalid")
	}
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	return s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, sessionID, now); err != nil {
			return err
		}
		result, err := tx.ExecContext(ctx, `UPDATE candidate_upload_leases SET lease_expires_at=?,updated_at=? WHERE operation_id=? AND owner_token=? AND lease_expires_at>? AND EXISTS (SELECT 1 FROM operations o JOIN candidates c ON c.id=o.candidate_id WHERE o.id=candidate_upload_leases.operation_id AND o.candidate_id=candidate_upload_leases.candidate_id AND o.kind='upload' AND o.state='pending' AND c.state='uploading' AND c.session_id=?)`, timestamp(expiresAt), timestamp(now), operationID, leaseOwner, timestamp(now), sessionID)
		if err != nil {
			return err
		}
		rows, err := result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrInvalidTransition
		}
		return nil
	})
}

func candidateFailureCodes(code string) (operationCode, publicCode string, gateCode audit.Code, ok bool) {
	switch code {
	case "signature_mismatch":
		return code, code, audit.CodeSignatureMismatch, true
	case "version_floor":
		return code, code, audit.CodeVersionFloor, true
	case "hash_mismatch":
		return code, "release_validation_failed", audit.CodeHashMismatch, true
	case "release_validation_failed":
		return "internal_failure", code, audit.CodeInternalFailure, true
	case "invalid_zip_envelope", "upload_too_large", "insufficient_storage", "operation_failed":
		return "internal_failure", code, audit.CodeInternalFailure, true
	case "expired", "candidate_expired":
		return "expired", "candidate_expired", audit.CodeExpired, true
	case "internal_failure":
		return code, "operation_failed", audit.CodeInternalFailure, true
	default:
		return "", "", "", false
	}
}

func (s *Store) AcceptCandidateForActiveSession(ctx context.Context, request CandidateAcceptance) (CandidateRecord, bool, error) {
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	commitAt := request.CommittedAt.UTC()
	if commitAt.IsZero() {
		return CandidateRecord{}, false, errors.New("candidate commit time is required")
	}
	if request.CreatedAt.IsZero() || !request.ExpiresAt.After(request.CreatedAt) || request.ExpiresAt.Sub(request.CreatedAt) > 24*time.Hour {
		return CandidateRecord{}, false, errors.New("candidate lifetime is invalid")
	}
	if !exactLowerHex(request.UploadDigest, 64) {
		return CandidateRecord{}, false, errors.New("candidate digest is invalid")
	}
	if !uuidPatternStore.MatchString(request.LeaseOwner) {
		return CandidateRecord{}, false, ErrInvalidTransition
	}
	parsed, err := parseVersion(request.Version)
	if err != nil {
		return CandidateRecord{}, false, err
	}
	var reportVersion struct {
		Verified bool   `json:"verified"`
		Version  string `json:"version"`
	}
	if len(request.ValidationReport) == 0 || len(request.ValidationReport) > 65536 || json.Unmarshal([]byte(request.ValidationReport), &reportVersion) != nil || !reportVersion.Verified || reportVersion.Version != request.Version {
		return CandidateRecord{}, false, errors.New("candidate validation report is invalid")
	}
	uploadCompleted, err := audit.NewUploadCompleted(audit.OutcomeSuccess, request.OperationID, request.CandidateID, audit.CodeCompleted, commitAt)
	if err != nil {
		return CandidateRecord{}, false, err
	}
	gateAccepted, err := audit.NewGateResult(audit.OutcomeSuccess, request.OperationID, request.CandidateID, audit.CodeAccepted, commitAt)
	if err != nil {
		return CandidateRecord{}, false, err
	}
	var accepted CandidateRecord
	created := false
	err = s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, request.SessionID, commitAt); err != nil {
			return err
		}
		existing, found, err := candidateByOperationTx(ctx, tx, request.OperationID)
		if err != nil {
			return err
		}
		if found {
			if existing.SessionID != request.SessionID {
				return ErrSessionInactive
			}
			if existing.State != CandidateUploading {
				accepted = existing
				return nil
			}
			if existing.ID != request.CandidateID || !commitAt.Before(existing.ExpiresAt) {
				return ErrCandidateExpired
			}
		}
		if !found {
			return ErrInvalidTransition
		}
		var matches int
		if err := tx.QueryRowContext(ctx, `SELECT count(*) FROM candidate_upload_leases WHERE operation_id=? AND candidate_id=? AND owner_token=? AND lease_expires_at>?`, request.OperationID, request.CandidateID, request.LeaseOwner, timestamp(commitAt)).Scan(&matches); err != nil {
			return err
		}
		if matches != 1 {
			return ErrInvalidTransition
		}
		var major, minor, patch int64
		floorErr := tx.QueryRowContext(ctx, `SELECT major,minor,patch FROM highest_committed_version WHERE singleton_id=1`).Scan(&major, &minor, &patch)
		if floorErr == nil && parsed.compare(numericVersion{major, minor, patch}) <= 0 {
			return ErrVersionNotIncreasing
		}
		if floorErr != nil && !errors.Is(floorErr, sql.ErrNoRows) {
			return floorErr
		}
		if _, err := tx.ExecContext(ctx, `UPDATE operations SET state='running' WHERE id=?`, request.OperationID); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `UPDATE candidates SET upload_digest=?,validation_report=?,state='validated' WHERE id=?`, request.UploadDigest, request.ValidationReport, request.CandidateID); err != nil {
			return err
		}
		if err := insertAudit(ctx, tx, gateAccepted); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `UPDATE operations SET state='succeeded',result_code='completed',finished_at=? WHERE id=?`, timestamp(commitAt), request.OperationID); err != nil {
			return err
		}
		if err := insertAudit(ctx, tx, uploadCompleted); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `DELETE FROM candidate_upload_leases WHERE operation_id=? AND candidate_id=?`, request.OperationID, request.CandidateID); err != nil {
			return err
		}
		accepted = CandidateRecord{Candidate: Candidate{ID: request.CandidateID, SessionID: request.SessionID, State: CandidateValidated, CreatedAt: request.CreatedAt.UTC(), ExpiresAt: request.ExpiresAt.UTC()}, UploadDigest: request.UploadDigest, ValidationReport: request.ValidationReport, Version: request.Version, OperationID: request.OperationID, OperationState: OperationSucceeded, ResultCode: "completed"}
		created = true
		return nil
	})
	return accepted, created, err
}

func (s *Store) CandidateByOperationForActiveSession(ctx context.Context, sessionID, operationID string, now time.Time) (CandidateRecord, bool, error) {
	var record CandidateRecord
	var found bool
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, sessionID, now); err != nil {
			return err
		}
		var err error
		record, found, err = candidateByOperationTx(ctx, tx, operationID)
		if err != nil {
			return err
		}
		if found && record.SessionID != sessionID {
			return ErrSessionInactive
		}
		return nil
	})
	return record, found, err
}

func (s *Store) CurrentCandidateForActiveSession(ctx context.Context, sessionID string, now time.Time) (CandidateRecord, error) {
	var record CandidateRecord
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, sessionID, now); err != nil {
			return err
		}
		row := tx.QueryRowContext(ctx, candidateRecordSQL+` WHERE c.session_id=? ORDER BY c.created_at DESC,c.id DESC LIMIT 1`, sessionID)
		var err error
		record, err = scanCandidateRecord(row)
		return err
	})
	return record, err
}

func (s *Store) CandidateForActiveSession(ctx context.Context, sessionID, candidateID string, now time.Time) (CandidateRecord, error) {
	var record CandidateRecord
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, sessionID, now); err != nil {
			return err
		}
		var err error
		record, err = scanCandidateRecord(tx.QueryRowContext(ctx, candidateRecordSQL+` WHERE c.id=? AND c.session_id=?`, candidateID, sessionID))
		if errors.Is(err, ErrNotFound) {
			return ErrSessionInactive
		}
		return err
	})
	return record, err
}

func (s *Store) CandidateForConfirmForActiveSession(ctx context.Context, sessionID, candidateID string, now time.Time) (CandidateRecord, error) {
	var record CandidateRecord
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, sessionID, now); err != nil {
			return err
		}
		row := tx.QueryRowContext(ctx, candidateRecordSQL+` WHERE c.id=? AND c.session_id=?`, candidateID, sessionID)
		var err error
		record, err = scanCandidateRecord(row)
		if errors.Is(err, ErrNotFound) {
			return ErrSessionInactive
		}
		if err != nil {
			return err
		}
		if record.State != CandidateValidated {
			return ErrInvalidTransition
		}
		if !now.Before(record.ExpiresAt) {
			return ErrCandidateExpired
		}
		return nil
	})
	return record, err
}

func (s *Store) ExpireCandidates(ctx context.Context, now time.Time) ([]string, error) {
	var ids []string
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, `UPDATE candidates SET state='expired' WHERE state IN ('uploading','validated') AND expires_at<=?`, timestamp(now)); err != nil {
			return err
		}
		rows, err := tx.QueryContext(ctx, `SELECT id FROM candidates WHERE state='expired' ORDER BY id`)
		if err != nil {
			return err
		}
		defer rows.Close()
		for rows.Next() {
			var id string
			if err := rows.Scan(&id); err != nil {
				return err
			}
			ids = append(ids, id)
		}
		return rows.Err()
	})
	return ids, err
}

// QueueExpiredCandidateCleanups advances at most limit due candidates and
// durably records cleanup work. Repeated calls are idempotent.
func (s *Store) QueueExpiredCandidateCleanups(ctx context.Context, now time.Time, limit int) ([]string, error) {
	if limit <= 0 || limit > 256 {
		return nil, errors.New("cleanup page limit is invalid")
	}
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	var ids []string
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		rows, err := tx.QueryContext(ctx, `SELECT c.id,c.state FROM candidates c LEFT JOIN candidate_cleanups x ON x.candidate_id=c.id WHERE x.candidate_id IS NULL AND ((c.state IN ('uploading','validated') AND c.expires_at<=?) OR c.state IN ('failed','expired')) ORDER BY c.id LIMIT ?`, timestamp(now), limit)
		if err != nil {
			return err
		}
		type due struct {
			id    string
			state CandidateState
		}
		var selected []due
		for rows.Next() {
			var item due
			if err := rows.Scan(&item.id, &item.state); err != nil {
				rows.Close()
				return err
			}
			selected = append(selected, item)
		}
		if err := rows.Close(); err != nil {
			return err
		}
		for _, item := range selected {
			if item.state == CandidateUploading || item.state == CandidateValidated {
				if _, err := tx.ExecContext(ctx, `UPDATE candidates SET state='expired' WHERE id=? AND state IN ('uploading','validated')`, item.id); err != nil {
					return err
				}
				if item.state == CandidateUploading {
					var operationID string
					if err := tx.QueryRowContext(ctx, `SELECT id FROM operations WHERE candidate_id=? AND kind='upload'`, item.id).Scan(&operationID); err != nil {
						return err
					}
					if _, err := tx.ExecContext(ctx, `UPDATE operations SET state='failed',result_code='expired',finished_at=? WHERE id=? AND state='pending'`, timestamp(now), operationID); err != nil {
						return err
					}
					if _, err := tx.ExecContext(ctx, `DELETE FROM candidate_upload_leases WHERE operation_id=? AND candidate_id=?`, operationID, item.id); err != nil {
						return err
					}
					event, err := audit.NewUploadCompleted(audit.OutcomeFailure, operationID, item.id, audit.CodeExpired, now)
					if err != nil {
						return err
					}
					if err := insertAudit(ctx, tx, event); err != nil {
						return err
					}
				}
			}
			if _, err := tx.ExecContext(ctx, `INSERT INTO candidate_cleanups(candidate_id,state,updated_at) VALUES(?,'pending',?) ON CONFLICT(candidate_id) DO NOTHING`, item.id, timestamp(now)); err != nil {
				return err
			}
			ids = append(ids, item.id)
		}
		return nil
	})
	return ids, err
}

func (s *Store) RequeueCandidateCleanupIfDue(ctx context.Context, candidateID string, now time.Time) (bool, error) {
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	requeued := false
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		var state CandidateState
		var expiresAt int64
		if err := tx.QueryRowContext(ctx, `SELECT state,expires_at FROM candidates WHERE id=?`, candidateID).Scan(&state, &expiresAt); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return nil
			}
			return err
		}
		due := state == CandidateFailed || state == CandidateExpired || (state == CandidateUploading || state == CandidateValidated) && timestamp(now) >= expiresAt
		if !due {
			return nil
		}
		result, err := tx.ExecContext(ctx, `UPDATE candidate_cleanups SET state='pending',operation_id=NULL,claim_token='',claim_expires_at=NULL,last_error_code='',updated_at=?,completed_at=NULL WHERE candidate_id=? AND state='completed'`, timestamp(now), candidateID)
		if err != nil {
			return err
		}
		rows, err := result.RowsAffected()
		if err != nil {
			return err
		}
		requeued = rows == 1
		return nil
	})
	return requeued, err
}

func (s *Store) ClaimCandidateCleanups(ctx context.Context, now time.Time, lease time.Duration, limit int, claimToken string, operationIDs []string) ([]CleanupClaim, error) {
	if lease <= 0 || lease > time.Hour || limit <= 0 || limit > 256 || len(operationIDs) < limit {
		return nil, errors.New("cleanup claim request is invalid")
	}
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	var claims []CleanupClaim
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		rows, err := tx.QueryContext(ctx, `SELECT candidate_id,attempt_count,operation_id,claim_token FROM candidate_cleanups WHERE state='pending' AND (claim_expires_at IS NULL OR claim_expires_at<=?) AND (last_error_code='' OR updated_at<=?) ORDER BY candidate_id LIMIT ?`, timestamp(now), timestamp(now.Add(-5*time.Minute)), limit)
		if err != nil {
			return err
		}
		type ready struct {
			id           string
			attempts     int
			oldOperation sql.NullString
			oldClaim     string
		}
		var selected []ready
		for rows.Next() {
			var item ready
			if err := rows.Scan(&item.id, &item.attempts, &item.oldOperation, &item.oldClaim); err != nil {
				rows.Close()
				return err
			}
			selected = append(selected, item)
		}
		if err := rows.Close(); err != nil {
			return err
		}
		for index, item := range selected {
			if item.oldOperation.Valid && item.oldClaim != "" {
				result, err := tx.ExecContext(ctx, `UPDATE operations SET state='failed',result_code='internal_failure',finished_at=? WHERE id=? AND state='running'`, timestamp(now), item.oldOperation.String)
				if err != nil {
					return err
				}
				changed, err := result.RowsAffected()
				if err != nil {
					return err
				}
				if changed == 0 {
					if err := verifyFailedCleanupAttemptTx(ctx, tx, item.oldOperation.String, item.id, item.attempts); err != nil {
						return err
					}
				} else {
					attemptResult, err := tx.ExecContext(ctx, `UPDATE candidate_cleanup_attempts SET outcome='failure',finished_at=? WHERE operation_id=? AND candidate_id=? AND outcome=''`, timestamp(now), item.oldOperation.String, item.id)
					if err != nil {
						return err
					}
					attemptRows, err := attemptResult.RowsAffected()
					if err != nil {
						return err
					}
					if attemptRows != 1 {
						return ErrInvalidTransition
					}
					event, err := audit.NewCleanupResult(audit.OutcomeFailure, item.oldOperation.String, "", audit.CodeInternalFailure, now)
					if err != nil {
						return err
					}
					if err := insertAudit(ctx, tx, event); err != nil {
						return err
					}
				}
			}
			opID := operationIDs[index]
			result, err := tx.ExecContext(ctx, `UPDATE candidate_cleanups SET operation_id=?,attempt_count=attempt_count+1,claim_token=?,claim_expires_at=?,last_error_code='',updated_at=? WHERE candidate_id=? AND state='pending' AND (claim_expires_at IS NULL OR claim_expires_at<=?)`, opID, claimToken, timestamp(now.Add(lease)), timestamp(now), item.id, timestamp(now))
			if err != nil {
				return err
			}
			changed, err := result.RowsAffected()
			if err != nil {
				return err
			}
			if changed != 1 {
				return ErrInvalidTransition
			}
			if _, err := tx.ExecContext(ctx, `INSERT INTO operations(id,kind,state,created_at) VALUES(?,'cleanup','pending',?)`, opID, timestamp(now)); err != nil {
				return err
			}
			if _, err := tx.ExecContext(ctx, `INSERT INTO candidate_cleanup_attempts(operation_id,candidate_id,attempt_number,started_at) VALUES(?,?,?,?)`, opID, item.id, item.attempts+1, timestamp(now)); err != nil {
				return err
			}
			if _, err := tx.ExecContext(ctx, `UPDATE operations SET state='running' WHERE id=?`, opID); err != nil {
				return err
			}
			claims = append(claims, CleanupClaim{CandidateID: item.id, OperationID: opID, ClaimToken: claimToken, AttemptCount: item.attempts + 1})
		}
		return nil
	})
	return claims, err
}

func verifyFailedCleanupAttemptTx(ctx context.Context, tx *sql.Tx, operationID, candidateID string, attemptNumber int) error {
	var kind, state, resultCode, attemptCandidate, outcome string
	var storedAttempt int
	var finishedAt sql.NullInt64
	err := tx.QueryRowContext(ctx, `SELECT o.kind,o.state,o.result_code,a.candidate_id,a.attempt_number,a.outcome,a.finished_at FROM operations o JOIN candidate_cleanup_attempts a ON a.operation_id=o.id WHERE o.id=?`, operationID).Scan(&kind, &state, &resultCode, &attemptCandidate, &storedAttempt, &outcome, &finishedAt)
	if err != nil {
		return ErrInvalidTransition
	}
	if kind != string(OperationCleanup) || state != string(OperationFailed) || resultCode != string(audit.CodeInternalFailure) || attemptCandidate != candidateID || storedAttempt != attemptNumber || outcome != "failure" || !finishedAt.Valid {
		return ErrInvalidTransition
	}
	return nil
}

func (s *Store) CompleteCandidateCleanup(ctx context.Context, claim CleanupClaim, at time.Time) error {
	event, err := audit.NewCleanupResult(audit.OutcomeSuccess, claim.OperationID, "", audit.CodeCompleted, at)
	if err != nil {
		return err
	}
	return s.finishCandidateCleanup(ctx, claim, at, true, event)
}

func (s *Store) FailCandidateCleanup(ctx context.Context, claim CleanupClaim, at time.Time) error {
	event, err := audit.NewCleanupResult(audit.OutcomeFailure, claim.OperationID, "", audit.CodeInternalFailure, at)
	if err != nil {
		return err
	}
	return s.finishCandidateCleanup(ctx, claim, at, false, event)
}

func (s *Store) finishCandidateCleanup(ctx context.Context, claim CleanupClaim, at time.Time, success bool, event audit.Event) error {
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	return s.withTx(ctx, func(tx *sql.Tx) error {
		terminal, code := OperationFailed, audit.CodeInternalFailure
		if success {
			terminal, code = OperationSucceeded, audit.CodeCompleted
		}
		result, err := tx.ExecContext(ctx, `UPDATE operations SET state=?,result_code=?,finished_at=? WHERE id=? AND state='running'`, terminal, code, timestamp(at), claim.OperationID)
		if err != nil {
			return err
		}
		rows, err := result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrInvalidTransition
		}
		if err := insertAudit(ctx, tx, event); err != nil {
			return err
		}
		outcome := "failure"
		if success {
			outcome = "success"
		}
		result, err = tx.ExecContext(ctx, `UPDATE candidate_cleanup_attempts SET outcome=?,finished_at=? WHERE operation_id=? AND candidate_id=? AND outcome=''`, outcome, timestamp(at), claim.OperationID, claim.CandidateID)
		if err != nil {
			return err
		}
		rows, err = result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrInvalidTransition
		}
		if success {
			result, err = tx.ExecContext(ctx, `UPDATE candidate_cleanups SET state='completed',claim_token='',claim_expires_at=NULL,last_error_code='',updated_at=?,completed_at=? WHERE candidate_id=? AND operation_id=? AND claim_token=? AND state='pending'`, timestamp(at), timestamp(at), claim.CandidateID, claim.OperationID, claim.ClaimToken)
		} else {
			result, err = tx.ExecContext(ctx, `UPDATE candidate_cleanups SET claim_token='',claim_expires_at=NULL,last_error_code='internal_failure',updated_at=? WHERE candidate_id=? AND operation_id=? AND claim_token=? AND state='pending'`, timestamp(at), claim.CandidateID, claim.OperationID, claim.ClaimToken)
		}
		if err != nil {
			return err
		}
		rows, err = result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrInvalidTransition
		}
		return nil
	})
}

func (s *Store) RecordCleanupResult(ctx context.Context, operationID string, success bool, at time.Time) error {
	outcome, code, terminal := audit.OutcomeFailure, audit.CodeInternalFailure, OperationFailed
	if success {
		outcome, code, terminal = audit.OutcomeSuccess, audit.CodeCompleted, OperationSucceeded
	}
	event, err := audit.NewCleanupResult(outcome, operationID, "", code, at)
	if err != nil {
		return err
	}
	return s.withTx(ctx, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, `INSERT INTO operations(id,kind,state,created_at) VALUES(?,'cleanup','pending',?)`, operationID, timestamp(at)); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `UPDATE operations SET state='running' WHERE id=?`, operationID); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `UPDATE operations SET state=?,result_code=?,finished_at=? WHERE id=?`, terminal, code, timestamp(at), operationID); err != nil {
			return err
		}
		return insertAudit(ctx, tx, event)
	})
}

const candidateRecordSQL = `SELECT c.id,c.session_id,c.state,c.created_at,c.expires_at,c.upload_digest,c.validation_report,CASE WHEN c.validation_report='' THEN '' ELSE COALESCE(json_extract(c.validation_report,'$.version'),'') END,o.id,o.state,COALESCE(f.public_code,o.result_code) FROM candidates c JOIN operations o ON o.candidate_id=c.id AND o.kind='upload' LEFT JOIN candidate_upload_failures f ON f.operation_id=o.id`
const candidateUploadOperationRecordSQL = `SELECT c.id,c.session_id,CASE WHEN o.state='succeeded' THEN 'validated' WHEN o.state='failed' THEN 'failed' ELSE 'uploading' END,c.created_at,c.expires_at,c.upload_digest,c.validation_report,CASE WHEN c.validation_report='' THEN '' ELSE COALESCE(json_extract(c.validation_report,'$.version'),'') END,o.id,o.state,COALESCE(f.public_code,o.result_code) FROM candidates c JOIN operations o ON o.candidate_id=c.id AND o.kind='upload' LEFT JOIN candidate_upload_failures f ON f.operation_id=o.id`

type rowScanner interface{ Scan(...any) error }

func scanCandidateRecord(row rowScanner) (CandidateRecord, error) {
	var record CandidateRecord
	var createdAt, expiresAt int64
	err := row.Scan(&record.ID, &record.SessionID, &record.State, &createdAt, &expiresAt, &record.UploadDigest, &record.ValidationReport, &record.Version, &record.OperationID, &record.OperationState, &record.ResultCode)
	if errors.Is(err, sql.ErrNoRows) {
		return CandidateRecord{}, ErrNotFound
	}
	if err != nil {
		return CandidateRecord{}, err
	}
	record.CreatedAt = time.Unix(createdAt, 0).UTC()
	record.ExpiresAt = time.Unix(expiresAt, 0).UTC()
	return record, nil
}

func candidateByOperationTx(ctx context.Context, tx *sql.Tx, operationID string) (CandidateRecord, bool, error) {
	record, err := scanCandidateRecord(tx.QueryRowContext(ctx, candidateUploadOperationRecordSQL+` WHERE o.id=?`, operationID))
	if errors.Is(err, ErrNotFound) {
		return CandidateRecord{}, false, nil
	}
	return record, err == nil, err
}

func requireActiveSessionTx(ctx context.Context, tx *sql.Tx, sessionID string, now time.Time) error {
	var state SessionState
	var idleExpiresAt, absoluteExpiresAt int64
	err := tx.QueryRowContext(ctx, `SELECT state,idle_expires_at,absolute_expires_at FROM sessions WHERE id_hash=?`, sessionID).Scan(&state, &idleExpiresAt, &absoluteExpiresAt)
	if errors.Is(err, sql.ErrNoRows) || err == nil && (state != SessionActive || timestamp(now) >= idleExpiresAt || timestamp(now) >= absoluteExpiresAt) {
		return ErrSessionInactive
	}
	return err
}

func (s *Store) RequireActiveSession(ctx context.Context, sessionID string, now time.Time) error {
	return s.withTx(ctx, func(tx *sql.Tx) error {
		return requireActiveSessionTx(ctx, tx, sessionID, now)
	})
}

func exactLowerHex(value string, length int) bool {
	if len(value) != length {
		return false
	}
	for _, character := range value {
		if !(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f') {
			return false
		}
	}
	return true
}

func (s *Store) ValidateCandidate(ctx context.Context, id, digest, report string) error {
	result, err := s.db.ExecContext(ctx, `UPDATE candidates SET upload_digest=?,validation_report=?,state='validated' WHERE id=?`, digest, report, id)
	return transitionResult(result, err)
}

func (s *Store) TransitionCandidate(ctx context.Context, id string, state CandidateState) error {
	if state == CandidateValidated {
		return errors.New("use ValidateCandidate to bind validation evidence")
	}
	result, err := s.db.ExecContext(ctx, `UPDATE candidates SET state=? WHERE id=?`, state, id)
	return transitionResult(result, err)
}

func (s *Store) CreateOperation(ctx context.Context, operation Operation) error {
	if operation.State != OperationPending {
		return errors.New("operation must start pending")
	}
	if !validOperationAssociations(operation.Kind, operation.CandidateID, operation.ReleaseID) {
		return errors.New("operation associations do not match its kind")
	}
	var candidateID, releaseID any
	if operation.CandidateID != "" {
		candidateID = operation.CandidateID
	}
	if operation.ReleaseID != "" {
		releaseID = operation.ReleaseID
	}
	_, err := s.db.ExecContext(ctx, `INSERT INTO operations(id,kind,state,candidate_id,release_id,created_at) VALUES(?,?,?,?,?,?)`, operation.ID, operation.Kind, operation.State, candidateID, releaseID, timestamp(operation.CreatedAt))
	return wrapWrite("create operation", err)
}

func validOperationAssociations(kind OperationKind, candidateID, releaseID string) bool {
	switch kind {
	case OperationUpload, OperationPublish:
		return candidateID != "" && releaseID == ""
	case OperationRollback:
		return candidateID == "" && releaseID != ""
	case OperationCleanup:
		return candidateID == ""
	default:
		return false
	}
}

func (s *Store) TransitionOperation(ctx context.Context, id string, state OperationState, resultCode string) error {
	if !validOperationResult(state, resultCode) {
		return errors.New("invalid operation result code")
	}
	var currentState, currentCode string
	if err := s.db.QueryRowContext(ctx, `SELECT state,result_code FROM operations WHERE id=?`, id).Scan(&currentState, &currentCode); errors.Is(err, sql.ErrNoRows) {
		return ErrNotFound
	} else if err != nil {
		return err
	}
	if currentState == string(OperationSucceeded) || currentState == string(OperationFailed) {
		if currentState == string(state) && currentCode == resultCode {
			return nil
		}
		return ErrInvalidTransition
	}
	var finished any
	if state == OperationSucceeded || state == OperationFailed {
		finished = timestamp(time.Now())
	}
	result, err := s.db.ExecContext(ctx, `UPDATE operations SET state=?,result_code=?,finished_at=? WHERE id=?`, state, resultCode, finished, id)
	return transitionResult(result, err)
}

func (s *Store) BeginPublishForActiveSession(ctx context.Context, sessionID, candidateID, operationID string, now time.Time) (PublishOperation, bool, error) {
	s.candidateMu.Lock()
	defer s.candidateMu.Unlock()
	var operation PublishOperation
	created := false
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, sessionID, now); err != nil {
			return err
		}
		var kind, operationOwner string
		err := tx.QueryRowContext(ctx, `SELECT o.id,o.candidate_id,o.state,o.result_code,COALESCE(o.release_id,''),o.kind,c.session_id FROM operations o JOIN candidates c ON c.id=o.candidate_id WHERE o.id=?`, operationID).Scan(&operation.OperationID, &operation.CandidateID, &operation.State, &operation.ResultCode, &operation.ReleaseID, &kind, &operationOwner)
		if err == nil {
			if operationOwner != sessionID {
				return ErrSessionInactive
			}
			if kind != "publish" || operation.CandidateID != candidateID {
				return ErrInvalidTransition
			}
			return nil
		}
		if !errors.Is(err, sql.ErrNoRows) {
			return err
		}
		var state CandidateState
		var owner string
		var expires int64
		if err := tx.QueryRowContext(ctx, `SELECT state,session_id,expires_at FROM candidates WHERE id=?`, candidateID).Scan(&state, &owner, &expires); err != nil {
			return err
		}
		if owner != sessionID {
			return ErrSessionInactive
		}
		if state != CandidateValidated {
			return ErrInvalidTransition
		}
		if timestamp(now) >= expires {
			return ErrCandidateExpired
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO operations(id,kind,state,candidate_id,created_at) VALUES(?,'publish','pending',?,?)`, operationID, candidateID, timestamp(now)); err != nil {
			return err
		}
		result, err := tx.ExecContext(ctx, `UPDATE candidates SET state='publishing' WHERE id=? AND state='validated'`, candidateID)
		if err != nil {
			return err
		}
		rows, err := result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrInvalidTransition
		}
		result, err = tx.ExecContext(ctx, `UPDATE operations SET state='running' WHERE id=? AND state='pending'`, operationID)
		if err != nil {
			return err
		}
		rows, err = result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrInvalidTransition
		}
		operation = PublishOperation{OperationID: operationID, CandidateID: candidateID, State: OperationRunning}
		created = true
		return nil
	})
	return operation, created, err
}

func (s *Store) PublishOperationForActiveSession(ctx context.Context, sessionID, operationID string, now time.Time) (PublishOperation, bool, error) {
	var operation PublishOperation
	found := false
	err := s.withTx(ctx, func(tx *sql.Tx) error {
		if err := requireActiveSessionTx(ctx, tx, sessionID, now); err != nil {
			return err
		}
		var owner, kind string
		err := tx.QueryRowContext(ctx, `SELECT o.id,o.candidate_id,o.state,o.result_code,COALESCE(o.release_id,''),o.kind,c.session_id FROM operations o JOIN candidates c ON c.id=o.candidate_id WHERE o.id=?`, operationID).Scan(&operation.OperationID, &operation.CandidateID, &operation.State, &operation.ResultCode, &operation.ReleaseID, &kind, &owner)
		if errors.Is(err, sql.ErrNoRows) {
			return nil
		}
		if err != nil {
			return err
		}
		if owner != sessionID {
			return ErrSessionInactive
		}
		if kind != "publish" {
			return ErrInvalidTransition
		}
		found = true
		return nil
	})
	return operation, found, err
}

func (s *Store) FailPublish(ctx context.Context, operationID, candidateID, code string, at time.Time) error {
	if code != "hash_mismatch" && code != "signature_mismatch" && code != "version_floor" && code != "internal_failure" {
		return errors.New("publish failure code invalid")
	}
	eventCode := audit.CodeInternalFailure
	if code == "hash_mismatch" {
		eventCode = audit.CodeHashMismatch
	}
	if code == "signature_mismatch" {
		eventCode = audit.CodeSignatureMismatch
	}
	if code == "version_floor" {
		eventCode = audit.CodeVersionFloor
	}
	event, err := audit.NewPublishFailed(operationID, candidateID, eventCode, at)
	if err != nil {
		return err
	}
	return s.withTx(ctx, func(tx *sql.Tx) error {
		result, err := tx.ExecContext(ctx, `UPDATE candidates SET state='failed' WHERE id=? AND state='publishing'`, candidateID)
		if err != nil {
			return err
		}
		rows, err := result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrInvalidTransition
		}
		result, err = tx.ExecContext(ctx, `UPDATE operations SET state='failed',result_code=?,finished_at=? WHERE id=? AND candidate_id=? AND kind='publish' AND state='running'`, code, timestamp(at), operationID, candidateID)
		if err != nil {
			return err
		}
		rows, err = result.RowsAffected()
		if err != nil {
			return err
		}
		if rows != 1 {
			return ErrInvalidTransition
		}
		return insertAudit(ctx, tx, event)
	})
}

func (s *Store) InFlightPublish(ctx context.Context) (PublishOperation, bool, error) {
	rows, err := s.db.QueryContext(ctx, `SELECT id,candidate_id,state,result_code,COALESCE(release_id,'') FROM operations WHERE kind='publish' AND state='running' ORDER BY id LIMIT 2`)
	if err != nil {
		return PublishOperation{}, false, err
	}
	defer rows.Close()
	var found []PublishOperation
	for rows.Next() {
		var item PublishOperation
		if err := rows.Scan(&item.OperationID, &item.CandidateID, &item.State, &item.ResultCode, &item.ReleaseID); err != nil {
			return PublishOperation{}, false, err
		}
		found = append(found, item)
	}
	if err := rows.Err(); err != nil {
		return PublishOperation{}, false, err
	}
	if len(found) > 1 {
		return PublishOperation{}, false, errors.New("multiple publish operations are in flight")
	}
	if len(found) == 0 {
		return PublishOperation{}, false, nil
	}
	return found[0], true, nil
}

func (s *Store) CurrentRelease(ctx context.Context) (Release, []ReleaseAsset, bool, error) {
	var value Release
	var committed int64
	err := s.db.QueryRowContext(ctx, `SELECT id,version,generation_name,committed_at FROM releases WHERE state='current'`).Scan(&value.ID, &value.Version, &value.GenerationName, &committed)
	if errors.Is(err, sql.ErrNoRows) {
		return Release{}, nil, false, nil
	}
	if err != nil {
		return Release{}, nil, false, err
	}
	value.CommittedAt = time.Unix(committed, 0).UTC()
	rows, err := s.db.QueryContext(ctx, `SELECT file_name,size_bytes,sha256 FROM release_assets WHERE release_id=? ORDER BY file_name`, value.ID)
	if err != nil {
		return Release{}, nil, false, err
	}
	defer rows.Close()
	var assets []ReleaseAsset
	for rows.Next() {
		var asset ReleaseAsset
		if err := rows.Scan(&asset.FileName, &asset.SizeBytes, &asset.SHA256); err != nil {
			return Release{}, nil, false, err
		}
		assets = append(assets, asset)
	}
	if err := rows.Err(); err != nil {
		return Release{}, nil, false, err
	}
	return value, assets, true, nil
}

func (s *Store) CommitPublishedRelease(ctx context.Context, request PublishCommit) error {
	if len(request.Assets) == 0 {
		return errors.New("published release requires at least one asset")
	}
	if request.Release.ID != request.OperationID || request.Release.GenerationName != "release-"+request.Release.ID {
		return ErrInvalidTransition
	}
	next, err := parseVersion(request.Release.Version)
	if err != nil {
		return err
	}
	event, err := audit.NewPublishConfirmed(request.OperationID, request.CandidateID, request.Release.ID, request.Release.CommittedAt)
	if err != nil {
		return err
	}
	return s.withTx(ctx, func(tx *sql.Tx) error {
		var candidateState, candidateVersion, operationState, operationKind, operationCode, operationRelease string
		if err := tx.QueryRowContext(ctx, `SELECT state,COALESCE(release_version,'') FROM candidates WHERE id=?`, request.CandidateID).Scan(&candidateState, &candidateVersion); err != nil {
			return err
		}
		if err := tx.QueryRowContext(ctx, `SELECT state,kind,result_code,COALESCE(release_id,'') FROM operations WHERE id=? AND candidate_id=?`, request.OperationID, request.CandidateID).Scan(&operationState, &operationKind, &operationCode, &operationRelease); err != nil {
			return err
		}
		if operationState == "succeeded" || operationState == "failed" {
			if operationState == "succeeded" && operationKind == "publish" && operationCode == "completed" && operationRelease == request.Release.ID && candidateState == "published" && candidateVersion == request.Release.Version {
				matches, err := publishedResultMatches(ctx, tx, request, next)
				if err != nil {
					return err
				}
				if matches {
					return queuePublishedCandidateCleanup(ctx, tx, request.CandidateID, request.Release.CommittedAt)
				}
			}
			return ErrInvalidTransition
		}
		if candidateState != "publishing" || operationState != "running" || operationKind != "publish" {
			return ErrInvalidTransition
		}
		if _, err := tx.ExecContext(ctx, `UPDATE releases SET state='retained' WHERE state='current'`); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO releases(id,version,generation_name,state,committed_at) VALUES(?,?,?,'current',?)`, request.Release.ID, request.Release.Version, request.Release.GenerationName, timestamp(request.Release.CommittedAt)); err != nil {
			return err
		}
		if err := s.publishBoundary("release"); err != nil {
			return err
		}
		for _, asset := range request.Assets {
			if _, err := tx.ExecContext(ctx, `INSERT INTO release_assets(release_id,file_name,size_bytes,sha256) VALUES(?,?,?,?)`, request.Release.ID, asset.FileName, asset.SizeBytes, asset.SHA256); err != nil {
				return err
			}
		}
		if err := s.publishBoundary("assets"); err != nil {
			return err
		}
		if err := advanceFloorTx(ctx, tx, next, request.Release.CommittedAt); err != nil {
			return err
		}
		if err := s.publishBoundary("floor"); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `UPDATE candidates SET state='published',release_version=? WHERE id=?`, request.Release.Version, request.CandidateID); err != nil {
			return err
		}
		if err := queuePublishedCandidateCleanup(ctx, tx, request.CandidateID, request.Release.CommittedAt); err != nil {
			return err
		}
		if err := s.publishBoundary("candidate"); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, `UPDATE operations SET state='succeeded',result_code='completed',release_id=?,finished_at=? WHERE id=?`, request.Release.ID, timestamp(request.Release.CommittedAt), request.OperationID); err != nil {
			return err
		}
		if err := s.publishBoundary("operation"); err != nil {
			return err
		}
		if err := insertAudit(ctx, tx, event); err != nil {
			return err
		}
		return s.publishBoundary("audit")
	})
}

func queuePublishedCandidateCleanup(ctx context.Context, tx *sql.Tx, candidateID string, at time.Time) error {
	_, err := tx.ExecContext(ctx, `INSERT INTO candidate_cleanups(candidate_id,state,updated_at) VALUES(?,'pending',?) ON CONFLICT(candidate_id) DO NOTHING`, candidateID, timestamp(at))
	return err
}

func (s *Store) PublishedRelease(ctx context.Context, id string) (Release, bool, error) {
	var value Release
	var committed int64
	err := s.db.QueryRowContext(ctx, `SELECT id,version,generation_name,committed_at FROM releases WHERE id=?`, id).Scan(&value.ID, &value.Version, &value.GenerationName, &committed)
	if errors.Is(err, sql.ErrNoRows) {
		return Release{}, false, nil
	}
	if err != nil {
		return Release{}, false, err
	}
	value.CommittedAt = time.Unix(committed, 0).UTC()
	return value, true, nil
}

func publishedResultMatches(ctx context.Context, tx *sql.Tx, request PublishCommit, version numericVersion) (bool, error) {
	var storedVersion, generation string
	var committedAt int64
	if err := tx.QueryRowContext(ctx, `SELECT version,generation_name,committed_at FROM releases WHERE id=?`, request.Release.ID).Scan(&storedVersion, &generation, &committedAt); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return false, nil
		}
		return false, err
	}
	if storedVersion != request.Release.Version || generation != request.Release.GenerationName || committedAt != timestamp(request.Release.CommittedAt) {
		return false, nil
	}
	var floorMajor, floorMinor, floorPatch int64
	if err := tx.QueryRowContext(ctx, `SELECT major,minor,patch FROM highest_committed_version WHERE singleton_id=1`).Scan(&floorMajor, &floorMinor, &floorPatch); err != nil {
		return false, err
	}
	if (numericVersion{floorMajor, floorMinor, floorPatch}).compare(version) < 0 {
		return false, nil
	}
	var assetCount int
	if err := tx.QueryRowContext(ctx, `SELECT count(*) FROM release_assets WHERE release_id=?`, request.Release.ID).Scan(&assetCount); err != nil {
		return false, err
	}
	if assetCount != len(request.Assets) {
		return false, nil
	}
	for _, asset := range request.Assets {
		var size int64
		var digest string
		if err := tx.QueryRowContext(ctx, `SELECT size_bytes,sha256 FROM release_assets WHERE release_id=? AND file_name=?`, request.Release.ID, asset.FileName).Scan(&size, &digest); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return false, nil
			}
			return false, err
		}
		if size != asset.SizeBytes || digest != asset.SHA256 {
			return false, nil
		}
	}
	var auditCount int
	if err := tx.QueryRowContext(ctx, `SELECT count(*) FROM audit_events WHERE event_type='publish_confirmed' AND outcome='success' AND operation_id=? AND candidate_id=? AND release_id=? AND code='completed'`, request.OperationID, request.CandidateID, request.Release.ID).Scan(&auditCount); err != nil {
		return false, err
	}
	return auditCount == 1, nil
}

func (s *Store) publishBoundary(stage string) error {
	if s.commitPublishHook != nil {
		return s.commitPublishHook(stage)
	}
	return nil
}

func advanceFloorTx(ctx context.Context, tx *sql.Tx, next numericVersion, at time.Time) error {
	var major, minor, patch int64
	err := tx.QueryRowContext(ctx, `SELECT major,minor,patch FROM highest_committed_version WHERE singleton_id=1`).Scan(&major, &minor, &patch)
	if err == nil {
		if next.compare(numericVersion{major, minor, patch}) <= 0 {
			return ErrVersionNotIncreasing
		}
		_, err = tx.ExecContext(ctx, `UPDATE highest_committed_version SET version=?,major=?,minor=?,patch=?,committed_at=? WHERE singleton_id=1`, next.String(), next.major, next.minor, next.patch, timestamp(at))
		return err
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return err
	}
	_, err = tx.ExecContext(ctx, `INSERT INTO highest_committed_version(singleton_id,version,major,minor,patch,committed_at) VALUES(1,?,?,?,?,?)`, next.String(), next.major, next.minor, next.patch, timestamp(at))
	return err
}

func (s *Store) SetCurrentRelease(ctx context.Context, id string) error {
	return s.withTx(ctx, func(tx *sql.Tx) error {
		var state string
		if err := tx.QueryRowContext(ctx, `SELECT state FROM releases WHERE id=?`, id).Scan(&state); errors.Is(err, sql.ErrNoRows) {
			return ErrNotFound
		} else if err != nil {
			return err
		}
		if state != "retained" {
			return ErrInvalidTransition
		}
		if _, err := tx.ExecContext(ctx, `UPDATE releases SET state='retained' WHERE state='current'`); err != nil {
			return err
		}
		_, err := tx.ExecContext(ctx, `UPDATE releases SET state='current' WHERE id=?`, id)
		return err
	})
}

func (s *Store) RecordAudit(ctx context.Context, event audit.Event) error {
	return insertAudit(ctx, s.db, event)
}

type auditExecer interface {
	ExecContext(context.Context, string, ...any) (sql.Result, error)
}

func insertAudit(ctx context.Context, execer auditExecer, event audit.Event) error {
	_, err := execer.ExecContext(ctx, `INSERT INTO audit_events(occurred_at,event_type,outcome,operation_id,candidate_id,release_id,code,source_pseudonym) VALUES(?,?,?,?,?,?,?,?)`, timestamp(event.At()), event.Type(), event.Outcome(), event.OperationID(), event.CandidateID(), event.ReleaseID(), event.Code(), event.SourcePseudonym().String())
	return wrapWrite("record audit event", err)
}

func transitionResult(result sql.Result, err error) error {
	if err != nil {
		if strings.Contains(err.Error(), "transition") || strings.Contains(err.Error(), "immutable") {
			return ErrInvalidTransition
		}
		return fmt.Errorf("transition state: %w", err)
	}
	rows, err := result.RowsAffected()
	if err != nil {
		return err
	}
	if rows == 0 {
		return ErrNotFound
	}
	return nil
}

func wrapWrite(action string, err error) error {
	if err != nil {
		return fmt.Errorf("%s: %w", action, err)
	}
	return nil
}
func timestamp(value time.Time) int64 { return value.UTC().Unix() }

func validOperationResult(state OperationState, code string) bool {
	switch state {
	case OperationRunning:
		return code == ""
	case OperationSucceeded:
		return code == "completed"
	case OperationFailed:
		return code == "hash_mismatch" || code == "signature_mismatch" || code == "version_floor" || code == "expired" || code == "internal_failure"
	default:
		return false
	}
}

type numericVersion struct{ major, minor, patch int64 }

var numericVersionPattern = regexp.MustCompile(`^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$`)
var uuidPatternStore = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)
var flatNamePattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`)

func parseVersion(value string) (numericVersion, error) {
	match := numericVersionPattern.FindStringSubmatch(value)
	if match == nil {
		return numericVersion{}, errors.New("version must contain exactly three canonical numeric components")
	}
	parts := [3]int64{}
	for index := range parts {
		parsed, err := strconv.ParseInt(match[index+1], 10, 64)
		if err != nil {
			return numericVersion{}, errors.New("version component is too large")
		}
		parts[index] = parsed
		if parsed > 65534 {
			return numericVersion{}, errors.New("version component exceeds client maximum 65534")
		}
	}
	return numericVersion{parts[0], parts[1], parts[2]}, nil
}
func (v numericVersion) String() string { return fmt.Sprintf("%d.%d.%d", v.major, v.minor, v.patch) }
func (v numericVersion) compare(other numericVersion) int {
	for _, pair := range [][2]int64{{v.major, other.major}, {v.minor, other.minor}, {v.patch, other.patch}} {
		if pair[0] < pair[1] {
			return -1
		}
		if pair[0] > pair[1] {
			return 1
		}
	}
	return 0
}

func generatePassword(random io.Reader, length int) (string, error) {
	sets := []string{"ABCDEFGHJKLMNPQRSTUVWXYZ", "abcdefghijkmnopqrstuvwxyz", "23456789", "!@#$%^&*-_=+"}
	all := strings.Join(sets, "")
	password := make([]byte, length)
	for index, set := range sets {
		selected, err := randomCharacter(random, set)
		if err != nil {
			return "", err
		}
		password[index] = selected
	}
	for index := len(sets); index < length; index++ {
		selected, err := randomCharacter(random, all)
		if err != nil {
			return "", err
		}
		password[index] = selected
	}
	for index := len(password) - 1; index > 0; index-- {
		swap, err := randomIndex(random, index+1)
		if err != nil {
			return "", err
		}
		password[index], password[swap] = password[swap], password[index]
	}
	return string(password), nil
}
func randomCharacter(random io.Reader, characters string) (byte, error) {
	index, err := randomIndex(random, len(characters))
	if err != nil {
		return 0, err
	}
	return characters[index], nil
}
func randomIndex(random io.Reader, limit int) (int, error) {
	if limit <= 0 || limit > 256 {
		return 0, errors.New("invalid random selection limit")
	}
	ceiling := 256 - (256 % limit)
	var value [1]byte
	for {
		if _, err := io.ReadFull(random, value[:]); err != nil {
			return 0, err
		}
		if int(value[0]) < ceiling {
			return int(value[0]) % limit, nil
		}
	}
}

func validateStateDirectoryMetadata(owner, expectedOwner uint32, mode os.FileMode, isDirectory, symlink bool) error {
	if symlink || !isDirectory || owner != expectedOwner || mode.Perm() != 0o700 {
		return errors.New("state directory must be a non-symlink directory owned by the service user with mode 0700")
	}
	return nil
}
func validateStateFileMetadata(owner, expectedOwner uint32, mode os.FileMode, regular, symlink bool, links uint64) error {
	if symlink || !regular || owner != expectedOwner || mode.Perm() != 0o600 || links != 1 {
		return errors.New("state file must be one non-symlink regular file owned by the service user with mode 0600")
	}
	return nil
}
