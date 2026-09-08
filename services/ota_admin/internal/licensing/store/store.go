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
	ErrNotFound                 = errors.New("license record not found")
	ErrMachineBound             = errors.New("card is bound to another machine")
	ErrLeaseNotBound            = errors.New("lease has no current activation binding")
	ErrSoftwareNotAllowed       = errors.New("software is not authorized by the card package")
	ErrSoftwareDisabled         = errors.New("software is disabled")
	ErrLicenseExpired           = errors.New("license is expired")
	ErrReplayNonceCapacity      = errors.New("replay nonce capacity reached")
	ErrConflict                 = errors.New("license record conflicts with existing state")
	ErrAdminIdempotencyCapacity = errors.New("admin idempotency capacity reached")
)

const busyTimeoutMilliseconds = 10_000

type Store struct {
	db *sql.DB
}

type AdminTransaction struct {
	conn            *sql.Conn
	active          bool
	dirty           bool
	failed          bool
	createdAt       time.Time
	replayCardIDs   map[string]struct{}
	maxReplayBytes  int64
	usedReplayBytes int64
}

func (s *Store) BeginAdminMutation(ctx context.Context, key, fingerprint string, now, expiresAt time.Time, maximumEntries int, maximumBytes int64) (*AdminTransaction, model.AdminIdempotencyRecord, model.AdminIdempotencyOutcome, error) {
	if !validCanonicalUUID(key) || len(fingerprint) != 64 || !expiresAt.After(now) || maximumEntries < 1 || maximumBytes < 1 {
		return nil, model.AdminIdempotencyRecord{}, "", errors.New("invalid admin mutation reservation")
	}
	conn, err := s.db.Conn(ctx)
	if err != nil {
		return nil, model.AdminIdempotencyRecord{}, "", err
	}
	if _, err := conn.ExecContext(ctx, `BEGIN IMMEDIATE`); err != nil {
		_ = conn.Close()
		return nil, model.AdminIdempotencyRecord{}, "", err
	}
	tx := &AdminTransaction{conn: conn, active: true, createdAt: canonicalTime(now), replayCardIDs: make(map[string]struct{})}
	fail := func(err error) (*AdminTransaction, model.AdminIdempotencyRecord, model.AdminIdempotencyOutcome, error) {
		tx.Rollback()
		return nil, model.AdminIdempotencyRecord{}, "", err
	}
	if _, err := conn.ExecContext(ctx, `DELETE FROM license_admin_idempotency WHERE expires_at<=? AND status<>102`, unixTime(now)); err != nil {
		return fail(err)
	}
	var record model.AdminIdempotencyRecord
	var storedExpires int64
	err = conn.QueryRowContext(ctx, `SELECT fingerprint,status,response_body,replay_key_id,content_type,content_disposition,expires_at FROM license_admin_idempotency WHERE key=?`, key).Scan(&record.Fingerprint, &record.Status, &record.Body, &record.ReplayKeyID, &record.ContentType, &record.ContentDisposition, &storedExpires)
	if err == nil {
		record.ExpiresAt = time.Unix(storedExpires, 0).UTC()
		outcome := model.AdminIdempotencyReplay
		if record.Fingerprint != fingerprint {
			outcome = model.AdminIdempotencyConflict
		} else if record.Status == httpStatusProcessing {
			outcome = model.AdminIdempotencyPending
		}
		tx.Rollback()
		return nil, record, outcome, nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return fail(err)
	}
	var count int
	var usedBytes int64
	if err := conn.QueryRowContext(ctx, `SELECT count(*),COALESCE(sum(length(response_body)),0) FROM license_admin_idempotency`).Scan(&count, &usedBytes); err != nil {
		return fail(err)
	}
	if count >= maximumEntries || usedBytes >= maximumBytes {
		return fail(ErrAdminIdempotencyCapacity)
	}
	tx.maxReplayBytes = maximumBytes
	tx.usedReplayBytes = usedBytes
	return tx, model.AdminIdempotencyRecord{}, model.AdminIdempotencyNew, nil
}

const httpStatusProcessing = 102

func (tx *AdminTransaction) Rollback() {
	if tx == nil || !tx.active {
		return
	}
	_, _ = tx.conn.ExecContext(context.Background(), `ROLLBACK`)
	_ = tx.conn.Close()
	tx.active = false
}

func (tx *AdminTransaction) Complete(ctx context.Context, key string, record model.AdminIdempotencyRecord) error {
	if tx == nil || !tx.active {
		return errors.New("admin transaction is not active")
	}
	if tx.failed {
		tx.Rollback()
		return errors.New("admin transaction mutation failed")
	}
	if record.ReplayKeyID == "" {
		tx.Rollback()
		return errors.New("admin replay key ID is required")
	}
	if int64(len(record.Body)) > tx.maxReplayBytes-tx.usedReplayBytes {
		tx.Rollback()
		return ErrAdminIdempotencyCapacity
	}
	_, err := tx.conn.ExecContext(ctx, `INSERT INTO license_admin_idempotency(key,fingerprint,status,response_body,replay_key_id,content_type,content_disposition,expires_at,created_at) VALUES(?,?,?,?,?,?,?,?,?)`, key, record.Fingerprint, record.Status, record.Body, record.ReplayKeyID, record.ContentType, record.ContentDisposition, ceilUnixSecond(record.ExpiresAt), unixTime(tx.createdAt))
	if err == nil {
		for cardID := range tx.replayCardIDs {
			if _, err = tx.conn.ExecContext(ctx, `INSERT INTO license_admin_idempotency_cards(idempotency_key,card_id) VALUES(?,?)`, key, cardID); err != nil {
				break
			}
		}
	}
	if err == nil {
		_, err = tx.conn.ExecContext(ctx, `COMMIT`)
	}
	if err != nil {
		tx.Rollback()
		return err
	}
	_ = tx.conn.Close()
	tx.active = false
	return nil
}

