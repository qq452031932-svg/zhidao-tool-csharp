package store

const originalV1FixtureDDL = `
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

var hardenedV1FixtureDDL = `
CREATE TABLE administrators (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    username TEXT NOT NULL UNIQUE CHECK (username = 'admin'),
    password_hash TEXT NOT NULL CHECK (password_hash GLOB '$argon2id$v=19$m=65536,t=3,p=2$*$*'),
    must_change_password INTEGER NOT NULL CHECK (must_change_password IN (0, 1)),
    created_at TEXT NOT NULL
);

CREATE TABLE highest_committed_version (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    version TEXT NOT NULL,
    major INTEGER NOT NULL CHECK (major >= 0),
    minor INTEGER NOT NULL CHECK (minor >= 0),
    patch INTEGER NOT NULL CHECK (patch >= 0),
    committed_at TEXT NOT NULL,
    CHECK (version = printf('%d.%d.%d', major, minor, patch))
);

CREATE TRIGGER highest_committed_version_no_delete
BEFORE DELETE ON highest_committed_version
BEGIN SELECT RAISE(ABORT, 'highest committed version is permanent'); END;

CREATE TRIGGER highest_committed_version_monotonic
BEFORE UPDATE ON highest_committed_version
WHEN NOT (
    NEW.major > OLD.major OR
    (NEW.major = OLD.major AND NEW.minor > OLD.minor) OR
    (NEW.major = OLD.major AND NEW.minor = OLD.minor AND NEW.patch > OLD.patch)
)
BEGIN SELECT RAISE(ABORT, 'highest committed version must increase'); END;

CREATE TABLE releases (
    id TEXT PRIMARY KEY CHECK (` + uuidCheckSQL("id") + `),
    version TEXT NOT NULL UNIQUE,
    generation_name TEXT NOT NULL UNIQUE CHECK (` + hardenedV1FlatNameCheckSQL("generation_name") + `),
    state TEXT NOT NULL CHECK (state IN ('current', 'retained', 'removed')),
    committed_at TEXT NOT NULL,
    actor TEXT NOT NULL DEFAULT 'admin' CHECK (actor = 'admin')
);

CREATE UNIQUE INDEX releases_one_current ON releases(state) WHERE state = 'current';

CREATE TRIGGER releases_initial_state
BEFORE INSERT ON releases WHEN NEW.state <> 'current'
BEGIN SELECT RAISE(ABORT, 'release must start current'); END;

CREATE TRIGGER releases_state_machine
BEFORE UPDATE OF state ON releases
WHEN NOT (
    (OLD.state = 'current' AND NEW.state = 'retained') OR
    (OLD.state = 'retained' AND NEW.state IN ('current', 'removed'))
)
BEGIN SELECT RAISE(ABORT, 'invalid release transition'); END;

CREATE TABLE release_assets (
    release_id TEXT NOT NULL REFERENCES releases(id) ON DELETE RESTRICT,
    file_name TEXT NOT NULL CHECK (` + hardenedV1FlatNameCheckSQL("file_name") + `),
    size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
    sha256 TEXT NOT NULL CHECK (` + hexCheckSQL("sha256", 64) + `),
    PRIMARY KEY (release_id, file_name)
);

CREATE TABLE sessions (
    id_hash TEXT PRIMARY KEY CHECK (` + hexCheckSQL("id_hash", 64) + `),
    state TEXT NOT NULL CHECK (state IN ('active', 'revoked', 'expired')),
    created_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    idle_expires_at TEXT NOT NULL,
    absolute_expires_at TEXT NOT NULL,
    CHECK (created_at <= last_seen_at AND last_seen_at <= idle_expires_at AND idle_expires_at <= absolute_expires_at)
);

CREATE TRIGGER sessions_initial_state
BEFORE INSERT ON sessions WHEN NEW.state <> 'active'
BEGIN SELECT RAISE(ABORT, 'session must start active'); END;

CREATE TRIGGER sessions_state_machine
BEFORE UPDATE OF state ON sessions
WHEN NOT (OLD.state = 'active' AND NEW.state IN ('revoked', 'expired'))
BEGIN SELECT RAISE(ABORT, 'invalid session transition'); END;

