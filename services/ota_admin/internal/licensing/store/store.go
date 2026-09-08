package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"github.com/google/uuid"
	_ "modernc.org/sqlite"
)

var (
	ErrNotFound           = errors.New("license record not found")
	ErrMachineBound       = errors.New("card is bound to another machine")
	ErrLeaseNotBound      = errors.New("lease has no current activation binding")
	ErrSoftwareNotAllowed = errors.New("software is not authorized by the card package")
	ErrSoftwareDisabled   = errors.New("software is disabled")
	ErrLicenseExpired     = errors.New("license is expired")
)

const busyTimeoutMilliseconds = 10_000

type Store struct {
	db *sql.DB
}

func Open(ctx context.Context, path string) (*Store, error) {
	if strings.TrimSpace(path) == "" {
		return nil, errors.New("license database path is required")
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return nil, fmt.Errorf("resolve license database path: %w", err)
	}
	if err := os.MkdirAll(filepath.Dir(abs), 0o700); err != nil {
		return nil, fmt.Errorf("create license database directory: %w", err)
	}
	uriPath := filepath.ToSlash(abs)
	if volume := filepath.VolumeName(abs); volume != "" && !strings.HasPrefix(uriPath, "/") {
		uriPath = "/" + uriPath
	}
	u := &url.URL{Scheme: "file", Path: uriPath}
	query := u.Query()
	query.Add("_pragma", "foreign_keys(1)")
	query.Add("_pragma", fmt.Sprintf("busy_timeout(%d)", busyTimeoutMilliseconds))
	u.RawQuery = query.Encode()
	db, err := sql.Open("sqlite", u.String())
	if err != nil {
		return nil, fmt.Errorf("open license sqlite: %w", err)
	}
	db.SetMaxOpenConns(8)
	db.SetMaxIdleConns(8)
	closeOnError := func(err error) (*Store, error) {
		_ = db.Close()
		return nil, err
	}
	if err := db.PingContext(ctx); err != nil {
		return closeOnError(fmt.Errorf("connect license sqlite: %w", err))
	}
	if err := migrate(ctx, db); err != nil {
		return closeOnError(err)
	}
	var mode string
	if err := retrySQLiteBusy(ctx, func() error {
		return db.QueryRowContext(ctx, `PRAGMA journal_mode=WAL`).Scan(&mode)
	}); err != nil {
		return closeOnError(fmt.Errorf("enable license sqlite WAL: %w", err))
	}
	if !strings.EqualFold(mode, "wal") {
		return closeOnError(fmt.Errorf("license sqlite journal mode is %q, want WAL", mode))
	}
	return &Store{db: db}, nil
}

func (s *Store) Close() error {
	if s == nil || s.db == nil {
		return nil
	}
	return s.db.Close()
}

func (s *Store) CreateSoftware(ctx context.Context, name string) (model.Software, error) {
	name = strings.TrimSpace(name)
	if name == "" {
		return model.Software{}, errors.New("software name is required")
	}
	software := model.Software{ID: uuid.NewString(), Name: name, Enabled: true, CreatedAt: time.Now().UTC().Truncate(time.Second)}
	_, err := s.db.ExecContext(ctx, `INSERT INTO license_software(id,name,enabled,created_at) VALUES(?,?,1,?)`, software.ID, software.Name, unixTime(software.CreatedAt))
	if err != nil {
		return model.Software{}, fmt.Errorf("create software: %w", err)
	}
	return software, nil
}

func (s *Store) SetSoftwareEnabled(ctx context.Context, id string, enabled bool) error {
	result, err := s.db.ExecContext(ctx, `UPDATE license_software SET enabled=? WHERE id=?`, enabled, id)
	if err != nil {
		return fmt.Errorf("set software enabled: %w", err)
	}
	return requireAffected(result)
}

