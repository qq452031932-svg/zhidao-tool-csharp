#!/usr/bin/env bash
set -euo pipefail
umask 077

# Executable only on an explicitly authorized disposable Linux v219 fixture.
# Windows runs contract and bash syntax tests; it does not execute this matrix.
[ "$(uname -s)" = Linux ] || { echo "v219 staging harness requires Linux" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || { echo "v219 staging harness must run as root" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALLER="$SCRIPT_DIR/install.sh"
ROLLBACK="$SCRIPT_DIR/rollback.sh"
OTA_DIR="$(cd "$SCRIPT_DIR/../ota-admin" && pwd)"
BACKUP_ROOT=/var/backups/superpan-license
BUNDLE=""
FIXTURE_VHOST=""
declare -A SEEN_ARGS=()
FAILPOINTS=(identity secret-encryption secret-lookup secret-admin secret-signing secret-publish backup license-binary license-config license-unit license-start license-health ota-binary ota-config ota-unit ota-restart ota-health)
RETRY_FAILPOINTS=(secret-publish license-binary license-config license-unit)

die() { echo "ERROR: $*" >&2; exit 1; }
usage() { echo "usage: test-install-v219.sh --bundle ABSOLUTE_PATH --fixture-vhost FIXTURE_ID" >&2; exit 2; }
mark_seen() { [ "${SEEN_ARGS[$1]+set}" != set ] || die "duplicate option: $1"; SEEN_ARGS["$1"]=1; }
while [ "$#" -gt 0 ]; do
    case "$1" in
        --bundle) mark_seen "$1"; [ "$#" -ge 2 ] || usage; BUNDLE="$2"; shift 2 ;;
        --fixture-vhost) mark_seen "$1"; [ "$#" -ge 2 ] || usage; FIXTURE_VHOST="$2"; shift 2 ;;
        *) usage ;;
    esac