CREATE TABLE candidates (
    id TEXT PRIMARY KEY CHECK (` + uuidCheckSQL("id") + `),
    session_id TEXT NOT NULL REFERENCES sessions(id_hash) ON DELETE RESTRICT,
    upload_digest TEXT NOT NULL DEFAULT '' CHECK (upload_digest = '' OR ` + hexCheckSQL("upload_digest", 64) + `),
    validation_report TEXT NOT NULL DEFAULT '' CHECK (validation_report = '' OR (length(validation_report) <= 65536 AND json_valid(validation_report))),
    state TEXT NOT NULL CHECK (state IN ('uploading', 'validated', 'publishing', 'published', 'failed', 'expired')),
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL CHECK (expires_at > created_at),
    release_version TEXT
);

CREATE TRIGGER candidates_initial_state
BEFORE INSERT ON candidates WHEN NEW.state <> 'uploading'
BEGIN SELECT RAISE(ABORT, 'candidate must start uploading'); END;

CREATE TRIGGER candidates_immutable_bindings
BEFORE UPDATE ON candidates
WHEN NEW.session_id <> OLD.session_id OR (
     (NEW.upload_digest <> OLD.upload_digest OR NEW.validation_report <> OLD.validation_report) AND NOT (
       OLD.state = 'uploading' AND NEW.state = 'validated' AND
       OLD.upload_digest = '' AND OLD.validation_report = '' AND
       ` + hexCheckSQL("NEW.upload_digest", 64) + ` AND NEW.validation_report <> '' AND json_valid(NEW.validation_report)
     )
)
BEGIN SELECT RAISE(ABORT, 'candidate binding is immutable'); END;

CREATE TRIGGER candidates_state_machine
BEFORE UPDATE OF state ON candidates
WHEN NOT (
    (OLD.state = 'uploading' AND NEW.state = 'validated' AND ` + hexCheckSQL("NEW.upload_digest", 64) + ` AND NEW.validation_report <> '' AND json_valid(NEW.validation_report)) OR
    (OLD.state = 'uploading' AND NEW.state IN ('failed', 'expired')) OR
    (OLD.state = 'validated' AND NEW.state IN ('publishing', 'failed', 'expired')) OR
    (OLD.state = 'publishing' AND NEW.state IN ('published', 'failed'))
)
BEGIN SELECT RAISE(ABORT, 'invalid candidate transition'); END;

CREATE TABLE operations (
    id TEXT PRIMARY KEY CHECK (` + uuidCheckSQL("id") + `),
    kind TEXT NOT NULL CHECK (kind IN ('upload', 'publish', 'rollback', 'cleanup')),
    state TEXT NOT NULL CHECK (state IN ('pending', 'running', 'succeeded', 'failed')),
    candidate_id TEXT REFERENCES candidates(id) ON DELETE RESTRICT,
    release_id TEXT REFERENCES releases(id) ON DELETE RESTRICT,
    result_code TEXT NOT NULL DEFAULT '' CHECK (result_code IN ('', 'accepted', 'completed', 'hash_mismatch', 'signature_mismatch', 'version_floor', 'expired', 'internal_failure')),
    created_at TEXT NOT NULL,
    finished_at TEXT,
    CHECK ((state IN ('succeeded', 'failed')) = (finished_at IS NOT NULL))
);

CREATE TRIGGER operations_initial_state
BEFORE INSERT ON operations WHEN NEW.state <> 'pending' OR NEW.finished_at IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'operation must start pending'); END;

CREATE TRIGGER operations_state_machine
BEFORE UPDATE OF state ON operations
WHEN NOT (
    (OLD.state = 'pending' AND NEW.state IN ('running', 'failed')) OR
    (OLD.state = 'running' AND NEW.state IN ('succeeded', 'failed'))
)
BEGIN SELECT RAISE(ABORT, 'invalid operation transition'); END;

