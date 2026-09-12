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
SOURCE_COMMIT_TARGET="/opt/superpan-license/source-commit.txt"
OTA_INSTALLER="$(cd "$(dirname "${BASH_SOURCE[0]}")/../ota-admin" && pwd)/install.sh"
LICENSE_USER="superpan-license"
LICENSE_GROUP="superpan-license"
OTA_USER="superpan-ota-admin"
OTA_GROUP="superpan-ota-admin"
SHARED_GROUP="superpan-license-admin"

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
BACKUP_DIR=/var/backups/superpan-license
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
        database) ;;
        *) die "unknown validation format" ;;
    esac
}

validate_signing_identity() {
    local actual_hash expected_id
    openssl ec -in "$SIGNING_KEY" -noout -text 2>/dev/null | grep -Eq 'ASN1 OID: prime256v1|NIST CURVE: P-256' ||
        die "signing key must use P-256"
    openssl ec -in "$SIGNING_KEY" -pubout -outform DER 2>/dev/null | cmp -s - "$SIGNING_SPKI_DER" ||
        die "signing private/public key mismatch"
    base64 -w 0 "$SIGNING_SPKI_DER" | cmp -s - <(tr -d '\n' <"$SIGNING_SPKI_BASE64") ||
        die "SPKI base64 does not match DER"
    actual_hash="$(sha256sum -- "$SIGNING_SPKI_DER" | awk '{print $1}')"
    [ "$(cat "$SIGNING_SPKI_SHA256")" = "$actual_hash" ] || die "SPKI hash does not match DER"
    expected_id="license-signing-${actual_hash:0:32}"
    [ "$(cat "$SIGNING_KEY_ID")" = "$expected_id" ] || die "signing key ID does not match SPKI identity"
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
    if [ -e "$BACKUP_DIR" ] || [ -L "$BACKUP_DIR" ]; then
        validate_directory "$BACKUP_DIR" root root 700
        validate_trusted_path "$BACKUP_DIR"
    fi
    validate_database_acl
}

validate_persistent_state_before_mutation() {
    local present=0 path
    for path in "${PERSISTENT_PATHS[@]}"; do
        if [ -e "$path" ] || [ -L "$path" ]; then present=$((present + 1)); fi
    done
    if [ "$present" -eq 0 ]; then
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
    validate_signing_identity
    PERSISTENT_STATE_MODE="existing"
}

assert_exact_identity_before_mutation
validate_existing_acl_before_mutation
validate_persistent_state_before_mutation

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

install -d -m 0755 -o root -g root "$DEST"
install -d -m 0710 -o root -g "$SHARED_GROUP" "$ETC"
install -d -m 0700 -o "$LICENSE_USER" -g "$LICENSE_GROUP" "$DATA"
install -d -m 0700 -o root -g root "$BACKUP_DIR"

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

create_generated_file() {
    local target="$1" owner="$2" group="$3" mode="$4" generator="$5" temp
    temp="$(mktemp "${target}.tmp.XXXXXX")" || die "cannot create secret temporary file"
    [ "$(dirname "$temp")" = "$ETC" ] && [ ! -L "$temp" ] && [ "$(stat -c '%h' "$temp")" = 1 ] || die "unsafe secret temporary file"
    case "$generator" in
        raw32) head -c 32 /dev/urandom >"$temp" ;;
        p256) openssl ecparam -name prime256v1 -genkey -noout >"$temp" ;;
        *) die "unknown secret generator" ;;
    esac
    chown "$owner:$group" "$temp"
    chmod "$mode" "$temp"
    sync "$temp" 2>/dev/null || sync
    mv -T -- "$temp" "$target"
}

create_persistent_state() {
    local hash id temp
    [ "$PERSISTENT_STATE_MODE" = "absent" ] || return
    create_generated_file "$ENCRYPTION_KEY" root "$LICENSE_GROUP" 640 raw32
    create_generated_file "$LOOKUP_KEY" root "$LICENSE_GROUP" 640 raw32
    ! cmp -s "$ENCRYPTION_KEY" "$LOOKUP_KEY" || die "independent random keys unexpectedly matched"
    create_generated_file "$ADMIN_SECRET" root "$SHARED_GROUP" 640 raw32
    create_generated_file "$SIGNING_KEY" root "$LICENSE_GROUP" 640 p256
    temp="$(mktemp "$ETC/response-signing.spki.der.tmp.XXXXXX")"
    openssl ec -in "$SIGNING_KEY" -pubout -outform DER >"$temp" 2>/dev/null
    atomic_install_file "$temp" "$SIGNING_SPKI_DER" root root 644
    rm -f -- "$temp"
    temp="$(mktemp "$ETC/response-signing.spki.base64.tmp.XXXXXX")"
    base64 -w 0 "$SIGNING_SPKI_DER" >"$temp"
    printf '\n' >>"$temp"
    atomic_install_file "$temp" "$SIGNING_SPKI_BASE64" root root 644
    rm -f -- "$temp"
    hash="$(sha256sum -- "$SIGNING_SPKI_DER" | awk '{print $1}')"
    temp="$(mktemp "$ETC/response-signing.spki.sha256.tmp.XXXXXX")"
    printf '%s\n' "$hash" >"$temp"
    atomic_install_file "$temp" "$SIGNING_SPKI_SHA256" root root 644
    rm -f -- "$temp"
    id="license-signing-${hash:0:32}"
    temp="$(mktemp "$ETC/response-signing.key-id.tmp.XXXXXX")"
    printf '%s\n' "$id" >"$temp"
    atomic_install_file "$temp" "$SIGNING_KEY_ID" root root 644
    rm -f -- "$temp"
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

render_license_config

record_persistent_identity() {
    local record="$BACKUP_DIR/key-set-identity.txt" db_record="$BACKUP_DIR/database-snapshot-identity.txt"
    {
        printf 'key-id %s\n' "$(cat "$SIGNING_KEY_ID")"
        printf 'spki-sha256 %s\n' "$(cat "$SIGNING_SPKI_SHA256")"
        printf 'encryption-sha256 %s\n' "$(sha256sum -- "$ENCRYPTION_KEY" | awk '{print $1}')"
        printf 'lookup-sha256 %s\n' "$(sha256sum -- "$LOOKUP_KEY" | awk '{print $1}')"
        printf 'admin-sha256 %s\n' "$(sha256sum -- "$ADMIN_SECRET" | awk '{print $1}')"
    } >"$record"
    if [ -f "$DATA/state.db" ]; then
        printf 'state.db sha256=%s size=%s\n' "$(sha256sum -- "$DATA/state.db" | awk '{print $1}')" "$(stat -c '%s' "$DATA/state.db")" >"$db_record"
    else
        printf 'state.db absent\n' >"$db_record"
    fi
    chmod 0600 "$record" "$db_record"
}
record_persistent_identity

install -m 0755 -o root -g root "$BUNDLE/superpan-license" "$DEST/superpan-license"
install -m 0644 -o root -g root "$LICENSE_UNIT_SOURCE" /etc/systemd/system/superpan-license.service
install -m 0644 -o root -g root "$BUNDLE/source-commit.txt" "$SOURCE_COMMIT_TARGET"
systemctl daemon-reload
systemctl enable --now superpan-license.service
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:18092/internal/health >/dev/null
echo "license service installed on 127.0.0.1:18092"
