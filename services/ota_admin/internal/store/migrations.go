package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strings"
	"time"
)

type migration struct {
	version  int
	sql      string
	validate func(*sql.Tx) error
	apply    func(context.Context, *sql.Tx) error
}

var migrations = []migration{{version: 1, sql: legacyV1DDL}, {version: 2, apply: applyV2Migration}, {version: 3, sql: schemaV3}, {version: 4, sql: schemaV4}}

const schemaVersion = 4

var schemaV3 = `
CREATE TABLE candidate_cleanups (
    candidate_id TEXT PRIMARY KEY REFERENCES candidates(id) ON DELETE RESTRICT,
    operation_id TEXT UNIQUE CHECK (operation_id IS NULL OR ` + uuidCheckSQL("operation_id") + `),
    state TEXT NOT NULL CHECK (state IN ('pending','completed')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (` + integerCheckSQL("attempt_count") + `),
    claim_token TEXT NOT NULL DEFAULT '' CHECK (claim_token = '' OR ` + uuidCheckSQL("claim_token") + `),
    claim_expires_at INTEGER CHECK (claim_expires_at IS NULL OR (` + integerCheckSQL("claim_expires_at") + `)),
    last_error_code TEXT NOT NULL DEFAULT '' CHECK (last_error_code IN ('','internal_failure')),
    updated_at INTEGER NOT NULL CHECK (` + integerCheckSQL("updated_at") + `),
    completed_at INTEGER CHECK (completed_at IS NULL OR (` + integerCheckSQL("completed_at") + `)),
    CHECK ((claim_token='' AND claim_expires_at IS NULL) OR (claim_token<>'' AND claim_expires_at IS NOT NULL)),
    CHECK ((state='pending' AND completed_at IS NULL) OR (state='completed' AND completed_at IS NOT NULL AND claim_token='' AND claim_expires_at IS NULL AND last_error_code=''))
);
CREATE INDEX candidate_cleanups_pending ON candidate_cleanups(state,claim_expires_at,candidate_id);
`

var schemaV4 = `
CREATE TABLE candidate_upload_failures (
    operation_id TEXT PRIMARY KEY REFERENCES operations(id) ON DELETE RESTRICT,
    candidate_id TEXT NOT NULL UNIQUE REFERENCES candidates(id) ON DELETE RESTRICT,
    public_code TEXT NOT NULL CHECK (public_code IN ('signature_mismatch','version_floor','release_validation_failed','invalid_zip_envelope','upload_too_large','insufficient_storage','operation_failed','candidate_expired'))
);
CREATE TABLE candidate_cleanup_attempts (
    operation_id TEXT PRIMARY KEY REFERENCES operations(id) ON DELETE RESTRICT,
    candidate_id TEXT NOT NULL REFERENCES candidates(id) ON DELETE RESTRICT,
    attempt_number INTEGER NOT NULL CHECK (` + integerCheckSQL("attempt_number") + ` AND attempt_number > 0),
    started_at INTEGER NOT NULL CHECK (` + integerCheckSQL("started_at") + `),
    finished_at INTEGER CHECK (finished_at IS NULL OR (` + integerCheckSQL("finished_at") + `)),
    outcome TEXT NOT NULL DEFAULT '' CHECK (outcome IN ('','success','failure')),
    CHECK ((outcome='' AND finished_at IS NULL) OR (outcome<>'' AND finished_at IS NOT NULL)),
    UNIQUE(candidate_id,attempt_number)
);
CREATE INDEX candidate_cleanup_attempts_candidate ON candidate_cleanup_attempts(candidate_id,attempt_number);
INSERT INTO candidate_cleanup_attempts(operation_id,candidate_id,attempt_number,started_at,finished_at,outcome)
SELECT x.operation_id,x.candidate_id,x.attempt_count,x.updated_at,
       CASE WHEN o.state IN ('succeeded','failed') THEN o.finished_at ELSE NULL END,
       CASE WHEN o.state='succeeded' THEN 'success' WHEN o.state='failed' THEN 'failure' ELSE '' END
FROM candidate_cleanups x JOIN operations o ON o.id=x.operation_id
WHERE x.operation_id IS NOT NULL AND x.attempt_count>0;
`

