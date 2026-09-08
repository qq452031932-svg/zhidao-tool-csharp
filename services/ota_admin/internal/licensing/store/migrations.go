package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strings"
)

const licenseSchemaVersion = 1

const licenseSchemaV1 = `
CREATE TABLE license_schema_version (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id=1),
    version INTEGER NOT NULL CHECK (version>0)
);
INSERT INTO license_schema_version(singleton_id,version) VALUES(1,1);

CREATE TABLE license_software (
    id TEXT PRIMARY KEY CHECK (length(id)=36),
    name TEXT NOT NULL UNIQUE CHECK (length(name)>0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0,1)),
    created_at INTEGER NOT NULL
);
CREATE TRIGGER license_software_id_immutable BEFORE UPDATE OF id ON license_software
BEGIN SELECT RAISE(ABORT, 'software id is immutable'); END;
CREATE TRIGGER license_software_no_delete BEFORE DELETE ON license_software
BEGIN SELECT RAISE(ABORT, 'software is disabled, not deleted'); END;

CREATE TABLE license_packages (
    id TEXT PRIMARY KEY CHECK (length(id)=36),
    name TEXT NOT NULL UNIQUE CHECK (length(name)>0),
    created_at INTEGER NOT NULL
);
CREATE TRIGGER license_package_id_immutable BEFORE UPDATE OF id ON license_packages
BEGIN SELECT RAISE(ABORT, 'package id is immutable'); END;

CREATE TABLE license_package_members (
    package_id TEXT NOT NULL REFERENCES license_packages(id) ON DELETE CASCADE,
    software_id TEXT NOT NULL REFERENCES license_software(id) ON DELETE RESTRICT,
    PRIMARY KEY(package_id,software_id)
);
CREATE INDEX license_package_members_software ON license_package_members(software_id,package_id);

CREATE TABLE license_cards (
    id TEXT PRIMARY KEY CHECK (length(id)=36),
    package_id TEXT NOT NULL REFERENCES license_packages(id) ON DELETE RESTRICT,
    ciphertext BLOB NOT NULL CHECK (length(ciphertext)>0),
    full_digest TEXT NOT NULL UNIQUE CHECK (length(full_digest)>0),
    prefix8 TEXT NOT NULL CHECK (length(prefix8)=8),
    last4 TEXT NOT NULL CHECK (length(last4)=4),
    base_days INTEGER NOT NULL CHECK (base_days>0),
    unactivated_total_days INTEGER NOT NULL CHECK (unactivated_total_days>=0),
    note TEXT,
    activated_at INTEGER,
    expires_at INTEGER,
    machine_hash TEXT,
    created_at INTEGER NOT NULL,
    CHECK ((activated_at IS NULL AND expires_at IS NULL AND machine_hash IS NULL) OR
           (activated_at IS NOT NULL AND expires_at IS NOT NULL AND expires_at>=activated_at))
);
CREATE TRIGGER license_card_id_immutable BEFORE UPDATE OF id ON license_cards
BEGIN SELECT RAISE(ABORT, 'card id is immutable'); END;
CREATE INDEX license_cards_prefix8 ON license_cards(prefix8,id);
CREATE INDEX license_cards_last4 ON license_cards(last4,id);
CREATE INDEX license_cards_package ON license_cards(package_id,id);

CREATE TABLE license_activation_bindings (
    card_id TEXT NOT NULL REFERENCES license_cards(id) ON DELETE CASCADE,
    software_id TEXT NOT NULL REFERENCES license_software(id) ON DELETE RESTRICT,
    machine_hash TEXT NOT NULL,
    activated_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    PRIMARY KEY(card_id,software_id)
);

CREATE TABLE license_activation_requests (
    request_id TEXT NOT NULL,
    card_id TEXT NOT NULL REFERENCES license_cards(id) ON DELETE CASCADE,
    software_id TEXT NOT NULL REFERENCES license_software(id) ON DELETE RESTRICT,
    machine_hash TEXT NOT NULL,
    activated_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    lease_until INTEGER NOT NULL,
    PRIMARY KEY(request_id,card_id,software_id,machine_hash)
);
CREATE INDEX license_activation_requests_card ON license_activation_requests(card_id);

CREATE TABLE license_replay_nonces (
    nonce TEXT PRIMARY KEY CHECK (length(nonce)>0),
    expires_at INTEGER NOT NULL
);
CREATE INDEX license_replay_nonces_expiry ON license_replay_nonces(expires_at,nonce);

CREATE TABLE license_leases (
    card_id TEXT NOT NULL REFERENCES license_cards(id) ON DELETE CASCADE,
    software_id TEXT NOT NULL REFERENCES license_software(id) ON DELETE RESTRICT,
    machine_hash TEXT NOT NULL,
    last_seen_at INTEGER NOT NULL,
    lease_until INTEGER NOT NULL,
    PRIMARY KEY(card_id,software_id,machine_hash)
);
CREATE INDEX license_leases_expiry ON license_leases(lease_until,card_id);

CREATE TRIGGER license_card_unbind_cleanup AFTER UPDATE OF machine_hash ON license_cards
WHEN OLD.machine_hash IS NOT NULL AND NEW.machine_hash IS NULL
BEGIN
    DELETE FROM license_activation_bindings WHERE card_id=NEW.id;
    DELETE FROM license_activation_requests WHERE card_id=NEW.id;
    DELETE FROM license_leases WHERE card_id=NEW.id;
END;
`

func migrate(ctx context.Context, db *sql.DB) error {
	return retrySQLiteBusy(ctx, func() error { return migrateOnce(ctx, db) })
}

func migrateOnce(ctx context.Context, db *sql.DB) error {
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("begin license migration: %w", err)
	}
	defer tx.Rollback()

	rows, err := tx.QueryContext(ctx, `SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'`)
	if err != nil {
		return fmt.Errorf("inspect license schema: %w", err)
	}
	var tables []string
	for rows.Next() {
		var name string
		if err := rows.Scan(&name); err != nil {
			rows.Close()
			return err
		}
		tables = append(tables, name)
	}
	if err := rows.Close(); err != nil {
		return err
	}
	for _, table := range tables {
		if !strings.HasPrefix(table, "license_") {
			return fmt.Errorf("refusing non-license SQLite schema containing table %q", table)
		}
	}

	if len(tables) == 0 {
		if _, err := tx.ExecContext(ctx, licenseSchemaV1); err != nil {
			return fmt.Errorf("create license schema: %w", err)
		}
	} else {
		var version int
		if err := tx.QueryRowContext(ctx, `SELECT version FROM license_schema_version WHERE singleton_id=1`).Scan(&version); err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return errors.New("license schema has no version")
			}
			return fmt.Errorf("read license schema version: %w", err)
		}
		if version != licenseSchemaVersion {
			return fmt.Errorf("unsupported license schema version %d", version)
		}
	}
	if err := tx.Commit(); err != nil {
		return fmt.Errorf("commit license migration: %w", err)
	}
	return nil
}
