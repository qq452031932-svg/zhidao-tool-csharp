#!/usr/bin/env bash
set -euo pipefail
umask 077

ACTIVE_VHOST=/www/server/panel/vhost/nginx/ota.fanfh.cn.conf
ROUTE_INCLUDE=/etc/nginx/conf.d/ota.fanfh.cn.license-api.include
INCLUDE_LINE='include /etc/nginx/conf.d/ota.fanfh.cn.license-api.include;'
BACKUP_ROOT=/var/backups/superpan-license-route
NGINX=/www/server/nginx/sbin/nginx
NGINX_PID_FILE=/www/server/nginx/logs/nginx.pid
BASELINE= SNIPPET= SNIPPET_SHA256= MODE=

die() { echo "ROUTE TRANSACTION FAILED: $*" >&2; exit 1; }
while [ "$#" -gt 0 ]; do
    case "$1" in
        --baseline) BASELINE="$2"; shift 2 ;;
        --snippet) SNIPPET="$2"; shift 2 ;;
        --snippet-sha256) SNIPPET_SHA256="$2"; shift 2 ;;
        --mode) MODE="$2"; shift 2 ;;
        *) die "unknown or incomplete option: $1" ;;
    esac
done
[ "$MODE" = enable ] || [ "$MODE" = restore ] || die "--mode must be enable|restore"
[ "$(id -u)" -eq 0 ] || die "run as root"
BASELINE="$(readlink -f -- "$BASELINE")"
case "$BASELINE" in "$BACKUP_ROOT"/baseline-*) ;; *) die "baseline is outside the fixed backup root" ;; esac
[ -d "$BASELINE" ] && [ ! -L "$BASELINE" ] || die "baseline directory is invalid"
[ "$(stat -c '%U:%G' "$BASELINE")" = root:root ] && [ "$(stat -c '%a' "$BASELINE")" = 700 ] || die "baseline must be root:root 0700"
(cd "$BASELINE" && sha256sum -c manifest.sha256 >/dev/null) || die "baseline manifest verification failed"

TRANSACTION="$(mktemp -d "$BACKUP_ROOT/transaction.XXXXXX")"
cp -a -- "$ACTIVE_VHOST" "$TRANSACTION/active-vhost.conf"
if [ -e "$ROUTE_INCLUDE" ]; then cp -a -- "$ROUTE_INCLUDE" "$TRANSACTION/route-include.conf"; else : >"$TRANSACTION/include-was-absent"; fi
ROLLBACK_ARMED=false
restore_pre_route_state() {
    cp -a -- "$TRANSACTION/active-vhost.conf" "$ACTIVE_VHOST"
    if [ -f "$TRANSACTION/include-was-absent" ]; then rm -f -- "$ROUTE_INCLUDE"; else cp -a -- "$TRANSACTION/route-include.conf" "$ROUTE_INCLUDE"; fi
    "$NGINX" -t && "$NGINX" -s reload
}
transaction_exit() {
    local status=$?
    trap - EXIT
    if [ "$status" -ne 0 ] && [ "$ROLLBACK_ARMED" = true ]; then restore_pre_route_state || status=1; fi
    rm -rf -- "$TRANSACTION"
    exit "$status"
}
trap transaction_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
ROLLBACK_ARMED=true

EXPECTED_VHOST_SHA256="$(cat "$BASELINE/active-vhost.sha256")"
BEFORE_PID="$(tr -d '[:space:]' <"$NGINX_PID_FILE")"
[ "$BEFORE_PID" = "$(cat "$BASELINE/nginx-master.pid")" ] || die "Nginx master PID changed since baseline"
if [ "$MODE" = enable ]; then
    [ "$(cat "$BASELINE/include-state.txt")" = absent ] || die "enable requires a baseline with the route absent"
    [ "$(sha256sum "$ACTIVE_VHOST" | awk '{print $1}')" = "$EXPECTED_VHOST_SHA256" ] || die "active vhost changed since baseline"
    [[ "$SNIPPET_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "--snippet-sha256 is required"
    SNIPPET="$(readlink -f -- "$SNIPPET")"
    [ -f "$SNIPPET" ] && [ ! -L "$SNIPPET" ] || die "snippet must be a regular file"
    [ "$(sha256sum "$SNIPPET" | awk '{print $1}')" = "$SNIPPET_SHA256" ] || die "snippet hash mismatch"
    install -m 0644 -o root -g root "$SNIPPET" "$ROUTE_INCLUDE.new"
    mv -f -- "$ROUTE_INCLUDE.new" "$ROUTE_INCLUDE"
    awk -v line="$INCLUDE_LINE" '
        /^[[:space:]]*include \/etc\/superpan-ota-admin\/ota\.fanfh\.cn\.admin\.nginx\.conf;[[:space:]]*$/ && !done { print "    " line; done=1 }
        { print }
        END { if (!done) exit 42 }
    ' "$ACTIVE_VHOST" >"$ACTIVE_VHOST.new" || die "verified insertion anchor was not found"
    [ "$(awk -v line="$INCLUDE_LINE" '{gsub(/^[[:space:]]+|[[:space:]]+$/,""); if($0==line)n++} END{print n+0}' "$ACTIVE_VHOST.new")" = 1 ] || die "include line must occur exactly once"
    chown root:root "$ACTIVE_VHOST.new"; chmod --reference="$ACTIVE_VHOST" "$ACTIVE_VHOST.new"; mv -f -- "$ACTIVE_VHOST.new" "$ACTIVE_VHOST"
else
    awk -v line="$INCLUDE_LINE" '{trim=$0; gsub(/^[[:space:]]+|[[:space:]]+$/, "", trim); if(trim!=line) print}' "$ACTIVE_VHOST" >"$ACTIVE_VHOST.new"
    cmp -s "$ACTIVE_VHOST.new" "$BASELINE/active-vhost.conf" || die "vhost has changes beyond the managed include line"
    cp -a -- "$BASELINE/active-vhost.conf" "$ACTIVE_VHOST"
    if [ "$(cat "$BASELINE/include-state.txt")" = absent ]; then rm -f -- "$ROUTE_INCLUDE"; else cp -a -- "$BASELINE/route-include.conf" "$ROUTE_INCLUDE"; fi
fi

# The absolute-path invocations below are the required nginx -t / nginx -s reload transaction.
"$NGINX" -t
"$NGINX" -s reload
sleep 1
[ "$(tr -d '[:space:]' <"$NGINX_PID_FILE")" = "$BEFORE_PID" ] || die "Nginx master PID changed across reload"
if [ "$MODE" = enable ]; then
    [ "$(awk -v line="$INCLUDE_LINE" '{gsub(/^[[:space:]]+|[[:space:]]+$/,""); if($0==line)n++} END{print n+0}' "$ACTIVE_VHOST")" = 1 ] || die "installed include count changed"
fi
ROLLBACK_ARMED=false
printf 'LICENSE_PUBLIC_ROUTE_%s_OK\n' "${MODE^^}"
