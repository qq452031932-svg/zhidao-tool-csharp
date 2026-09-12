#!/usr/bin/env bash
set -euo pipefail
umask 077

BUNDLE=""
SOURCE_COMMIT=""
OTA_BINARY_SHA256=""
LICENSE_BINARY_SHA256=""
SOURCE_MANIFEST_SHA256=""
LICENSE_CONFIG_SOURCE=""
LICENSE_CONFIG_SHA256=""
LICENSE_UNIT_SOURCE=""
LICENSE_UNIT_SHA256=""
OTA_CONFIG_SOURCE=""
OTA_CONFIG_SHA256=""
OTA_UNIT_SOURCE=""
OTA_UNIT_SHA256=""
PREFLIGHT_ONLY=false
declare -A SEEN_ARGS=()
declare -A PREFLIGHT_UNIT_ENABLED=()
declare -A PREFLIGHT_UNIT_WANTS=()
SOURCE_COMMIT_TARGET="/opt/superpan-license/source-commit.txt"
OTA_INSTALLER="$(cd "$(dirname "${BASH_SOURCE[0]}")/../ota-admin" && pwd)/install.sh"
LICENSE_USER="superpan-license"
LICENSE_GROUP="superpan-license"
OTA_USER="superpan-ota-admin"
OTA_GROUP="superpan-ota-admin"
SHARED_GROUP="superpan-license-admin"
ROLLBACK_SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/rollback.sh"
LOCK_FILE="/run/lock/superpan-license-install.lock"
LICENSE_DB_SNAPSHOT="/opt/superpan-license-maintenance/license-db-snapshot"
LICENSE_DB_SNAPSHOT_SHA256="${LICENSE_DB_SNAPSHOT}.sha256"
LICENSE_DB_SNAPSHOT_COMMIT="/opt/superpan-license-maintenance/source-commit.txt"
TRANSACTION_ARMED=false
TRANSACTION_MUTATED=false
TRANSACTION_SUCCEEDED=false
TRANSACTION_BACKUP=""
declare -A SCHEMA_TEST_IDENTITIES=()
declare -A SCHEMA_TEST_BINARIES=()
FAILPOINT_REQUIRES_STOPPED_RECOVERY=false

usage() {
    cat >&2 <<'USAGE'
Usage: install.sh --bundle ABSOLUTE_PATH --source-commit 40_LOWERCASE_HEX \
  --ota-binary-sha256 HEX --license-binary-sha256 HEX \
  --source-manifest-sha256 HEX \
  --license-config ABSOLUTE_PATH --license-config-sha256 HEX \
  --license-unit ABSOLUTE_PATH --license-unit-sha256 HEX \
  --ota-config ABSOLUTE_PATH --ota-config-sha256 HEX \
  --ota-unit ABSOLUTE_PATH --ota-unit-sha256 HEX [--preflight-only]
USAGE
}

die() {
    echo "ERROR: $*" >&2
    exit 1
}

require_value() {
    [ "$#" -eq 2 ] && [ -n "$2" ] || die "missing value for $1"
}

mark_seen() {
    local option="$1"
    if [ "${SEEN_ARGS[$option]+present}" = "present" ]; then
        usage
        die "option must be provided exactly once: $option"
    fi
    SEEN_ARGS["$option"]=1
}

require_seen() {
    local option="$1"
    if [ "${SEEN_ARGS[$option]+present}" != "present" ]; then
        usage
        die "missing required option: $option"
    fi
}