func (tx *AdminTransaction) CreateSoftwareAt(ctx context.Context, name string, createdAt time.Time) (model.Software, error) {
	if tx == nil || !tx.active || model.ValidateAdminName(name) != nil || createdAt.IsZero() {
		return model.Software{}, errors.New("invalid software")
	}
	software := model.Software{ID: uuid.NewString(), Name: name, Enabled: true, CreatedAt: canonicalTime(createdAt)}
	if _, err := tx.conn.ExecContext(ctx, `INSERT INTO license_software(id,name,enabled,created_at) VALUES(?,?,1,?)`, software.ID, software.Name, unixTime(software.CreatedAt)); err != nil {
		if isUniqueConstraint(err) {
			return model.Software{}, ErrConflict
		}
		return model.Software{}, err
	}
	tx.dirty = true
	return software, nil
}

func (tx *AdminTransaction) RenameSoftware(ctx context.Context, id, name string) error {
	if tx == nil || !tx.active || model.ValidateAdminName(name) != nil {
		return errors.New("invalid software")
	}
	result, err := tx.conn.ExecContext(ctx, `UPDATE license_software SET name=? WHERE id=?`, name, id)
	if err != nil {
		if isUniqueConstraint(err) {
			return ErrConflict
		}
		return err
	}
	if err := requireAffected(result); err != nil {
		return err
	}
	tx.dirty = true
	return nil
}

func (tx *AdminTransaction) SetSoftwareEnabled(ctx context.Context, id string, enabled bool) error {
	if tx == nil || !tx.active {
		return errors.New("admin transaction is not active")
	}
	result, err := tx.conn.ExecContext(ctx, `UPDATE license_software SET enabled=? WHERE id=?`, enabled, id)
	if err != nil {
		return err
	}
	if err := requireAffected(result); err != nil {
		return err
	}
	tx.dirty = true
	return nil
}

func (tx *AdminTransaction) CreatePackageAt(ctx context.Context, name string, softwareIDs []string, createdAt time.Time) (model.Package, error) {
	if tx == nil || !tx.active || model.ValidatePackageName(name) != nil || createdAt.IsZero() {
		return model.Package{}, errors.New("invalid package")
	}
	ids, err := normalizedIDs(softwareIDs)
	if err != nil {
		return model.Package{}, err
	}
	if err := tx.ensureSoftwareExists(ctx, ids); err != nil {
		return model.Package{}, err
	}
	pkg := model.Package{ID: uuid.NewString(), Name: strings.TrimSpace(name), SoftwareIDs: ids, CreatedAt: canonicalTime(createdAt)}
	if _, err := tx.conn.ExecContext(ctx, `INSERT INTO license_packages(id,name,created_at) VALUES(?,?,?)`, pkg.ID, pkg.Name, unixTime(pkg.CreatedAt)); err != nil {
		if isUniqueConstraint(err) {
			return model.Package{}, ErrConflict
		}
		return model.Package{}, err
	}
	tx.dirty = true
	if err := insertPackageMembers(ctx, tx.conn, pkg.ID, ids); err != nil {
		tx.failed = true
		return model.Package{}, err
	}
	return pkg, nil
}

func (tx *AdminTransaction) RenamePackage(ctx context.Context, id, name string) error {
	if tx == nil || !tx.active || model.ValidatePackageName(name) != nil {
		return errors.New("invalid package")
	}
	result, err := tx.conn.ExecContext(ctx, `UPDATE license_packages SET name=? WHERE id=?`, name, id)
	if err != nil {
		if isUniqueConstraint(err) {
			return ErrConflict
		}
		return err
	}
	if err := requireAffected(result); err != nil {
		return err
	}
	tx.dirty = true
	return nil
}

func (tx *AdminTransaction) ReplacePackageMembers(ctx context.Context, id string, softwareIDs []string) error {
	if tx == nil || !tx.active {
		return errors.New("admin transaction is not active")
	}
	ids, err := normalizedIDs(softwareIDs)
	if err != nil {
		return err
	}
	var exists int
	if err := tx.conn.QueryRowContext(ctx, `SELECT count(*) FROM license_packages WHERE id=?`, id).Scan(&exists); err != nil {
		return err
	}
	if exists == 0 {
		return ErrNotFound
	}
	if err := tx.ensureSoftwareExists(ctx, ids); err != nil {
		return err
	}
	if _, err := tx.conn.ExecContext(ctx, `DELETE FROM license_package_members WHERE package_id=?`, id); err != nil {
		return err
	}
	tx.dirty = true
	if err := insertPackageMembers(ctx, tx.conn, id, ids); err != nil {
		tx.failed = true
		return err
	}
	return nil
}

func (tx *AdminTransaction) GenerateCards(ctx context.Context, req model.GenerateCardsRequest, sealed []model.SealedCard) ([]model.Card, error) {
	if tx == nil || !tx.active || req.Count <= 0 || req.Count != len(sealed) || req.BaseDays <= 0 || req.CreatedAt.IsZero() {
		return nil, errors.New("invalid generated cards")
	}
	createdAt := canonicalTime(req.CreatedAt)
	cards := make([]model.Card, 0, len(sealed))
	for _, item := range sealed {
		if _, err := uuid.Parse(item.ID); err != nil || len(item.Ciphertext) == 0 || item.FullDigest == "" || len(item.Prefix8) != 8 || len(item.Last4) != 4 {
			return nil, errors.New("sealed card is invalid")
		}
	}
	for _, item := range sealed {
		note := req.Note
		if item.Note != nil {
			note = item.Note
		}
		keyID := item.EncryptionKeyID
		if keyID == "" {
			keyID = "legacy-v2"
		}
		if _, err := tx.conn.ExecContext(ctx, `INSERT INTO license_cards(id,package_id,ciphertext,encryption_key_id,full_digest,prefix8,last4,base_days,unactivated_total_days,note,created_at) VALUES(?,?,?,?,?,?,?,?,?,?,?)`, item.ID, req.PackageID, item.Ciphertext, keyID, item.FullDigest, item.Prefix8, item.Last4, req.BaseDays, req.BaseDays, note, unixTime(createdAt)); err != nil {
			if tx.dirty {
				tx.failed = true
			}
			if isForeignKeyConstraint(err) {
				return nil, ErrNotFound
			}
			if isUniqueConstraint(err) {
				return nil, ErrConflict
			}
			return nil, err
		}
		tx.dirty = true
		tx.replayCardIDs[item.ID] = struct{}{}
		cards = append(cards, model.Card{ID: item.ID, PackageID: req.PackageID, FullDigest: item.FullDigest, Prefix8: item.Prefix8, Last4: item.Last4, BaseDays: req.BaseDays, UnactivatedTotalDays: req.BaseDays, Note: cloneString(note), CreatedAt: createdAt})
	}
	return cards, nil
}

