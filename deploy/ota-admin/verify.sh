#!/usr/bin/env bash
set -euo pipefail

SERVICE_NAME="superpan-ota-admin.service"
ADMIN_URL="${ADMIN_URL:-https://ota.fanfh.cn/admin/}"
HTTP_ADMIN_URL="${HTTP_ADMIN_URL:-http://ota.fanfh.cn/admin/}"
HEALTH_URL="${HEALTH_URL:-https://ota.fanfh.cn/health}"
STABLE_PROBE_URL="${STABLE_PROBE_URL:-https://ota.fanfh.cn/releases/win-x64/stable/RELEASES-win-x64-stable}"
MANIFEST_URL="${MANIFEST_URL:-https://ota.fanfh.cn/releases/win-x64/stable/upload-manifest.sha256}"
SIGNATURE_URL="${SIGNATURE_URL:-https://ota.fanfh.cn/releases/win-x64/stable/upload-manifest.sha256.sig}"
STATIC_ASSET_URL="${STATIC_ASSET_URL:-}"
EXPECTED_CERT_SHA256="${EXPECTED_CERT_SHA256:-}"
EXPECTED_HEALTH_SHA256="${EXPECTED_HEALTH_SHA256:-}"
EXPECTED_STABLE_SHA256="${EXPECTED_STABLE_SHA256:-}"
EXPECTED_STATIC_CACHE_CONTROL="${EXPECTED_STATIC_CACHE_CONTROL:-}"

die() {
    echo "VERIFY FAILED: $*" >&2
    exit 1
}

