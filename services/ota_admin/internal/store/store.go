package store

import (
	"context"
	"database/sql"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
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
	db *sql.DB
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
	ID          string
	SessionID   string
	Fingerprint string
	State       CandidateState
	CreatedAt   time.Time
	ExpiresAt   time.Time
}

type SessionState string

const (
	SessionActive  SessionState = "active"
	SessionRevoked SessionState = "revoked"
	SessionExpired SessionState = "expired"
)

type Session struct {
	IDHash            string
	State             SessionState
	CreatedAt         time.Time
	LastSeenAt        time.Time
	IdleExpiresAt     time.Time
	AbsoluteExpiresAt time.Time
}

type OperationKind string
type OperationState string

const (
	OperationUpload   OperationKind = "upload"
	OperationPublish  OperationKind = "publish"
	OperationRollback OperationKind = "rollback"
	OperationCleanup  OperationKind = "cleanup"

	OperationPending   OperationState = "pending"
	OperationRunning   OperationState = "running"
	OperationSucceeded OperationState = "succeeded"
	OperationFailed    OperationState = "failed"
)

type Operation struct {
	ID          string
	Kind        OperationKind
	State       OperationState
	CandidateID string
	ReleaseID   string
	CreatedAt   time.Time
}

func Open(ctx context.Context, path string) (*Store, error) {
	db, err := sql.Open("sqlite", path)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	store := &Store{db: db}
	if err := store.initialize(ctx); err != nil {
		_ = db.Close()
		return nil, err
	}
	return store, nil
}

func (s *Store) initialize(ctx context.Context) error {
	for _, statement := range []string{
		`PRAGMA busy_timeout = 5000`,
		`PRAGMA foreign_keys = ON`,
		`PRAGMA journal_mode = WAL`,
		`PRAGMA synchronous = FULL`,
	} {
		if _, err := s.db.ExecContext(ctx, statement); err != nil {
			return fmt.Errorf("configure sqlite: %w", err)
		}
	}
	var version int
	if err := s.db.QueryRowContext(ctx, `PRAGMA user_version`).Scan(&version); err != nil {
		return fmt.Errorf("read schema version: %w", err)
	}
	if version > schemaVersion {
		return fmt.Errorf("database schema %d is newer than supported schema %d", version, schemaVersion)
	}
	if version == 0 {
		if err := s.WithTx(ctx, func(tx *sql.Tx) error {
			_, err := tx.ExecContext(ctx, migrationV1)
			return err
		}); err != nil {
			return fmt.Errorf("migrate database: %w", err)
		}
	}
	return nil
}

func (s *Store) DB() *sql.DB  { return s.db }
func (s *Store) Close() error { return s.db.Close() }