func (tx *AdminTransaction) AssociateReplayCards(cardIDs []string) error {
	if tx == nil || !tx.active {
		return errors.New("admin transaction is not active")
	}
	for _, cardID := range cardIDs {
		if !validCanonicalUUID(cardID) {
			return errors.New("invalid replay card ID")
		}
		tx.replayCardIDs[cardID] = struct{}{}
	}
	return nil
}

func (tx *AdminTransaction) ensureSoftwareExists(ctx context.Context, ids []string) error {
	for _, id := range ids {
		var exists int
		if err := tx.conn.QueryRowContext(ctx, `SELECT count(*) FROM license_software WHERE id=?`, id).Scan(&exists); err != nil {
			return err
		}
		if exists == 0 {
			return ErrNotFound
		}
	}
	return nil
}

func (tx *AdminTransaction) SetCardNote(ctx context.Context, id string, note *string) error {
	return tx.singleCardMutation(ctx, `UPDATE license_cards SET note=? WHERE id=?`, note, id)
}

func (tx *AdminTransaction) UnbindCard(ctx context.Context, id string) error {
	return tx.singleCardMutation(ctx, `UPDATE license_cards SET machine_hash=NULL WHERE id=?`, id)
}

func (tx *AdminTransaction) DeleteCard(ctx context.Context, id string) error {
	return tx.singleCardMutation(ctx, `DELETE FROM license_cards WHERE id=?`, id)
}

func (tx *AdminTransaction) singleCardMutation(ctx context.Context, statement string, args ...any) error {
	if tx == nil || !tx.active {
		return errors.New("admin transaction is not active")
	}
	result, err := tx.conn.ExecContext(ctx, statement, args...)
	if err != nil {
		return err
	}
	if err := requireAffected(result); err != nil {
		return err
	}
	tx.dirty = true
	return nil
}

func (tx *AdminTransaction) RenewCard(ctx context.Context, id string, days int, now time.Time) error {
	if tx == nil || !tx.active || days <= 0 || now.IsZero() {
		return errors.New("invalid renewal")
	}
	var activatedAt, expiresAt sql.NullInt64
	var unactivatedDays int64
	if err := tx.conn.QueryRowContext(ctx, `SELECT activated_at,expires_at,unactivated_total_days FROM license_cards WHERE id=?`, id).Scan(&activatedAt, &expiresAt, &unactivatedDays); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return ErrNotFound
		}
		return err
	}
	if !activatedAt.Valid {
		if int64(days) > maximumInt64-unactivatedDays {
			return errors.New("unactivated renewal days overflow")
		}
		return tx.singleCardMutation(ctx, `UPDATE license_cards SET unactivated_total_days=unactivated_total_days+? WHERE id=?`, days, id)
	}
	if int64(days) > maximumInt64/secondsPerDay {
		return errors.New("renewal duration overflow")
	}
	base := now.UTC()
	if expiry := time.Unix(expiresAt.Int64, 0).UTC(); expiry.After(base) {
		base = expiry
	}
	deltaSeconds := int64(days) * secondsPerDay
	if base.Unix() > maximumInt64-deltaSeconds {
		return errors.New("renewal expiry overflow")
	}
	newExpiry := time.Unix(base.Unix()+deltaSeconds, 0).UTC()
	if _, err := tx.conn.ExecContext(ctx, `UPDATE license_cards SET expires_at=? WHERE id=?`, unixTime(newExpiry), id); err != nil {
		return err
	}
	tx.dirty = true
	if _, err := tx.conn.ExecContext(ctx, `UPDATE license_activation_bindings SET expires_at=? WHERE card_id=?`, unixTime(newExpiry), id); err != nil {
		tx.failed = true
		return err
	}
	return nil
}

