#!/usr/bin/env bash
set -euo pipefail
umask 077

FIXED_BACKUP_ROOT="/var/backups/superpan-license"
EXPECTED_MANIFEST_NAMES=$'config.json\nsuperpan-license'

die() {
    echo "ERROR: $*" >&2
    exit 1
}

validate_trusted_path() {
    local path="$1" component current="/" mode canonical
    local -a components=()
    case "$path" in /*) ;; *) die "trusted path must be absolute" ;; esac
    IFS='/' read -r -a components <<<"${path#/}"
    for component in "${components[@]}"; do
        [ -n "$component" ] && [ "$component" != "." ] && [ "$component" != ".." ] || die "non-canonical trusted path"
        current="${current%/}/$component"
        [ -e "$current" ] && [ ! -L "$current" ] || die "trusted path component is missing or symlinked"
        [ "$(stat -c '%U' "$current")" = root ] || die "trusted path component is not root owned"
        mode="$(stat -c '%a' "$current")"
        (( (8#$mode & 8#022) == 0 )) || die "trusted path component is writable by group/world"
    done
    canonical="$(readlink -f -- "$path")"
    [ "$canonical" = "$path" ] || die "trusted path is not canonical"
}

[ "$(id -u)" -eq 0 ] || die "rollback.sh must run as root"
: "${BACKUP_ROOT:?BACKUP_ROOT is required}"
case "$BACKUP_ROOT" in
    "$FIXED_BACKUP_ROOT"/[A-Za-z0-9][A-Za-z0-9._-]*) ;;
    *) die "BACKUP_ROOT must be /var/backups/superpan-license/<id>" ;;
esac
[ "$(dirname "$BACKUP_ROOT")" = "$FIXED_BACKUP_ROOT" ] || die "backup must be an immediate child of fixed root"
validate_trusted_path "$FIXED_BACKUP_ROOT"
validate_trusted_path "$BACKUP_ROOT"
[ -d "$FIXED_BACKUP_ROOT" ] && [ ! -L "$FIXED_BACKUP_ROOT" ] &&
    [ "$(stat -c '%U:%G:%a' "$FIXED_BACKUP_ROOT")" = root:root:700 ] || die "fixed backup root metadata is unsafe"
[ -d "$BACKUP_ROOT" ] && [ ! -L "$BACKUP_ROOT" ] &&
    [ "$(stat -c '%U:%G:%a' "$BACKUP_ROOT")" = root:root:700 ] || die "backup directory metadata is unsafe"

MANIFEST="$BACKUP_ROOT/manifest.sha256"
[ -f "$MANIFEST" ] && [ ! -L "$MANIFEST" ] && [ "$(stat -c '%h:%U:%G:%a' "$MANIFEST")" = 1:root:root:600 ] ||
    die "backup manifest metadata is unsafe"
ACTUAL_MANIFEST_NAMES="$(awk 'NF == 2 && $1 ~ /^[0-9a-f]{64}$/ && $2 !~ /^\*/ { print $2; next } { exit 97 }' "$MANIFEST" | LC_ALL=C sort)" ||
    die "backup manifest is malformed"
[ "$ACTUAL_MANIFEST_NAMES" = "$EXPECTED_MANIFEST_NAMES" ] || die "backup manifest allowlist mismatch"
(
    cd "$BACKUP_ROOT"
    sha256sum -c manifest.sha256 >/dev/null
) || die "backup payload hash mismatch"
for name in config.json superpan-license; do
    path="$BACKUP_ROOT/$name"
    [ -f "$path" ] && [ ! -L "$path" ] && [ "$(stat -c '%h:%U:%G' "$path")" = 1:root:root ] || die "unsafe backup payload: $name"
done

if systemctl is-active --quiet superpan-license.service; then systemctl stop superpan-license.service; fi
install -m 0755 -o root -g root "$BACKUP_ROOT/superpan-license" /opt/superpan-license/superpan-license
install -m 0640 -o root -g superpan-license "$BACKUP_ROOT/config.json" /etc/superpan-license/config.json
# Persistent database and key material are deliberately preserved as one state.
# Rollback never writes /var/lib/superpan-license or any persistent file under
# /etc/superpan-license other than the non-secret rendered config.json above;
# specifically it never restores /etc/superpan-license/encryption.key.
systemctl daemon-reload
systemctl start superpan-license.service
echo "license binary/config rollback complete; persistent database and keys preserved"