CREATE TABLE audit_events (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at TEXT NOT NULL,
    event_type TEXT NOT NULL CHECK (event_type IN ('login_result', 'upload_started', 'upload_completed', 'gate_result', 'publish_confirmed', 'publish_failed', 'rollback_completed', 'cleanup_result')),
    outcome TEXT NOT NULL CHECK (outcome IN ('success', 'failure')),
    operation_id TEXT NOT NULL DEFAULT '' CHECK (operation_id = '' OR ` + uuidCheckSQL("operation_id") + `),
    candidate_id TEXT NOT NULL DEFAULT '' CHECK (candidate_id = '' OR ` + uuidCheckSQL("candidate_id") + `),
    release_id TEXT NOT NULL DEFAULT '' CHECK (release_id = '' OR ` + uuidCheckSQL("release_id") + `),
    code TEXT NOT NULL CHECK (code IN ('accepted', 'completed', 'login_accepted', 'login_rejected', 'login_locked', 'hash_mismatch', 'signature_mismatch', 'version_floor', 'expired', 'internal_failure')),
    source_pseudonym TEXT NOT NULL DEFAULT '' CHECK (source_pseudonym = '' OR (length(source_pseudonym) = 76 AND substr(source_pseudonym, 1, 12) = 'hmac-sha256:' AND substr(source_pseudonym, 13) NOT GLOB '*[^0-9a-f]*')),
    CHECK (
      (event_type = 'login_result' AND operation_id = '' AND candidate_id = '' AND release_id = '' AND source_pseudonym <> '' AND ((outcome='success' AND code='login_accepted') OR (outcome='failure' AND code IN ('login_rejected','login_locked')))) OR
      (event_type = 'upload_started' AND outcome = 'success' AND operation_id <> '' AND candidate_id <> '' AND release_id = '' AND source_pseudonym = '' AND code = 'accepted') OR
      (event_type = 'upload_completed' AND operation_id <> '' AND candidate_id <> '' AND release_id = '' AND source_pseudonym = '' AND ((outcome='success' AND code='completed') OR (outcome='failure' AND code IN ('hash_mismatch','expired','internal_failure')))) OR
      (event_type = 'gate_result' AND operation_id <> '' AND candidate_id <> '' AND release_id = '' AND source_pseudonym = '' AND ((outcome='success' AND code='accepted') OR (outcome='failure' AND code IN ('hash_mismatch','signature_mismatch','version_floor','internal_failure')))) OR
      (event_type = 'publish_confirmed' AND outcome = 'success' AND operation_id <> '' AND candidate_id <> '' AND release_id <> '' AND source_pseudonym = '' AND code = 'completed') OR
      (event_type = 'publish_failed' AND outcome = 'failure' AND operation_id <> '' AND candidate_id <> '' AND release_id = '' AND source_pseudonym = '' AND code IN ('hash_mismatch','signature_mismatch','version_floor','internal_failure')) OR
      (event_type = 'rollback_completed' AND outcome = 'success' AND operation_id <> '' AND candidate_id = '' AND release_id <> '' AND source_pseudonym = '' AND code = 'completed') OR
      (event_type = 'cleanup_result' AND operation_id <> '' AND candidate_id = '' AND source_pseudonym = '' AND ((outcome='success' AND code='completed') OR (outcome='failure' AND code='internal_failure')))
    )
);

CREATE TRIGGER audit_events_append_only_update
BEFORE UPDATE ON audit_events
BEGIN SELECT RAISE(ABORT, 'audit events are append-only'); END;

CREATE TRIGGER audit_events_append_only_delete
BEFORE DELETE ON audit_events
BEGIN SELECT RAISE(ABORT, 'audit events are append-only'); END;

CREATE TRIGGER audit_events_no_replace
BEFORE INSERT ON audit_events
WHEN NEW.sequence >= 0 AND EXISTS(SELECT 1 FROM audit_events WHERE sequence = NEW.sequence)
BEGIN SELECT RAISE(ABORT, 'audit events cannot be replaced'); END;
`

func hardenedV1FlatNameCheckSQL(column string) string {
	return `length(` + column + `) BETWEEN 1 AND 128 AND ` + column + ` NOT IN ('.','..') AND ` + column + ` NOT GLOB '*[^A-Za-z0-9._-]*'`
}