func validCanonicalUUID(value string) bool {
	parsed, err := uuid.Parse(value)
	return err == nil && parsed.String() == value
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
	if err := validatePersistedPackageNames(ctx, db); err != nil {
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
	return s.CreateSoftwareAt(ctx, name, time.Now().UTC())
}

func (s *Store) CreateSoftwareAt(ctx context.Context, name string, createdAt time.Time) (model.Software, error) {
	if err := model.ValidateAdminName(name); err != nil {
		return model.Software{}, errors.New("software name is required")
	}
	software := model.Software{ID: uuid.NewString(), Name: name, Enabled: true, CreatedAt: canonicalTime(createdAt)}
	_, err := s.db.ExecContext(ctx, `INSERT INTO license_software(id,name,enabled,created_at) VALUES(?,?,1,?)`, software.ID, software.Name, unixTime(software.CreatedAt))
	if err != nil {
		if isUniqueConstraint(err) {
			return model.Software{}, ErrConflict
		}
		return model.Software{}, fmt.Errorf("create software: %w", err)
	}
	return software, nil
}

func (s *Store) ListSoftware(ctx context.Context, offset, limit int) ([]model.Software, int, error) {
	if offset < 0 || limit < 1 {
		return nil, 0, errors.New("invalid software page")
	}
	var total int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_software`).Scan(&total); err != nil {
		return nil, 0, fmt.Errorf("count software: %w", err)
	}
	rows, err := s.db.QueryContext(ctx, `SELECT id,name,enabled,created_at FROM license_software ORDER BY created_at,id LIMIT ? OFFSET ?`, limit, offset)
	if err != nil {
		return nil, 0, fmt.Errorf("list software: %w", err)
	}
	defer rows.Close()
	items := make([]model.Software, 0, limit)
	for rows.Next() {
		var item model.Software
		var enabled int
		var created int64
		if err := rows.Scan(&item.ID, &item.Name, &enabled, &created); err != nil {
			return nil, 0, fmt.Errorf("scan software: %w", err)
		}
		item.Enabled = enabled != 0
		item.CreatedAt = time.Unix(created, 0).UTC()
		items = append(items, item)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, fmt.Errorf("list software: %w", err)
	}
	return items, total, nil
}

func (s *Store) RenameSoftware(ctx context.Context, id, name string) error {
	if err := model.ValidateAdminName(name); err != nil {
		return errors.New("software name is invalid")
	}
	result, err := s.db.ExecContext(ctx, `UPDATE license_software SET name=? WHERE id=?`, name, id)
	if err != nil {
		if isUniqueConstraint(err) {
			return ErrConflict
		}
		return fmt.Errorf("rename software: %w", err)
	}
	return requireAffected(result)
}

func (s *Store) SetSoftwareEnabled(ctx context.Context, id string, enabled bool) error {
	result, err := s.db.ExecContext(ctx, `UPDATE license_software SET enabled=? WHERE id=?`, enabled, id)
	if err != nil {
		return fmt.Errorf("set software enabled: %w", err)
	}
	return requireAffected(result)
}

func (s *Store) CreatePackage(ctx context.Context, name string, softwareIDs []string) (model.Package, error) {
	return s.CreatePackageAt(ctx, name, softwareIDs, time.Now().UTC())
}

func (s *Store) CreatePackageAt(ctx context.Context, name string, softwareIDs []string, createdAt time.Time) (model.Package, error) {
	name = strings.TrimSpace(name)
	if err := model.ValidatePackageName(name); err != nil {
		return model.Package{}, errors.New("package name is invalid")
	}
	ids, err := normalizedIDs(softwareIDs)
	if err != nil {
		return model.Package{}, err
	}
	pkg := model.Package{ID: uuid.NewString(), Name: name, SoftwareIDs: ids, CreatedAt: canonicalTime(createdAt)}
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return model.Package{}, err
	}
	defer tx.Rollback()
	if _, err := tx.ExecContext(ctx, `INSERT INTO license_packages(id,name,created_at) VALUES(?,?,?)`, pkg.ID, pkg.Name, unixTime(pkg.CreatedAt)); err != nil {
		if isUniqueConstraint(err) {
			return model.Package{}, ErrConflict
		}
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

func (s *Store) ListPackages(ctx context.Context, offset, limit int) ([]model.Package, int, error) {
	if offset < 0 || limit < 1 {
		return nil, 0, errors.New("invalid package page")
	}
	var total int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_packages`).Scan(&total); err != nil {
		return nil, 0, fmt.Errorf("count packages: %w", err)
	}
	rows, err := s.db.QueryContext(ctx, `SELECT p.id,p.name,p.created_at,COALESCE(group_concat(m.software_id,','),'')
FROM (SELECT id,name,created_at FROM license_packages ORDER BY created_at,id LIMIT ? OFFSET ?) p
LEFT JOIN license_package_members m ON m.package_id=p.id
GROUP BY p.id,p.name,p.created_at ORDER BY p.created_at,p.id`, limit, offset)
	if err != nil {
		return nil, 0, fmt.Errorf("list packages: %w", err)
	}
	defer rows.Close()
	items := make([]model.Package, 0, limit)
	for rows.Next() {
		item, err := scanPackage(rows)
		if err != nil {
			return nil, 0, err
		}
		items = append(items, item)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, err
	}
	return items, total, nil
}

func (s *Store) GetPackage(ctx context.Context, id string) (model.Package, error) {
	row := s.db.QueryRowContext(ctx, `SELECT p.id,p.name,p.created_at,COALESCE(group_concat(m.software_id, ','), '') FROM license_packages p LEFT JOIN license_package_members m ON m.package_id=p.id WHERE p.id=? GROUP BY p.id,p.name,p.created_at`, id)
	item, err := scanPackage(row)
	if errors.Is(err, sql.ErrNoRows) {
		return model.Package{}, ErrNotFound
	}
	if err != nil {
		return model.Package{}, fmt.Errorf("get package: %w", err)
	}
	return item, nil
}

func scanPackage(row rowScanner) (model.Package, error) {
	var item model.Package
	var created int64
	var members string
	if err := row.Scan(&item.ID, &item.Name, &created, &members); err != nil {
		return item, err
	}
	item.CreatedAt = time.Unix(created, 0).UTC()
	if members != "" {
		item.SoftwareIDs = strings.Split(members, ",")
		sort.Strings(item.SoftwareIDs)
	}
	return item, nil
}

func (s *Store) RenamePackage(ctx context.Context, id, name string) error {
	if model.ValidatePackageName(name) != nil {
		return errors.New("package name is invalid")
	}
	result, err := s.db.ExecContext(ctx, `UPDATE license_packages SET name=? WHERE id=?`, name, id)
	if err != nil {
		if isUniqueConstraint(err) {
			return ErrConflict
		}
		return fmt.Errorf("rename package: %w", err)
	}
	return requireAffected(result)
}

