package store

const schemaVersion = 1

const migrationV1 = `
CREATE TABLE administrators (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    username TEXT NOT NULL UNIQUE CHECK (username = 'admin'),
    password_hash TEXT NOT NULL CHECK (length(password_hash) > 0),
    must_change_password INTEGER NOT NULL CHECK (must_change_password IN (0, 1)),
    created_at TEXT NOT NULL
);

CREATE TABLE highest_committed_version (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    version TEXT NOT NULL,
    major INTEGER NOT NULL CHECK (major >= 0),
    minor INTEGER NOT NULL CHECK (minor >= 0),
    patch INTEGER NOT NULL CHECK (patch >= 0),
    prerelease TEXT NOT NULL,
    precedence_key BLOB NOT NULL,
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    committed_at TEXT NOT NULL
);

CREATE TRIGGER highest_committed_version_no_delete
BEFORE DELETE ON highest_committed_version
BEGIN
    SELECT RAISE(ABORT, 'highest committed version is permanent');
END;

CREATE TRIGGER highest_committed_version_monotonic
BEFORE UPDATE ON highest_committed_version
WHEN NEW.sequence <= OLD.sequence OR NEW.precedence_key <= OLD.precedence_key
BEGIN
    SELECT RAISE(ABORT, 'highest committed version must increase');
END;

CREATE TABLE releases (
    id TEXT PRIMARY KEY,
    version TEXT NOT NULL UNIQUE,
    generation_name TEXT NOT NULL UNIQUE CHECK (generation_name NOT LIKE '/%' AND generation_name NOT LIKE '%\\%' AND generation_name NOT LIKE '%..%'),
    state TEXT NOT NULL CHECK (state IN ('committed', 'retained', 'removed')),
    committed_at TEXT NOT NULL,
    actor TEXT NOT NULL DEFAULT 'admin' CHECK (actor = 'admin')
);

CREATE TABLE release_assets (
    release_id TEXT NOT NULL REFERENCES releases(id) ON DELETE RESTRICT,
    file_name TEXT NOT NULL CHECK (file_name NOT LIKE '%/%' AND file_name NOT LIKE '%\\%' AND file_name NOT IN ('.', '..')),
    size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
    sha256 TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 = lower(sha256)),
    PRIMARY KEY (release_id, file_name)
);

CREATE TABLE candidates (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('uploading', 'validated', 'publishing', 'published', 'failed', 'expired')),
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    release_version TEXT
);

CREATE TRIGGER candidates_state_machine
BEFORE UPDATE OF state ON candidates
WHEN NOT (
    (OLD.state = 'uploading' AND NEW.state IN ('validated', 'failed', 'expired')) OR
    (OLD.state = 'validated' AND NEW.state IN ('publishing', 'failed', 'expired')) OR
    (OLD.state = 'publishing' AND NEW.state IN ('published', 'failed'))
)
BEGIN
    SELECT RAISE(ABORT, 'invalid candidate transition');
END;

CREATE TABLE sessions (
    id_hash TEXT PRIMARY KEY,
    state TEXT NOT NULL CHECK (state IN ('active', 'revoked', 'expired')),
    created_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    idle_expires_at TEXT NOT NULL,
    absolute_expires_at TEXT NOT NULL,
    CHECK (idle_expires_at <= absolute_expires_at)
);

CREATE TRIGGER sessions_state_machine
BEFORE UPDATE OF state ON sessions
WHEN NOT (OLD.state = 'active' AND NEW.state IN ('revoked', 'expired'))
BEGIN
    SELECT RAISE(ABORT, 'invalid session transition');
END;

CREATE TABLE operations (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL CHECK (kind IN ('upload', 'publish', 'rollback', 'cleanup')),
    state TEXT NOT NULL CHECK (state IN ('pending', 'running', 'succeeded', 'failed')),
    candidate_id TEXT REFERENCES candidates(id) ON DELETE RESTRICT,
    release_id TEXT REFERENCES releases(id) ON DELETE RESTRICT,
    result_code TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    finished_at TEXT
);

CREATE TRIGGER operations_state_machine
BEFORE UPDATE OF state ON operations
WHEN NOT (
    (OLD.state = 'pending' AND NEW.state IN ('running', 'failed')) OR
    (OLD.state = 'running' AND NEW.state IN ('succeeded', 'failed'))
)
BEGIN
    SELECT RAISE(ABORT, 'invalid operation transition');
END;

CREATE TABLE audit_events (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at TEXT NOT NULL,
    event_type TEXT NOT NULL,
    outcome TEXT NOT NULL CHECK (outcome IN ('success', 'failure')),
    operation_id TEXT NOT NULL DEFAULT '',
    candidate_id TEXT NOT NULL DEFAULT '',
    release_id TEXT NOT NULL DEFAULT '',
    code TEXT NOT NULL DEFAULT '',
    source_hash TEXT NOT NULL DEFAULT ''
);

CREATE TRIGGER audit_events_append_only_update
BEFORE UPDATE ON audit_events
BEGIN
    SELECT RAISE(ABORT, 'audit events are append-only');
END;

CREATE TRIGGER audit_events_append_only_delete
BEFORE DELETE ON audit_events
BEGIN
    SELECT RAISE(ABORT, 'audit events are append-only');
END;

PRAGMA user_version = 1;
`