func (s *Store) CreatePackage(ctx context.Context, name string, softwareIDs []string) (model.Package, error) {
	name = strings.TrimSpace(name)
	if name == "" {
		return model.Package{}, errors.New("package name is required")
	}
	ids, err := normalizedIDs(softwareIDs)
	if err != nil {
		return model.Package{}, err
	}
	pkg := model.Package{ID: uuid.NewString(), Name: name, SoftwareIDs: ids, CreatedAt: time.Now().UTC().Truncate(time.Second)}
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return model.Package{}, err
	}
	defer tx.Rollback()
	if _, err := tx.ExecContext(ctx, `INSERT INTO license_packages(id,name,created_at) VALUES(?,?,?)`, pkg.ID, pkg.Name, unixTime(pkg.CreatedAt)); err != nil {
		return model.Package{}, fmt.Errorf("create package: %w", err)
	}
	if err := insertPackageMembers(ctx, tx, pkg.ID, ids); err != nil {
		return model.Package{}, err
	}
	if err := tx.Commit(); err != nil {
		return model.Package{}, fmt.Errorf("commit package: %w", err)
	}
	return pkg, nil
}

func (s *Store) ReplacePackageMembers(ctx context.Context, id string, softwareIDs []string) error {
	ids, err := normalizedIDs(softwareIDs)
	if err != nil {
		return err
	}
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	defer tx.Rollback()
	var exists int
	if err := tx.QueryRowContext(ctx, `SELECT count(*) FROM license_packages WHERE id=?`, id).Scan(&exists); err != nil {
		return err
	}
	if exists == 0 {
		return ErrNotFound
	}
	if _, err := tx.ExecContext(ctx, `DELETE FROM license_package_members WHERE package_id=?`, id); err != nil {
		return fmt.Errorf("clear package members: %w", err)
	}
	if err := insertPackageMembers(ctx, tx, id, ids); err != nil {
		return err
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("commit package members: %w", err)
	}
	return nil
}

