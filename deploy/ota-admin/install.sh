#!/usr/bin/env bash
set -euo pipefail
umask 077

SERVICE_NAME="superpan-ota-admin.service"
MOUNT_UNIT_NAME="var-lib-otaadmin-releases.mount"
SERVICE_USER="superpan-ota-admin"
SERVICE_GROUP="superpan-ota-admin"
SHARED_LICENSE_GROUP="superpan-license-admin"
LICENSE_ADMIN_SECRET="/etc/superpan-license/admin.secret"
BINARY_TARGET="/opt/superpan-ota-admin/ota-admin"
UNIT_TARGET="/etc/systemd/system/superpan-ota-admin.service"
MOUNT_UNIT_TARGET="/etc/systemd/system/${MOUNT_UNIT_NAME}"
CONFIG_DIR="/etc/superpan-ota-admin"
CONFIG_TARGET="${CONFIG_DIR}/config.json"
CERTIFICATE_TARGET="${CONFIG_DIR}/publisher.cer"
HMAC_TARGET="${CONFIG_DIR}/source-hmac.key"
NGINX_SNIPPET_TARGET="${CONFIG_DIR}/ota.fanfh.cn.admin.nginx.conf"
ACTIVE_VHOST="/www/server/panel/vhost/nginx/ota.fanfh.cn.conf"
DATA_ROOT="/var/lib/superpan-ota-admin"
STAGING_ROOT="${DATA_ROOT}/staging"
RELEASE_ROOT="/www/wwwroot/ota.fanfh.cn/releases/win-x64"
RELEASE_VIEW_ROOT="/var/lib/otaadmin/releases"
INITIAL_PASSWORD_FILE="/root/superpan-ota-admin.initial-password"
PASSWORD_TEMP="${INITIAL_PASSWORD_FILE}.new"
NGINX_BIN="${NGINX_BIN:-/www/server/nginx/sbin/nginx}"
NGINX_PID_FILE="${NGINX_PID_FILE:-/www/server/nginx/logs/nginx.pid}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY_SOURCE=""
BINARY_SHA256=""
CERTIFICATE_SOURCE=""
CERTIFICATE_SHA256=""
EXPECTED_SPKI_SHA256=""
COMPILED_PUBLISHER_SPKI_SHA256="f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff"
ACTIVE_VHOST_SHA256=""
BACKUP_ROOT="/var/backups/superpan-ota-admin"
LOOPBACK_LICENSE_TRANSACTION=false
LOOPBACK_CONFIG_SOURCE=""
LOOPBACK_CONFIG_SHA256=""
LOOPBACK_UNIT_SOURCE=""
LOOPBACK_UNIT_SHA256=""
SOURCE_COMMIT=""
PREFLIGHT_ONLY=false
OUTER_TRANSACTION_ROOT=""
declare -A SEEN_ARGS=()