var schemaV2 = `
CREATE TABLE administrators (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    username TEXT NOT NULL UNIQUE CHECK (username = 'admin'),
    password_hash TEXT NOT NULL CHECK (password_hash GLOB '$argon2id$v=19$m=65536,t=3,p=2$*$*'),
    must_change_password INTEGER NOT NULL CHECK (must_change_password IN (0, 1)),
    created_at INTEGER NOT NULL CHECK (` + integerCheckSQL("created_at") + `)
);

CREATE TABLE highest_committed_version (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    version TEXT NOT NULL,
    major INTEGER NOT NULL CHECK (major BETWEEN 0 AND 65534),
    minor INTEGER NOT NULL CHECK (minor BETWEEN 0 AND 65534),
    patch INTEGER NOT NULL CHECK (patch BETWEEN 0 AND 65534),
    committed_at INTEGER NOT NULL CHECK (` + integerCheckSQL("committed_at") + `),
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
    version TEXT NOT NULL UNIQUE CHECK (` + versionCheckSQL("version") + `),
    generation_name TEXT NOT NULL UNIQUE CHECK (` + flatNameCheckSQL("generation_name") + `),
    state TEXT NOT NULL CHECK (state IN ('current', 'retained', 'removed')),
    committed_at INTEGER NOT NULL CHECK (` + integerCheckSQL("committed_at") + `),
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
    file_name TEXT NOT NULL CHECK (` + flatNameCheckSQL("file_name") + `),
    size_bytes INTEGER NOT NULL CHECK (` + integerCheckSQL("size_bytes") + `),
    sha256 TEXT NOT NULL CHECK (` + hexCheckSQL("sha256", 64) + `),
    PRIMARY KEY (release_id, file_name)
);

CREATE TABLE sessions (
    id_hash TEXT PRIMARY KEY CHECK (` + hexCheckSQL("id_hash", 64) + `),
    state TEXT NOT NULL CHECK (state IN ('active', 'revoked', 'expired')),
    created_at INTEGER NOT NULL CHECK (` + integerCheckSQL("created_at") + `),
    last_seen_at INTEGER NOT NULL CHECK (` + integerCheckSQL("last_seen_at") + `),
    idle_expires_at INTEGER NOT NULL CHECK (` + integerCheckSQL("idle_expires_at") + `),
    absolute_expires_at INTEGER NOT NULL CHECK (` + integerCheckSQL("absolute_expires_at") + `),
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
    created_at INTEGER NOT NULL CHECK (` + integerCheckSQL("created_at") + `),
    expires_at INTEGER NOT NULL CHECK (` + integerCheckSQL("expires_at") + ` AND expires_at > created_at),
    release_version TEXT REFERENCES releases(version) ON DELETE RESTRICT,
    CHECK ((state = 'published' AND release_version IS NOT NULL) OR (state <> 'published' AND release_version IS NULL))
);

CREATE TRIGGER candidates_initial_state
BEFORE INSERT ON candidates WHEN NEW.state <> 'uploading' OR NEW.upload_digest <> '' OR NEW.validation_report <> '' OR NEW.release_version IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'candidate must start uploading'); END;

CREATE TRIGGER candidates_immutable_bindings
BEFORE UPDATE ON candidates
WHEN NEW.session_id <> OLD.session_id OR (
     (NEW.upload_digest <> OLD.upload_digest OR NEW.validation_report <> OLD.validation_report) AND NOT (
       OLD.state = 'uploading' AND NEW.state = 'validated' AND
       OLD.upload_digest = '' AND OLD.validation_report = '' AND
       ` + hexCheckSQL("NEW.upload_digest", 64) + ` AND NEW.validation_report <> '' AND json_valid(NEW.validation_report)
     )
) OR (
     NEW.release_version IS NOT OLD.release_version AND NOT (
       OLD.state = 'publishing' AND NEW.state = 'published' AND
       OLD.release_version IS NULL AND NEW.release_version IS NOT NULL
     )
)
BEGIN SELECT RAISE(ABORT, 'candidate binding is immutable'); END;

CREATE TRIGGER candidates_state_machine
BEFORE UPDATE OF state ON candidates
WHEN NOT (
    (OLD.state = 'uploading' AND NEW.state = 'validated' AND ` + hexCheckSQL("NEW.upload_digest", 64) + ` AND NEW.validation_report <> '' AND json_valid(NEW.validation_report)) OR
    (OLD.state = 'uploading' AND NEW.state IN ('failed', 'expired')) OR
    (OLD.state = 'validated' AND NEW.state IN ('publishing', 'failed', 'expired')) OR
    (OLD.state = 'publishing' AND ((NEW.state = 'published' AND NEW.release_version IS NOT NULL) OR NEW.state = 'failed'))
)
BEGIN SELECT RAISE(ABORT, 'invalid candidate transition'); END;

CREATE TABLE operations (
    id TEXT PRIMARY KEY CHECK (` + uuidCheckSQL("id") + `),
    kind TEXT NOT NULL CHECK (kind IN ('upload', 'publish', 'rollback', 'cleanup')),
    state TEXT NOT NULL CHECK (state IN ('pending', 'running', 'succeeded', 'failed')),
    candidate_id TEXT REFERENCES candidates(id) ON DELETE RESTRICT,
    release_id TEXT REFERENCES releases(id) ON DELETE RESTRICT,
    result_code TEXT NOT NULL DEFAULT '' CHECK (result_code IN ('', 'completed', 'hash_mismatch', 'signature_mismatch', 'version_floor', 'expired', 'internal_failure')),
    created_at INTEGER NOT NULL CHECK (` + integerCheckSQL("created_at") + `),
    finished_at INTEGER CHECK (finished_at IS NULL OR (` + integerCheckSQL("finished_at") + `)),
    CHECK (
      (state IN ('pending','running') AND result_code='' AND finished_at IS NULL) OR
      (state='succeeded' AND result_code='completed' AND finished_at IS NOT NULL) OR
      (state='failed' AND result_code IN ('hash_mismatch','signature_mismatch','version_floor','expired','internal_failure') AND finished_at IS NOT NULL)
    ),
    CHECK (
      (kind='upload' AND candidate_id IS NOT NULL AND release_id IS NULL) OR
      (kind='publish' AND candidate_id IS NOT NULL AND ((state='succeeded' AND release_id IS NOT NULL) OR (state<>'succeeded' AND release_id IS NULL))) OR
      (kind='rollback' AND candidate_id IS NULL AND release_id IS NOT NULL) OR
      (kind='cleanup' AND candidate_id IS NULL)
    )
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

CREATE TRIGGER operations_terminal_immutable
BEFORE UPDATE ON operations WHEN OLD.state IN ('succeeded','failed')
BEGIN SELECT RAISE(ABORT, 'terminal operation is immutable'); END;

CREATE TRIGGER operations_associations_immutable
BEFORE UPDATE ON operations
WHEN NEW.kind <> OLD.kind OR NEW.candidate_id IS NOT OLD.candidate_id OR (
    NEW.release_id IS NOT OLD.release_id AND NOT (
      OLD.kind='publish' AND OLD.release_id IS NULL AND NEW.release_id IS NOT NULL AND
      OLD.state='running' AND NEW.state='succeeded'
    )
)
BEGIN SELECT RAISE(ABORT, 'operation associations are immutable'); END;

CREATE TABLE audit_events (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at INTEGER NOT NULL CHECK (` + integerCheckSQL("occurred_at") + `),
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

func uuidCheckSQL(column string) string {
	return `length(` + column + `) = 36 AND lower(` + column + `) = ` + column + ` AND substr(` + column + `,9,1)='-' AND substr(` + column + `,14,1)='-' AND substr(` + column + `,19,1)='-' AND substr(` + column + `,24,1)='-' AND substr(` + column + `,15,1) GLOB '[1-5]' AND substr(` + column + `,20,1) GLOB '[89ab]' AND replace(` + column + `,'-','') NOT GLOB '*[^0-9a-f]*'`
}

func hexCheckSQL(column string, length int) string {
	return `length(` + column + `) = ` + itoa(length) + ` AND lower(` + column + `) = ` + column + ` AND ` + column + ` NOT GLOB '*[^0-9a-f]*'`
}

func flatNameCheckSQL(column string) string {
	return `length(` + column + `) BETWEEN 1 AND 128 AND substr(` + column + `,1,1) NOT GLOB '[^A-Za-z0-9]' AND ` + column + ` NOT IN ('.','..') AND ` + column + ` NOT GLOB '*[^A-Za-z0-9._-]*'`
}

func versionCheckSQL(column string) string {
	tail := `substr(` + column + `,instr(` + column + `,'.')+1)`
	major := `CAST(substr(` + column + `,1,instr(` + column + `,'.')-1) AS INTEGER)`
	minor := `CAST(substr(` + tail + `,1,instr(` + tail + `,'.')-1) AS INTEGER)`
	patch := `CAST(substr(` + tail + `,instr(` + tail + `,'.')+1) AS INTEGER)`
	return column + ` NOT GLOB '*[^0-9.]*' AND ` + column + ` = printf('%d.%d.%d',` + major + `,` + minor + `,` + patch + `) AND ` + major + ` BETWEEN 0 AND 65534 AND ` + minor + ` BETWEEN 0 AND 65534 AND ` + patch + ` BETWEEN 0 AND 65534`
}

func integerCheckSQL(column string) string {
	return `typeof(` + column + `) = 'integer' AND ` + column + ` >= 0`
}

func itoa(value int) string {
	if value == 64 {
		return "64"
	}
	panic("unsupported schema constant")
}

var migrationOriginalV2 = `
DROP TRIGGER candidates_state_machine;
DROP TRIGGER sessions_state_machine;
DROP TRIGGER operations_state_machine;
DROP TRIGGER highest_committed_version_no_delete;
DROP TRIGGER highest_committed_version_monotonic;
DROP TRIGGER audit_events_append_only_update;
DROP TRIGGER audit_events_append_only_delete;
ALTER TABLE administrators RENAME TO administrators_legacy;
ALTER TABLE highest_committed_version RENAME TO highest_committed_version_legacy;
ALTER TABLE releases RENAME TO releases_legacy;
ALTER TABLE release_assets RENAME TO release_assets_legacy;
ALTER TABLE candidates RENAME TO candidates_legacy;
ALTER TABLE sessions RENAME TO sessions_legacy;
ALTER TABLE operations RENAME TO operations_legacy;
ALTER TABLE audit_events RENAME TO audit_events_legacy;
` + schemaV2 + `
INSERT INTO administrators SELECT singleton_id,username,password_hash,must_change_password,CAST(strftime('%s',created_at) AS INTEGER) FROM administrators_legacy;
INSERT INTO highest_committed_version SELECT singleton_id,version,major,minor,patch,CAST(strftime('%s',committed_at) AS INTEGER) FROM highest_committed_version_legacy;
INSERT INTO releases SELECT id,version,generation_name,'current',CAST(strftime('%s',committed_at) AS INTEGER),actor FROM releases_legacy;
INSERT INTO release_assets SELECT release_id,file_name,size_bytes,sha256 FROM release_assets_legacy;
DROP TABLE audit_events_legacy;
DROP TABLE operations_legacy;
DROP TABLE candidates_legacy;
DROP TABLE sessions_legacy;
DROP TABLE release_assets_legacy;
DROP TABLE releases_legacy;
DROP TABLE highest_committed_version_legacy;
DROP TABLE administrators_legacy;
`

var migrationHardenedV2 = `
DROP TRIGGER releases_initial_state;
DROP TRIGGER releases_state_machine;
DROP TRIGGER candidates_initial_state;
DROP TRIGGER candidates_immutable_bindings;
DROP TRIGGER candidates_state_machine;
DROP TRIGGER sessions_initial_state;
DROP TRIGGER sessions_state_machine;
DROP TRIGGER operations_initial_state;
DROP TRIGGER operations_state_machine;
DROP TRIGGER highest_committed_version_no_delete;
DROP TRIGGER highest_committed_version_monotonic;
DROP TRIGGER audit_events_append_only_update;
DROP TRIGGER audit_events_append_only_delete;
DROP TRIGGER audit_events_no_replace;
DROP INDEX releases_one_current;
ALTER TABLE administrators RENAME TO administrators_legacy;
ALTER TABLE highest_committed_version RENAME TO highest_committed_version_legacy;
ALTER TABLE releases RENAME TO releases_legacy;
ALTER TABLE release_assets RENAME TO release_assets_legacy;
ALTER TABLE candidates RENAME TO candidates_legacy;
ALTER TABLE sessions RENAME TO sessions_legacy;
ALTER TABLE operations RENAME TO operations_legacy;
ALTER TABLE audit_events RENAME TO audit_events_legacy;
` + schemaV2 + `
DROP TRIGGER releases_initial_state;
DROP TRIGGER sessions_initial_state;
DROP TRIGGER candidates_initial_state;
DROP TRIGGER operations_initial_state;
INSERT INTO administrators SELECT singleton_id,username,password_hash,must_change_password,CAST(strftime('%s',created_at) AS INTEGER) FROM administrators_legacy;
INSERT INTO highest_committed_version SELECT singleton_id,version,major,minor,patch,CAST(strftime('%s',committed_at) AS INTEGER) FROM highest_committed_version_legacy;
INSERT INTO releases SELECT id,version,generation_name,state,CAST(strftime('%s',committed_at) AS INTEGER),actor FROM releases_legacy;
INSERT INTO release_assets SELECT release_id,file_name,size_bytes,sha256 FROM release_assets_legacy;
INSERT INTO sessions SELECT id_hash,state,CAST(strftime('%s',created_at) AS INTEGER),CAST(strftime('%s',last_seen_at) AS INTEGER),CAST(strftime('%s',idle_expires_at) AS INTEGER),CAST(strftime('%s',absolute_expires_at) AS INTEGER) FROM sessions_legacy;
INSERT INTO candidates SELECT id,session_id,upload_digest,validation_report,state,CAST(strftime('%s',created_at) AS INTEGER),CAST(strftime('%s',expires_at) AS INTEGER),release_version FROM candidates_legacy;
INSERT INTO operations SELECT id,kind,state,candidate_id,release_id,result_code,CAST(strftime('%s',created_at) AS INTEGER),CASE WHEN finished_at IS NULL THEN NULL ELSE CAST(strftime('%s',finished_at) AS INTEGER) END FROM operations_legacy;
INSERT INTO audit_events(sequence,occurred_at,event_type,outcome,operation_id,candidate_id,release_id,code,source_pseudonym) SELECT sequence,CAST(strftime('%s',occurred_at) AS INTEGER),event_type,outcome,operation_id,candidate_id,release_id,code,source_pseudonym FROM audit_events_legacy;
` + initialStateTriggers + `
DROP TABLE audit_events_legacy;
DROP TABLE operations_legacy;
DROP TABLE candidates_legacy;
DROP TABLE sessions_legacy;
DROP TABLE release_assets_legacy;
DROP TABLE releases_legacy;
DROP TABLE highest_committed_version_legacy;
DROP TABLE administrators_legacy;
`

const initialStateTriggers = `
CREATE TRIGGER releases_initial_state
BEFORE INSERT ON releases WHEN NEW.state <> 'current'
BEGIN SELECT RAISE(ABORT, 'release must start current'); END;
CREATE TRIGGER sessions_initial_state
BEFORE INSERT ON sessions WHEN NEW.state <> 'active'
BEGIN SELECT RAISE(ABORT, 'session must start active'); END;
CREATE TRIGGER candidates_initial_state
BEFORE INSERT ON candidates WHEN NEW.state <> 'uploading' OR NEW.upload_digest <> '' OR NEW.validation_report <> '' OR NEW.release_version IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'candidate must start uploading'); END;
CREATE TRIGGER operations_initial_state
BEFORE INSERT ON operations WHEN NEW.state <> 'pending' OR NEW.finished_at IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'operation must start pending'); END;
`

type v1Layout int

const (
	v1LayoutOriginal v1Layout = iota + 1
	v1LayoutHardened
)

func applyV2Migration(ctx context.Context, tx *sql.Tx) error {
	layout, err := detectV1Layout(tx)
	if err != nil {
		return err
	}
	switch layout {
	case v1LayoutOriginal:
		if err := validateLegacyV1(tx); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, migrationOriginalV2); err != nil {
			return fmt.Errorf("migrate original V1 data: %w", err)
		}
	case v1LayoutHardened:
		if err := validateHardenedV1(tx); err != nil {
			return err
		}
		if _, err := tx.ExecContext(ctx, migrationHardenedV2); err != nil {
			return fmt.Errorf("migrate hardened V1 data: %w", err)
		}
	default:
		return errors.New("unsupported V1 schema layout")
	}
	return nil
}

func detectV1Layout(tx *sql.Tx) (v1Layout, error) {
	original, err := matchesV1Layout(tx, originalV1Columns, map[string][]string{
		"administrators":            {"check (length(password_hash) > 0)"},
		"releases":                  {"state in ('committed', 'retained', 'removed')"},
		"candidates":                {"fingerprint text not null"},
		"audit_events":              {"source_hash text not null default ''"},
		"highest_committed_version": {"prerelease text not null", "precedence_key blob not null"},
	})
	if err != nil {
		return 0, err
	}
	hardened, err := matchesV1Layout(tx, hardenedV1Columns, map[string][]string{
		"administrators":            {"password_hash glob '$argon2id$v=19$m=65536,t=3,p=2$*$*'"},
		"highest_committed_version": {"check (version = printf('%d.%d.%d', major, minor, patch))"},
		"releases":                  {"state in ('current', 'retained', 'removed')"},
		"candidates":                {"validation_report text not null default ''"},
		"operations":                {"check ((state in ('succeeded', 'failed')) = (finished_at is not null))"},
		"audit_events":              {"source_pseudonym text not null default ''"},
	})
	if err != nil {
		return 0, err
	}
	if original == hardened {
		return 0, errors.New("V1 schema does not match exactly one committed layout")
	}
	if original {
		return v1LayoutOriginal, nil
	}
	return v1LayoutHardened, nil
}

var originalV1Columns = map[string][]string{
	"administrators":            {"singleton_id", "username", "password_hash", "must_change_password", "created_at"},
	"highest_committed_version": {"singleton_id", "version", "major", "minor", "patch", "prerelease", "precedence_key", "sequence", "committed_at"},
	"releases":                  {"id", "version", "generation_name", "state", "committed_at", "actor"},
	"release_assets":            {"release_id", "file_name", "size_bytes", "sha256"},
	"candidates":                {"id", "session_id", "fingerprint", "state", "created_at", "expires_at", "release_version"},
	"sessions":                  {"id_hash", "state", "created_at", "last_seen_at", "idle_expires_at", "absolute_expires_at"},
	"operations":                {"id", "kind", "state", "candidate_id", "release_id", "result_code", "created_at", "finished_at"},
	"audit_events":              {"sequence", "occurred_at", "event_type", "outcome", "operation_id", "candidate_id", "release_id", "code", "source_hash"},
}

var hardenedV1Columns = map[string][]string{
	"administrators":            {"singleton_id", "username", "password_hash", "must_change_password", "created_at"},
	"highest_committed_version": {"singleton_id", "version", "major", "minor", "patch", "committed_at"},
	"releases":                  {"id", "version", "generation_name", "state", "committed_at", "actor"},
	"release_assets":            {"release_id", "file_name", "size_bytes", "sha256"},
	"candidates":                {"id", "session_id", "upload_digest", "validation_report", "state", "created_at", "expires_at", "release_version"},
	"sessions":                  {"id_hash", "state", "created_at", "last_seen_at", "idle_expires_at", "absolute_expires_at"},
	"operations":                {"id", "kind", "state", "candidate_id", "release_id", "result_code", "created_at", "finished_at"},
	"audit_events":              {"sequence", "occurred_at", "event_type", "outcome", "operation_id", "candidate_id", "release_id", "code", "source_pseudonym"},
}

func matchesV1Layout(tx *sql.Tx, expected map[string][]string, markers map[string][]string) (bool, error) {
	var tableCount int
	if err := tx.QueryRow(`SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'`).Scan(&tableCount); err != nil {
		return false, err
	}
	if tableCount != len(expected) {
		return false, nil
	}
	for table, wantColumns := range expected {
		rows, err := tx.Query(`SELECT name FROM pragma_table_info(?) ORDER BY cid`, table)
		if err != nil {
			return false, err
		}
		var gotColumns []string
		for rows.Next() {
			var name string
			if err := rows.Scan(&name); err != nil {
				rows.Close()
				return false, err
			}
			gotColumns = append(gotColumns, name)
		}
		if err := rows.Close(); err != nil {
			return false, err
		}
		if !equalStrings(gotColumns, wantColumns) {
			return false, nil
		}
		if len(markers[table]) == 0 {
			continue
		}
		var definition string
		if err := tx.QueryRow(`SELECT sql FROM sqlite_master WHERE type='table' AND name=?`, table).Scan(&definition); err != nil {
			return false, err
		}
		definition = normalizeSQL(definition)
		for _, marker := range markers[table] {
			if !strings.Contains(definition, normalizeSQL(marker)) {
				return false, nil
			}
		}
	}
	return true, nil
}