func (s *Store) GenerateCards(ctx context.Context, req model.GenerateCardsRequest, sealed []model.SealedCard) ([]model.Card, error) {
	if req.Count <= 0 || req.Count != len(sealed) {
		return nil, errors.New("card count must match sealed cards")
	}
	if req.BaseDays <= 0 {
		return nil, errors.New("base days must be positive")
	}
	createdAt := time.Now().UTC().Truncate(time.Second)
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	cards := make([]model.Card, 0, len(sealed))
	for _, item := range sealed {
		if _, err := uuid.Parse(item.ID); err != nil {
			return nil, fmt.Errorf("invalid card id: %w", err)
		}
		if len(item.Ciphertext) == 0 || item.FullDigest == "" || len(item.Prefix8) != 8 || len(item.Last4) != 4 {
			return nil, errors.New("sealed card is incomplete")
		}
		note := req.Note
		if item.Note != nil {
			note = item.Note
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO license_cards(id,package_id,ciphertext,full_digest,prefix8,last4,base_days,unactivated_total_days,note,created_at) VALUES(?,?,?,?,?,?,?,?,?,?)`,
			item.ID, req.PackageID, item.Ciphertext, item.FullDigest, item.Prefix8, item.Last4, req.BaseDays, req.BaseDays, note, unixTime(createdAt)); err != nil {
			return nil, fmt.Errorf("store sealed card: %w", err)
		}
		cards = append(cards, model.Card{ID: item.ID, PackageID: req.PackageID, FullDigest: item.FullDigest, Prefix8: item.Prefix8, Last4: item.Last4, BaseDays: req.BaseDays, UnactivatedTotalDays: req.BaseDays, Note: cloneString(note), CreatedAt: createdAt})
	}
	if err := tx.Commit(); err != nil {
		return nil, fmt.Errorf("commit generated cards: %w", err)
	}
	return cards, nil
}

// FindCardByDigest performs an exact lookup on the keyed full-card digest. It
// deliberately omits ciphertext, so normal service reads cannot expose the
// encrypted secret material.
func (s *Store) FindCardByDigest(ctx context.Context, digest string) (model.Card, error) {
	var card model.Card
	var note, machine sql.NullString
	var activatedAt, expiresAt sql.NullInt64
	var createdAt int64
	err := s.db.QueryRowContext(ctx, `SELECT id,package_id,full_digest,prefix8,last4,base_days,unactivated_total_days,note,activated_at,expires_at,machine_hash,created_at FROM license_cards WHERE full_digest=?`, digest).Scan(
		&card.ID, &card.PackageID, &card.FullDigest, &card.Prefix8, &card.Last4, &card.BaseDays, &card.UnactivatedTotalDays, &note, &activatedAt, &expiresAt, &machine, &createdAt,
	)
	if errors.Is(err, sql.ErrNoRows) {
		return model.Card{}, ErrNotFound
	}
	if err != nil {
		return model.Card{}, fmt.Errorf("find card by digest: %w", err)
	}
	card.Note = nullableString(note)
	card.ActivatedAt = nullableTime(activatedAt)
	card.ExpiresAt = nullableTime(expiresAt)
	card.MachineHash = nullableString(machine)
	card.CreatedAt = time.Unix(createdAt, 0).UTC()
	return card, nil
}

// GetSoftwareAuthorization returns the current software state and current
// membership in packageID. Callers must perform this query on every online
// authorization decision rather than cache package membership.
func (s *Store) GetSoftwareAuthorization(ctx context.Context, packageID, softwareID string) (model.Software, bool, error) {
	var software model.Software
	var enabled, member int
	var createdAt int64
	err := s.db.QueryRowContext(ctx, `SELECT id,name,enabled,created_at,EXISTS(SELECT 1 FROM license_package_members WHERE package_id=? AND software_id=license_software.id) FROM license_software WHERE id=?`, packageID, softwareID).Scan(
		&software.ID, &software.Name, &enabled, &createdAt, &member,
	)
	if errors.Is(err, sql.ErrNoRows) {
		return model.Software{}, false, ErrNotFound
	}
	if err != nil {
		return model.Software{}, false, fmt.Errorf("read software authorization: %w", err)
	}
	software.Enabled = enabled != 0
	software.CreatedAt = time.Unix(createdAt, 0).UTC()
	return software, member != 0, nil
}

func (s *Store) Activate(ctx context.Context, req model.ActivationMutation) (state model.ActivationState, err error) {
	if req.RequestID == "" || req.CardID == "" || req.SoftwareID == "" || req.MachineHash == "" {
		return state, errors.New("activation identifiers are required")
	}
	if req.ActivatedAt.IsZero() || req.ExpiresAt.Before(req.ActivatedAt) || req.LeaseUntil.IsZero() {
		return state, errors.New("invalid activation times")
	}
	req.ActivatedAt = canonicalTime(req.ActivatedAt)
	req.ExpiresAt = canonicalTime(req.ExpiresAt)
	req.LeaseUntil = canonicalTime(req.LeaseUntil)
	err = s.immediate(ctx, func(conn *sql.Conn) error {
		found, existing, err := queryActivationRequest(ctx, conn, req)
		if err != nil {
			return err
		}
		if found {
			state = existing
			return nil
		}

		var machine sql.NullString
		var storedActivatedAt, storedExpiresAt sql.NullInt64
		if err := conn.QueryRowContext(ctx, `SELECT machine_hash,activated_at,expires_at FROM license_cards WHERE id=?`, req.CardID).Scan(&machine, &storedActivatedAt, &storedExpiresAt); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return ErrNotFound
			}
			return err
		}
		if machine.Valid && machine.String != req.MachineHash {
			return ErrMachineBound
		}
		effectiveActivatedAt := req.ActivatedAt
		effectiveExpiresAt := req.ExpiresAt
		if storedActivatedAt.Valid || storedExpiresAt.Valid {
			if !storedActivatedAt.Valid || !storedExpiresAt.Valid {
				return errors.New("card has incomplete activation lifetime")
			}
			effectiveActivatedAt = time.Unix(storedActivatedAt.Int64, 0).UTC()
			effectiveExpiresAt = time.Unix(storedExpiresAt.Int64, 0).UTC()
		}
		if _, err := conn.ExecContext(ctx, `UPDATE license_cards SET activated_at=?,expires_at=?,machine_hash=?,unactivated_total_days=0 WHERE id=?`, unixTime(effectiveActivatedAt), unixTime(effectiveExpiresAt), req.MachineHash, req.CardID); err != nil {
			return err
		}
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_activation_bindings(card_id,software_id,machine_hash,activated_at,expires_at) VALUES(?,?,?,?,?) ON CONFLICT(card_id,software_id) DO UPDATE SET machine_hash=excluded.machine_hash,activated_at=excluded.activated_at,expires_at=excluded.expires_at`, req.CardID, req.SoftwareID, req.MachineHash, unixTime(effectiveActivatedAt), unixTime(effectiveExpiresAt)); err != nil {
			return err
		}
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_leases(card_id,software_id,machine_hash,last_seen_at,lease_until) VALUES(?,?,?,?,?) ON CONFLICT(card_id,software_id,machine_hash) DO UPDATE SET last_seen_at=excluded.last_seen_at,lease_until=excluded.lease_until`, req.CardID, req.SoftwareID, req.MachineHash, unixTime(req.ActivatedAt), unixTime(req.LeaseUntil)); err != nil {
			return err
		}
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_activation_requests(request_id,card_id,software_id,machine_hash,activated_at,expires_at,lease_until) VALUES(?,?,?,?,?,?,?)`, req.RequestID, req.CardID, req.SoftwareID, req.MachineHash, unixTime(effectiveActivatedAt), unixTime(effectiveExpiresAt), unixTime(req.LeaseUntil)); err != nil {
			return err
		}
		state = activationState(req, effectiveActivatedAt, effectiveExpiresAt)
		return nil
	})
	if err != nil {
		return model.ActivationState{}, fmt.Errorf("activate card: %w", err)
	}
	return state, nil
}