func (s *Store) PackageRemovalImpact(ctx context.Context, id string, replacement []string, now time.Time) (int, error) {
	ids, err := normalizedIDs(replacement)
	if err != nil {
		return 0, err
	}
	pkg, err := s.GetPackage(ctx, id)
	if err != nil {
		return 0, err
	}
	existing, err := s.countExistingSoftwareIDs(ctx, ids)
	if err != nil {
		return 0, err
	}
	if existing != len(ids) {
		return 0, ErrNotFound
	}
	kept := make(map[string]struct{}, len(ids))
	for _, value := range ids {
		kept[value] = struct{}{}
	}
	removed := false
	for _, value := range pkg.SoftwareIDs {
		if _, ok := kept[value]; !ok {
			removed = true
			break
		}
	}
	if !removed {
		return 0, nil
	}
	var count int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_cards WHERE package_id=? AND activated_at IS NOT NULL AND expires_at>?`, id, unixTime(now)).Scan(&count); err != nil {
		return 0, fmt.Errorf("count package impact: %w", err)
	}
	return count, nil
}

func (s *Store) countExistingSoftwareIDs(ctx context.Context, ids []string) (int, error) {
	if len(ids) == 0 {
		return 0, nil
	}
	placeholders := strings.TrimSuffix(strings.Repeat("?,", len(ids)), ",")
	args := make([]any, len(ids))
	for index, id := range ids {
		args[index] = id
	}
	var count int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_software WHERE id IN (`+placeholders+`)`, args...).Scan(&count); err != nil {
		return 0, fmt.Errorf("count candidate software: %w", err)
	}
	return count, nil
}

func (s *Store) GetAdminIdempotency(ctx context.Context, key string, now time.Time) (model.AdminIdempotencyRecord, bool, error) {
	if _, err := s.db.ExecContext(ctx, `DELETE FROM license_admin_idempotency WHERE expires_at<=? AND status<>102`, unixTime(now)); err != nil {
		return model.AdminIdempotencyRecord{}, false, fmt.Errorf("prune admin idempotency: %w", err)
	}
	var record model.AdminIdempotencyRecord
	var expires int64
	err := s.db.QueryRowContext(ctx, `SELECT fingerprint,status,response_body,replay_key_id,content_type,content_disposition,expires_at FROM license_admin_idempotency WHERE key=?`, key).Scan(&record.Fingerprint, &record.Status, &record.Body, &record.ReplayKeyID, &record.ContentType, &record.ContentDisposition, &expires)
	if errors.Is(err, sql.ErrNoRows) {
		return model.AdminIdempotencyRecord{}, false, nil
	}
	if err != nil {
		return model.AdminIdempotencyRecord{}, false, fmt.Errorf("read admin idempotency: %w", err)
	}
	record.ExpiresAt = time.Unix(expires, 0).UTC()
	return record, true, nil
}

func (s *Store) PutAdminIdempotency(ctx context.Context, key string, record model.AdminIdempotencyRecord, now time.Time, maximum int) error {
	if maximum < 1 || !record.ExpiresAt.After(now) {
		return errors.New("invalid admin idempotency record")
	}
	return s.immediate(ctx, func(conn *sql.Conn) error {
		if _, err := conn.ExecContext(ctx, `DELETE FROM license_admin_idempotency WHERE expires_at<=? AND status<>102`, unixTime(now)); err != nil {
			return err
		}
		var count int
		if err := conn.QueryRowContext(ctx, `SELECT count(*) FROM license_admin_idempotency`).Scan(&count); err != nil {
			return err
		}
		if count >= maximum {
			return ErrAdminIdempotencyCapacity
		}
		keyID := record.ReplayKeyID
		if keyID == "" {
			keyID = "legacy-v2"
		}
		_, err := conn.ExecContext(ctx, `INSERT INTO license_admin_idempotency(key,fingerprint,status,response_body,replay_key_id,content_type,content_disposition,expires_at,created_at) VALUES(?,?,?,?,?,?,?,?,?)`, key, record.Fingerprint, record.Status, record.Body, keyID, record.ContentType, record.ContentDisposition, ceilUnixSecond(record.ExpiresAt), unixTime(now))
		return err
	})
}

func (s *Store) CompleteAdminIdempotency(ctx context.Context, key, fingerprint string, record model.AdminIdempotencyRecord) error {
	keyID := record.ReplayKeyID
	if keyID == "" {
		keyID = "legacy-v2"
	}
	result, err := s.db.ExecContext(ctx, `UPDATE license_admin_idempotency SET status=?,response_body=?,replay_key_id=?,content_type=?,content_disposition=? WHERE key=? AND fingerprint=? AND status=102`, record.Status, record.Body, keyID, record.ContentType, record.ContentDisposition, key, fingerprint)
	if err != nil {
		return fmt.Errorf("complete admin idempotency: %w", err)
	}
	return requireAffected(result)
}

func (s *Store) GetPackageName(ctx context.Context, id string) (string, error) {
	var name string
	if err := s.db.QueryRowContext(ctx, `SELECT name FROM license_packages WHERE id=?`, id).Scan(&name); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return "", ErrNotFound
		}
		return "", fmt.Errorf("read package name: %w", err)
	}
	if err := model.ValidatePackageName(name); err != nil {
		return "", errors.New("persisted package name is invalid")
	}
	return name, nil
}