usage() {
    cat >&2 <<'USAGE'
Usage: install.sh --binary PATH --binary-sha256 HEX \
  --certificate PATH --certificate-sha256 HEX --publisher-spki-sha256 HEX \
  --active-vhost-sha256 HEX

Loopback license transaction (preflight-only or controlled apply):
  install.sh --loopback-license-transaction --binary PATH --binary-sha256 HEX \
    --config PATH --config-sha256 HEX --unit PATH --unit-sha256 HEX \
    --source-commit 40_LOWERCASE_HEX [--preflight-only | \
    --outer-transaction-root /var/backups/superpan-license/TRANSACTION_ID]

The existing OTA HTTPS server block must already contain exactly this line:
  include /etc/superpan-ota-admin/ota.fanfh.cn.admin.nginx.conf;
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

# This makes pathname reopen safe against non-root rename: every component from
# the leaf to the root trust boundary is root-owned and not group/world writable.
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
            die "trusted path component must not be a symlink: $component_path"
        [ "$(stat -c '%u' "$component_path")" = "0" ] ||
            die "trusted path component must be owned by root: $component_path"
        component_mode="$(stat -c '%a' "$component_path")"
        (( (8#$component_mode & 8#022) == 0 )) ||
            die "trusted path component must not be group/world writable: $component_path"
    done
    canonical="$(readlink -f -- "$trusted_path")" || die "trusted path cannot be canonicalized: $trusted_path"
    [ "$canonical" = "$trusted_path" ] || die "trusted path must be canonical: $trusted_path"
}

verify_sha256() {
    local source="$1"
    local expected="$2"
    local actual
    actual="$(sha256sum -- "$source" | awk '{print $1}')"
    [ "$actual" = "$expected" ] || die "SHA-256 mismatch for $source"
}

preflight_loopback_license_transaction() {
    [ -z "$CERTIFICATE_SOURCE$CERTIFICATE_SHA256$EXPECTED_SPKI_SHA256$ACTIVE_VHOST_SHA256" ] ||
        die "public-install arguments are forbidden in loopback license transaction mode"
    [ -n "$BINARY_SOURCE" ] || die "--binary is required for loopback license transaction"
    [ -n "$LOOPBACK_CONFIG_SOURCE" ] || die "--config is required for loopback license transaction"
    [ -n "$LOOPBACK_UNIT_SOURCE" ] || die "--unit is required for loopback license transaction"
    [[ "$BINARY_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "binary SHA-256 must be 64 lowercase hexadecimal characters"
    [[ "$LOOPBACK_CONFIG_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "config SHA-256 must be 64 lowercase hexadecimal characters"
    [[ "$LOOPBACK_UNIT_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "unit SHA-256 must be 64 lowercase hexadecimal characters"
    [[ "$SOURCE_COMMIT" =~ ^[0-9a-f]{40}$ ]] || die "source commit must be exactly 40 lowercase hexadecimal characters"
    command -v sha256sum >/dev/null || die "sha256sum is required"
    validate_trusted_path "$BINARY_SOURCE"
    validate_trusted_path "$LOOPBACK_CONFIG_SOURCE"
    validate_trusted_path "$LOOPBACK_UNIT_SOURCE"
    for source in "$BINARY_SOURCE" "$LOOPBACK_CONFIG_SOURCE" "$LOOPBACK_UNIT_SOURCE"; do
        case "$source" in /*) ;; *) die "loopback transaction inputs must use absolute paths" ;; esac
        [ "$(readlink -f -- "$source")" = "$source" ] || die "loopback transaction inputs must use canonical paths"
        [ -f "$source" ] && [ ! -L "$source" ] || die "loopback transaction input must be a regular non-symlink file"
        [ "$(stat -c '%h' "$source")" = "1" ] || die "loopback transaction input must have exactly one link"
        [ "$(stat -c '%U:%G' "$source")" = "root:root" ] || die "loopback transaction inputs must be owned by root:root"
        mode="$(stat -c '%a' "$source")"
        (( (8#$mode & 8#022) == 0 )) || die "loopback transaction input must not be group/world writable"
    done
    verify_sha256 "$BINARY_SOURCE" "$BINARY_SHA256"
    verify_sha256 "$LOOPBACK_CONFIG_SOURCE" "$LOOPBACK_CONFIG_SHA256"
    verify_sha256 "$LOOPBACK_UNIT_SOURCE" "$LOOPBACK_UNIT_SHA256"
}

validate_outer_transaction_root() {
    case "$OUTER_TRANSACTION_ROOT" in
        /var/backups/superpan-license/[A-Za-z0-9][A-Za-z0-9._-]*) ;;
        *) die "--outer-transaction-root must name the license install transaction backup" ;;
    esac
    [ "$(dirname "$OUTER_TRANSACTION_ROOT")" = /var/backups/superpan-license ] || die "outer transaction backup must be an immediate child"
    validate_trusted_path "$OUTER_TRANSACTION_ROOT"
    [ "$(stat -c '%U:%G:%a' "$OUTER_TRANSACTION_ROOT")" = root:root:700 ] || die "outer transaction backup metadata is unsafe"
    [ -f "$OUTER_TRANSACTION_ROOT/manifest.sha256" ] || die "outer transaction manifest is missing"
}

controlled_failpoint() {
    local point="$1"
    [ "${LICENSE_INSTALL_FAILPOINT:-}" != "$point" ] || die "injected failure at $point"
}

apply_loopback_license_transaction() {
    # The outer license installer exclusively owns backup and rollback. This
    # path deliberately performs no Nginx, vhost, mount, credential, or backup work.
    validate_outer_transaction_root
    [ -f "$LICENSE_ADMIN_SECRET" ] && [ ! -L "$LICENSE_ADMIN_SECRET" ] || die "shared license admin secret is unavailable"
    [ "$(stat -c '%U:%G:%a' "$LICENSE_ADMIN_SECRET")" = root:superpan-license-admin:640 ] || die "shared license admin secret metadata is unsafe"
    install -d -m 0755 -o root -g root /opt/superpan-ota-admin
    install -d -m 0750 -o root -g "$SERVICE_GROUP" "$CONFIG_DIR"
    install -m 0755 -o root -g root "$BINARY_SOURCE" "$BINARY_TARGET"
    controlled_failpoint ota-binary
    install -m 0640 -o root -g "$SERVICE_GROUP" "$LOOPBACK_CONFIG_SOURCE" "$CONFIG_TARGET"
    controlled_failpoint ota-config
    install -m 0644 -o root -g root "$LOOPBACK_UNIT_SOURCE" "$UNIT_TARGET"
    controlled_failpoint ota-unit
    "$BINARY_TARGET" check-config --config "$CONFIG_TARGET" >/dev/null
    systemctl daemon-reload
    systemctl enable "$SERVICE_NAME" >/dev/null
    systemctl restart "$SERVICE_NAME"
    controlled_failpoint ota-restart
    local attempt
    for attempt in $(seq 1 30); do
        if systemctl is-active --quiet "$SERVICE_NAME" &&
           curl --noproxy '*' --fail --silent --show-error --max-time 2 http://127.0.0.1:18091/admin/ >/dev/null; then
            controlled_failpoint ota-health
            return 0
        fi
        sleep 1
    done
    die "controlled OTA service did not become ready"
}
# OTA_CONTROLLED_APPLY_COMPLETE

while [ "$#" -gt 0 ]; do
    case "$1" in
        --binary) mark_seen "$1"; require_value "$1" "${2:-}"; BINARY_SOURCE="$2"; shift 2 ;;
        --binary-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; BINARY_SHA256="$2"; shift 2 ;;
        --certificate) mark_seen "$1"; require_value "$1" "${2:-}"; CERTIFICATE_SOURCE="$2"; shift 2 ;;
        --certificate-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; CERTIFICATE_SHA256="$2"; shift 2 ;;
        --publisher-spki-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; EXPECTED_SPKI_SHA256="$2"; shift 2 ;;
        --active-vhost-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; ACTIVE_VHOST_SHA256="$2"; shift 2 ;;
        --loopback-license-transaction) mark_seen "$1"; LOOPBACK_LICENSE_TRANSACTION=true; shift ;;
        --preflight-only) mark_seen "$1"; PREFLIGHT_ONLY=true; shift ;;
        --config) mark_seen "$1"; require_value "$1" "${2:-}"; LOOPBACK_CONFIG_SOURCE="$2"; shift 2 ;;
        --config-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; LOOPBACK_CONFIG_SHA256="$2"; shift 2 ;;
        --unit) mark_seen "$1"; require_value "$1" "${2:-}"; LOOPBACK_UNIT_SOURCE="$2"; shift 2 ;;
        --unit-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; LOOPBACK_UNIT_SHA256="$2"; shift 2 ;;
        --source-commit) mark_seen "$1"; require_value "$1" "${2:-}"; SOURCE_COMMIT="$2"; shift 2 ;;
        --outer-transaction-root) mark_seen "$1"; require_value "$1" "${2:-}"; OUTER_TRANSACTION_ROOT="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage; die "unknown argument: $1" ;;
    esac
done

if [ "$LOOPBACK_LICENSE_TRANSACTION" = true ]; then
    for required in --loopback-license-transaction --binary --binary-sha256 \
        --config --config-sha256 --unit --unit-sha256 --source-commit; do
        require_seen "$required"
    done
    [ "$(id -u)" -eq 0 ] || die "run this installer as root"
    preflight_loopback_license_transaction
    if [ "$PREFLIGHT_ONLY" = true ]; then
        [ -z "$OUTER_TRANSACTION_ROOT" ] || die "--preflight-only and --outer-transaction-root are mutually exclusive"
        echo "loopback license transaction preflight complete for source commit $SOURCE_COMMIT"
        exit 0
    fi
    [ -n "$OUTER_TRANSACTION_ROOT" ] || die "controlled apply requires --outer-transaction-root"
    apply_loopback_license_transaction
    echo "controlled loopback OTA transaction complete for source commit $SOURCE_COMMIT"
    exit 0
fi
[ "$PREFLIGHT_ONLY" = false ] || die "--preflight-only requires --loopback-license-transaction"
[ -z "$LOOPBACK_CONFIG_SOURCE$LOOPBACK_CONFIG_SHA256$LOOPBACK_UNIT_SOURCE$LOOPBACK_UNIT_SHA256$SOURCE_COMMIT$OUTER_TRANSACTION_ROOT" ] ||
    die "loopback transaction arguments require --loopback-license-transaction"
for required in --binary --binary-sha256 --certificate --certificate-sha256 \
    --publisher-spki-sha256 --active-vhost-sha256; do
    require_seen "$required"
done
[ "$(id -u)" -eq 0 ] || die "run this installer as root"
[[ "$BINARY_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "binary SHA-256 must be 64 lowercase hexadecimal characters"
[[ "$CERTIFICATE_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "certificate SHA-256 must be 64 lowercase hexadecimal characters"
[[ "$EXPECTED_SPKI_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "publisher SPKI SHA-256 must be 64 lowercase hexadecimal characters"
[ "$EXPECTED_SPKI_SHA256" = "$COMPILED_PUBLISHER_SPKI_SHA256" ] || die "publisher SPKI does not match the compiled production trust pin"
[[ "$ACTIVE_VHOST_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "active vhost SHA-256 must be 64 lowercase hexadecimal characters"
[ -f "$BINARY_SOURCE" ] && [ ! -L "$BINARY_SOURCE" ] || die "binary source must be a regular non-symlink file"
[ -f "$CERTIFICATE_SOURCE" ] && [ ! -L "$CERTIFICATE_SOURCE" ] || die "certificate source must be a regular non-symlink file"
[ -f "$SCRIPT_DIR/superpan-ota-admin.service" ] || die "unit template is missing"
[ -f "$SCRIPT_DIR/var-lib-otaadmin-releases.mount" ] || die "mount unit template is missing"
[ -f "$SCRIPT_DIR/ota.fanfh.cn.admin.nginx.conf" ] || die "Nginx include template is missing"
[ -f "$SCRIPT_DIR/config.example.json" ] || die "configuration template is missing"
[ -f "$ACTIVE_VHOST" ] || die "the existing OTA virtual host was not found"
[ -x "$NGINX_BIN" ] || die "Nginx executable was not found: $NGINX_BIN"
command -v sha256sum >/dev/null || die "sha256sum is required"
command -v openssl >/dev/null || die "openssl is required"
command -v runuser >/dev/null || die "runuser is required"
command -v curl >/dev/null || die "curl is required"
command -v mountpoint >/dev/null || die "mountpoint is required"
command -v find >/dev/null || die "find is required"
command -v ps >/dev/null || die "ps is required"

read_nginx_master_pid() {
    [ -f "$NGINX_PID_FILE" ] && [ ! -L "$NGINX_PID_FILE" ] || return 1
    local pid command_name
    pid="$(tr -d '[:space:]' <"$NGINX_PID_FILE")"
    [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1
    kill -0 "$pid" 2>/dev/null || return 1
    command_name="$(ps -p "$pid" -o comm= 2>/dev/null | tr -d '[:space:]')"
    [ "$command_name" = "nginx" ] || return 1
    printf '%s\n' "$pid"
}

reload_nginx_and_verify() {
    local before_pid after_pid attempt
    before_pid="$(read_nginx_master_pid)" || die "Nginx master PID is unavailable before reload"
    "$NGINX_BIN" -s reload || die "Nginx executable reload failed"

    attempt=0
    while [ "$attempt" -lt 10 ]; do
        attempt=$((attempt + 1))
        if ! after_pid="$(read_nginx_master_pid 2>/dev/null)"; then
            after_pid=""
        fi
        if [ "$after_pid" = "$before_pid" ] &&
           curl --noproxy '*' --fail --silent --show-error --max-time 2 \
             --resolve ota.fanfh.cn:443:127.0.0.1 \
             https://ota.fanfh.cn/admin/ >/dev/null; then
            return 0
        fi
        sleep 1
    done
    die "Nginx reload did not preserve a healthy master and expose HTTPS /admin/"
}

# Authentication of both supplied artifacts is the mutation boundary. Nothing
# above this point creates users, directories, backups, keys, or service state.
verify_sha256 "$BINARY_SOURCE" "$BINARY_SHA256"
verify_sha256 "$CERTIFICATE_SOURCE" "$CERTIFICATE_SHA256"
verify_sha256 "$ACTIVE_VHOST" "$ACTIVE_VHOST_SHA256"
openssl x509 -inform DER -in "$CERTIFICATE_SOURCE" -noout >/dev/null 2>&1 || die "publisher certificate is not a valid DER X.509 certificate"
CERTIFICATE_SPKI_SHA256="$(openssl x509 -inform DER -in "$CERTIFICATE_SOURCE" -pubkey -noout | openssl pkey -pubin -outform DER 2>/dev/null | sha256sum | awk '{print $1}')"
[ "$CERTIFICATE_SPKI_SHA256" = "$EXPECTED_SPKI_SHA256" ] || die "publisher certificate SPKI SHA-256 mismatch"

INCLUDE_LINE="include ${NGINX_SNIPPET_TARGET};"
INCLUDE_COUNT="$(awk -v wanted="$INCLUDE_LINE" '{ line=$0; sub(/^[[:space:]]+/, "", line); sub(/[[:space:]]+$/, "", line); if (line == wanted) count++ } END { print count+0 }' "$ACTIVE_VHOST")"
[ "$INCLUDE_COUNT" -eq 1 ] ||
    die "existing OTA HTTPS server block must include ${NGINX_SNIPPET_TARGET} exactly once before installation"

assert_ota_exact_identity_before_mutation() {
    local actual expected
    if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then return; fi
    [ "$(id -gn "$SERVICE_USER")" = "$SERVICE_GROUP" ] || die "existing service user has the wrong primary group"
    actual="$(id -Gn "$SERVICE_USER" | tr ' ' '\n' | LC_ALL=C sort -u | paste -sd, -)"
    expected="$(printf '%s\n%s\n' "$SERVICE_GROUP" "$SHARED_LICENSE_GROUP" | LC_ALL=C sort -u | paste -sd, -)"
    [ "$actual" = "$expected" ] || die "existing OTA service user has ambient groups"
}
assert_ota_exact_identity_before_mutation

validate_release_directory() {
    local path="$1"
    local label="$2"
    [ -d "$path" ] && [ ! -L "$path" ] || die "Unexpected release root ${label}: not a real directory"
    local owner mode
    owner="$(stat -c '%U:%G' "$path")"
    mode="$(stat -c '%a' "$path")"
    case "$owner" in
        root:root|superpan-ota-admin:superpan-ota-admin) ;;
        *) die "Unexpected release root ${label} owner: $owner" ;;
    esac
    [ "$mode" = "755" ] || die "Unexpected release root ${label} mode: $mode"
}

validate_release_directory "$RELEASE_ROOT" "directory"
GENERATIONS_EXISTED=false
if [ -e "${RELEASE_ROOT}/generations" ] || [ -L "${RELEASE_ROOT}/generations" ]; then
    validate_release_directory "${RELEASE_ROOT}/generations" "generations directory"
    GENERATIONS_EXISTED=true
fi

TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
# OTA_INSTALL_MUTATION_BOUNDARY: identity and authenticated inputs are valid.
validate_backup_root() {
    if [ -e "$BACKUP_ROOT" ] || [ -L "$BACKUP_ROOT" ]; then
        [ -d "$BACKUP_ROOT" ] && [ ! -L "$BACKUP_ROOT" ] || die "fixed backup root is not a real directory"
        [ "$(stat -c '%U:%G' "$BACKUP_ROOT")" = "root:root" ] || die "fixed backup root owner must be root:root"
        [ "$(stat -c '%a' "$BACKUP_ROOT")" = "700" ] || die "fixed backup root mode must be 0700"
    else
        install -d -m 0700 -o root -g root "$BACKUP_ROOT"
    fi
}
validate_backup_root
BACKUP_DIR="$(mktemp -d "${BACKUP_ROOT}/${TIMESTAMP}.XXXXXX")"
chown root:root "$BACKUP_DIR"
chmod 0700 "$BACKUP_DIR"

INSTALL_SUCCEEDED=false
SERVICE_WAS_ACTIVE=false
SERVICE_STOP_REQUESTED=false
report_install_result() {
    local status=$?
    trap - EXIT
    if [ "$INSTALL_SUCCEEDED" != true ]; then
        if [ "$SERVICE_WAS_ACTIVE" = true ] && [ "$SERVICE_STOP_REQUESTED" = true ] &&
           ! systemctl is-active --quiet "$SERVICE_NAME"; then
            if systemctl start "$SERVICE_NAME"; then
                printf '%s\n' "Recovered previously active ${SERVICE_NAME} after installation failure." >&2
            else
                printf '%s\n' "WARNING: failed to recover previously active ${SERVICE_NAME}." >&2
            fi
        fi
        cat >&2 <<EOF
Installation did not complete. Backup: ${BACKUP_DIR}
Rollback (run as root): restore only the named targets and prior unit states recorded in ${BACKUP_DIR}, then validate Nginx.
EOF
    fi
    exit "$status"
}
trap report_install_result EXIT

backup_target() {
    local target="$1"
    local name="$2"
    if [ -e "$target" ] || [ -L "$target" ]; then
        cp -a -- "$target" "${BACKUP_DIR}/${name}"
        printf '%s\n' "$target" >>"${BACKUP_DIR}/present-targets.txt"
    else
        printf '%s\n' "$target" >>"${BACKUP_DIR}/absent-targets.txt"
    fi
}

# The database is deliberately never rolled back. Credentials that authorize or
# authenticate its retained state therefore share its lifecycle: existing values
# are backed up, while newly created values are explicitly kept on rollback.
backup_persistent_target() {
    local target="$1"
    local name="$2"
    if [ -e "$target" ] || [ -L "$target" ]; then
        cp -a -- "$target" "${BACKUP_DIR}/${name}"
        printf '%s\n' "$target" >>"${BACKUP_DIR}/persistent-existing-targets.txt"
    else
        printf '%s\n' "$target" >>"${BACKUP_DIR}/persistent-new-targets.txt"
    fi
}

# Only individually enumerated OTA administration targets are backed up.
backup_target "$BINARY_TARGET" "ota-admin"
backup_target "$UNIT_TARGET" "superpan-ota-admin.service"
backup_target "$MOUNT_UNIT_TARGET" "var-lib-otaadmin-releases.mount"
backup_target "$NGINX_SNIPPET_TARGET" "ota.fanfh.cn.admin.nginx.conf"
backup_target "$CONFIG_TARGET" "config.json"
backup_target "$CERTIFICATE_TARGET" "publisher.cer"
backup_target "$ACTIVE_VHOST" "ota.fanfh.cn.conf"
backup_persistent_target "$HMAC_TARGET" "source-hmac.key"
backup_persistent_target "$INITIAL_PASSWORD_FILE" "superpan-ota-admin.initial-password"
# The .new path is transaction recovery media, not rollback state. Restoring an
# older temporary password while retaining a newer DB administrator would lock
# out the operator, so only record its pre-install presence for the audit trail.
if [ -e "$PASSWORD_TEMP" ] || [ -L "$PASSWORD_TEMP" ]; then
    printf 'present %s\n' "$PASSWORD_TEMP" >"${BACKUP_DIR}/transient-targets.txt"
else
    printf 'absent %s\n' "$PASSWORD_TEMP" >"${BACKUP_DIR}/transient-targets.txt"
fi
sha256sum -- "$ACTIVE_VHOST" >"${BACKUP_DIR}/ota.fanfh.cn.conf.sha256"
stat -c '%u %g %a %n' "$RELEASE_ROOT" >"${BACKUP_DIR}/release-directory-metadata.txt"
if [ "$GENERATIONS_EXISTED" = true ]; then
    stat -c '%u %g %a %n' "${RELEASE_ROOT}/generations" >>"${BACKUP_DIR}/release-directory-metadata.txt"
else
    printf 'absent absent absent %s\n' "${RELEASE_ROOT}/generations" >>"${BACKUP_DIR}/release-directory-metadata.txt"
fi

record_unit_enabled_state() {
    local unit="$1"
    local state
    if ! state="$(systemctl is-enabled "$unit" 2>/dev/null)"; then
        [ -n "$state" ] || state="not-found"
    fi
    printf '%s %s\n' "$unit" "$state" >>"${BACKUP_DIR}/unit-enabled-state.txt"
}
record_unit_enabled_state "$SERVICE_NAME"
record_unit_enabled_state "$MOUNT_UNIT_NAME"
record_unit_active_state() {
    local unit="$1"
    local state="inactive"
    if systemctl is-active --quiet "$unit"; then
        state="active"
    fi
    printf '%s %s\n' "$unit" "$state" >>"${BACKUP_DIR}/unit-active-state.txt"
}
record_unit_active_state "$SERVICE_NAME"
record_unit_active_state "$MOUNT_UNIT_NAME"
if systemctl is-active --quiet "$SERVICE_NAME"; then
    SERVICE_WAS_ACTIVE=true
fi

if ! getent group superpan-ota-admin >/dev/null; then
    groupadd --system "$SERVICE_GROUP"
fi
if ! getent group "$SHARED_LICENSE_GROUP" >/dev/null; then
    groupadd --system "$SHARED_LICENSE_GROUP"
fi
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --gid "$SERVICE_GROUP" --groups "$SHARED_LICENSE_GROUP" --home-dir "$DATA_ROOT" --shell /sbin/nologin "$SERVICE_USER"
fi
[ "$(id -gn "$SERVICE_USER")" = "$SERVICE_GROUP" ] || die "existing service user has the wrong primary group"
usermod --gid "$SERVICE_GROUP" --groups "$SHARED_LICENSE_GROUP" "$SERVICE_USER"

install -d -m 0755 -o root -g root /opt/superpan-ota-admin
install -d -m 0750 -o root -g "$SERVICE_GROUP" "$CONFIG_DIR"
install -d -m 0700 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$DATA_ROOT"
install -d -m 0700 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$STAGING_ROOT"
[ -d "$RELEASE_ROOT" ] || die "existing OTA release root is missing: $RELEASE_ROOT"
install -d -m 0755 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "${RELEASE_ROOT}/generations"
chown "$SERVICE_USER":"$SERVICE_GROUP" "$RELEASE_ROOT" "${RELEASE_ROOT}/generations"
chmod 0755 "$RELEASE_ROOT" "${RELEASE_ROOT}/generations"

if [ -e /var/lib/otaadmin ] || [ -L /var/lib/otaadmin ]; then
    [ -d /var/lib/otaadmin ] && [ ! -L /var/lib/otaadmin ] &&
       [ "$(stat -c '%U:%G' /var/lib/otaadmin)" = "root:root" ] && [ "$(stat -c '%a' /var/lib/otaadmin)" = "755" ] ||
        die "release bind parent metadata is unsafe"
else
    install -d -m 0755 -o root -g root /var/lib/otaadmin
fi
if [ -e "$RELEASE_VIEW_ROOT" ] || [ -L "$RELEASE_VIEW_ROOT" ]; then
    [ -d "$RELEASE_VIEW_ROOT" ] && [ ! -L "$RELEASE_VIEW_ROOT" ] || die "release bind target is unsafe"
    if mountpoint -q "$RELEASE_VIEW_ROOT"; then
        [ "$(stat -Lc '%d:%i' "$RELEASE_VIEW_ROOT")" = "$(stat -Lc '%d:%i' "$RELEASE_ROOT")" ] ||
            die "existing release bind target points elsewhere"
    else
        [ "$(stat -c '%U:%G' "$RELEASE_VIEW_ROOT")" = "root:root" ] && [ "$(stat -c '%a' "$RELEASE_VIEW_ROOT")" = "755" ] ||
            die "release bind target metadata is unsafe"
        [ -z "$(find "$RELEASE_VIEW_ROOT" -mindepth 1 -maxdepth 1 -print -quit)" ] || die "release bind target is not empty"
    fi
else
    install -d -m 0755 -o root -g root "$RELEASE_VIEW_ROOT"
fi

install -m 0755 -o root -g root "$BINARY_SOURCE" "$BINARY_TARGET"
install -m 0644 -o root -g root "$SCRIPT_DIR/superpan-ota-admin.service" "$UNIT_TARGET"
install -m 0644 -o root -g root "$SCRIPT_DIR/var-lib-otaadmin-releases.mount" "$MOUNT_UNIT_TARGET"
install -m 0644 -o root -g root "$SCRIPT_DIR/ota.fanfh.cn.admin.nginx.conf" "$NGINX_SNIPPET_TARGET"
install -m 0640 -o root -g "$SERVICE_GROUP" "$CERTIFICATE_SOURCE" "$CERTIFICATE_TARGET"

if [ ! -s "$HMAC_TARGET" ]; then
    HMAC_TEMP="${CONFIG_DIR}/.source-hmac.key.$$"
    openssl rand -hex 32 >"$HMAC_TEMP"
    install -m 0640 -o root -g "$SERVICE_GROUP" "$HMAC_TEMP" "$HMAC_TARGET"
    unlink "$HMAC_TEMP"
fi
chown root:"$SERVICE_GROUP" "$HMAC_TARGET"
chmod 0640 "$HMAC_TARGET"

install -m 0640 -o root -g "$SERVICE_GROUP" "$SCRIPT_DIR/config.example.json" "$CONFIG_TARGET"
chown root:superpan-ota-admin "$CONFIG_TARGET"
chmod 0640 "$CONFIG_TARGET"

# Initialize according to the administrator row, not the database file. If an
# earlier run committed the row but stopped before installing the password file,
# recover the root-only temporary output before invoking the idempotent command.
if [ -e "$PASSWORD_TEMP" ] || [ -L "$PASSWORD_TEMP" ]; then
    [ -f "$PASSWORD_TEMP" ] && [ ! -L "$PASSWORD_TEMP" ] &&
       [ "$(stat -c '%U:%G' "$PASSWORD_TEMP")" = "root:root" ] && [ "$(stat -c '%a' "$PASSWORD_TEMP")" = "600" ] ||
        die "unsafe interrupted initial-password file: ${PASSWORD_TEMP}"
    if [ -s "$PASSWORD_TEMP" ]; then
        mv -f -- "$PASSWORD_TEMP" "$INITIAL_PASSWORD_FILE"
    else
        unlink "$PASSWORD_TEMP"
    fi
fi
if [ "$SERVICE_WAS_ACTIVE" = true ]; then
    SERVICE_STOP_REQUESTED=true
    systemctl stop "$SERVICE_NAME"
    systemctl is-active --quiet "$SERVICE_NAME" && die "failed to stop ${SERVICE_NAME} before database initialization"
fi
runuser -u "$SERVICE_USER" -- "$BINARY_TARGET" init-admin --if-missing --config "$CONFIG_TARGET" >"$PASSWORD_TEMP"
if [ -s "$PASSWORD_TEMP" ]; then
    chmod 0600 "$PASSWORD_TEMP"
    mv -f -- "$PASSWORD_TEMP" "$INITIAL_PASSWORD_FILE"
else
    unlink "$PASSWORD_TEMP"
fi

# Safety gate: nginx -t must succeed before any reload.
if ! "$NGINX_BIN" -t; then
    die "nginx -t failed; Nginx was not reloaded. Use the Rollback commands below."
fi

systemctl daemon-reload
systemctl enable "$MOUNT_UNIT_NAME" >/dev/null
systemctl restart "$MOUNT_UNIT_NAME"
mountpoint -q "$RELEASE_VIEW_ROOT" || die "OTA release bind mount did not become active"
[ "$(stat -Lc '%d:%i' "$RELEASE_VIEW_ROOT")" = "$(stat -Lc '%d:%i' "$RELEASE_ROOT")" ] ||
    die "OTA release bind mount does not reference the audited release root"
systemctl enable "$SERVICE_NAME" >/dev/null
systemctl restart "$SERVICE_NAME"

SERVICE_READY=false
READINESS_ATTEMPT=0
while [ "$READINESS_ATTEMPT" -lt 30 ]; do
    READINESS_ATTEMPT=$((READINESS_ATTEMPT + 1))
    if systemctl is-active --quiet "$SERVICE_NAME" &&
       curl --noproxy '*' --fail --silent --show-error --max-time 2 http://127.0.0.1:18091/admin/ >/dev/null; then
        SERVICE_READY=true
        break
    fi
    sleep 1
done
[ "$SERVICE_READY" = true ] || die "OTA admin service did not become ready; Nginx was not reloaded"
reload_nginx_and_verify

cat <<EOF
Installed ${SERVICE_NAME}.
Initial password (first installation only): ${INITIAL_PASSWORD_FILE}
Backup: ${BACKUP_DIR}

Rollback (run as root):
  systemctl stop ${SERVICE_NAME}
  systemctl stop ${MOUNT_UNIT_NAME}
  restore only the named files listed in ${BACKUP_DIR}/present-targets.txt
  remove only targets listed in ${BACKUP_DIR}/absent-targets.txt
  keep post-install credentials listed in ${BACKUP_DIR}/persistent-existing-targets.txt and persistent-new-targets.txt because the database is retained
  restore uid/gid/mode from ${BACKUP_DIR}/release-directory-metadata.txt (never remove a non-empty generations directory)
  restore the service and mount-unit enablement states recorded in ${BACKUP_DIR}/unit-enabled-state.txt
  restore each unit's active/inactive state recorded in ${BACKUP_DIR}/unit-active-state.txt
  ${NGINX_BIN} -t && ${NGINX_BIN} -s reload
  systemctl daemon-reload
Do not roll back or delete the published release tree.
EOF
INSTALL_SUCCEEDED=true