func (s *Store) WithTx(ctx context.Context, work func(*sql.Tx) error) error {
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

func (s *Store) InitializeAdmin(ctx context.Context, passwordHash string) error {
	if strings.TrimSpace(passwordHash) == "" {
		return errors.New("password hash is required")
	}
	_, err := s.db.ExecContext(ctx, `
INSERT INTO administrators(singleton_id, username, password_hash, must_change_password, created_at)
VALUES(1, ?, ?, 1, ?)`, AdministratorUsername, passwordHash, timestamp(time.Now()))
	if err != nil {
		var count int
		if countErr := s.db.QueryRowContext(ctx, `SELECT count(*) FROM administrators`).Scan(&count); countErr == nil && count > 0 {
			return ErrAdminAlreadyInitialized
		}
		return fmt.Errorf("initialize administrator: %w", err)
	}
	return nil
}

func (s *Store) InitializeAdminWithGeneratedPassword(ctx context.Context, output io.Writer, random io.Reader) error {
	password, err := generatePassword(random, 32)
	if err != nil {
		return fmt.Errorf("generate initial password: %w", err)
	}
	salt := make([]byte, 16)
	if _, err := io.ReadFull(random, salt); err != nil {
		return fmt.Errorf("generate password salt: %w", err)
	}
	hash := argon2.IDKey([]byte(password), salt, 3, 64*1024, 2, 32)
	encodedHash := fmt.Sprintf("$argon2id$v=19$m=65536,t=3,p=2$%s$%s", base64.RawStdEncoding.EncodeToString(salt), base64.RawStdEncoding.EncodeToString(hash))
	if err := s.InitializeAdmin(ctx, encodedHash); err != nil {
		return err
	}
	if _, err := fmt.Fprintln(output, password); err != nil {
		return fmt.Errorf("print initial password: %w", err)
	}
	return nil
}

func (s *Store) Admin(ctx context.Context) (Administrator, error) {
	var admin Administrator
	var mustChange int
	err := s.db.QueryRowContext(ctx, `SELECT username, password_hash, must_change_password FROM administrators WHERE singleton_id=1`).Scan(&admin.Username, &admin.PasswordHash, &mustChange)
	if errors.Is(err, sql.ErrNoRows) {
		return Administrator{}, ErrNotFound
	}
	if err != nil {
		return Administrator{}, fmt.Errorf("read administrator: %w", err)
	}
	admin.MustChangePassword = mustChange == 1
	return admin, nil
}

func (s *Store) AdvanceHighestCommittedVersion(ctx context.Context, version string) error {
	next, err := parseSemVer(version)
	if err != nil {
		return err
	}
	return s.WithTx(ctx, func(tx *sql.Tx) error {
		var currentVersion string
		var sequence int64
		err := tx.QueryRowContext(ctx, `SELECT version, sequence FROM highest_committed_version WHERE singleton_id=1`).Scan(&currentVersion, &sequence)
		if err != nil && !errors.Is(err, sql.ErrNoRows) {
			return err
		}
		if err == nil {
			current, parseErr := parseSemVer(currentVersion)
			if parseErr != nil {
				return fmt.Errorf("stored version is invalid: %w", parseErr)
			}
			if next.compare(current) <= 0 {
				return ErrVersionNotIncreasing
			}
			_, err = tx.ExecContext(ctx, `UPDATE highest_committed_version SET version=?, major=?, minor=?, patch=?, prerelease=?, precedence_key=?, sequence=?, committed_at=? WHERE singleton_id=1 AND sequence=?`, version, next.major, next.minor, next.patch, next.prerelease, next.precedenceKey(), sequence+1, timestamp(time.Now()), sequence)
			return err
		}
		_, err = tx.ExecContext(ctx, `INSERT INTO highest_committed_version(singleton_id, version, major, minor, patch, prerelease, precedence_key, sequence, committed_at) VALUES(1, ?, ?, ?, ?, ?, ?, 1, ?)`, version, next.major, next.minor, next.patch, next.prerelease, next.precedenceKey(), timestamp(time.Now()))
		return err
	})
}

func (s *Store) HighestCommittedVersion(ctx context.Context) (string, bool, error) {
	var version string
	err := s.db.QueryRowContext(ctx, `SELECT version FROM highest_committed_version WHERE singleton_id=1`).Scan(&version)
	if errors.Is(err, sql.ErrNoRows) {
		return "", false, nil
	}
	if err != nil {
		return "", false, fmt.Errorf("read highest committed version: %w", err)
	}
	return version, true, nil
}

func (s *Store) CreateCandidate(ctx context.Context, candidate Candidate) error {
	_, err := s.db.ExecContext(ctx, `INSERT INTO candidates(id, session_id, fingerprint, state, created_at, expires_at) VALUES(?, ?, ?, ?, ?, ?)`, candidate.ID, candidate.SessionID, candidate.Fingerprint, candidate.State, timestamp(candidate.CreatedAt), timestamp(candidate.ExpiresAt))
	return wrapWrite("create candidate", err)
}

func (s *Store) TransitionCandidate(ctx context.Context, id string, state CandidateState) error {
	result, err := s.db.ExecContext(ctx, `UPDATE candidates SET state=? WHERE id=?`, state, id)
	return transitionResult(result, err)
}

func (s *Store) CreateSession(ctx context.Context, session Session) error {
	_, err := s.db.ExecContext(ctx, `INSERT INTO sessions(id_hash, state, created_at, last_seen_at, idle_expires_at, absolute_expires_at) VALUES(?, ?, ?, ?, ?, ?)`, session.IDHash, session.State, timestamp(session.CreatedAt), timestamp(session.LastSeenAt), timestamp(session.IdleExpiresAt), timestamp(session.AbsoluteExpiresAt))
	return wrapWrite("create session", err)
}

func (s *Store) TransitionSession(ctx context.Context, idHash string, state SessionState) error {
	result, err := s.db.ExecContext(ctx, `UPDATE sessions SET state=? WHERE id_hash=?`, state, idHash)
	return transitionResult(result, err)
}

func (s *Store) CreateOperation(ctx context.Context, operation Operation) error {
	var candidateID, releaseID any
	if operation.CandidateID != "" {
		candidateID = operation.CandidateID
	}
	if operation.ReleaseID != "" {
		releaseID = operation.ReleaseID
	}
	_, err := s.db.ExecContext(ctx, `INSERT INTO operations(id, kind, state, candidate_id, release_id, created_at) VALUES(?, ?, ?, ?, ?, ?)`, operation.ID, operation.Kind, operation.State, candidateID, releaseID, timestamp(operation.CreatedAt))
	return wrapWrite("create operation", err)
}

func (s *Store) TransitionOperation(ctx context.Context, id string, state OperationState, resultCode string) error {
	if !safeResultCode(resultCode) {
		return errors.New("operation result code is not safe metadata")
	}
	var finishedAt any
	if state == OperationSucceeded || state == OperationFailed {
		finishedAt = timestamp(time.Now())
	}
	result, err := s.db.ExecContext(ctx, `UPDATE operations SET state=?, result_code=?, finished_at=? WHERE id=?`, state, resultCode, finishedAt, id)
	return transitionResult(result, err)
}

func (s *Store) RecordAudit(ctx context.Context, event audit.Event) error {
	return InsertAudit(ctx, s.db, event)
}

type auditExecer interface {
	ExecContext(context.Context, string, ...any) (sql.Result, error)
}

func InsertAudit(ctx context.Context, execer auditExecer, event audit.Event) error {
	if err := event.Validate(); err != nil {
		return err
	}
	if event.At.IsZero() {
		event.At = time.Now().UTC()
	}
	_, err := execer.ExecContext(ctx, `INSERT INTO audit_events(occurred_at, event_type, outcome, operation_id, candidate_id, release_id, code, source_hash) VALUES(?, ?, ?, ?, ?, ?, ?, ?)`, timestamp(event.At), event.Type, event.Outcome, event.OperationID, event.CandidateID, event.ReleaseID, event.Code, event.SourceHash)
	return wrapWrite("record audit event", err)
}

func transitionResult(result sql.Result, err error) error {
	if err != nil {
		if strings.Contains(err.Error(), "invalid ") && strings.Contains(err.Error(), " transition") {
			return ErrInvalidTransition
		}
		return fmt.Errorf("transition state: %w", err)
	}
	rows, rowsErr := result.RowsAffected()
	if rowsErr != nil {
		return fmt.Errorf("read transition result: %w", rowsErr)
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

func timestamp(value time.Time) string { return value.UTC().Format(time.RFC3339Nano) }

func generatePassword(random io.Reader, length int) (string, error) {
	const (
		uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ"
		lowercase = "abcdefghijkmnopqrstuvwxyz"
		digits    = "23456789"
		symbols   = "!@#$%^&*-_=+"
	)
	characterSets := []string{uppercase, lowercase, digits, symbols}
	all := strings.Join(characterSets, "")
	password := make([]byte, length)
	for index, characterSet := range characterSets {
		selected, err := randomCharacter(random, characterSet)
		if err != nil {
			return "", err
		}
		password[index] = selected
	}
	for index := len(characterSets); index < length; index++ {
		selected, err := randomCharacter(random, all)
		if err != nil {
			return "", err
		}
		password[index] = selected
	}
	for index := len(password) - 1; index > 0; index-- {
		swapIndex, err := randomIndex(random, index+1)
		if err != nil {
			return "", err
		}
		password[index], password[swapIndex] = password[swapIndex], password[index]
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

var resultCodePattern = regexp.MustCompile(`^[a-z0-9_:-]{0,64}$`)

func safeResultCode(code string) bool { return resultCodePattern.MatchString(code) }

type semVersion struct {
	major, minor, patch uint64
	prerelease          string
}

var semVerPattern = regexp.MustCompile(`^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$`)

func parseSemVer(value string) (semVersion, error) {
	match := semVerPattern.FindStringSubmatch(value)
	if match == nil {
		return semVersion{}, errors.New("version must be strict semantic version")
	}
	parts := make([]uint64, 3)
	for index := range parts {
		parsed, err := strconv.ParseUint(match[index+1], 10, 64)
		if err != nil {
			return semVersion{}, errors.New("version component is too large")
		}
		parts[index] = parsed
	}
	if invalidPrerelease(match[4]) {
		return semVersion{}, errors.New("numeric prerelease identifiers cannot contain leading zeroes")
	}
	return semVersion{major: parts[0], minor: parts[1], patch: parts[2], prerelease: match[4]}, nil
}

func invalidPrerelease(value string) bool {
	for _, identifier := range strings.Split(value, ".") {
		if len(identifier) > 1 && identifier[0] == '0' && isDecimal(identifier) {
			return true
		}
	}
	return false
}

func (version semVersion) precedenceKey() []byte {
	key := make([]byte, 0, 32+len(version.prerelease))
	for _, component := range []uint64{version.major, version.minor, version.patch} {
		var encoded [8]byte
		binary.BigEndian.PutUint64(encoded[:], component)
		key = append(key, encoded[:]...)
	}
	if version.prerelease == "" {
		return append(key, 1)
	}
	key = append(key, 0)
	for _, identifier := range strings.Split(version.prerelease, ".") {
		if isDecimal(identifier) {
			key = append(key, 1)
			var encodedLength [8]byte
			binary.BigEndian.PutUint64(encodedLength[:], uint64(len(identifier)))
			key = append(key, encodedLength[:]...)
			key = append(key, identifier...)
		} else {
			key = append(key, 2)
			key = append(key, identifier...)
			key = append(key, 0)
		}
	}
	return append(key, 0)
}

func (version semVersion) compare(other semVersion) int {
	for _, pair := range [][2]uint64{{version.major, other.major}, {version.minor, other.minor}, {version.patch, other.patch}} {
		if pair[0] < pair[1] {
			return -1
		}
		if pair[0] > pair[1] {
			return 1
		}
	}
	return comparePrerelease(version.prerelease, other.prerelease)
}

func comparePrerelease(left, right string) int {
	if left == right {
		return 0
	}
	if left == "" {
		return 1
	}
	if right == "" {
		return -1
	}
	leftParts, rightParts := strings.Split(left, "."), strings.Split(right, ".")
	for index := 0; index < len(leftParts) && index < len(rightParts); index++ {
		if leftParts[index] == rightParts[index] {
			continue
		}
		leftNumeric, rightNumeric := isDecimal(leftParts[index]), isDecimal(rightParts[index])
		if leftNumeric && rightNumeric {
			if len(leftParts[index]) < len(rightParts[index]) || (len(leftParts[index]) == len(rightParts[index]) && leftParts[index] < rightParts[index]) {
				return -1
			}
			return 1
		}
		if leftNumeric {
			return -1
		}
		if rightNumeric {
			return 1
		}
		if leftParts[index] < rightParts[index] {
			return -1
		}
		return 1
	}
	if len(leftParts) < len(rightParts) {
		return -1
	}
	return 1
}

func isDecimal(value string) bool {
	if value == "" {
		return false
	}
	for _, character := range value {
		if character < '0' || character > '9' {
			return false
		}
	}
	return true
}