func validatePersistedPackageNames(ctx context.Context, db *sql.DB) error {
	rows, err := db.QueryContext(ctx, `SELECT name FROM license_packages`)
	if err != nil {
		return errors.New("validate persisted package names")
	}
	defer rows.Close()
	for rows.Next() {
		var name string
		if err := rows.Scan(&name); err != nil || model.ValidatePackageName(name) != nil {
			return errors.New("persisted package name violates wire invariant")
		}
	}
	if err := rows.Err(); err != nil {
		return errors.New("validate persisted package names")
	}
	return nil
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
	if req.CreatedAt.IsZero() {
		return nil, errors.New("card creation time is required")
	}
	createdAt := canonicalTime(req.CreatedAt)
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
		keyID := item.EncryptionKeyID
		if keyID == "" {
			keyID = "legacy-v2"
		}
		if _, err := tx.ExecContext(ctx, `INSERT INTO license_cards(id,package_id,ciphertext,encryption_key_id,full_digest,prefix8,last4,base_days,unactivated_total_days,note,created_at) VALUES(?,?,?,?,?,?,?,?,?,?,?)`,
			item.ID, req.PackageID, item.Ciphertext, keyID, item.FullDigest, item.Prefix8, item.Last4, req.BaseDays, req.BaseDays, note, unixTime(createdAt)); err != nil {
			if isForeignKeyConstraint(err) {
				return nil, ErrNotFound
			}
			if isUniqueConstraint(err) {
				return nil, ErrConflict
			}
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
	if req.ObservedAt.IsZero() || req.LeaseUntil.IsZero() || req.LeaseUntil.Before(req.ObservedAt) {
		return state, errors.New("invalid activation times")
	}
	req.ObservedAt = canonicalTime(req.ObservedAt)
	req.LeaseUntil = canonicalTime(req.LeaseUntil)
	err = s.immediate(ctx, func(conn *sql.Conn) error {
		var packageID string
		var machine sql.NullString
		var storedActivatedAt, storedExpiresAt sql.NullInt64
		var unactivatedDays int64
		if err := conn.QueryRowContext(ctx, `SELECT package_id,machine_hash,activated_at,expires_at,unactivated_total_days FROM license_cards WHERE id=?`, req.CardID).Scan(&packageID, &machine, &storedActivatedAt, &storedExpiresAt, &unactivatedDays); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return ErrNotFound
			}
			return err
		}
		var enabled, member int
		if err := conn.QueryRowContext(ctx, `SELECT enabled,EXISTS(SELECT 1 FROM license_package_members WHERE package_id=? AND software_id=license_software.id) FROM license_software WHERE id=?`, packageID, req.SoftwareID).Scan(&enabled, &member); err != nil {
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
		if machine.Valid && machine.String != req.MachineHash {
			return ErrMachineBound
		}
		if found, existing, err := queryActivationRequest(ctx, conn, req); err != nil {
			return err
		} else if found {
			if !existing.ExpiresAt.After(req.ObservedAt) {
				return ErrLicenseExpired
			}
			state = existing
			return nil
		}

		var effectiveActivatedAt, effectiveExpiresAt time.Time
		if storedActivatedAt.Valid || storedExpiresAt.Valid {
			if !storedActivatedAt.Valid || !storedExpiresAt.Valid {
				return errors.New("card has incomplete activation lifetime")
			}
			effectiveActivatedAt = time.Unix(storedActivatedAt.Int64, 0).UTC()
			effectiveExpiresAt = time.Unix(storedExpiresAt.Int64, 0).UTC()
		} else {
			if unactivatedDays <= 0 {
				return errors.New("card has no unactivated duration")
			}
			expiryUnix, err := addDaysUnix(req.ObservedAt.Unix(), unactivatedDays)
			if err != nil {
				return err
			}
			effectiveActivatedAt = req.ObservedAt
			effectiveExpiresAt = time.Unix(expiryUnix, 0).UTC()
		}
		if !effectiveExpiresAt.After(req.ObservedAt) {
			return ErrLicenseExpired
		}
		effectiveLeaseUntil := req.LeaseUntil
		if effectiveLeaseUntil.After(effectiveExpiresAt) {
			effectiveLeaseUntil = effectiveExpiresAt
		}
		if _, err := conn.ExecContext(ctx, `UPDATE license_cards SET activated_at=?,expires_at=?,machine_hash=?,unactivated_total_days=0 WHERE id=?`, unixTime(effectiveActivatedAt), unixTime(effectiveExpiresAt), req.MachineHash, req.CardID); err != nil {
			return err
		}
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_activation_bindings(card_id,software_id,machine_hash,activated_at,expires_at) VALUES(?,?,?,?,?) ON CONFLICT(card_id,software_id) DO UPDATE SET machine_hash=excluded.machine_hash,activated_at=excluded.activated_at,expires_at=excluded.expires_at`, req.CardID, req.SoftwareID, req.MachineHash, unixTime(effectiveActivatedAt), unixTime(effectiveExpiresAt)); err != nil {
			return err
		}
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_leases(card_id,software_id,machine_hash,last_seen_at,lease_until) VALUES(?,?,?,?,?) ON CONFLICT(card_id,software_id,machine_hash) DO UPDATE SET last_seen_at=excluded.last_seen_at,lease_until=excluded.lease_until`, req.CardID, req.SoftwareID, req.MachineHash, unixTime(req.ObservedAt), unixTime(effectiveLeaseUntil)); err != nil {
			return err
		}
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_activation_requests(request_id,card_id,software_id,machine_hash,activated_at,expires_at,lease_until) VALUES(?,?,?,?,?,?,?)`, req.RequestID, req.CardID, req.SoftwareID, req.MachineHash, unixTime(effectiveActivatedAt), unixTime(effectiveExpiresAt), unixTime(effectiveLeaseUntil)); err != nil {
			return err
		}
		state = model.ActivationState{RequestID: req.RequestID, CardID: req.CardID, SoftwareID: req.SoftwareID, MachineHash: req.MachineHash, ActivatedAt: effectiveActivatedAt, ExpiresAt: effectiveExpiresAt, LeaseUntil: effectiveLeaseUntil}
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

func (s *Store) SearchAdminCards(ctx context.Context, filter model.AdminCardFilter, now time.Time, offset, limit int) ([]model.AdminCardRecord, int, error) {
	if offset < 0 || limit < 1 {
		return nil, 0, errors.New("invalid card page")
	}
	where, args := adminCardWhere(filter, now)
	var total int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_cards c WHERE `+where, args...).Scan(&total); err != nil {
		return nil, 0, fmt.Errorf("count admin cards: %w", err)
	}
	query := `SELECT c.id,c.package_id,p.name,c.prefix8,c.last4,c.base_days,c.unactivated_total_days,c.note,c.activated_at,c.expires_at,c.machine_hash,c.created_at
FROM license_cards c JOIN license_packages p ON p.id=c.package_id WHERE ` + where + ` ORDER BY c.created_at,c.id LIMIT ? OFFSET ?`
	pageArgs := append(append([]any(nil), args...), limit, offset)
	rows, err := s.db.QueryContext(ctx, query, pageArgs...)
	if err != nil {
		return nil, 0, fmt.Errorf("search admin cards: %w", err)
	}
	defer rows.Close()
	items := make([]model.AdminCardRecord, 0, limit)
	for rows.Next() {
		item, err := scanAdminCardSummary(rows)
		if err != nil {
			return nil, 0, err
		}
		items = append(items, item)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, err
	}
	return items, total, nil
}