func (s *Store) RenewCard(ctx context.Context, id string, days int, now time.Time) error {
	if days <= 0 || now.IsZero() {
		return errors.New("renewal days and time must be positive")
	}
	return s.immediate(ctx, func(conn *sql.Conn) error {
		var activatedAt, expiresAt sql.NullInt64
		var unactivatedDays int64
		if err := conn.QueryRowContext(ctx, `SELECT activated_at,expires_at,unactivated_total_days FROM license_cards WHERE id=?`, id).Scan(&activatedAt, &expiresAt, &unactivatedDays); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return ErrNotFound
			}
			return err
		}
		if !activatedAt.Valid {
			if int64(days) > maximumInt64-unactivatedDays {
				return errors.New("unactivated renewal days overflow")
			}
			_, err := conn.ExecContext(ctx, `UPDATE license_cards SET unactivated_total_days=unactivated_total_days+? WHERE id=?`, days, id)
			return err
		}
		base := now.UTC()
		if expiry := time.Unix(expiresAt.Int64, 0).UTC(); expiry.After(base) {
			base = expiry
		}
		deltaSeconds := int64(days)
		if deltaSeconds > maximumInt64/secondsPerDay {
			return errors.New("renewal duration overflow")
		}
		deltaSeconds *= secondsPerDay
		if base.Unix() > maximumInt64-deltaSeconds {
			return errors.New("renewal expiry overflow")
		}
		newExpiry := time.Unix(base.Unix()+deltaSeconds, 0).UTC()
		if _, err := conn.ExecContext(ctx, `UPDATE license_cards SET expires_at=? WHERE id=?`, unixTime(newExpiry), id); err != nil {
			return err
		}
		_, err := conn.ExecContext(ctx, `UPDATE license_activation_bindings SET expires_at=? WHERE card_id=?`, unixTime(newExpiry), id)
		return err
	})
}

