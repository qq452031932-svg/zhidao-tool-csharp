#!/usr/bin/env bash
set -euo pipefail
umask 077

# Real Linux staging entry point for v2.19 lifecycle rehearsal. Windows tests
# validate this contract only; they do not claim that the staging run passed.
[ "$(uname -s)" = Linux ] || { echo "v219 staging harness requires Linux" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || { echo "v219 staging harness must run as root" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALLER="$SCRIPT_DIR/install.sh"
ROLLBACK="$SCRIPT_DIR/rollback.sh"
BACKUP_ROOT=/var/backups/superpan-license
SCENARIO="${1:-}"
shift || :

usage() {
    echo "usage: test-install-v219.sh first-install|upgrade|restart [installer arguments...]" >&2
    echo "       test-install-v219.sh rollback /var/backups/superpan-license/TRANSACTION_ID" >&2
    echo "       test-install-v219.sh failpoint POINT [installer arguments...]" >&2
    exit 2
}

backup_count() { [ -d "$BACKUP_ROOT" ] && find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d | wc -l || printf '0\n'; }
latest_backup() { find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' | sort -nr | head -n1 | cut -d' ' -f2-; }
assert_single_backup() {
    local before="$1" after
    after="$(backup_count)"
    [ "$after" -eq $((before + 1)) ] || { echo "scenario did not create exactly a single backup" >&2; exit 1; }
}
assert_ready() {
    systemctl is-active --quiet superpan-license.service
    systemctl is-active --quiet superpan-ota-admin.service
    curl --noproxy '*' --fail --silent --show-error --max-time 5 http://127.0.0.1:18092/internal/health >/dev/null
    curl --noproxy '*' --fail --silent --show-error --max-time 5 http://127.0.0.1:18091/admin/ >/dev/null
}
state_fingerprint() {
    local path
    for path in /opt/superpan-license/superpan-license /etc/superpan-license/config.json /etc/systemd/system/superpan-license.service \
        /opt/superpan-ota-admin/ota-admin /etc/superpan-ota-admin/config.json /etc/systemd/system/superpan-ota-admin.service; do
        if [ -f "$path" ]; then printf '%s present %s\n' "$path" "$(sha256sum -- "$path" | awk '{print $1}')"; else printf '%s absent\n' "$path"; fi
    done
    systemctl is-active superpan-license.service 2>/dev/null || :
    systemctl is-active superpan-ota-admin.service 2>/dev/null || :
}
run_install() {
    local label="$1" before
    shift
    before="$(backup_count)"
    "$INSTALLER" "$@"
    assert_single_backup "$before"
    assert_ready
    echo "$label staging scenario passed; single backup: $(latest_backup)"
}

case "$SCENARIO" in
    first-install) run_install first-install "$@" ;;
    upgrade) run_install upgrade "$@" ;;
    restart)
        [ "$#" -eq 0 ] || usage
        systemctl restart superpan-license.service superpan-ota-admin.service
        assert_ready
        echo "restart staging scenario passed"
        ;;
    rollback)
        [ "$#" -eq 1 ] || usage
        "$ROLLBACK" "$1"
        echo "rollback staging scenario passed"
        ;;
    failpoint)
        [ "$#" -ge 2 ] || usage
        point="$1"; shift
        before="$(backup_count)"; prior="$(state_fingerprint)"
        if LICENSE_INSTALL_FAILPOINT="$point" "$INSTALLER" "$@"; then echo "failpoint unexpectedly succeeded: $point" >&2; exit 1; fi
        assert_single_backup "$before"
        after="$(state_fingerprint)"
        if [ "$after" != "$prior" ]; then
            systemctl is-active --quiet superpan-license.service && { echo "failpoint changed state without stopping license service: $point" >&2; exit 1; }
            systemctl is-active --quiet superpan-ota-admin.service && { echo "failpoint changed state without stopping OTA service: $point" >&2; exit 1; }
        fi
        echo "failpoint staging scenario passed: $point; single backup: $(latest_backup)"
        ;;
    *) usage ;;
esac
