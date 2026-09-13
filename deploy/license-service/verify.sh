#!/usr/bin/env bash
set -euo pipefail
umask 077

BASELINE=
while [ "$#" -gt 0 ]; do case "$1" in --baseline) BASELINE="$2"; shift 2 ;; *) echo "VERIFY FAILED: unknown option $1" >&2; exit 1 ;; esac; done
die() { echo "VERIFY FAILED: $*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || die "run as root"
BASELINE="$(readlink -f -- "$BASELINE")"
case "$BASELINE" in /var/backups/superpan-license-route/baseline-*) ;; *) die "invalid baseline" ;; esac
(cd "$BASELINE" && sha256sum -c manifest.sha256 >/dev/null) || die "baseline verification failed"

HEADERS="$(mktemp /tmp/superpan-license-route-headers.XXXXXX)"
LICENSE_STOPPED=false OTA_STOPPED=false SERVICE_RESTORE_ARMED=true
cleanup() {
    local status=$?; trap - EXIT
    if [ "$LICENSE_STOPPED" = true ]; then systemctl start superpan-license.service && systemctl is-active --quiet superpan-license.service || status=1; fi
    if [ "$OTA_STOPPED" = true ]; then systemctl start superpan-ota-admin.service && systemctl is-active --quiet superpan-ota-admin.service || status=1; fi
    unlink "$HEADERS" 2>/dev/null || true
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

local_body_hash() { curl --resolve 'ota.fanfh.cn:443:127.0.0.1' --noproxy '*' --fail --silent --show-error --max-time 120 "$1" | sha256sum | awk '{print $1}'; }
route_probe() {
    local path="$1" status cache
    : >"$HEADERS"
    status="$(curl --resolve 'ota.fanfh.cn:443:127.0.0.1' --noproxy '*' --silent --show-error --max-time 30 -X POST -H 'Content-Type: application/json' --data '{}' --output /dev/null --dump-header "$HEADERS" --write-out '%{http_code}' "https://ota.fanfh.cn$path")"
    [ "$status" != 404 ] && [ "$status" != 000 ] || die "$path is not publicly routed"
    cache="$(awk 'BEGIN{IGNORECASE=1}/^Cache-Control:/{sub(/^[^:]+:[[:space:]]*/,"");sub(/\r$/,"");v=$0}END{print v}' "$HEADERS")"
    [[ "${cache,,}" == *no-store* ]] || die "$path lacks Cache-Control no-store"
}
for unit in superpan-license.service superpan-ota-admin.service; do systemctl is-active --quiet "$unit" || die "$unit is not active"; done
LISTENERS="$(ss -ltnp)"
for port in 18091 18092; do
    echo "$LISTENERS" | awk '{print $4}' | grep -Eq "^127\\.0\\.0\\.1:${port}$|^\\[::1\\]:${port}$" || die "loopback listener $port is missing"
    if echo "$LISTENERS" | awk '{print $4}' | grep -E ":${port}$" | grep -Evq "^127\\.0\\.0\\.1:${port}$|^\\[::1\\]:${port}$"; then die "port $port has a non-loopback listener"; fi
    if curl --noproxy '*' --silent --connect-timeout 3 --max-time 5 --output /dev/null "http://ota.fanfh.cn:${port}/"; then die "port $port is externally reachable"; fi
done
route_probe /license-api/v1/activate
route_probe /license-api/v1/validate
for path in /internal/health /internal/v1/; do
    code="$(curl --resolve 'ota.fanfh.cn:443:127.0.0.1' --noproxy '*' --silent --max-time 15 --output /dev/null --write-out '%{http_code}' "https://ota.fanfh.cn$path")"
    [ "$code" = 404 ] || die "$path is externally exposed (HTTP $code)"
done
[ "$(local_body_hash https://ota.fanfh.cn/health)" = "$(cat "$BASELINE/ota-health.sha256")" ] || die "OTA health baseline changed"
[ "$(local_body_hash https://ota.fanfh.cn/releases/win-x64/stable/RELEASES-win-x64-stable)" = "$(cat "$BASELINE/stable.sha256")" ] || die "stable release baseline changed"

LICENSE_STOPPED=true; systemctl stop superpan-license.service
[ "$(local_body_hash https://ota.fanfh.cn/health)" = "$(cat "$BASELINE/ota-health.sha256")" ] || die "OTA health depends on license service"
[ "$(local_body_hash https://ota.fanfh.cn/releases/win-x64/stable/RELEASES-win-x64-stable)" = "$(cat "$BASELINE/stable.sha256")" ] || die "stable release depends on license service"
systemctl start superpan-license.service; systemctl is-active --quiet superpan-license.service || die "license service did not recover"; LICENSE_STOPPED=false

OTA_STOPPED=true; systemctl stop superpan-ota-admin.service
[ "$(local_body_hash https://ota.fanfh.cn/health)" = "$(cat "$BASELINE/ota-health.sha256")" ] || die "OTA health depends on admin service"
[ "$(local_body_hash https://ota.fanfh.cn/releases/win-x64/stable/RELEASES-win-x64-stable)" = "$(cat "$BASELINE/stable.sha256")" ] || die "stable release depends on admin service"
code="$(curl --resolve 'ota.fanfh.cn:443:127.0.0.1' --noproxy '*' --silent --max-time 15 --output /dev/null --write-out '%{http_code}' https://ota.fanfh.cn/internal/v1/)"; [ "$code" = 404 ] || die "internal route appeared while admin was stopped"
systemctl start superpan-ota-admin.service; systemctl is-active --quiet superpan-ota-admin.service || die "OTA admin did not recover"; OTA_STOPPED=false

SERVICE_RESTORE_ARMED=false
echo "LICENSE_PUBLIC_ROUTE_VERIFY_OK"