func (s *Store) UnbindCard(ctx context.Context, id string) error {
	// The AFTER trigger removes all machine-specific state in the same SQLite
	// statement while preserving the card's activation and expiry.
	result, err := s.db.ExecContext(ctx, `UPDATE license_cards SET machine_hash=NULL WHERE id=?`, id)
	if err != nil {
		return fmt.Errorf("unbind card: %w", err)
	}
	return requireAffected(result)
}

func (s *Store) DeleteCard(ctx context.Context, id string) error {
	return s.immediate(ctx, func(conn *sql.Conn) error {
		result, err := conn.ExecContext(ctx, `DELETE FROM license_cards WHERE id=?`, id)
		if err != nil {
			return err
		}
		return requireAffected(result)
	})
}

func (s *Store) SearchCards(ctx context.Context, filter model.CardFilter) ([]model.CardSummary, error) {
	query := `SELECT id,package_id,prefix8,last4,base_days,unactivated_total_days,note,activated_at,expires_at,machine_hash,created_at FROM license_cards WHERE 1=1`
	args := make([]any, 0, 5)
	if filter.FullDigest != "" {
		query += ` AND full_digest=?`
		args = append(args, filter.FullDigest)
	}
	if filter.Prefix8 != "" {
		query += ` AND prefix8=?`
		args = append(args, filter.Prefix8)
	}
	if filter.Last4 != "" {
		query += ` AND last4=?`
		args = append(args, filter.Last4)
	}
	if filter.NoteContains != "" {
		// SQLite lower() gives deterministic ASCII case-insensitive semantics;
		// instr treats %, _, and all other input as literal text.
		query += ` AND note IS NOT NULL AND instr(lower(note),lower(?))>0`
		args = append(args, filter.NoteContains)
	}
	if filter.PackageID != "" {
		query += ` AND package_id=?`
		args = append(args, filter.PackageID)
	}
	query += ` ORDER BY created_at,id`
	rows, err := s.db.QueryContext(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("search cards: %w", err)
	}
	defer rows.Close()
	var cards []model.CardSummary
	for rows.Next() {
		card, err := scanCardSummary(rows)
		if err != nil {
			return nil, err
		}
		cards = append(cards, card)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return cards, nil
}

// ClaimReplayNonce atomically removes expired copies of nonce and claims the
// nonce until expiresAt. A false result means it is still live and is a replay.
func (s *Store) ClaimReplayNonce(ctx context.Context, nonce string, expiresAt, now time.Time) (bool, error) {
	if nonce == "" || !expiresAt.After(now) {
		return false, errors.New("invalid replay nonce or expiry")
	}
	result, err := s.db.ExecContext(ctx, `INSERT INTO license_replay_nonces(nonce,expires_at) VALUES(?,?) ON CONFLICT(nonce) DO UPDATE SET expires_at=excluded.expires_at WHERE license_replay_nonces.expires_at<=?`, nonce, ceilUnixSecond(expiresAt), unixTime(now))
	if err != nil {
		return false, fmt.Errorf("claim replay nonce: %w", err)
	}
	affected, err := result.RowsAffected()
	return affected == 1, err
}

// RecordLease persists a service-side lease. To avoid making every validation
// a write, last_seen advances at most once per hour; lease_until may advance on
// every call so a freshly issued lease is faithfully represented.
func (s *Store) RecordLease(ctx context.Context, lease model.Lease) (model.Lease, error) {
	if lease.CardID == "" || lease.SoftwareID == "" || lease.MachineHash == "" || lease.LastSeenAt.IsZero() || lease.LeaseUntil.IsZero() {
		return model.Lease{}, errors.New("lease fields are required")
	}
	result, err := s.db.ExecContext(ctx, `INSERT INTO license_leases(card_id,software_id,machine_hash,last_seen_at,lease_until)
SELECT ?,?,?,?,? WHERE EXISTS (
    SELECT 1 FROM license_activation_bindings WHERE card_id=? AND software_id=? AND machine_hash=?
)
ON CONFLICT(card_id,software_id,machine_hash) DO UPDATE SET
last_seen_at=CASE WHEN excluded.last_seen_at>=license_leases.last_seen_at+3600 THEN excluded.last_seen_at ELSE license_leases.last_seen_at END,
lease_until=CASE WHEN excluded.lease_until>license_leases.lease_until THEN excluded.lease_until ELSE license_leases.lease_until END`,
		lease.CardID, lease.SoftwareID, lease.MachineHash, unixTime(lease.LastSeenAt), unixTime(lease.LeaseUntil), lease.CardID, lease.SoftwareID, lease.MachineHash)
	if err != nil {
		return model.Lease{}, fmt.Errorf("record lease: %w", err)
	}
	affected, err := result.RowsAffected()
	if err != nil {
		return model.Lease{}, err
	}
	if affected == 0 {
		return model.Lease{}, ErrLeaseNotBound
	}
	var seen, until int64
	if err := s.db.QueryRowContext(ctx, `SELECT last_seen_at,lease_until FROM license_leases WHERE card_id=? AND software_id=? AND machine_hash=?`, lease.CardID, lease.SoftwareID, lease.MachineHash).Scan(&seen, &until); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return model.Lease{}, ErrLeaseNotBound
		}
		return model.Lease{}, err
	}
	lease.LastSeenAt = time.Unix(seen, 0).UTC()
	lease.LeaseUntil = time.Unix(until, 0).UTC()
	return lease, nil
}