[[ "$EXPECTED_CERT_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "EXPECTED_CERT_SHA256 is required (64 lowercase hex)"
[[ "$EXPECTED_HEALTH_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "EXPECTED_HEALTH_SHA256 is required (64 lowercase hex)"
[[ "$EXPECTED_STABLE_SHA256" =~ ^[0-9a-f]{64}$ ]] || die "EXPECTED_STABLE_SHA256 is required (64 lowercase hex)"
[ "$(id -u)" -eq 0 ] || die "run verification as root so service recovery is guaranteed"

HEADERS_FILE="$(mktemp /tmp/superpan-ota-admin-headers.XXXXXX)"
SERVICE_STOPPED=false
cleanup() {
    local status=$?
    trap - EXIT
    if [ "$SERVICE_STOPPED" = true ]; then
        if ! systemctl start "$SERVICE_NAME" || ! systemctl is-active --quiet "$SERVICE_NAME"; then
            echo "VERIFY FAILED: could not recover ${SERVICE_NAME}" >&2
            status=1
        fi
    fi
    unlink "$HEADERS_FILE" 2>/dev/null || status=1
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

body_sha256() {
    curl --resolve 'ota.fanfh.cn:443:127.0.0.1' --noproxy '*' --fail --silent --show-error --location --max-time 120 "$1" | sha256sum | awk '{print $1}'
}

header_value() {
    local url="$1"
    local name="$2"
    : >"$HEADERS_FILE"
    curl --resolve 'ota.fanfh.cn:443:127.0.0.1' --noproxy '*' --fail --silent --show-error --location --max-time 120 --output /dev/null --dump-header "$HEADERS_FILE" "$url"
    awk -v wanted="$name" 'BEGIN { IGNORECASE=1 } $0 ~ "^" wanted ":" { sub(/^[^:]+:[[:space:]]*/, ""); sub(/\r$/, ""); value=$0 } END { print value }' "$HEADERS_FILE"
}

assert_no_cache() {
    local url="$1"
    local value
    value="$(header_value "$url" "Cache-Control")"
    [[ "${value,,}" == *no-cache* || "${value,,}" == *no-store* || "${value,,}" == *max-age=0* ]] ||
        die "signed metadata is cacheable at $url: $value"
}

systemctl is-active --quiet superpan-ota-admin.service || die "service is not active"

LISTENERS="$(ss -ltnH 'sport = :18091')"
[ -n "$LISTENERS" ] || die "127.0.0.1:18091 is not listening"
echo "$LISTENERS" | awk '{print $4}' | grep -Eq '^127\.0\.0\.1:18091$|^\[::1\]:18091$' || die "loopback listener is missing"
if echo "$LISTENERS" | awk '{print $4}' | grep -Evq '^127\.0\.0\.1:18091$|^\[::1\]:18091$'; then
    die "port 18091 is exposed on a non-loopback address"
fi
if curl --noproxy '*' --silent --show-error --connect-timeout 3 --max-time 5 --output /dev/null http://ota.fanfh.cn:18091/; then
    die "port 18091 is reachable externally"
fi

: >"$HEADERS_FILE"
HTTP_ADMIN_STATUS="$(curl --resolve 'ota.fanfh.cn:80:127.0.0.1' --noproxy '*' --silent --show-error --max-time 15 --output /dev/null --dump-header "$HEADERS_FILE" --write-out '%{http_code}' "$HTTP_ADMIN_URL")"
case "$HTTP_ADMIN_STATUS" in
    301|302|307|308) ;;
    *) die "HTTP /admin did not redirect to HTTPS (status ${HTTP_ADMIN_STATUS})" ;;
esac
HTTP_ADMIN_LOCATION="$(awk 'BEGIN { IGNORECASE=1 } /^Location:/ { sub(/^[^:]+:[[:space:]]*/, ""); sub(/\r$/, ""); value=$0 } END { print value }' "$HEADERS_FILE")"
[[ "$HTTP_ADMIN_LOCATION" == https://ota.fanfh.cn/admin/* ]] || die "HTTP /admin redirect is not HTTPS: $HTTP_ADMIN_LOCATION"

ADMIN_CACHE="$(header_value "$ADMIN_URL" "Cache-Control")"
[[ "${ADMIN_CACHE,,}" == *no-store* ]] || die "admin response lacks Cache-Control no-store"
assert_no_cache "$MANIFEST_URL"
assert_no_cache "$SIGNATURE_URL"

CERT_SHA256="$(echo | openssl s_client -connect ota.fanfh.cn:443 -servername ota.fanfh.cn -verify_hostname ota.fanfh.cn 2>/dev/null | openssl x509 -outform DER | sha256sum | awk '{print $1}')"
[ "$CERT_SHA256" = "$EXPECTED_CERT_SHA256" ] || die "HTTPS certificate baseline changed"
[ "$(body_sha256 "$HEALTH_URL")" = "$EXPECTED_HEALTH_SHA256" ] || die "health baseline changed"
[ "$(body_sha256 "$STABLE_PROBE_URL")" = "$EXPECTED_STABLE_SHA256" ] || die "stable content baseline changed"

if [ -n "$STATIC_ASSET_URL" ] || [ -n "$EXPECTED_STATIC_CACHE_CONTROL" ]; then
    [ -n "$STATIC_ASSET_URL" ] && [ -n "$EXPECTED_STATIC_CACHE_CONTROL" ] || die "set both STATIC_ASSET_URL and EXPECTED_STATIC_CACHE_CONTROL"
    [ "$(header_value "$STATIC_ASSET_URL" "Cache-Control")" = "$EXPECTED_STATIC_CACHE_CONTROL" ] || die "existing static cache policy changed"
fi

SERVICE_STOPPED=true
systemctl stop superpan-ota-admin.service
[ "$(body_sha256 "$STABLE_PROBE_URL")" = "$EXPECTED_STABLE_SHA256" ] || die "stable content depends on the admin service"
[ "$(body_sha256 "$HEALTH_URL")" = "$EXPECTED_HEALTH_SHA256" ] || die "health endpoint changed while admin service was stopped"

systemctl start superpan-ota-admin.service
systemctl is-active --quiet superpan-ota-admin.service || die "service did not recover"
SERVICE_STOPPED=false
[ "$(body_sha256 "$STABLE_PROBE_URL")" = "$EXPECTED_STABLE_SHA256" ] || die "stable baseline changed after recovery"
[ "$(body_sha256 "$HEALTH_URL")" = "$EXPECTED_HEALTH_SHA256" ] || die "health baseline changed after recovery"

echo "OTA admin deployment verification passed."