# Root ownership plus a non-writable ancestor chain prevents unprivileged rename
# between pathname validation and the later read-only reopen. Root is the trust
# boundary; shell cannot pin an inode across separate tool invocations.
validate_trusted_path() {
    local trusted_path="$1" component component_path component_mode canonical
    local -a TRUSTED_PATH_COMPONENTS=()
    case "$trusted_path" in /*) ;; *) die "trusted path must be absolute: $trusted_path" ;; esac
    [ "$(stat -c '%u' /)" = "0" ] || die "trusted root must be owned by root"
    component_mode="$(stat -c '%a' /)"
    (( (8#$component_mode & 8#022) == 0 )) || die "trusted root must not be group/world writable"
    IFS='/' read -r -a TRUSTED_PATH_COMPONENTS <<<"${trusted_path#/}"
    component_path="/"
    for component in "${TRUSTED_PATH_COMPONENTS[@]}"; do
        [ -n "$component" ] && [ "$component" != "." ] && [ "$component" != ".." ] ||
            die "trusted path contains a non-canonical component: $trusted_path"
        component_path="${component_path%/}/$component"
        [ -e "$component_path" ] && [ ! -L "$component_path" ] ||
            die "trusted path component must exist and not be a symlink: $component_path"
        [ "$(stat -c '%u' "$component_path")" = "0" ] ||
            die "trusted path component must be owned by root: $component_path"
        component_mode="$(stat -c '%a' "$component_path")"
        (( (8#$component_mode & 8#022) == 0 )) ||
            die "trusted path component must not be group/world writable: $component_path"
    done
    canonical="$(readlink -f -- "$trusted_path")" || die "trusted path cannot be canonicalized: $trusted_path"
    [ "$canonical" = "$trusted_path" ] || die "trusted path must be canonical: $trusted_path"
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --bundle) mark_seen "$1"; require_value "$1" "${2:-}"; BUNDLE="$2"; shift 2 ;;
        --source-commit) mark_seen "$1"; require_value "$1" "${2:-}"; SOURCE_COMMIT="$2"; shift 2 ;;
        --ota-binary-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_BINARY_SHA256="$2"; shift 2 ;;
        --license-binary-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_BINARY_SHA256="$2"; shift 2 ;;
        --source-manifest-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; SOURCE_MANIFEST_SHA256="$2"; shift 2 ;;
        --license-config) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_CONFIG_SOURCE="$2"; shift 2 ;;
        --license-config-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_CONFIG_SHA256="$2"; shift 2 ;;
        --license-unit) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_UNIT_SOURCE="$2"; shift 2 ;;
        --license-unit-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_UNIT_SHA256="$2"; shift 2 ;;
        --ota-config) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_CONFIG_SOURCE="$2"; shift 2 ;;
        --ota-config-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_CONFIG_SHA256="$2"; shift 2 ;;
        --ota-unit) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_UNIT_SOURCE="$2"; shift 2 ;;
        --ota-unit-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_UNIT_SHA256="$2"; shift 2 ;;
        --preflight-only) mark_seen "$1"; PREFLIGHT_ONLY=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage; die "unknown argument: $1" ;;
    esac
done

for required in --bundle --source-commit --ota-binary-sha256 --license-binary-sha256 \
    --source-manifest-sha256 --license-config --license-config-sha256 \
    --license-unit --license-unit-sha256 --ota-config --ota-config-sha256 \
    --ota-unit --ota-unit-sha256; do
    require_seen "$required"
done
[ "$(id -u)" -eq 0 ] || die "install.sh must run as root"
case "$BUNDLE" in /*) ;; *) die "bundle path must be absolute and canonical" ;; esac
validate_trusted_path "$BUNDLE"
BUNDLE_CANONICAL="$(readlink -f -- "$BUNDLE")" || die "bundle path cannot be canonicalized"
[ "$BUNDLE_CANONICAL" = "$BUNDLE" ] || die "bundle path must be absolute and canonical"
[ -d "$BUNDLE" ] && [ ! -L "$BUNDLE" ] || die "bundle must be a real directory"
[ "$(stat -c '%U:%G' "$BUNDLE")" = "root:root" ] || die "bundle owner must be root:root"
BUNDLE_MODE="$(stat -c '%a' "$BUNDLE")"
[ "$BUNDLE_MODE" = "700" ] || die "bundle directory mode must be exactly 0700"

for hash in "$OTA_BINARY_SHA256" "$LICENSE_BINARY_SHA256" "$SOURCE_MANIFEST_SHA256" \
    "$LICENSE_CONFIG_SHA256" "$LICENSE_UNIT_SHA256" "$OTA_CONFIG_SHA256" "$OTA_UNIT_SHA256"; do
    [[ "$hash" =~ ^[0-9a-f]{64}$ ]] || die "every expected SHA-256 must be 64 lowercase hexadecimal characters"
done
[[ "$SOURCE_COMMIT" =~ ^[0-9a-f]{40}$ ]] || die "source commit must be exactly 40 lowercase hexadecimal characters"

EXPECTED_BUNDLE_NAMES=$'ota-admin\nota-admin.sha256\nsource-commit.txt\nsource-manifest.sha256\nsource-manifest.sha256.sha256\nsuperpan-license\nsuperpan-license.sha256'
ACTUAL_BUNDLE_NAMES="$(find "$BUNDLE" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)"
[ "$ACTUAL_BUNDLE_NAMES" = "$EXPECTED_BUNDLE_NAMES" ] || die "bundle is incomplete or contains unexpected files"
[ -z "$(find "$BUNDLE" -mindepth 1 -maxdepth 1 -type l -print -quit)" ] || die "bundle contains a symlink"
[ -z "$(find "$BUNDLE" -mindepth 1 -maxdepth 1 ! -type f -print -quit)" ] || die "bundle entries must be regular files"
[ -z "$(find "$BUNDLE" -mindepth 1 -maxdepth 1 -type f -links +1 -print -quit)" ] || die "bundle contains a hard-linked file"
[ -z "$(find "$BUNDLE" -mindepth 1 -maxdepth 1 -type f -perm /022 -print -quit)" ] || die "bundle files must not be group/world writable"

verify_sha256() {
    local source="$1" expected="$2" actual
    [ -f "$source" ] && [ ! -L "$source" ] || die "trusted input must be a regular non-symlink file: $source"
    [ "$(stat -c '%h' "$source")" = "1" ] || die "trusted input must have exactly one link: $source"
    actual="$(sha256sum -- "$source" | awk '{print $1}')"
    [ "$actual" = "$expected" ] || die "SHA-256 mismatch for $source"
}

verify_sidecar() {
    local payload="$1" sidecar="$2" expected="$3" name
    name="$(basename "$payload")"
    printf '%s  %s\n' "$expected" "$name" | cmp -s - "$sidecar" ||
        die "sidecar does not match the explicit expected SHA-256 as one canonical line: $sidecar"
}

validate_trusted_path "$LICENSE_CONFIG_SOURCE"
validate_trusted_path "$LICENSE_UNIT_SOURCE"
validate_trusted_path "$OTA_CONFIG_SOURCE"
validate_trusted_path "$OTA_UNIT_SOURCE"
for source in "$LICENSE_CONFIG_SOURCE" "$LICENSE_UNIT_SOURCE" "$OTA_CONFIG_SOURCE" "$OTA_UNIT_SOURCE"; do
    case "$source" in /*) ;; *) die "trusted template paths must be absolute" ;; esac
    [ "$(readlink -f -- "$source")" = "$source" ] || die "trusted template paths must be canonical"
    [ "$(stat -c '%U:%G' "$source")" = "root:root" ] || die "trusted templates must be owned by root:root"
    mode="$(stat -c '%a' "$source")"
    (( (8#$mode & 8#022) == 0 )) || die "trusted templates must not be group/world writable"
done

verify_sidecar "$BUNDLE/ota-admin" "$BUNDLE/ota-admin.sha256" "$OTA_BINARY_SHA256"
verify_sidecar "$BUNDLE/superpan-license" "$BUNDLE/superpan-license.sha256" "$LICENSE_BINARY_SHA256"
verify_sidecar "$BUNDLE/source-manifest.sha256" "$BUNDLE/source-manifest.sha256.sha256" "$SOURCE_MANIFEST_SHA256"
verify_sha256 "$BUNDLE/ota-admin" "$OTA_BINARY_SHA256"
verify_sha256 "$BUNDLE/superpan-license" "$LICENSE_BINARY_SHA256"
verify_sha256 "$BUNDLE/source-manifest.sha256" "$SOURCE_MANIFEST_SHA256"
verify_sha256 "$LICENSE_CONFIG_SOURCE" "$LICENSE_CONFIG_SHA256"
verify_sha256 "$LICENSE_UNIT_SOURCE" "$LICENSE_UNIT_SHA256"
verify_sha256 "$OTA_CONFIG_SOURCE" "$OTA_CONFIG_SHA256"
verify_sha256 "$OTA_UNIT_SOURCE" "$OTA_UNIT_SHA256"

validate_ota_shared_secret_config() {
    [ "$(grep -Ec '"license_admin_secret_path"[[:space:]]*:[[:space:]]*"/etc/superpan-license/admin.secret"' "$OTA_CONFIG_SOURCE")" -eq 1 ] ||
        die "OTA config must consume the shared license admin secret"
}
validate_ota_shared_secret_config

[ "$(wc -l <"$BUNDLE/source-commit.txt")" -eq 1 ] || die "source-commit.txt must contain one canonical line"
BUNDLE_SOURCE_COMMIT="$(cat "$BUNDLE/source-commit.txt")"
[[ "$BUNDLE_SOURCE_COMMIT" =~ ^[0-9a-f]{40}$ ]] || die "source-commit.txt is malformed"
[ "$BUNDLE_SOURCE_COMMIT" = "$SOURCE_COMMIT" ] || die "source-commit.txt does not match --source-commit"
printf '%s\n' "$SOURCE_COMMIT" | cmp -s - "$BUNDLE/source-commit.txt" || die "source-commit.txt is not canonical"

validate_trusted_path "$OTA_INSTALLER"
[ -f "$OTA_INSTALLER" ] && [ ! -L "$OTA_INSTALLER" ] || die "OTA installer is missing or unsafe"
[ "$(readlink -f -- "$OTA_INSTALLER")" = "$OTA_INSTALLER" ] || die "OTA installer path is not canonical"
[ "$(stat -c '%U:%G' "$OTA_INSTALLER")" = "root:root" ] || die "OTA installer must be owned by root:root"
[ "$(stat -c '%h' "$OTA_INSTALLER")" = "1" ] || die "OTA installer must have exactly one link"
OTA_INSTALLER_MODE="$(stat -c '%a' "$OTA_INSTALLER")"
(( (8#$OTA_INSTALLER_MODE & 8#022) == 0 )) || die "OTA installer must not be group/world writable"
validate_trusted_path "$ROLLBACK_SCRIPT"
[ -f "$ROLLBACK_SCRIPT" ] && [ ! -L "$ROLLBACK_SCRIPT" ] && [ "$(stat -c '%h:%U:%G' "$ROLLBACK_SCRIPT")" = 1:root:root ] || die "rollback script is missing or unsafe"
ROLLBACK_MODE="$(stat -c '%a' "$ROLLBACK_SCRIPT")"
(( (8#$ROLLBACK_MODE & 8#022) == 0 )) || die "rollback script must not be group/world writable"
bash "$OTA_INSTALLER" --loopback-license-transaction \
    --preflight-only \
    --binary "$BUNDLE/ota-admin" --binary-sha256 "$OTA_BINARY_SHA256" \
    --config "$OTA_CONFIG_SOURCE" --config-sha256 "$OTA_CONFIG_SHA256" \
    --unit "$OTA_UNIT_SOURCE" --unit-sha256 "$OTA_UNIT_SHA256" \
    --source-commit "$SOURCE_COMMIT"

# LICENSE_INSTALL_PREFLIGHT_COMPLETE: every input byte and source record is authenticated.
if [ "$PREFLIGHT_ONLY" = true ]; then
    echo "license installation preflight complete for source commit $SOURCE_COMMIT"
    exit 0
fi

DEST=/opt/superpan-license
ETC=/etc/superpan-license
DATA=/var/lib/superpan-license
BACKUP_ROOT=/var/backups/superpan-license
ENCRYPTION_KEY="$ETC/encryption.key"
LOOKUP_KEY="$ETC/lookup.key"
ADMIN_SECRET="$ETC/admin.secret"
SIGNING_KEY="$ETC/response-signing.key"
SIGNING_KEY_ID="$ETC/response-signing.key-id"
SIGNING_SPKI_DER="$ETC/response-signing.spki.der"
SIGNING_SPKI_BASE64="$ETC/response-signing.spki.base64"
SIGNING_SPKI_SHA256="$ETC/response-signing.spki.sha256"
PERSISTENT_PATHS=("$ENCRYPTION_KEY" "$LOOKUP_KEY" "$ADMIN_SECRET" "$SIGNING_KEY" "$SIGNING_KEY_ID" "$SIGNING_SPKI_DER" "$SIGNING_SPKI_BASE64" "$SIGNING_SPKI_SHA256")
PERSISTENT_STATE_MODE=""
PERSISTENT_TEMP_PATHS=()
PERSISTENT_PUBLISHED_PATHS=()
PERSISTENT_TRANSACTION_ARMED=false

exact_groups() {
    local user="$1" primary="$2" actual expected
    [ "$(id -gn "$user")" = "$primary" ] || die "service user $user has unexpected primary group"
    actual="$(id -Gn "$user" | tr ' ' '\n' | LC_ALL=C sort -u | paste -sd, -)"
    expected="$(printf '%s\n%s\n' "$primary" "$SHARED_GROUP" | LC_ALL=C sort -u | paste -sd, -)"
    [ "$actual" = "$expected" ] || die "service user $user must have exactly primary $primary and supplementary $SHARED_GROUP"
}

assert_exact_identity_before_mutation() {
    if id -u "$LICENSE_USER" >/dev/null 2>&1; then exact_groups "$LICENSE_USER" "$LICENSE_GROUP"; fi
    if id -u "$OTA_USER" >/dev/null 2>&1; then exact_groups "$OTA_USER" "$OTA_GROUP"; fi
}

assert_exact_identity_after_creation() {
    exact_groups "$LICENSE_USER" "$LICENSE_GROUP"
    exact_groups "$OTA_USER" "$OTA_GROUP"
}

validate_directory() {
    local path="$1" owner="$2" group="$3" mode="$4"
    [ -d "$path" ] && [ ! -L "$path" ] || die "unsafe directory: $path"
    [ "$(stat -c '%U:%G' "$path")" = "$owner:$group" ] || die "wrong directory owner/group: $path"
    [ "$(stat -c '%a' "$path")" = "$mode" ] || die "wrong directory mode: $path"
}

validate_file() {
    local path="$1" owner="$2" group="$3" mode="$4" size="$5" format="$6"
    [ -f "$path" ] && [ ! -L "$path" ] || die "persistent file must be regular and non-symlink: $path"
    [ "$(stat -c '%h' "$path")" = "1" ] || die "persistent file must have one link: $path"
    [ "$(stat -c '%U:%G' "$path")" = "$owner:$group" ] || die "wrong persistent file owner/group: $path"
    [ "$(stat -c '%a' "$path")" = "$mode" ] || die "wrong persistent file mode: $path"
    if [ "$size" != "variable" ]; then
        [ "$(stat -c '%s' "$path")" = "$size" ] || die "wrong persistent file size: $path"
    fi
    case "$format" in
        raw32) [ "$(wc -c <"$path")" -eq 32 ] || die "invalid raw secret: $path" ;;
        key-id) [[ "$(cat "$path")" =~ ^license-signing-[0-9a-f]{32}$ ]] && [ "$(wc -l <"$path")" -eq 1 ] || die "invalid signing key ID" ;;
        sha256) [[ "$(cat "$path")" =~ ^[0-9a-f]{64}$ ]] && [ "$(wc -l <"$path")" -eq 1 ] || die "invalid SPKI SHA-256" ;;
        base64) [[ "$(cat "$path")" =~ ^[A-Za-z0-9+/]+={0,2}$ ]] && [ "$(wc -l <"$path")" -eq 1 ] || die "invalid canonical SPKI base64" ;;
        ec-private)
            [ "$(grep -c '^-----BEGIN EC PRIVATE KEY-----$' "$path")" -eq 1 ] &&
                [ "$(grep -c '^-----END EC PRIVATE KEY-----$' "$path")" -eq 1 ] || die "invalid canonical EC private key PEM"
            openssl ec -in "$path" -check -noout >/dev/null 2>&1 || die "invalid EC private key"
            ;;
        der) openssl pkey -pubin -inform DER -in "$path" -noout >/dev/null 2>&1 || die "invalid SPKI DER" ;;
        config) ;;
        database) ;;
        *) die "unknown validation format" ;;
    esac
}

validate_signing_identity_set() {
    local private_key="$1" spki_der="$2" spki_base64="$3" spki_sha256="$4" key_id="$5"
    local actual_hash expected_id
    openssl ec -in "$private_key" -noout -text 2>/dev/null | grep -Eq 'ASN1 OID: prime256v1|NIST CURVE: P-256' ||
        die "signing key must use P-256"
    openssl ec -in "$private_key" -pubout -outform DER 2>/dev/null | cmp -s - "$spki_der" ||
        die "signing private/public key mismatch"
    base64 -w 0 "$spki_der" | cmp -s - <(tr -d '\n' <"$spki_base64") ||
        die "SPKI base64 does not match DER"
    actual_hash="$(sha256sum -- "$spki_der" | awk '{print $1}')"
    [ "$(cat "$spki_sha256")" = "$actual_hash" ] || die "SPKI hash does not match DER"
    expected_id="license-signing-${actual_hash:0:32}"
    [ "$(cat "$key_id")" = "$expected_id" ] || die "signing key ID does not match SPKI identity"
}

validate_signing_identity() {
    validate_signing_identity_set "$SIGNING_KEY" "$SIGNING_SPKI_DER" "$SIGNING_SPKI_BASE64" "$SIGNING_SPKI_SHA256" "$SIGNING_KEY_ID"
}

validate_database_acl() {
    local path
    for path in "$DATA/state.db" "$DATA/state.db-wal" "$DATA/state.db-shm"; do
        if [ -e "$path" ] || [ -L "$path" ]; then
            validate_file "$path" "$LICENSE_USER" "$LICENSE_GROUP" 600 variable database
        fi
    done
}

validate_existing_acl_before_mutation() {
    if [ -e "$ETC" ] || [ -L "$ETC" ]; then
        validate_directory "$ETC" root "$SHARED_GROUP" 710
        validate_trusted_path "$ETC"
    fi
    if [ -e "$DATA" ] || [ -L "$DATA" ]; then
        validate_trusted_path /var/lib
        validate_directory "$DATA" "$LICENSE_USER" "$LICENSE_GROUP" 700
    fi
    if [ -e "$BACKUP_ROOT" ] || [ -L "$BACKUP_ROOT" ]; then
        validate_directory "$BACKUP_ROOT" root root 700
        validate_trusted_path "$BACKUP_ROOT"
    fi
    validate_database_acl
}

validate_persistent_state_before_mutation() {
    local present=0 path database_present=false
    [ ! -e "$DATA/state.db" ] && [ ! -L "$DATA/state.db" ] || database_present=true
    for path in "${PERSISTENT_PATHS[@]}"; do
        if [ -e "$path" ] || [ -L "$path" ]; then present=$((present + 1)); fi
    done
    if [ "$present" -eq 0 ]; then
        [ "$database_present" = false ] || die "database exists without a complete valid key set"
        PERSISTENT_STATE_MODE="absent"
        return
    fi
    [ "$present" -eq "${#PERSISTENT_PATHS[@]}" ] || die "persistent key set is partial; refusing to overwrite"
    validate_directory "$ETC" root "$SHARED_GROUP" 710
    validate_trusted_path "$ETC"
    validate_file "$ENCRYPTION_KEY" root "$LICENSE_GROUP" 640 32 raw32
    validate_file "$LOOKUP_KEY" root "$LICENSE_GROUP" 640 32 raw32
    validate_file "$ADMIN_SECRET" root "$SHARED_GROUP" 640 32 raw32
    validate_file "$SIGNING_KEY" root "$LICENSE_GROUP" 640 227 ec-private
    validate_file "$SIGNING_KEY_ID" root root 644 49 key-id
    validate_file "$SIGNING_SPKI_DER" root root 644 91 der
    validate_file "$SIGNING_SPKI_BASE64" root root 644 125 base64
    validate_file "$SIGNING_SPKI_SHA256" root root 644 65 sha256
    ! cmp -s "$ENCRYPTION_KEY" "$LOOKUP_KEY" || die "encryption and lookup keys must differ"
    ! cmp -s "$ENCRYPTION_KEY" "$ADMIN_SECRET" || die "encryption and admin keys must differ"
    ! cmp -s "$LOOKUP_KEY" "$ADMIN_SECRET" || die "lookup and admin keys must differ"
    validate_signing_identity
    if [ "$database_present" = true ]; then
        PERSISTENT_STATE_MODE="existing"
    else
        PERSISTENT_STATE_MODE="keys-without-database"
    fi
}

validate_existing_config_before_mutation() {
    local config="$ETC/config.json"
    if [ -e "$config" ] || [ -L "$config" ]; then
        validate_file "$config" root "$LICENSE_GROUP" 640 variable config
        "$BUNDLE/superpan-license" check-config --config "$config" >/dev/null || die "existing production license config is invalid"
    fi
}

assert_exact_identity_before_mutation
validate_existing_acl_before_mutation
validate_existing_config_before_mutation
validate_persistent_state_before_mutation

unit_wants_link() {
    case "$1" in
        superpan-license.service) printf '%s\n' /etc/systemd/system/multi-user.target.wants/superpan-license.service ;;
        superpan-ota-admin.service) printf '%s\n' /etc/systemd/system/multi-user.target.wants/superpan-ota-admin.service ;;
        *) die "unexpected unit state target" ;;
    esac
}
capture_recoverable_unit_state() {
    local unit="$1" enabled link expected wants=absent
    enabled="$(systemctl is-enabled "$unit" 2>/dev/null || :)"; [ -n "$enabled" ] || enabled=not-found
    case "$enabled" in enabled|disabled|not-found) ;; *) die "unit enablement state cannot be restored exactly: $unit=$enabled" ;; esac
    link="$(unit_wants_link "$unit")"; expected="/etc/systemd/system/$unit"
    if [ -e "$link" ] || [ -L "$link" ]; then
        [ -L "$link" ] && [ "$(readlink -f -- "$link")" = "$expected" ] || die "unsafe preexisting wants link for $unit"
        wants=present
    fi
    case "$enabled:$wants" in enabled:present|disabled:absent|not-found:absent) ;; *) die "unit enablement and wants link disagree: $unit" ;; esac
    PREFLIGHT_UNIT_ENABLED["$unit"]="$enabled"
    PREFLIGHT_UNIT_WANTS["$unit"]="$wants"
}
assert_recoverable_unit_state_before_mutation() {
    capture_recoverable_unit_state superpan-license.service
    capture_recoverable_unit_state superpan-ota-admin.service
}
assert_recoverable_unit_state_before_mutation

UPGRADE_MODE=false
if [ -e "$DEST/superpan-license" ] || [ -e "$DATA/state.db" ]; then
    [ -f "$DEST/superpan-license" ] && [ ! -L "$DEST/superpan-license" ] && [ -f "$DATA/state.db" ] && [ ! -L "$DATA/state.db" ] ||
        die "upgrade requires paired old license binary and schema; initial install requires both absent"
    for source in "$LICENSE_DB_SNAPSHOT" "$LICENSE_DB_SNAPSHOT_SHA256" "$LICENSE_DB_SNAPSHOT_COMMIT"; do
        validate_trusted_path "$source"
        [ -f "$source" ] && [ ! -L "$source" ] && [ "$(stat -c '%h:%U:%G' "$source")" = 1:root:root ] || die "unsafe license-db-snapshot input"
    done
    SNAPSHOT_EXPECTED="$(awk 'NF==2 && $1 ~ /^[0-9a-f]{64}$/ && $2 == "license-db-snapshot" {print $1}' "$LICENSE_DB_SNAPSHOT_SHA256")"
    [ -n "$SNAPSHOT_EXPECTED" ] || die "license-db-snapshot sidecar is malformed"
    verify_sha256 "$LICENSE_DB_SNAPSHOT" "$SNAPSHOT_EXPECTED"
    printf '%s\n' "$SOURCE_COMMIT" | cmp -s - "$LICENSE_DB_SNAPSHOT_COMMIT" || die "license-db-snapshot is not from the authenticated source commit"
    UPGRADE_MODE=true
fi

case "${LICENSE_INSTALL_FAILPOINT:-}" in
    ""|identity|secret-encryption|secret-lookup|secret-admin|secret-signing|secret-publish|backup|license-binary|license-config|license-unit|license-start|license-health|ota-binary|ota-config|ota-unit|ota-restart|ota-health) ;;
    *) die "unknown LICENSE_INSTALL_FAILPOINT" ;;
esac
failpoint() {
    local point="$1"
    if [ "${LICENSE_INSTALL_FAILPOINT:-}" = "$point" ]; then
        [ "$point" != identity ] || FAILPOINT_REQUIRES_STOPPED_RECOVERY=true
        die "injected failure at $point"
    fi
}

transaction_signal() { exit "$1"; }
transaction_exit() {
    local status=$? pid
    trap - EXIT INT TERM HUP
    set +e
    if declare -F kill_registered_schema_processes >/dev/null; then kill_registered_schema_processes; fi
    if declare -F cleanup_persistent_transaction >/dev/null; then cleanup_persistent_transaction; fi
    if [ "$TRANSACTION_ARMED" = true ] && [ "$TRANSACTION_MUTATED" = true ] && [ "$TRANSACTION_SUCCEEDED" != true ]; then
        if [ -f "$TRANSACTION_BACKUP/manifest.sha256" ]; then
            if ! bash "$ROLLBACK_SCRIPT" "$TRANSACTION_BACKUP"; then
                systemctl stop superpan-ota-admin.service superpan-license.service 2>/dev/null
                echo "ERROR: automatic rollback failed; backup retained; services remain stopped for manual repair: $TRANSACTION_BACKUP" >&2
            fi
        else
            systemctl stop superpan-ota-admin.service superpan-license.service 2>/dev/null
            echo "ERROR: transaction failed before recoverable manifest; services remain stopped for manual repair" >&2
        fi
        if [ "$FAILPOINT_REQUIRES_STOPPED_RECOVERY" = true ]; then
            systemctl stop superpan-ota-admin.service superpan-license.service 2>/dev/null
            echo "ERROR: identity failure restored file state; services remain stopped for recoverable identity review" >&2
        fi
    fi
    exit "$status"
}

TRANSACTION_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$-${RANDOM}"
TRANSACTION_BACKUP="$BACKUP_ROOT/$TRANSACTION_ID"
trap transaction_exit EXIT
trap 'transaction_signal 130' INT
trap 'transaction_signal 143' TERM
trap 'transaction_signal 129' HUP
TRANSACTION_ARMED=true
if [ -e "$LOCK_FILE" ] || [ -L "$LOCK_FILE" ]; then
    [ -f "$LOCK_FILE" ] && [ ! -L "$LOCK_FILE" ] && [ "$(stat -c '%h:%U:%G:%a' "$LOCK_FILE")" = 1:root:root:600 ] || die "fixed transaction lock is unsafe"
    exec 9>>"$LOCK_FILE"
else
    set -o noclobber
    exec 9>"$LOCK_FILE" || die "cannot create fixed transaction lock"
    set +o noclobber
    chmod 0600 "$LOCK_FILE"; chown root:root "$LOCK_FILE"
fi
flock -n 9 || die "another license installation transaction is active"

TRANSACTION_MUTATED=true
install -d -m 0700 -o root -g root "$BACKUP_ROOT"
mkdir -m 0700 "$TRANSACTION_BACKUP"
chown root:root "$TRANSACTION_BACKUP"
[ "$(stat -c '%U:%G:%a' "$TRANSACTION_BACKUP")" = root:root:700 ] || die "transaction backup metadata is unsafe"
BACKUP_DIR="$TRANSACTION_BACKUP"

record_unit_state() {
    local unit="$1" enabled active link expected wants=absent
    enabled="$(systemctl is-enabled "$unit" 2>/dev/null || :)"; [ -n "$enabled" ] || enabled=not-found
    [ "$enabled" = "${PREFLIGHT_UNIT_ENABLED[$unit]}" ] || die "unit enablement changed after preflight"
    active=inactive; systemctl is-active --quiet "$unit" && active=active
    link="$(unit_wants_link "$unit")"
    expected="/etc/systemd/system/$unit"
    if [ -e "$link" ] || [ -L "$link" ]; then
        [ -L "$link" ] && [ "$(readlink -f -- "$link")" = "$expected" ] || die "unsafe preexisting wants link for $unit"
        wants=present
    fi
    [ "$wants" = "${PREFLIGHT_UNIT_WANTS[$unit]}" ] || die "unit wants link changed after preflight"
    printf '%s\t%s\t%s\t%s\n' "$unit" "$enabled" "$active" "$wants" >>"$BACKUP_DIR/unit-state.tsv"
}
backup_target() {
    local target="$1" name="$2"
    if [ -e "$target" ] || [ -L "$target" ]; then
        [ -f "$target" ] && [ ! -L "$target" ] && [ "$(stat -c '%h' "$target")" = 1 ] || die "unsafe rollback target: $target"
        cp -a -- "$target" "$BACKUP_DIR/$name"
        printf '%s\t%s\tpresent\t%s\t%s\t%s\t%s\n' "$target" "$name" "$(stat -c '%u' "$target")" "$(stat -c '%g' "$target")" "$(stat -c '%a' "$target")" "$(sha256sum -- "$target" | awk '{print $1}')" >>"$BACKUP_DIR/inventory.tsv"
    else
        printf '%s\t%s\tabsent\t-\t-\t-\t-\n' "$target" "$name" >>"$BACKUP_DIR/inventory.tsv"
    fi
}
backup_target "$DEST/superpan-license" license-binary
backup_target "$ETC/config.json" license-config
backup_target /etc/systemd/system/superpan-license.service license-unit
backup_target /opt/superpan-ota-admin/ota-admin ota-binary
backup_target /etc/superpan-ota-admin/config.json ota-config
backup_target /etc/systemd/system/superpan-ota-admin.service ota-unit
backup_target "$SOURCE_COMMIT_TARGET" license-source-commit
record_unit_state superpan-license.service
record_unit_state superpan-ota-admin.service
printf '%s\n' "$SOURCE_COMMIT" >"$BACKUP_DIR/source-commit.txt"
if [ "$PERSISTENT_STATE_MODE" = existing ]; then
    [ -f "$DATA/state.db" ] && [ ! -L "$DATA/state.db" ] || die "existing persistent state lost its database"
    {
        printf 'state existing\n'
        printf 'key-id %s\n' "$(cat "$SIGNING_KEY_ID")"
        printf 'spki-sha256 %s\n' "$(cat "$SIGNING_SPKI_SHA256")"
        printf 'encryption-sha256 %s\n' "$(sha256sum -- "$ENCRYPTION_KEY" | awk '{print $1}')"
        printf 'lookup-sha256 %s\n' "$(sha256sum -- "$LOOKUP_KEY" | awk '{print $1}')"
        printf 'admin-sha256 %s\n' "$(sha256sum -- "$ADMIN_SECRET" | awk '{print $1}')"
        if [ -f "$DATA/state.db" ]; then printf 'database-sha256 %s\n' "$(sha256sum -- "$DATA/state.db" | awk '{print $1}')"; fi
    } >"$BACKUP_DIR/persistent-identity.txt"
elif [ "$PERSISTENT_STATE_MODE" = keys-without-database ]; then
    {
        printf 'state keys-without-database\n'
        printf 'key-id %s\n' "$(cat "$SIGNING_KEY_ID")"
        printf 'spki-sha256 %s\n' "$(cat "$SIGNING_SPKI_SHA256")"
        printf 'encryption-sha256 %s\n' "$(sha256sum -- "$ENCRYPTION_KEY" | awk '{print $1}')"
        printf 'lookup-sha256 %s\n' "$(sha256sum -- "$LOOKUP_KEY" | awk '{print $1}')"
        printf 'admin-sha256 %s\n' "$(sha256sum -- "$ADMIN_SECRET" | awk '{print $1}')"
        printf 'database absent\n'
    } >"$BACKUP_DIR/persistent-identity.txt"
else
    printf 'state absent\ndatabase absent\n' >"$BACKUP_DIR/persistent-identity.txt"
fi
chmod 0600 "$BACKUP_DIR"/*.tsv "$BACKUP_DIR/source-commit.txt" "$BACKUP_DIR/persistent-identity.txt"
refresh_manifest() {
    (cd "$BACKUP_DIR"; find . -maxdepth 1 -type f ! -name manifest.sha256 -printf '%f\n' | LC_ALL=C sort | while IFS= read -r name; do sha256sum -- "$name"; done) >"$BACKUP_DIR/manifest.sha256"
    chmod 0600 "$BACKUP_DIR/manifest.sha256"
}
refresh_manifest
failpoint backup

if ! getent group "$LICENSE_GROUP" >/dev/null; then groupadd --system superpan-license; fi
if ! getent group "$OTA_GROUP" >/dev/null; then groupadd --system superpan-ota-admin; fi
if ! getent group "$SHARED_GROUP" >/dev/null; then groupadd --system superpan-license-admin; fi
if ! id -u "$LICENSE_USER" >/dev/null 2>&1; then
    useradd --system --gid "$LICENSE_GROUP" --groups "$SHARED_GROUP" --home-dir "$DATA" --shell /sbin/nologin "$LICENSE_USER"
fi
if ! id -u "$OTA_USER" >/dev/null 2>&1; then
    useradd --system --gid "$OTA_GROUP" --groups "$SHARED_GROUP" --home-dir /var/lib/superpan-ota-admin --shell /sbin/nologin "$OTA_USER"
fi
# Existing identities were checked before mutation; this exact replacement is
# intentionally non-additive and makes newly created identities deterministic.
usermod --gid "$LICENSE_GROUP" --groups "$SHARED_GROUP" "$LICENSE_USER"
usermod --gid "$OTA_GROUP" --groups "$SHARED_GROUP" "$OTA_USER"
assert_exact_identity_after_creation
failpoint identity

install -d -m 0755 -o root -g root "$DEST"
install -d -m 0710 -o root -g "$SHARED_GROUP" "$ETC"
install -d -m 0700 -o "$LICENSE_USER" -g "$LICENSE_GROUP" "$DATA"

atomic_install_file() {
    local source="$1" target="$2" owner="$3" group="$4" mode="$5" temp
    temp="$(mktemp "${target}.tmp.XXXXXX")" || die "cannot create atomic temporary file"
    [ "$(dirname "$temp")" = "$(dirname "$target")" ] && [ ! -L "$temp" ] && [ "$(stat -c '%h' "$temp")" = 1 ] ||
        die "unsafe atomic temporary file"
    install -m 0600 -o "$owner" -g "$group" "$source" "$temp"
    sync "$temp" 2>/dev/null || sync
    chmod "$mode" "$temp"
    [ "$(stat -c '%U:%G:%a:%h' "$temp")" = "$owner:$group:$mode:1" ] || die "atomic temporary metadata mismatch"
    mv -T -- "$temp" "$target"
}

sync_directory() {
    local directory="$1"
    sync -f "$directory" 2>/dev/null || sync
}

cleanup_persistent_transaction() {
    local status=$? path
    if [ "$PERSISTENT_TRANSACTION_ARMED" = true ]; then
        set +e
        for path in "${PERSISTENT_TEMP_PATHS[@]}"; do rm -f -- "$path"; done
        for path in "${PERSISTENT_PUBLISHED_PATHS[@]}"; do rm -f -- "$path"; done
        sync_directory "$ETC"
    fi
    return "$status"
}

validate_open_file() {
    local fd="$1" owner="$2" group="$3" mode="$4" size="$5" format="$6" path="/proc/self/fd/$1"
    [ "$(stat -Lc '%U:%G:%a:%h' "$path")" = "$owner:$group:$mode:1" ] || die "open persistent file metadata mismatch"
    [ "$(stat -Lc '%s' "$path")" = "$size" ] || die "open persistent file size mismatch"
    case "$format" in
        raw32) [ "$(wc -c <"$path")" -eq 32 ] || die "invalid open raw secret" ;;
        key-id) [[ "$(cat "$path")" =~ ^license-signing-[0-9a-f]{32}$ ]] && [ "$(wc -l <"$path")" -eq 1 ] || die "invalid open key ID" ;;
        sha256) [[ "$(cat "$path")" =~ ^[0-9a-f]{64}$ ]] && [ "$(wc -l <"$path")" -eq 1 ] || die "invalid open hash" ;;
        base64) [[ "$(cat "$path")" =~ ^[A-Za-z0-9+/]+={0,2}$ ]] && [ "$(wc -l <"$path")" -eq 1 ] || die "invalid open base64" ;;
        ec-private) openssl ec -in "$path" -check -noout >/dev/null 2>&1 || die "invalid open EC key" ;;
        der) openssl pkey -pubin -inform DER -in "$path" -noout >/dev/null 2>&1 || die "invalid open DER" ;;
        *) die "unknown open persistent format" ;;
    esac
}

create_exclusive_temp() {
    local target="$1" owner="$2" group="$3" mode="$4" format="$5" size="$6" generator="$7"
    local attempt temp fd fd_identity path_identity
    for attempt in 1 2 3 4 5 6 7 8; do
        temp="${target}.tmp.$$.${RANDOM}.${attempt}"
        set -o noclobber
        if exec {fd}>"$temp"; then
            set +o noclobber
            break
        fi
        set +o noclobber
        temp=""
    done
    [ -n "$temp" ] || die "cannot create exclusive persistent temporary file"
    PERSISTENT_TEMP_PATHS+=("$temp")
    case "$generator" in
        raw32) head -c 32 /dev/urandom >&"$fd" ;;
        p256) openssl ecparam -name prime256v1 -genkey -noout >&"$fd" ;;
        *) die "unknown persistent generator" ;;
    esac
    chmod "$mode" "/proc/self/fd/$fd"
    chown "$owner:$group" "/proc/self/fd/$fd"
    fd_identity="$(stat -Lc '%d:%i:%h' "/proc/self/fd/$fd")"
    path_identity="$(stat -Lc '%d:%i:%h' "$temp")"
    [ "$fd_identity" = "$path_identity" ] && [ "${fd_identity##*:}" = 1 ] || die "persistent temporary inode changed"
    validate_open_file "$fd" "$owner" "$group" "$mode" "$size" "$format"
    sync -f "/proc/self/fd/$fd" 2>/dev/null || sync
    exec {fd}>&-
    CREATED_TEMP="$temp"
}

stage_derived_file() {
    local target="$1" owner="$2" group="$3" mode="$4" size="$5" format="$6" source="$7"
    local temp fd fd_identity path_identity attempt
    for attempt in 1 2 3 4 5 6 7 8; do
        temp="${target}.tmp.$$.${RANDOM}.${attempt}"
        set -o noclobber
        if exec {fd}>"$temp"; then set +o noclobber; break; fi
        set +o noclobber
        temp=""
    done
    [ -n "$temp" ] || die "cannot create exclusive derived temporary file"
    PERSISTENT_TEMP_PATHS+=("$temp")
    case "$format" in
        der) openssl ec -in "$source" -pubout -outform DER >&"$fd" 2>/dev/null ;;
        base64) base64 -w 0 "$source" >&"$fd"; printf '\n' >&"$fd" ;;
        sha256) printf '%s\n' "$(sha256sum -- "$source" | awk '{print $1}')" >&"$fd" ;;
        key-id) printf 'license-signing-%s\n' "$(sha256sum -- "$source" | awk '{print substr($1,1,32)}')" >&"$fd" ;;
        *) die "unknown derived persistent format" ;;
    esac
    chmod "$mode" "/proc/self/fd/$fd"
    chown "$owner:$group" "/proc/self/fd/$fd"
    fd_identity="$(stat -Lc '%d:%i:%h' "/proc/self/fd/$fd")"
    path_identity="$(stat -Lc '%d:%i:%h' "$temp")"
    [ "$fd_identity" = "$path_identity" ] && [ "${fd_identity##*:}" = 1 ] || die "derived temporary inode changed"
    validate_open_file "$fd" "$owner" "$group" "$mode" "$size" "$format"
    sync -f "/proc/self/fd/$fd" 2>/dev/null || sync
    exec {fd}>&-
    CREATED_TEMP="$temp"
}

publish_persistent_transaction() {
    local index target temp
    for index in "${!PERSISTENT_PATHS[@]}"; do
        target="${PERSISTENT_PATHS[$index]}"
        temp="${PERSISTENT_TEMP_PATHS[$index]}"
        [ ! -e "$target" ] && [ ! -L "$target" ] || die "persistent target appeared during transaction"
        mv -T -- "$temp" "$target"
        PERSISTENT_PUBLISHED_PATHS+=("$target")
    done
    sync_directory "$ETC"
    PERSISTENT_TRANSACTION_ARMED=false
}

create_persistent_state() {
    local encryption_temp lookup_temp admin_temp signing_temp der_temp base64_temp hash_temp id_temp
    [ "$PERSISTENT_STATE_MODE" = "absent" ] || return
    PERSISTENT_TRANSACTION_ARMED=true
    create_exclusive_temp "$ENCRYPTION_KEY" root "$LICENSE_GROUP" 640 raw32 32 raw32; encryption_temp="$CREATED_TEMP"
    failpoint secret-encryption
    create_exclusive_temp "$LOOKUP_KEY" root "$LICENSE_GROUP" 640 raw32 32 raw32; lookup_temp="$CREATED_TEMP"
    failpoint secret-lookup
    create_exclusive_temp "$ADMIN_SECRET" root "$SHARED_GROUP" 640 raw32 32 raw32; admin_temp="$CREATED_TEMP"
    failpoint secret-admin
    create_exclusive_temp "$SIGNING_KEY" root "$LICENSE_GROUP" 640 ec-private 227 p256; signing_temp="$CREATED_TEMP"
    stage_derived_file "$SIGNING_SPKI_DER" root root 644 91 der "$signing_temp"; der_temp="$CREATED_TEMP"
    stage_derived_file "$SIGNING_SPKI_BASE64" root root 644 125 base64 "$der_temp"; base64_temp="$CREATED_TEMP"
    stage_derived_file "$SIGNING_SPKI_SHA256" root root 644 65 sha256 "$der_temp"; hash_temp="$CREATED_TEMP"
    stage_derived_file "$SIGNING_KEY_ID" root root 644 49 key-id "$der_temp"; id_temp="$CREATED_TEMP"
    failpoint secret-signing
    PERSISTENT_TEMP_PATHS=("$encryption_temp" "$lookup_temp" "$admin_temp" "$signing_temp" "$id_temp" "$der_temp" "$base64_temp" "$hash_temp")
    ! cmp -s "$encryption_temp" "$lookup_temp" || die "independent random keys unexpectedly matched"
    ! cmp -s "$ENCRYPTION_KEY" "$ADMIN_SECRET" || die "encryption and admin keys must differ"
    ! cmp -s "$LOOKUP_KEY" "$ADMIN_SECRET" || die "lookup and admin keys must differ"
    ! cmp -s "$encryption_temp" "$admin_temp" || die "independent encryption/admin keys unexpectedly matched"
    ! cmp -s "$lookup_temp" "$admin_temp" || die "independent lookup/admin keys unexpectedly matched"
    validate_signing_identity_set "$signing_temp" "$der_temp" "$base64_temp" "$hash_temp" "$id_temp"
    publish_persistent_transaction
    failpoint secret-publish
}

create_persistent_state
validate_persistent_state_before_mutation

validate_database_acl

render_license_config() {
    local key_id temp rendered
    key_id="$(cat "$SIGNING_KEY_ID")"
    [[ "$key_id" =~ ^license-signing-[0-9a-f]{32}$ ]] || die "refusing placeholder signing key ID"
    [ "$(grep -Ec '"signing_key_id"[[:space:]]*:[[:space:]]*"replace-with-generated-key-id"' "$LICENSE_CONFIG_SOURCE")" -eq 1 ] ||
        die "license config template must contain exactly one signing key placeholder"
    temp="$(mktemp "$ETC/config.json.render.XXXXXX")"
    sed "s/replace-with-generated-key-id/$key_id/" "$LICENSE_CONFIG_SOURCE" >"$temp"
    rendered="$temp"
    validate_rendered_license_config "$rendered" "$key_id"
    atomic_install_file "$rendered" "$ETC/config.json" root "$LICENSE_GROUP" 640
    rm -f -- "$rendered"
}

validate_rendered_license_config() {
    local path="$1" key_id="$2"
    ! grep -q 'replace-with-generated-key-id' "$path" || die "rendered config contains placeholder"
    [ "$(grep -Ec '"listen_address"[[:space:]]*:[[:space:]]*"127.0.0.1:18092"' "$path")" -eq 1 ] || die "license config listener is not production loopback"
    [ "$(grep -Fc "\"signing_key_id\": \"$key_id\"" "$path")" -eq 1 ] || die "rendered config key ID mismatch"
    [ "$(grep -Ec '"internal_secret_path"[[:space:]]*:[[:space:]]*"/etc/superpan-license/admin.secret"' "$path")" -eq 1 ] || die "rendered config admin secret path mismatch"
}

write_schema_config() {
    local source_db="$1" listen="$2" target="$3" key_id
    key_id="$(cat "$SIGNING_KEY_ID")"
    sed -e "s/replace-with-generated-key-id/$key_id/" \
        -e "s#\"license_database_path\"[[:space:]]*:[[:space:]]*\"/var/lib/superpan-license/state.db\"#\"license_database_path\": \"$source_db\"#" \
        -e "s/127\.0\.0\.1:18092/$listen/" "$LICENSE_CONFIG_SOURCE" >"$target"
    chmod 0640 "$target"; chown root:"$LICENSE_GROUP" "$target"
}

wait_schema_health() {
    local attempt
    for attempt in $(seq 1 30); do
        curl --noproxy '*' --fail --silent --show-error --max-time 2 http://127.0.0.1:28092/internal/health >/dev/null && return 0
        sleep 1
    done
    return 1
}

schema_process_identity() {
    local pid="$1" expected_binary="$2" process_hash expected_hash stat_line stat_tail start_time
    kill -0 "$pid" 2>/dev/null || return 1
    [ -r "/proc/$pid/stat" ] && [ -e "/proc/$pid/exe" ] || return 1
    process_hash="$(sha256sum -- "/proc/$pid/exe" | awk '{print $1}')" || return 1
    expected_hash="$(sha256sum -- "$expected_binary" | awk '{print $1}')" || return 1
    [ "$process_hash" = "$expected_hash" ] || return 1
    stat_line="$(cat "/proc/$pid/stat")" || return 1
    stat_tail="${stat_line##*) }"
    start_time="$(awk '{print $20}' <<<"$stat_tail")"
    [[ "$start_time" =~ ^[0-9]+$ ]] || return 1
    printf '%s:%s\n' "$start_time" "$process_hash"
}

register_schema_pid() {
    local pid="$1" expected_binary="$2" identity
    identity="$(schema_process_identity "$pid" "$expected_binary")" || die "schema probe process identity was not established"
    SCHEMA_TEST_IDENTITIES["$pid"]="$identity"
    SCHEMA_TEST_BINARIES["$pid"]="$expected_binary"
}

unregister_schema_pid() {
    local pid="$1"
    unset 'SCHEMA_TEST_IDENTITIES['"$pid"']' 'SCHEMA_TEST_BINARIES['"$pid"']'
}

stop_registered_schema_process() {
    local schema_pid="$1" expected_binary="$2" identity
    identity="$(schema_process_identity "$schema_pid" "$expected_binary")" || die "schema probe process disappeared or changed identity"
    [ "${SCHEMA_TEST_IDENTITIES[$schema_pid]:-}" = "$identity" ] || die "schema probe registry identity mismatch"
    kill "$schema_pid"
    wait "$schema_pid" || :
    unregister_schema_pid "$schema_pid"
}

kill_registered_schema_processes() {
    local pid identity expected_binary
    for pid in "${!SCHEMA_TEST_IDENTITIES[@]}"; do
        identity="${SCHEMA_TEST_IDENTITIES[$pid]}"
        expected_binary="${SCHEMA_TEST_BINARIES[$pid]:-}"
        [ -n "$expected_binary" ] || { unregister_schema_pid "$pid"; continue; }
        if kill -0 "$pid" 2>/dev/null && [ "${SCHEMA_TEST_IDENTITIES[$pid]:-}" = "$identity" ] &&
           [ "$(schema_process_identity "$pid" "$expected_binary" 2>/dev/null || :)" = "$identity" ]; then
            kill "$pid" 2>/dev/null
            wait "$pid" 2>/dev/null
        fi
        unregister_schema_pid "$pid"
    done
}

restore_old_license_activity() {
    if [ "$LICENSE_WAS_ACTIVE" = true ]; then systemctl start superpan-license.service; else systemctl stop superpan-license.service; fi
}

schema_upgrade_preflight() {
    if [ "$UPGRADE_MODE" != true ]; then
        printf 'initial-install: old binary and schema absent\n' >"$BACKUP_DIR/schema-preflight.txt"
        chmod 0600 "$BACKUP_DIR/schema-preflight.txt"
        refresh_manifest
        return
    fi
    local before="$BACKUP_DIR/schema-before.db" migrated="$BACKUP_DIR/schema-migrated.db"
    local after="$BACKUP_DIR/schema-after.db" oldcopy="$BACKUP_DIR/schema-old.db" verified="$BACKUP_DIR/schema-old-verified.db" schema_pid
    SCHEMA_NEW_CONFIG="$BACKUP_DIR/schema-new.json"
    SCHEMA_OLD_CONFIG="$BACKUP_DIR/schema-old.json"
    LICENSE_WAS_ACTIVE=false; systemctl is-active --quiet superpan-license.service && LICENSE_WAS_ACTIVE=true
    systemctl stop superpan-license.service
    BEFORE_SUMMARY="$("$LICENSE_DB_SNAPSHOT" --service superpan-license.service --source "$DATA/state.db" --destination "$before")"
    restore_old_license_activity
    cp -- "$before" "$migrated"; chown root:root "$migrated"; chmod 0600 "$migrated"
    write_schema_config "$migrated" 127.0.0.1:28092 "$SCHEMA_NEW_CONFIG"
    "$BUNDLE/superpan-license" serve --config "$SCHEMA_NEW_CONFIG" >"$BACKUP_DIR/schema-new.log" 2>&1 &
    schema_pid="$!"
    register_schema_pid "$schema_pid" "$BUNDLE/superpan-license"
    wait_schema_health || die "new license binary failed schema migration health"
    stop_registered_schema_process "$schema_pid" "$BUNDLE/superpan-license"
    chown root:root "$migrated" "$migrated-wal" "$migrated-shm" 2>/dev/null || :
    chmod 0600 "$migrated" "$migrated-wal" "$migrated-shm" 2>/dev/null || :
    systemctl stop superpan-license.service
    AFTER_SUMMARY="$("$LICENSE_DB_SNAPSHOT" --service superpan-license.service --source "$migrated" --destination "$after")"
    restore_old_license_activity
    cp -- "$after" "$oldcopy"; chown root:root "$oldcopy"; chmod 0600 "$oldcopy"
    write_schema_config "$oldcopy" 127.0.0.1:28092 "$SCHEMA_OLD_CONFIG"
    "$BACKUP_DIR/license-binary" serve --config "$SCHEMA_OLD_CONFIG" >"$BACKUP_DIR/schema-old.log" 2>&1 &
    schema_pid="$!"
    register_schema_pid "$schema_pid" "$BACKUP_DIR/license-binary"
    wait_schema_health || die "old license binary cannot read migrated schema"
    stop_registered_schema_process "$schema_pid" "$BACKUP_DIR/license-binary"
    chown root:root "$oldcopy" "$oldcopy-wal" "$oldcopy-shm" 2>/dev/null || :
    chmod 0600 "$oldcopy" "$oldcopy-wal" "$oldcopy-shm" 2>/dev/null || :
    systemctl stop superpan-license.service
    OLD_SUMMARY="$("$LICENSE_DB_SNAPSHOT" --service superpan-license.service --source "$oldcopy" --destination "$verified")"
    restore_old_license_activity
    AFTER_AGGREGATE="$(printf '%s\n' "$AFTER_SUMMARY" | tr ' ' '\n' | grep '^count_' | LC_ALL=C sort)"
    OLD_AGGREGATE="$(printf '%s\n' "$OLD_SUMMARY" | tr ' ' '\n' | grep '^count_' | LC_ALL=C sort)"
    [ "$AFTER_AGGREGATE" = "$OLD_AGGREGATE" ] || die "semantic aggregate mismatch after old-binary compatibility probe"
    {
        printf 'before %s\n' "$BEFORE_SUMMARY"
        printf 'after %s\n' "$AFTER_SUMMARY"
        printf 'old-read-only %s\n' "$OLD_SUMMARY"
    } >"$BACKUP_DIR/schema-preflight.txt"
    chmod 0600 "$BACKUP_DIR/schema-preflight.txt" "$BACKUP_DIR"/schema-*.json "$BACKUP_DIR"/schema-*.log
    refresh_manifest
}

schema_upgrade_preflight
"$BUNDLE/ota-admin" check-config --config "$OTA_CONFIG_SOURCE" >/dev/null

record_persistent_identity() {
    local record="$BACKUP_DIR/persistent-identity.txt"
    {
        printf 'post-preflight\n'
        printf 'key-id %s\n' "$(cat "$SIGNING_KEY_ID")"
        printf 'spki-sha256 %s\n' "$(cat "$SIGNING_SPKI_SHA256")"
        printf 'encryption-sha256 %s\n' "$(sha256sum -- "$ENCRYPTION_KEY" | awk '{print $1}')"
        printf 'lookup-sha256 %s\n' "$(sha256sum -- "$LOOKUP_KEY" | awk '{print $1}')"
        printf 'admin-sha256 %s\n' "$(sha256sum -- "$ADMIN_SECRET" | awk '{print $1}')"
    } >>"$record"
    if [ -f "$DATA/state.db" ]; then
        printf 'state.db sha256=%s size=%s\n' "$(sha256sum -- "$DATA/state.db" | awk '{print $1}')" "$(stat -c '%s' "$DATA/state.db")" >>"$record"
    else
        printf 'state.db absent\n' >>"$record"
    fi
    chmod 0600 "$record"
}
record_persistent_identity
refresh_manifest

wait_for_installed_license_process() {
    local attempt observed_pid observed_again process_hash installed_hash process_exe
    installed_hash="$(sha256sum -- "$DEST/superpan-license" | awk '{print $1}')"
    for attempt in $(seq 1 30); do
        if systemctl is-active --quiet superpan-license.service; then
            LICENSE_MAIN_PID="$(systemctl show --property=MainPID --value superpan-license.service 2>/dev/null || :)"
            if [[ "$LICENSE_MAIN_PID" =~ ^[1-9][0-9]*$ ]] && [ -e "/proc/$LICENSE_MAIN_PID/exe" ]; then
                process_exe="$(readlink -f -- "/proc/$LICENSE_MAIN_PID/exe" 2>/dev/null || :)"
                process_hash="$(sha256sum -- "/proc/$LICENSE_MAIN_PID/exe" 2>/dev/null | awk '{print $1}')"
                if [ -n "$process_exe" ] && [ "$process_hash" = "$installed_hash" ] &&
                   curl --noproxy '*' --fail --silent --show-error --max-time 2 http://127.0.0.1:18092/internal/health >/dev/null; then
                    observed_pid="$LICENSE_MAIN_PID"
                    observed_again="$(systemctl show --property=MainPID --value superpan-license.service 2>/dev/null || :)"
                    [ "$observed_again" = "$observed_pid" ] || { sleep 1; continue; }
                    [ "$process_hash" = "$LICENSE_BINARY_SHA256" ] || die "installed license process binary hash mismatch"
                    return 0
                fi
            fi
        fi
        sleep 1
    done
    die "new license service process did not become healthy with the installed binary"
}

install -m 0755 -o root -g root "$BUNDLE/superpan-license" "$DEST/superpan-license"
failpoint license-binary
render_license_config
failpoint license-config
"$DEST/superpan-license" check-config --config "$ETC/config.json" >/dev/null
install -m 0644 -o root -g root "$LICENSE_UNIT_SOURCE" /etc/systemd/system/superpan-license.service
failpoint license-unit
install -m 0644 -o root -g root "$BUNDLE/source-commit.txt" "$SOURCE_COMMIT_TARGET"
systemctl daemon-reload
systemctl enable superpan-license.service >/dev/null
systemctl restart superpan-license.service
failpoint license-start
wait_for_installed_license_process
failpoint license-health
bash "$OTA_INSTALLER" --loopback-license-transaction \
    --binary "$BUNDLE/ota-admin" --binary-sha256 "$OTA_BINARY_SHA256" \
    --config "$OTA_CONFIG_SOURCE" --config-sha256 "$OTA_CONFIG_SHA256" \
    --unit "$OTA_UNIT_SOURCE" --unit-sha256 "$OTA_UNIT_SHA256" \
    --source-commit "$SOURCE_COMMIT" --outer-transaction-root "$TRANSACTION_BACKUP"
systemctl is-active --quiet superpan-ota-admin.service || die "OTA admin service is not active after controlled install"
curl --noproxy '*' --fail --silent --show-error --max-time 5 http://127.0.0.1:18091/admin/ >/dev/null
INSTALLED_LICENSE_SHA="$(sha256sum -- "$DEST/superpan-license" | awk '{print $1}')"
INSTALLED_OTA_SHA="$(sha256sum -- /opt/superpan-ota-admin/ota-admin | awk '{print $1}')"
[ "$INSTALLED_LICENSE_SHA" = "$LICENSE_BINARY_SHA256" ] || die "installed license binary hash mismatch"
[ "$INSTALLED_OTA_SHA" = "$OTA_BINARY_SHA256" ] || die "installed OTA binary hash mismatch"
{
    printf 'source-commit %s\n' "$SOURCE_COMMIT"
    printf 'superpan-license %s\n' "$INSTALLED_LICENSE_SHA"
    printf 'ota-admin %s\n' "$INSTALLED_OTA_SHA"
} >"$BACKUP_DIR/installed-hashes.txt"
chmod 0600 "$BACKUP_DIR/installed-hashes.txt"
refresh_manifest
TRANSACTION_SUCCEEDED=true
TRANSACTION_ARMED=false
echo "transactional license and OTA installation complete; backup: $TRANSACTION_BACKUP"