// ValidateAndRecordLease atomically establishes a product-specific binding for
// an already activated card and records its lease. The immediate transaction is
// serialized with UnbindCard's write, so an authorization that observed a stale
// preflight read cannot recreate bindings or leases after an admin unbind.
func (s *Store) ValidateAndRecordLease(ctx context.Context, lease model.Lease) (state model.ActivationState, err error) {
	if lease.CardID == "" || lease.SoftwareID == "" || lease.MachineHash == "" || lease.LastSeenAt.IsZero() || lease.LeaseUntil.IsZero() {
		return state, errors.New("validation lease fields are required")
	}
	lease.LastSeenAt = canonicalTime(lease.LastSeenAt)
	lease.LeaseUntil = canonicalTime(lease.LeaseUntil)
	err = s.immediate(ctx, func(conn *sql.Conn) error {
		var packageID string
		var machine sql.NullString
		var activatedAt, expiresAt sql.NullInt64
		if err := conn.QueryRowContext(ctx, `SELECT package_id,machine_hash,activated_at,expires_at FROM license_cards WHERE id=?`, lease.CardID).Scan(&packageID, &machine, &activatedAt, &expiresAt); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return ErrNotFound
			}
			return err
		}
		if !machine.Valid || !activatedAt.Valid || !expiresAt.Valid {
			return ErrLeaseNotBound
		}
		if machine.String != lease.MachineHash {
			return ErrMachineBound
		}

		var enabled, member int
		if err := conn.QueryRowContext(ctx, `SELECT enabled,EXISTS(SELECT 1 FROM license_package_members WHERE package_id=? AND software_id=license_software.id) FROM license_software WHERE id=?`, packageID, lease.SoftwareID).Scan(&enabled, &member); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return ErrSoftwareNotAllowed
			}
			return err
		}
		if member == 0 {
			return ErrSoftwareNotAllowed
		}
		if enabled == 0 {
			return ErrSoftwareDisabled
		}

		activationTime := time.Unix(activatedAt.Int64, 0).UTC()
		expiryTime := time.Unix(expiresAt.Int64, 0).UTC()
		if !expiryTime.After(lease.LastSeenAt) {
			return ErrLicenseExpired
		}
		if lease.LeaseUntil.After(expiryTime) {
			lease.LeaseUntil = expiryTime
		}
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_activation_bindings(card_id,software_id,machine_hash,activated_at,expires_at) VALUES(?,?,?,?,?) ON CONFLICT(card_id,software_id) DO UPDATE SET machine_hash=excluded.machine_hash,activated_at=excluded.activated_at,expires_at=excluded.expires_at`, lease.CardID, lease.SoftwareID, lease.MachineHash, activatedAt.Int64, expiresAt.Int64); err != nil {
			return err
		}
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_leases(card_id,software_id,machine_hash,last_seen_at,lease_until) VALUES(?,?,?,?,?)
ON CONFLICT(card_id,software_id,machine_hash) DO UPDATE SET
last_seen_at=CASE WHEN excluded.last_seen_at>=license_leases.last_seen_at+3600 THEN excluded.last_seen_at ELSE license_leases.last_seen_at END,
lease_until=CASE WHEN excluded.lease_until>license_leases.lease_until THEN excluded.lease_until ELSE license_leases.lease_until END`, lease.CardID, lease.SoftwareID, lease.MachineHash, unixTime(lease.LastSeenAt), unixTime(lease.LeaseUntil)); err != nil {
			return err
		}
		var leaseUntil int64
		if err := conn.QueryRowContext(ctx, `SELECT lease_until FROM license_leases WHERE card_id=? AND software_id=? AND machine_hash=?`, lease.CardID, lease.SoftwareID, lease.MachineHash).Scan(&leaseUntil); err != nil {
			return err
		}
		state = model.ActivationState{CardID: lease.CardID, SoftwareID: lease.SoftwareID, MachineHash: lease.MachineHash, ActivatedAt: activationTime, ExpiresAt: expiryTime, LeaseUntil: time.Unix(leaseUntil, 0).UTC()}
		return nil
	})
	if err != nil {
		return model.ActivationState{}, fmt.Errorf("validate card: %w", err)
	}
	return state, nil
}