func equalStrings(left, right []string) bool {
	if len(left) != len(right) {
		return false
	}
	for index := range left {
		if left[index] != right[index] {
			return false
		}
	}
	return true
}

func normalizeSQL(value string) string {
	return strings.Join(strings.Fields(strings.ToLower(value)), " ")
}

func validateHardenedV1(tx *sql.Tx) error {
	if err := validateLegacyAdministrators(tx); err != nil {
		return err
	}
	if err := validateHardenedTimestamps(tx); err != nil {
		return err
	}
	var unsafeCandidates int
	if err := tx.QueryRow(`SELECT count(*) FROM candidates WHERE
        (state='uploading' AND (upload_digest<>'' OR validation_report<>'' OR release_version IS NOT NULL)) OR
        (state IN ('validated','publishing','published') AND (upload_digest='' OR validation_report='')) OR
        (state='published' AND release_version IS NULL) OR
        (state<>'published' AND release_version IS NOT NULL)`).Scan(&unsafeCandidates); err != nil {
		return err
	}
	if unsafeCandidates != 0 {
		return errors.New("hardened V1 contains unsafe candidate bindings")
	}
	return validateHardenedVersionFloor(tx)
}

func validateLegacyAdministrators(tx *sql.Tx) error {
	rows, err := tx.Query(`SELECT password_hash,created_at FROM administrators`)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var hash, at string
		if err := rows.Scan(&hash, &at); err != nil {
			return err
		}
		if _, _, err := decodePasswordHash(hash); err != nil {
			return fmt.Errorf("unsafe legacy administrator: %w", err)
		}
		if err := validateLegacyTimestamp(at); err != nil {
			return errors.New("unsafe legacy administrator timestamp")
		}
	}
	return rows.Err()
}

