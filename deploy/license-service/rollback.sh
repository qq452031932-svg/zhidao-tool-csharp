#!/usr/bin/env bash
set -euo pipefail
umask 077

FIXED_BACKUP_ROOT="/var/backups/superpan-license"
BACKUP_ROOT="${1:-${BACKUP_ROOT:-}}"
ROLLBACK_SUCCEEDED=false
EXPECTED_TARGETS=$'/opt/superpan-license/superpan-license\tlicense-binary\n/etc/superpan-license/config.json\tlicense-config\n/etc/systemd/system/superpan-license.service\tlicense-unit\n/opt/superpan-ota-admin/ota-admin\tota-binary\n/etc/superpan-ota-admin/config.json\tota-config\n/etc/systemd/system/superpan-ota-admin.service\tota-unit\n/opt/superpan-license/source-commit.txt\tlicense-source-commit'

die() { echo "ERROR: $*" >&2; exit 1; }
rollback_exit() {
    local status=$?
    trap - EXIT INT TERM HUP
    if [ "$ROLLBACK_SUCCEEDED" != true ]; then
        systemctl stop superpan-ota-admin.service superpan-license.service 2>/dev/null || :
        echo "ERROR: rollback failed; backup retained; services remain stopped for manual repair: $BACKUP_ROOT" >&2
    fi
    exit "$status"
}
trap rollback_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

