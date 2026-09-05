package store

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"database/sql"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/audit"
	"golang.org/x/crypto/argon2"
	_ "modernc.org/sqlite"
)

const AdministratorUsername = "admin"

var (
	ErrAdminAlreadyInitialized = errors.New("administrator already initialized")
	ErrInvalidTransition       = errors.New("invalid state transition")
	ErrVersionNotIncreasing    = errors.New("version does not exceed historical maximum")
	ErrNotFound                = errors.New("record not found")
)

type Store struct {
	db                *sql.DB
	statePath         *statePathHandle
	commitPublishHook func(string) error
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

func (s *Store) CommitPublishedRelease(ctx context.Context, request PublishCommit) error {
	if len(request.Assets) == 0 {
		return errors.New("published release requires at least one asset")
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
					return nil
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