func scanAdminCardSummary(row rowScanner) (model.AdminCardRecord, error) {
	var item model.AdminCardRecord
	var note, machine sql.NullString
	var activated, expires sql.NullInt64
	var created int64
	if err := row.Scan(&item.ID, &item.PackageID, &item.PackageName, &item.Prefix8, &item.Last4, &item.BaseDays, &item.UnactivatedTotalDays, &note, &activated, &expires, &machine, &created); err != nil {
		return item, err
	}
	item.Note = nullableString(note)
	item.MachineHash = nullableString(machine)
	item.ActivatedAt = nullableTime(activated)
	item.ExpiresAt = nullableTime(expires)
	item.CreatedAt = time.Unix(created, 0).UTC()
	return item, nil
}

func adminCardWhere(filter model.AdminCardFilter, now time.Time) (string, []any) {
	clauses := []string{"1=1"}
	args := make([]any, 0, 7)
	add := func(clause string, value any) { clauses = append(clauses, clause); args = append(args, value) }
	if filter.FullDigest != "" {
		add("c.full_digest=?", filter.FullDigest)
	}
	if filter.Prefix8 != "" {
		add("c.prefix8=?", filter.Prefix8)
	}
	if filter.Last4 != "" {
		add("c.last4=?", filter.Last4)
	}
	if filter.NoteContains != "" {
		add("c.note IS NOT NULL AND instr(lower(c.note),lower(?))>0", filter.NoteContains)
	}
	if filter.PackageID != "" {
		add("c.package_id=?", filter.PackageID)
	}
	if filter.CardType != "" {
		if days, ok := model.DurationDaysForCardType(filter.CardType); ok {
			add("c.base_days=?", days)
		} else {
			clauses = append(clauses, "0=1")
		}
	}
	switch filter.State {
	case model.CardStateUnactivated:
		clauses = append(clauses, "c.activated_at IS NULL")
	case model.CardStateEffective:
		add("c.activated_at IS NOT NULL AND c.expires_at>?", unixTime(now))
	case model.CardStateExpired:
		add("c.expires_at IS NOT NULL AND c.expires_at<=?", unixTime(now))
	}
	return strings.Join(clauses, " AND "), args
}

func (s *Store) GetAdminCard(ctx context.Context, id string) (model.AdminCardRecord, error) {
	row := s.db.QueryRowContext(ctx, `SELECT c.id,c.package_id,p.name,c.ciphertext,c.encryption_key_id,c.full_digest,c.prefix8,c.last4,c.base_days,c.unactivated_total_days,c.note,c.activated_at,c.expires_at,c.machine_hash,c.created_at FROM license_cards c JOIN license_packages p ON p.id=c.package_id WHERE c.id=?`, id)
	item, err := scanAdminCard(row)
	if errors.Is(err, sql.ErrNoRows) {
		return model.AdminCardRecord{}, ErrNotFound
	}
	if err != nil {
		return model.AdminCardRecord{}, fmt.Errorf("get admin card: %w", err)
	}
	return item, nil
}

