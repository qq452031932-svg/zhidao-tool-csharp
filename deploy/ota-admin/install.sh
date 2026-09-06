#!/usr/bin/env bash
set -euo pipefail
umask 077

SERVICE_NAME="superpan-ota-admin.service"
MOUNT_UNIT_NAME="var-lib-otaadmin-releases.mount"
SERVICE_USER="superpan-ota-admin"
SERVICE_GROUP="superpan-ota-admin"
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY_SOURCE=""
BINARY_SHA256=""
CERTIFICATE_SOURCE=""
CERTIFICATE_SHA256=""
EXPECTED_SPKI_SHA256=""
COMPILED_PUBLISHER_SPKI_SHA256="f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff"
ACTIVE_VHOST_SHA256=""
BACKUP_ROOT="/var/backups/superpan-ota-admin"

usage() {
    cat >&2 <<'USAGE'
Usage: install.sh --binary PATH --binary-sha256 HEX \
  --certificate PATH --certificate-sha256 HEX --publisher-spki-sha256 HEX \
  --active-vhost-sha256 HEX

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

while [ "$#" -gt 0 ]; do
    case "$1" in
        --binary) require_value "$1" "${2:-}"; BINARY_SOURCE="$2"; shift 2 ;;
        --binary-sha256) require_value "$1" "${2:-}"; BINARY_SHA256="$2"; shift 2 ;;
        --certificate) require_value "$1" "${2:-}"; CERTIFICATE_SOURCE="$2"; shift 2 ;;
        --certificate-sha256) require_value "$1" "${2:-}"; CERTIFICATE_SHA256="$2"; shift 2 ;;
        --publisher-spki-sha256) require_value "$1" "${2:-}"; EXPECTED_SPKI_SHA256="$2"; shift 2 ;;
        --active-vhost-sha256) require_value "$1" "${2:-}"; ACTIVE_VHOST_SHA256="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage; die "unknown argument: $1" ;;
    esac
done

[ "$(id -u)" -eq 0 ] || die "run this installer as root"
[ -n "$BINARY_SOURCE" ] || { usage; die "--binary is required"; }
[ -n "$CERTIFICATE_SOURCE" ] || { usage; die "--certificate is required"; }
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

verify_sha256() {
    local source="$1"
    local expected="$2"
    local actual
    actual="$(sha256sum -- "$source" | awk '{print $1}')"
    [ "$actual" = "$expected" ] || die "SHA-256 mismatch for $source"
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
report_install_result() {
    local status=$?
    trap - EXIT
    if [ "$INSTALL_SUCCEEDED" != true ]; then
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
    state="$(systemctl is-enabled "$unit" 2>/dev/null || true)"
    [ -n "$state" ] || state="not-found"
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

if ! getent group superpan-ota-admin >/dev/null; then
    groupadd --system "$SERVICE_GROUP"
fi
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --gid "$SERVICE_GROUP" --home-dir "$DATA_ROOT" --shell /sbin/nologin "$SERVICE_USER"
fi
[ "$(id -gn "$SERVICE_USER")" = "$SERVICE_GROUP" ] || die "existing service user has the wrong primary group"

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
systemctl reload nginx

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
  ${NGINX_BIN} -t && systemctl reload nginx
  systemctl daemon-reload
Do not roll back or delete the published release tree.
EOF
INSTALL_SUCCEEDED=true