func validateHardenedTimestamps(tx *sql.Tx) error {
	queries := []string{
		`SELECT committed_at FROM highest_committed_version`,
		`SELECT committed_at FROM releases`,
		`SELECT created_at FROM sessions UNION ALL SELECT last_seen_at FROM sessions UNION ALL SELECT idle_expires_at FROM sessions UNION ALL SELECT absolute_expires_at FROM sessions`,
		`SELECT created_at FROM candidates UNION ALL SELECT expires_at FROM candidates`,
		`SELECT created_at FROM operations UNION ALL SELECT finished_at FROM operations WHERE finished_at IS NOT NULL`,
		`SELECT occurred_at FROM audit_events`,
	}
	for _, query := range queries {
		rows, err := tx.Query(query)
		if err != nil {
			return err
		}
		for rows.Next() {
			var value string
			if err := rows.Scan(&value); err != nil {
				rows.Close()
				return err
			}
			if err := validateLegacyTimestamp(value); err != nil {
				rows.Close()
				return errors.New("hardened V1 contains an unsafe timestamp")
			}
		}
		if err := rows.Close(); err != nil {
			return err
		}
	}
	return nil
}

func validateLegacyTimestamp(value string) error {
	parsed, err := time.Parse(time.RFC3339Nano, value)
	if err != nil || parsed.Unix() < 0 {
		return errors.New("timestamp must be non-negative RFC3339")
	}
	return nil
}