done
[ "${SEEN_ARGS[--bundle]+set}" = set ] && [ "${SEEN_ARGS[--fixture-vhost]+set}" = set ] || usage
case "$BUNDLE" in /*) ;; *) die "--bundle must be absolute" ;; esac
validate_trusted_fixture() {
    local component current=/ mode canonical
    local -a components=()
    case "$FIXTURE_VHOST" in /*) ;; *) die "--fixture-vhost must be absolute" ;; esac
    [ -f "$FIXTURE_VHOST" ] && [ ! -L "$FIXTURE_VHOST" ] || die "fixture path must be a regular non-symlink file"
    [ "$(stat -c '%h:%U:%G' "$FIXTURE_VHOST")" = 1:root:root ] || die "fixture path metadata is unsafe"
    mode="$(stat -c '%a' "$FIXTURE_VHOST")"; (( (8#$mode & 8#022) == 0 )) || die "fixture path is group/world writable"
    canonical="$(readlink -f -- "$FIXTURE_VHOST")"; [ "$canonical" = "$FIXTURE_VHOST" ] || die "fixture path must be canonical"
    IFS=/ read -r -a components <<<"${FIXTURE_VHOST#/}"
    for component in "${components[@]::${#components[@]}-1}"; do
        current="${current%/}/$component"
        [ -d "$current" ] && [ ! -L "$current" ] && [ "$(stat -c '%U' "$current")" = root ] || die "fixture ancestor is unsafe"
        mode="$(stat -c '%a' "$current")"; (( (8#$mode & 8#022) == 0 )) || die "fixture ancestor is writable"
    done
}
validate_trusted_fixture
[ -x "$FIXTURE_VHOST" ] || die "fixture driver must be executable"
command -v ss >/dev/null || die "ss is required"
reset_fixture() { "$FIXTURE_VHOST" reset "$1"; }
seed_wal_upgrade() { "$FIXTURE_VHOST" seed-wal; }

sidecar_hash() { awk -v name="$2" 'NF==2 && $1 ~ /^[0-9a-f]{64}$/ && $2==name {print $1}' "$1"; }
SOURCE_COMMIT="$(cat "$BUNDLE/source-commit.txt")"
OTA_SHA="$(sidecar_hash "$BUNDLE/ota-admin.sha256" ota-admin)"
LICENSE_SHA="$(sidecar_hash "$BUNDLE/superpan-license.sha256" superpan-license)"
MANIFEST_SHA="$(sidecar_hash "$BUNDLE/source-manifest.sha256.sha256" source-manifest.sha256)"
INSTALL_ARGS=(
    --bundle "$BUNDLE" --source-commit "$SOURCE_COMMIT"
    --ota-binary-sha256 "$OTA_SHA" --license-binary-sha256 "$LICENSE_SHA" --source-manifest-sha256 "$MANIFEST_SHA"
    --license-config "$SCRIPT_DIR/config.example.json" --license-config-sha256 "$(sha256sum "$SCRIPT_DIR/config.example.json" | awk '{print $1}')"
    --license-unit "$SCRIPT_DIR/superpan-license.service" --license-unit-sha256 "$(sha256sum "$SCRIPT_DIR/superpan-license.service" | awk '{print $1}')"
    --ota-config "$OTA_DIR/config.example.json" --ota-config-sha256 "$(sha256sum "$OTA_DIR/config.example.json" | awk '{print $1}')"
    --ota-unit "$OTA_DIR/superpan-ota-admin.service" --ota-unit-sha256 "$(sha256sum "$OTA_DIR/superpan-ota-admin.service" | awk '{print $1}')"
)

backup_count() { [ -d "$BACKUP_ROOT" ] && find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d | wc -l || printf '0\n'; }
latest_backup() { find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' | sort -nr | head -n1 | cut -d' ' -f2-; }
assert_single_backup() { local before="$1" after; after="$(backup_count)"; [ "$after" -eq $((before + 1)) ] || die "scenario did not create exactly one single backup"; }
assert_metadata() {
    [ "$(stat -c '%U:%G:%a' /opt/superpan-license/superpan-license)" = root:root:755 ] || die "license binary metadata"
    [ "$(stat -c '%U:%G:%a' /etc/superpan-license/config.json)" = root:superpan-license:640 ] || die "license config metadata"
    [ "$(stat -c '%U:%G:%a' /etc/systemd/system/superpan-license.service)" = root:root:644 ] || die "license unit metadata"
    [ "$(stat -c '%U:%G:%a' /opt/superpan-ota-admin/ota-admin)" = root:root:755 ] || die "OTA binary metadata"
    [ "$(stat -c '%U:%G:%a' /etc/superpan-ota-admin/config.json)" = root:superpan-ota-admin:640 ] || die "OTA config metadata"
    [ "$(stat -c '%U:%G:%a' /etc/systemd/system/superpan-ota-admin.service)" = root:root:644 ] || die "OTA unit metadata"
}
assert_present_service_acl() {
    local path expected
    while IFS=$'\t' read -r path expected; do
        if [ -e "$path" ]; then [ "$(stat -c '%U:%G:%a' "$path")" = "$expected" ] || die "service target ACL: $path"; fi
    done <<'ACL'
/opt/superpan-license/superpan-license	root:root:755
/etc/superpan-license/config.json	root:superpan-license:640
/etc/systemd/system/superpan-license.service	root:root:644
/opt/superpan-ota-admin/ota-admin	root:root:755
/etc/superpan-ota-admin/config.json	root:superpan-ota-admin:640
/etc/systemd/system/superpan-ota-admin.service	root:root:644
ACL
}
assert_persistent_acl() {
    local scenario="$1" present=0 path db=false
    if [ -e /etc/superpan-license ]; then
        [ "$(stat -c '%U:%G:%a' /etc/superpan-license)" = root:superpan-license-admin:710 ] || die "license etc ACL"
    else
        [ "$scenario" = failpoint ] || die "license etc missing"
    fi
    for path in encryption.key lookup.key response-signing.key; do
        if [ -e "/etc/superpan-license/$path" ]; then
            [ "$(stat -c '%U:%G:%a' "/etc/superpan-license/$path")" = root:superpan-license:640 ] || die "private key ACL"
            present=$((present + 1))
        fi
    done
    if [ -e /etc/superpan-license/admin.secret ]; then
        [ "$(stat -c '%U:%G:%a' /etc/superpan-license/admin.secret)" = root:superpan-license-admin:640 ] || die "admin secret ACL"
        present=$((present + 1))
    fi
    for path in response-signing.key-id response-signing.spki.der response-signing.spki.base64 response-signing.spki.sha256; do
        if [ -e "/etc/superpan-license/$path" ]; then
            [ "$(stat -c '%U:%G:%a' "/etc/superpan-license/$path")" = root:root:644 ] || die "public identity ACL"
            present=$((present + 1))
        fi
    done
    [ "$present" -eq 0 ] || [ "$present" -eq 8 ] || die "partial persistent key set"
    if [ -e /var/lib/superpan-license ]; then
        [ "$(stat -c '%U:%G:%a' /var/lib/superpan-license)" = superpan-license:superpan-license:700 ] || die "license data ACL"
    else
        [ "$scenario" = failpoint ] || die "license data missing"
    fi
    for path in state.db state.db-wal state.db-shm; do
        if [ -e "/var/lib/superpan-license/$path" ]; then
            db=true
            [ "$(stat -c '%U:%G:%a' "/var/lib/superpan-license/$path")" = superpan-license:superpan-license:600 ] || die "license database ACL"
        fi
    done
    [ "$db" = false ] || [ "$present" -eq 8 ] || die "database exists without full keys"
    if [ "$scenario" = installed ]; then [ "$present" -eq 8 ] && [ -f /var/lib/superpan-license/state.db ] || die "installed persistent state missing"; fi
    if [ -d "$BACKUP_ROOT" ]; then [ "$(stat -c '%U:%G:%a' "$BACKUP_ROOT")" = root:root:700 ] || die "backup root ACL"; fi
    if [ -n "$(latest_backup 2>/dev/null || :)" ]; then [ "$(stat -c '%U:%G:%a' "$(latest_backup)")" = root:root:700 ] || die "transaction backup ACL"; fi
}
assert_unit_state() {
    systemctl is-active --quiet superpan-license.service && systemctl is-enabled --quiet superpan-license.service
    systemctl is-active --quiet superpan-ota-admin.service && systemctl is-enabled --quiet superpan-ota-admin.service
}
assert_listener_identity() {
    local unit="$1" port="$2" binary="$3" expected_hash="$4" pid process_hash sockets endpoints
    pid="$(systemctl show --property=MainPID --value "$unit")"
    [[ "$pid" =~ ^[1-9][0-9]*$ ]] || die "$unit MainPID"
    process_hash="$(sha256sum -- "/proc/$pid/exe" | awk '{print $1}')"
    [ "$process_hash" = "$expected_hash" ] && [ "$(sha256sum -- "$binary" | awk '{print $1}')" = "$expected_hash" ] || die "$unit process identity"
    sockets="$(ss -H -ltnp)"
    endpoints="$(printf '%s\n' "$sockets" | awk -v needle="pid=$pid," 'index($0, needle) {print $4}' | LC_ALL=C sort -u)"
    [ "$endpoints" = "127.0.0.1:$port" ] || die "unexpected listener endpoint for MainPID $pid: $endpoints"
}
assert_persistent_pairing() {
    local present=0 path db=false
    for path in encryption.key lookup.key admin.secret response-signing.key response-signing.key-id response-signing.spki.der response-signing.spki.base64 response-signing.spki.sha256; do
        [ ! -e "/etc/superpan-license/$path" ] || present=$((present + 1))
    done
    [ ! -e /var/lib/superpan-license/state.db ] || db=true
    [ "$present" -eq 0 ] || [ "$present" -eq 8 ] || die "partial persistent key set"
    [ "$db" = false ] || [ "$present" -eq 8 ] || die "database is not paired with all keys"
}
assert_installed() {
    assert_metadata; assert_unit_state
    assert_listener_identity superpan-license.service 18092 /opt/superpan-license/superpan-license "$LICENSE_SHA"
    assert_listener_identity superpan-ota-admin.service 18091 /opt/superpan-ota-admin/ota-admin "$OTA_SHA"
    assert_persistent_pairing; assert_persistent_acl installed
}
state_fingerprint() {
    local path
    for path in /opt/superpan-license/superpan-license /etc/superpan-license/config.json /etc/systemd/system/superpan-license.service /opt/superpan-ota-admin/ota-admin /etc/superpan-ota-admin/config.json /etc/systemd/system/superpan-ota-admin.service; do
        if [ -f "$path" ]; then printf '%s %s %s\n' "$path" "$(stat -c '%u:%g:%a' "$path")" "$(sha256sum "$path" | awk '{print $1}')"; else printf '%s absent\n' "$path"; fi
    done
    systemctl is-enabled superpan-license.service 2>/dev/null || :; systemctl is-active superpan-license.service 2>/dev/null || :
    systemctl is-enabled superpan-ota-admin.service 2>/dev/null || :; systemctl is-active superpan-ota-admin.service 2>/dev/null || :
}
run_success_install() { local before; before="$(backup_count)"; "$INSTALLER" "${INSTALL_ARGS[@]}"; assert_single_backup "$before"; assert_installed; }
run_failed_install() {
    local point="$1" before prior after
    before="$(backup_count)"; prior="$(state_fingerprint)"
    if LICENSE_INSTALL_FAILPOINT="$point" "$INSTALLER" "${INSTALL_ARGS[@]}"; then die "failpoint unexpectedly succeeded: $point"; fi
    assert_single_backup "$before"
    assert_failpoint_state "$prior"
}
assert_failpoint_state() {
    local prior="$1" after unit enabled
    assert_persistent_pairing; assert_persistent_acl failpoint; assert_present_service_acl; after="$(state_fingerprint)"
    if [ "$after" != "$prior" ]; then
        systemctl is-active --quiet superpan-license.service && die "changed failure state left license active"
        systemctl is-active --quiet superpan-ota-admin.service && die "changed failure state left OTA active"
        for unit in superpan-license.service superpan-ota-admin.service; do
            enabled="$(systemctl is-enabled "$unit" 2>/dev/null || :)"; [ -n "$enabled" ] || enabled=not-found
            case "$enabled" in enabled|disabled|not-found) ;; *) die "stopped recoverable state has unsupported enablement: $unit=$enabled" ;; esac
        done
        [ -z "$(ss -H -ltnp | awk '$4 ~ /:18091$/ || $4 ~ /:18092$/ {print}')" ] || die "stopped recoverable state retains a service listener"
    else
        if systemctl is-active --quiet superpan-license.service; then
            assert_listener_identity superpan-license.service 18092 /opt/superpan-license/superpan-license "$(sha256sum /opt/superpan-license/superpan-license | awk '{print $1}')"
        fi
        if systemctl is-active --quiet superpan-ota-admin.service; then
            assert_listener_identity superpan-ota-admin.service 18091 /opt/superpan-ota-admin/ota-admin "$(sha256sum /opt/superpan-ota-admin/ota-admin | awk '{print $1}')"
        fi
        if ! systemctl is-active --quiet superpan-license.service && ! systemctl is-active --quiet superpan-ota-admin.service; then
            [ -z "$(ss -H -ltnp | awk '$4 ~ /:18091$/ || $4 ~ /:18092$/ {print}')" ] || die "inactive restored state retains a service listener"
        fi
    fi
}

run_full_matrix() {
    local point first_backup
    reset_fixture first-install
    run_success_install
    systemctl restart superpan-license.service superpan-ota-admin.service
    assert_installed
    first_backup="$(latest_backup)"
    "$ROLLBACK" "$first_backup"
    assert_persistent_pairing

    reset_fixture wal-upgrade
    seed_wal_upgrade
    run_success_install

    for point in "${FAILPOINTS[@]}"; do
        reset_fixture "failpoint-$point"
        run_failed_install "$point"
        case " ${RETRY_FAILPOINTS[*]} " in *" $point "*) run_success_install ;; esac
    done
    echo "v219 first-install, wal-upgrade, 17 failpoints, rollback, and restart staging matrix passed"
}

run_full_matrix