func (s *Store) immediate(ctx context.Context, fn func(*sql.Conn) error) (err error) {
	conn, err := s.db.Conn(ctx)
	if err != nil {
		return err
	}
	defer conn.Close()
	if _, err := conn.ExecContext(ctx, `BEGIN IMMEDIATE`); err != nil {
		return err
	}
	committed := false
	defer func() {
		if !committed {
			_, _ = conn.ExecContext(context.Background(), `ROLLBACK`)
		}
	}()
	if err := fn(conn); err != nil {
		return err
	}
	if _, err := conn.ExecContext(ctx, `COMMIT`); err != nil {
		return err
	}
	committed = true
	return nil
}

type queryRower interface {
	QueryRowContext(context.Context, string, ...any) *sql.Row
}

func queryActivationRequest(ctx context.Context, q queryRower, req model.ActivationMutation) (bool, model.ActivationState, error) {
	var activatedAt, expiresAt, leaseUntil int64
	err := q.QueryRowContext(ctx, `SELECT activated_at,expires_at,lease_until FROM license_activation_requests WHERE request_id=? AND card_id=? AND software_id=? AND machine_hash=?`, req.RequestID, req.CardID, req.SoftwareID, req.MachineHash).Scan(&activatedAt, &expiresAt, &leaseUntil)
	if errors.Is(err, sql.ErrNoRows) {
		return false, model.ActivationState{}, nil
	}
	if err != nil {
		return false, model.ActivationState{}, err
	}
	return true, model.ActivationState{RequestID: req.RequestID, CardID: req.CardID, SoftwareID: req.SoftwareID, MachineHash: req.MachineHash, ActivatedAt: time.Unix(activatedAt, 0).UTC(), ExpiresAt: time.Unix(expiresAt, 0).UTC(), LeaseUntil: time.Unix(leaseUntil, 0).UTC()}, nil
}

func activationState(req model.ActivationMutation, activatedAt, expiresAt time.Time) model.ActivationState {
	return model.ActivationState{RequestID: req.RequestID, CardID: req.CardID, SoftwareID: req.SoftwareID, MachineHash: req.MachineHash, ActivatedAt: activatedAt, ExpiresAt: expiresAt, LeaseUntil: req.LeaseUntil}
}