func validateHardenedVersionFloor(tx *sql.Tx) error {
	var floor numericVersion
	var haveFloor bool
	rows, err := tx.Query(`SELECT version,major,minor,patch FROM highest_committed_version`)
	if err != nil {
		return err
	}
	for rows.Next() {
		var version string
		if err := rows.Scan(&version, &floor.major, &floor.minor, &floor.patch); err != nil {
			rows.Close()
			return err
		}
		parsed, err := parseVersion(version)
		if err != nil || parsed != floor {
			rows.Close()
			return errors.New("unsafe hardened V1 highest version")
		}
		haveFloor = true
	}
	if err := rows.Close(); err != nil {
		return err
	}
	rows, err = tx.Query(`SELECT version FROM releases`)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var version string
		if err := rows.Scan(&version); err != nil {
			return err
		}
		parsed, err := parseVersion(version)
		if err != nil || !haveFloor || floor.compare(parsed) < 0 {
			return errors.New("hardened V1 release exceeds or lacks its permanent floor")
		}
	}
	return rows.Err()
}

func validateLegacyV1(tx *sql.Tx) error {
	for _, table := range []string{"candidates", "sessions", "operations", "audit_events"} {
		var count int
		if err := tx.QueryRow(`SELECT count(*) FROM ` + table).Scan(&count); err != nil {
			return err
		}
		if count != 0 {
			return fmt.Errorf("legacy %s rows cannot be safely transformed", table)
		}
	}
	rows, err := tx.Query(`SELECT password_hash,created_at FROM administrators`)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var hash, at string
		if err := rows.Scan(&hash, &at); err != nil {
			return err
		}
		if _, _, err := decodePasswordHash(hash); err != nil {
			return fmt.Errorf("unsafe legacy administrator: %w", err)
		}
		if _, err := time.Parse(time.RFC3339Nano, at); err != nil {
			return errors.New("unsafe legacy administrator timestamp")
		}
	}
	if err := rows.Err(); err != nil {
		return err
	}
	rows, err = tx.Query(`SELECT version,major,minor,patch,prerelease,committed_at FROM highest_committed_version`)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var version, pre, at string
		var major, minor, patch int64
		if err := rows.Scan(&version, &major, &minor, &patch, &pre, &at); err != nil {
			return err
		}
		parsed, err := parseVersion(version)
		if err != nil || pre != "" || parsed != (numericVersion{major, minor, patch}) {
			return errors.New("unsafe legacy highest version")
		}
		if _, err = time.Parse(time.RFC3339Nano, at); err != nil {
			return errors.New("unsafe legacy highest version timestamp")
		}
	}
	if err := rows.Err(); err != nil {
		return err
	}
	var releaseCount int
	if err := tx.QueryRow(`SELECT count(*) FROM releases`).Scan(&releaseCount); err != nil {
		return err
	}
	if releaseCount > 1 {
		return errors.New("multiple legacy releases cannot be safely assigned current state")
	}
	rows, err = tx.Query(`SELECT id,version,generation_name,state,committed_at FROM releases`)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var id, version, name, state, at string
		if err := rows.Scan(&id, &version, &name, &state, &at); err != nil {
			return err
		}
		if !uuidPatternStore.MatchString(id) || state != "committed" || !flatNamePattern.MatchString(name) {
			return errors.New("unsafe legacy release")
		}
		if _, err := parseVersion(version); err != nil {
			return err
		}
		if _, err = time.Parse(time.RFC3339Nano, at); err != nil {
			return errors.New("unsafe legacy release timestamp")
		}
	}
	if err := rows.Err(); err != nil {
		return err
	}
	if releaseCount == 1 {
		var releaseVersion, floorVersion string
		if err := tx.QueryRow(`SELECT version FROM releases LIMIT 1`).Scan(&releaseVersion); err != nil {
			return err
		}
		if err := tx.QueryRow(`SELECT version FROM highest_committed_version WHERE singleton_id=1`).Scan(&floorVersion); err != nil {
			return errors.New("legacy release lacks a permanent version floor")
		}
		releaseParsed, _ := parseVersion(releaseVersion)
		floorParsed, _ := parseVersion(floorVersion)
		if floorParsed.compare(releaseParsed) < 0 {
			return errors.New("legacy permanent floor is below committed release")
		}
	}
	rows, err = tx.Query(`SELECT file_name,sha256 FROM release_assets`)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var name, digest string
		if err := rows.Scan(&name, &digest); err != nil {
			return err
		}
		if !flatNamePattern.MatchString(name) || len(digest) != 64 || strings.ToLower(digest) != digest {
			return errors.New("unsafe legacy asset")
		}
	}
	return rows.Err()
}