func (s *Store) GetAdminCards(ctx context.Context, ids []string) ([]model.AdminCardRecord, error) {
	if len(ids) == 0 {
		return nil, errors.New("card IDs are required")
	}
	placeholders := strings.TrimSuffix(strings.Repeat("?,", len(ids)), ",")
	args := make([]any, len(ids))
	for i, id := range ids {
		args[i] = id
	}
	rows, err := s.db.QueryContext(ctx, `SELECT c.id,c.package_id,p.name,c.ciphertext,c.encryption_key_id,c.full_digest,c.prefix8,c.last4,c.base_days,c.unactivated_total_days,c.note,c.activated_at,c.expires_at,c.machine_hash,c.created_at FROM license_cards c JOIN license_packages p ON p.id=c.package_id WHERE c.id IN (`+placeholders+`)`, args...)
	if err != nil {
		return nil, fmt.Errorf("get admin cards: %w", err)
	}
	defer rows.Close()
	found := make(map[string]model.AdminCardRecord, len(ids))
	for rows.Next() {
		item, err := scanAdminCard(rows)
		if err != nil {
			return nil, err
		}
		found[item.ID] = item
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	items := make([]model.AdminCardRecord, len(ids))
	for i, id := range ids {
		item, ok := found[id]
		if !ok {
			return nil, ErrNotFound
		}
		items[i] = item
	}
	return items, nil
}

func scanAdminCard(row rowScanner) (model.AdminCardRecord, error) {
	var item model.AdminCardRecord
	var note, machine sql.NullString
	var activated, expires sql.NullInt64
	var created int64
	if err := row.Scan(&item.ID, &item.PackageID, &item.PackageName, &item.Ciphertext, &item.EncryptionKeyID, &item.FullDigest, &item.Prefix8, &item.Last4, &item.BaseDays, &item.UnactivatedTotalDays, &note, &activated, &expires, &machine, &created); err != nil {
		return item, err
	}
	item.Note = nullableString(note)
	item.MachineHash = nullableString(machine)
	item.ActivatedAt = nullableTime(activated)
	item.ExpiresAt = nullableTime(expires)
	item.CreatedAt = time.Unix(created, 0).UTC()
	return item, nil
}

func (s *Store) SetCardNote(ctx context.Context, id string, note *string) error {
	result, err := s.db.ExecContext(ctx, `UPDATE license_cards SET note=? WHERE id=?`, note, id)
	if err != nil {
		return fmt.Errorf("set card note: %w", err)
	}
	return requireAffected(result)
}

func (s *Store) ListDevices(ctx context.Context, offset, limit int) ([]model.DeviceRecord, int, error) {
	if offset < 0 || limit < 1 {
		return nil, 0, errors.New("invalid device page")
	}
	var total int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM (SELECT card_id,machine_hash FROM license_activation_bindings GROUP BY card_id,machine_hash)`).Scan(&total); err != nil {
		return nil, 0, err
	}
	rows, err := s.db.QueryContext(ctx, `SELECT b.card_id,b.machine_hash,min(b.activated_at),COALESCE(max(l.last_seen_at),min(b.activated_at)),max(b.expires_at)
FROM license_activation_bindings b LEFT JOIN license_leases l ON l.card_id=b.card_id AND l.machine_hash=b.machine_hash
GROUP BY b.card_id,b.machine_hash ORDER BY min(b.activated_at),b.card_id LIMIT ? OFFSET ?`, limit, offset)
	if err != nil {
		return nil, 0, fmt.Errorf("list devices: %w", err)
	}
	defer rows.Close()
	items := make([]model.DeviceRecord, 0, limit)
	for rows.Next() {
		var item model.DeviceRecord
		var bound, seen, expires int64
		if err := rows.Scan(&item.CardID, &item.MachineHash, &bound, &seen, &expires); err != nil {
			return nil, 0, err
		}
		item.BoundAt = time.Unix(bound, 0).UTC()
		item.LastSeenAt = time.Unix(seen, 0).UTC()
		item.ExpiresAt = time.Unix(expires, 0).UTC()
		items = append(items, item)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, err
	}
	return items, total, nil
}

func (s *Store) GetDevice(ctx context.Context, cardID string) (model.DeviceRecord, error) {
	var item model.DeviceRecord
	var bound, seen, expires int64
	err := s.db.QueryRowContext(ctx, `SELECT b.card_id,b.machine_hash,min(b.activated_at),COALESCE(max(l.last_seen_at),min(b.activated_at)),max(b.expires_at) FROM license_activation_bindings b LEFT JOIN license_leases l ON l.card_id=b.card_id AND l.machine_hash=b.machine_hash WHERE b.card_id=? GROUP BY b.card_id,b.machine_hash`, cardID).Scan(&item.CardID, &item.MachineHash, &bound, &seen, &expires)
	if errors.Is(err, sql.ErrNoRows) {
		return model.DeviceRecord{}, ErrNotFound
	}
	if err != nil {
		return model.DeviceRecord{}, fmt.Errorf("get device: %w", err)
	}
	item.BoundAt = time.Unix(bound, 0).UTC()
	item.LastSeenAt = time.Unix(seen, 0).UTC()
	item.ExpiresAt = time.Unix(expires, 0).UTC()
	return item, nil
}

// ClaimReplayNonce atomically removes expired copies of nonce and claims the
// nonce until expiresAt. A false result means it is still live and is a replay.
func (s *Store) ClaimReplayNonce(ctx context.Context, nonce string, expiresAt, now time.Time, maximum int) (bool, error) {
	if nonce == "" || !expiresAt.After(now) || maximum < 1 {
		return false, errors.New("invalid replay nonce or expiry")
	}
	claimed := false
	err := retrySQLiteBusy(ctx, func() error {
		claimed = false
		return s.immediate(ctx, func(conn *sql.Conn) error {
			if _, err := conn.ExecContext(ctx, `DELETE FROM license_replay_nonces WHERE expires_at<=?`, unixTime(now)); err != nil {
				return err
			}
			var exists int
			if err := conn.QueryRowContext(ctx, `SELECT EXISTS(SELECT 1 FROM license_replay_nonces WHERE nonce=?)`, nonce).Scan(&exists); err != nil {
				return err
			}
			if exists != 0 {
				return nil
			}
			var count int
			if err := conn.QueryRowContext(ctx, `SELECT count(*) FROM license_replay_nonces`).Scan(&count); err != nil {
				return err
			}
			if count >= maximum {
				return ErrReplayNonceCapacity
			}
			if _, err := conn.ExecContext(ctx, `INSERT INTO license_replay_nonces(nonce,expires_at) VALUES(?,?)`, nonce, ceilUnixSecond(expiresAt)); err != nil {
				return err
			}
			claimed = true
			return nil
		})
	})
	if err != nil {
		return false, fmt.Errorf("claim replay nonce: %w", err)
	}
	return claimed, nil
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

type sqlExecer interface {
	ExecContext(context.Context, string, ...any) (sql.Result, error)
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

func insertPackageMembers(ctx context.Context, tx sqlExecer, packageID string, softwareIDs []string) error {
	for _, softwareID := range softwareIDs {
		if _, err := tx.ExecContext(ctx, `INSERT INTO license_package_members(package_id,software_id) VALUES(?,?)`, packageID, softwareID); err != nil {
			if isForeignKeyConstraint(err) {
				return ErrNotFound
			}
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

func addDaysUnix(baseUnix, days int64) (int64, error) {
	if days <= 0 || days > maximumInt64/secondsPerDay {
		return 0, errors.New("activation duration overflow")
	}
	delta := days * secondsPerDay
	if baseUnix > maximumInt64-delta {
		return 0, errors.New("activation expiry overflow")
	}
	return baseUnix + delta, nil
}

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

func isUniqueConstraint(err error) bool {
	return err != nil && strings.Contains(strings.ToLower(err.Error()), "unique constraint failed")
}

func isForeignKeyConstraint(err error) bool {
	return err != nil && strings.Contains(strings.ToLower(err.Error()), "foreign key constraint failed")
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