type rowScanner interface {
	Scan(...any) error
}

func scanCardSummary(row rowScanner) (model.CardSummary, error) {
	var card model.CardSummary
	var note, machine sql.NullString
	var activated, expires sql.NullInt64
	var created int64
	if err := row.Scan(&card.ID, &card.PackageID, &card.Prefix8, &card.Last4, &card.BaseDays, &card.UnactivatedTotalDays, &note, &activated, &expires, &machine, &created); err != nil {
		return card, err
	}
	card.Note = nullableString(note)
	card.ActivatedAt = nullableTime(activated)
	card.ExpiresAt = nullableTime(expires)
	card.MachineHash = nullableString(machine)
	card.CreatedAt = time.Unix(created, 0).UTC()
	return card, nil
}

func insertPackageMembers(ctx context.Context, tx *sql.Tx, packageID string, softwareIDs []string) error {
	for _, softwareID := range softwareIDs {
		if _, err := tx.ExecContext(ctx, `INSERT INTO license_package_members(package_id,software_id) VALUES(?,?)`, packageID, softwareID); err != nil {
			return fmt.Errorf("insert package member: %w", err)
		}
	}
	return nil
}

func normalizedIDs(ids []string) ([]string, error) {
	if len(ids) == 0 {
		return nil, errors.New("package requires at least one software member")
	}
	seen := make(map[string]struct{}, len(ids))
	result := make([]string, 0, len(ids))
	for _, id := range ids {
		if _, err := uuid.Parse(id); err != nil {
			return nil, fmt.Errorf("invalid software id: %w", err)
		}
		if _, exists := seen[id]; exists {
			continue
		}
		seen[id] = struct{}{}
		result = append(result, id)
	}
	sort.Strings(result)
	return result, nil
}

func requireAffected(result sql.Result) error {
	affected, err := result.RowsAffected()
	if err != nil {
		return err
	}
	if affected == 0 {
		return ErrNotFound
	}
	return nil
}

func unixTime(value time.Time) int64 { return value.UTC().Unix() }

func ceilUnixSecond(value time.Time) int64 {
	second := value.UTC().Unix()
	if value.Nanosecond() != 0 && second < maximumInt64 {
		second++
	}
	return second
}

const (
	maximumInt64  = int64(^uint64(0) >> 1)
	secondsPerDay = int64(24 * 60 * 60)
)

func canonicalTime(value time.Time) time.Time {
	return time.Unix(value.UTC().Unix(), 0).UTC()
}

func retrySQLiteBusy(ctx context.Context, operation func() error) error {
	var err error
	for attempt := 0; attempt < 20; attempt++ {
		if err = operation(); err == nil || !isSQLiteBusy(err) {
			return err
		}
		delay := time.Duration(attempt+1) * 5 * time.Millisecond
		if delay > 100*time.Millisecond {
			delay = 100 * time.Millisecond
		}
		timer := time.NewTimer(delay)
		select {
		case <-ctx.Done():
			if !timer.Stop() {
				<-timer.C
			}
			return ctx.Err()
		case <-timer.C:
		}
	}
	return err
}

func isSQLiteBusy(err error) bool {
	message := strings.ToLower(err.Error())
	return strings.Contains(message, "sqlite_busy") || strings.Contains(message, "database is locked") || strings.Contains(message, "database table is locked")
}

func nullableTime(value sql.NullInt64) *time.Time {
	if !value.Valid {
		return nil
	}
	t := time.Unix(value.Int64, 0).UTC()
	return &t
}

func nullableString(value sql.NullString) *string {
	if !value.Valid {
		return nil
	}
	result := value.String
	return &result
}

func cloneString(value *string) *string {
	if value == nil {
		return nil
	}
	result := *value
	return &result
}