const legacyV1DDL = `
CREATE TABLE administrators (singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),username TEXT NOT NULL UNIQUE CHECK (username = 'admin'),password_hash TEXT NOT NULL CHECK (length(password_hash) > 0),must_change_password INTEGER NOT NULL CHECK (must_change_password IN (0, 1)),created_at TEXT NOT NULL);
CREATE TABLE highest_committed_version (singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),version TEXT NOT NULL,major INTEGER NOT NULL CHECK (major >= 0),minor INTEGER NOT NULL CHECK (minor >= 0),patch INTEGER NOT NULL CHECK (patch >= 0),prerelease TEXT NOT NULL,precedence_key BLOB NOT NULL,sequence INTEGER NOT NULL CHECK (sequence > 0),committed_at TEXT NOT NULL);
CREATE TRIGGER highest_committed_version_no_delete BEFORE DELETE ON highest_committed_version BEGIN SELECT RAISE(ABORT, 'highest committed version is permanent'); END;
CREATE TRIGGER highest_committed_version_monotonic BEFORE UPDATE ON highest_committed_version WHEN NEW.sequence <= OLD.sequence OR NEW.precedence_key <= OLD.precedence_key BEGIN SELECT RAISE(ABORT, 'highest committed version must increase'); END;
CREATE TABLE releases (id TEXT PRIMARY KEY,version TEXT NOT NULL UNIQUE,generation_name TEXT NOT NULL UNIQUE CHECK (generation_name NOT LIKE '/%' AND generation_name NOT LIKE '%\\%' AND generation_name NOT LIKE '%..%'),state TEXT NOT NULL CHECK (state IN ('committed', 'retained', 'removed')),committed_at TEXT NOT NULL,actor TEXT NOT NULL DEFAULT 'admin' CHECK (actor = 'admin'));
CREATE TABLE release_assets (release_id TEXT NOT NULL REFERENCES releases(id) ON DELETE RESTRICT,file_name TEXT NOT NULL CHECK (file_name NOT LIKE '%/%' AND file_name NOT LIKE '%\\%' AND file_name NOT IN ('.', '..')),size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),sha256 TEXT NOT NULL CHECK (length(sha256) = 64 AND sha256 = lower(sha256)),PRIMARY KEY (release_id, file_name));
CREATE TABLE candidates (id TEXT PRIMARY KEY,session_id TEXT NOT NULL,fingerprint TEXT NOT NULL,state TEXT NOT NULL CHECK (state IN ('uploading', 'validated', 'publishing', 'published', 'failed', 'expired')),created_at TEXT NOT NULL,expires_at TEXT NOT NULL,release_version TEXT);
CREATE TRIGGER candidates_state_machine BEFORE UPDATE OF state ON candidates WHEN NOT ((OLD.state = 'uploading' AND NEW.state IN ('validated', 'failed', 'expired')) OR (OLD.state = 'validated' AND NEW.state IN ('publishing', 'failed', 'expired')) OR (OLD.state = 'publishing' AND NEW.state IN ('published', 'failed'))) BEGIN SELECT RAISE(ABORT, 'invalid candidate transition'); END;
CREATE TABLE sessions (id_hash TEXT PRIMARY KEY,state TEXT NOT NULL CHECK (state IN ('active', 'revoked', 'expired')),created_at TEXT NOT NULL,last_seen_at TEXT NOT NULL,idle_expires_at TEXT NOT NULL,absolute_expires_at TEXT NOT NULL,CHECK (idle_expires_at <= absolute_expires_at));
CREATE TRIGGER sessions_state_machine BEFORE UPDATE OF state ON sessions WHEN NOT (OLD.state = 'active' AND NEW.state IN ('revoked', 'expired')) BEGIN SELECT RAISE(ABORT, 'invalid session transition'); END;
CREATE TABLE operations (id TEXT PRIMARY KEY,kind TEXT NOT NULL CHECK (kind IN ('upload', 'publish', 'rollback', 'cleanup')),state TEXT NOT NULL CHECK (state IN ('pending', 'running', 'succeeded', 'failed')),candidate_id TEXT REFERENCES candidates(id) ON DELETE RESTRICT,release_id TEXT REFERENCES releases(id) ON DELETE RESTRICT,result_code TEXT NOT NULL DEFAULT '',created_at TEXT NOT NULL,finished_at TEXT);
CREATE TRIGGER operations_state_machine BEFORE UPDATE OF state ON operations WHEN NOT ((OLD.state = 'pending' AND NEW.state IN ('running', 'failed')) OR (OLD.state = 'running' AND NEW.state IN ('succeeded', 'failed'))) BEGIN SELECT RAISE(ABORT, 'invalid operation transition'); END;
CREATE TABLE audit_events (sequence INTEGER PRIMARY KEY AUTOINCREMENT,occurred_at TEXT NOT NULL,event_type TEXT NOT NULL,outcome TEXT NOT NULL CHECK (outcome IN ('success', 'failure')),operation_id TEXT NOT NULL DEFAULT '',candidate_id TEXT NOT NULL DEFAULT '',release_id TEXT NOT NULL DEFAULT '',code TEXT NOT NULL DEFAULT '',source_hash TEXT NOT NULL DEFAULT '');
CREATE TRIGGER audit_events_append_only_update BEFORE UPDATE ON audit_events BEGIN SELECT RAISE(ABORT, 'audit events are append-only'); END;
CREATE TRIGGER audit_events_append_only_delete BEFORE DELETE ON audit_events BEGIN SELECT RAISE(ABORT, 'audit events are append-only'); END;
PRAGMA user_version=1;
`