validate_trusted_path() {
    local path="$1" component current="/" mode canonical
    local -a components=()
    case "$path" in /*) ;; *) die "trusted path must be absolute" ;; esac
    IFS='/' read -r -a components <<<"${path#/}"
    for component in "${components[@]}"; do
        [ -n "$component" ] && [ "$component" != . ] && [ "$component" != .. ] || die "non-canonical trusted path"
        current="${current%/}/$component"
        [ -e "$current" ] && [ ! -L "$current" ] || die "trusted path component is missing or symlinked"
        [ "$(stat -c '%U' "$current")" = root ] || die "trusted path component is not root owned"
        mode="$(stat -c '%a' "$current")"; (( (8#$mode & 8#022) == 0 )) || die "trusted path component is writable by group/world"
    done
    canonical="$(readlink -f -- "$path")"; [ "$canonical" = "$path" ] || die "trusted path is not canonical"
}

[ "$(id -u)" -eq 0 ] || die "rollback.sh must run as root"
[ -n "$BACKUP_ROOT" ] || die "BACKUP_ROOT is required"
case "$BACKUP_ROOT" in "$FIXED_BACKUP_ROOT"/[A-Za-z0-9][A-Za-z0-9._-]*) ;; *) die "BACKUP_ROOT must be /var/backups/superpan-license/<id>" ;; esac
[ "$(dirname "$BACKUP_ROOT")" = "$FIXED_BACKUP_ROOT" ] || die "backup must be an immediate child of fixed root"
validate_trusted_path "$FIXED_BACKUP_ROOT"
validate_trusted_path "$BACKUP_ROOT"
[ "$(stat -c '%U:%G:%a' "$FIXED_BACKUP_ROOT")" = root:root:700 ] || die "fixed backup root metadata is unsafe"
[ "$(stat -c '%U:%G:%a' "$BACKUP_ROOT")" = root:root:700 ] || die "backup directory metadata is unsafe"

MANIFEST="$BACKUP_ROOT/manifest.sha256"
[ -f "$MANIFEST" ] && [ ! -L "$MANIFEST" ] && [ "$(stat -c '%h:%U:%G:%a' "$MANIFEST")" = 1:root:root:600 ] || die "backup manifest metadata is unsafe"
ALLOWED_NAMES=$'installed-hashes.txt\ninventory.tsv\nlicense-binary\nlicense-config\nlicense-source-commit\nlicense-unit\nota-binary\nota-config\nota-unit\npersistent-identity.txt\nschema-after.db\nschema-before.db\nschema-migrated.db\nschema-migrated.db-shm\nschema-migrated.db-wal\nschema-new.json\nschema-new.log\nschema-old-verified.db\nschema-old.db\nschema-old.db-shm\nschema-old.db-wal\nschema-old.json\nschema-old.log\nschema-preflight.txt\nsource-commit.txt\nunit-state.tsv'
ACTUAL_MANIFEST_NAMES="$(awk 'NF == 2 && $1 ~ /^[0-9a-f]{64}$/ && $2 !~ /^\*/ { print $2; next } { exit 97 }' "$MANIFEST" | LC_ALL=C sort)" || die "backup manifest is malformed"
while IFS= read -r name; do printf '%s\n' "$ALLOWED_NAMES" | grep -Fxq "$name" || die "backup manifest allowlist mismatch"; done <<<"$ACTUAL_MANIFEST_NAMES"
ACTUAL_BACKUP_NAMES="$(find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | LC_ALL=C sort)"
[ -z "$(find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 ! -type f -print -quit)" ] || die "backup directory contains a non-regular entry"
EXPECTED_BACKUP_NAMES="$(printf '%s\nmanifest.sha256\n' "$ACTUAL_MANIFEST_NAMES" | LC_ALL=C sort)"
[ "$ACTUAL_BACKUP_NAMES" = "$EXPECTED_BACKUP_NAMES" ] || die "backup directory contains an unmanifested file"
(cd "$BACKUP_ROOT"; sha256sum -c manifest.sha256 >/dev/null) || die "backup payload hash mismatch"
while IFS= read -r name; do
    path="$BACKUP_ROOT/$name"
    [ -f "$path" ] && [ ! -L "$path" ] && [ "$(stat -c '%h:%U:%G:%a' "$path")" = 1:root:root:600 ] || die "unsafe backup control file: $name"
done <<<"$(printf '%s\n' inventory.tsv unit-state.tsv source-commit.txt persistent-identity.txt)"

INVENTORY_TARGETS="$(awk -F '\t' 'NF==7 {print $1 "\t" $2}' "$BACKUP_ROOT/inventory.tsv")"
[ "$INVENTORY_TARGETS" = "$EXPECTED_TARGETS" ] || die "inventory target allowlist mismatch"
[ "$(wc -l <"$BACKUP_ROOT/inventory.tsv")" -eq 7 ] || die "inventory must contain exactly seven targets"
[ "$(awk -F '\t' '$3!="present" && $3!="absent" {bad=1} END {print bad+0}' "$BACKUP_ROOT/inventory.tsv")" -eq 0 ] || die "inventory presence state is invalid"
while IFS=$'\t' read -r target name presence uid gid mode hash; do
    if [ "$presence" = present ]; then
        payload="$BACKUP_ROOT/$name"
        [ -f "$payload" ] && [ ! -L "$payload" ] && [ "$(stat -c '%h:%u:%g:%a' "$payload")" = "1:$uid:$gid:$mode" ] || die "backup payload metadata mismatch: $name"
        [ "$(sha256sum -- "$payload" | awk '{print $1}')" = "$hash" ] || die "backup payload hash mismatch: $name"
    else
        [ "$uid:$gid:$mode:$hash" = '-:-:-:-' ] || die "absent inventory metadata is malformed"
        [ ! -e "$BACKUP_ROOT/$name" ] && [ ! -L "$BACKUP_ROOT/$name" ] || die "absent target unexpectedly has a payload"
    fi
done <"$BACKUP_ROOT/inventory.tsv"
[ "$(wc -l <"$BACKUP_ROOT/unit-state.tsv")" -eq 2 ] || die "unit state must contain exactly two services"
[ "$(cut -f1 "$BACKUP_ROOT/unit-state.tsv")" = $'superpan-license.service\nsuperpan-ota-admin.service' ] || die "unit state allowlist mismatch"
[ "$(awk -F '\t' 'NF!=4 || ($4!="present" && $4!="absent") {bad=1} END {print bad+0}' "$BACKUP_ROOT/unit-state.tsv")" -eq 0 ] || die "unit wants-link state is malformed"

systemctl stop superpan-ota-admin.service 2>/dev/null || :
systemctl stop superpan-license.service 2>/dev/null || :
restore_inventory() {
    while IFS=$'\t' read -r target name presence uid gid mode hash; do
        if [ "$presence" = present ]; then install -m "$mode" -o "$uid" -g "$gid" "$BACKUP_ROOT/$name" "$target"; else rm -f -- "$target"; fi
    done <"$BACKUP_ROOT/inventory.tsv"
}
restore_inventory
# Persistent database and key material are deliberately preserved as one state.
# Rollback never writes or removes /var/lib/superpan-license, encryption.key,
# lookup.key, admin.secret, or response-signing key material.
systemctl daemon-reload
remove_transaction_wants_link() {
    local unit="$1" link expected resolved
    case "$unit" in
        superpan-license.service) link=/etc/systemd/system/multi-user.target.wants/superpan-license.service ;;
        superpan-ota-admin.service) link=/etc/systemd/system/multi-user.target.wants/superpan-ota-admin.service ;;
        *) die "unexpected wants-link cleanup unit" ;;
    esac
    expected="/etc/systemd/system/$unit"
    if [ -e "$link" ] || [ -L "$link" ]; then
        [ -L "$link" ] || die "transaction wants path is not a symlink"
        resolved="$(readlink -f -- "$link")" || die "transaction wants link cannot be resolved"
        [ "$resolved" = "$expected" ] || die "transaction wants link points outside its fixed unit"
        rm -f -- "$link"
    fi
}
restore_unit_state() {
    local unit enabled active wants observed
    while IFS=$'\t' read -r unit enabled active wants; do
        case "$enabled" in
            enabled) systemctl enable "$unit" >/dev/null ;;
            enabled-runtime) systemctl enable --runtime "$unit" >/dev/null ;;
            disabled) systemctl disable "$unit" >/dev/null ;;
            masked) systemctl mask "$unit" >/dev/null ;;
            masked-runtime) systemctl mask --runtime "$unit" >/dev/null ;;
            static|indirect|generated|transient|alias|linked|linked-runtime) ;;
            not-found)
                systemctl disable "$unit" >/dev/null 2>&1 || :
                [ "$wants" = absent ] || die "not-found unit unexpectedly had a prior wants link"
                remove_transaction_wants_link "$unit"
                ;;
            *) die "unsupported prior enablement state for $unit" ;;
        esac
        observed="$(systemctl is-enabled "$unit" 2>/dev/null || :)"; [ -n "$observed" ] || observed=not-found
        if [ "$enabled" = not-found ]; then
            [ "$observed" = not-found ] || die "prior not-found unit state was not restored"
        else
            [ "$observed" = "$enabled" ] || die "prior unit enablement state was not restored"
        fi
        case "$active" in active) systemctl start "$unit" ;; inactive) systemctl stop "$unit" 2>/dev/null || : ;; *) die "invalid prior active state" ;; esac
    done <"$BACKUP_ROOT/unit-state.tsv"
}
restore_unit_state
ROLLBACK_SUCCEEDED=true
echo "license and OTA rollback complete; persistent database and keys preserved; backup retained: $BACKUP_ROOT"
