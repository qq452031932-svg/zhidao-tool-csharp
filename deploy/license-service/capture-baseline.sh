#!/usr/bin/env bash
set -euo pipefail
umask 077

BACKUP_ROOT=/var/backups/superpan-license-route
ACTIVE_VHOST=/www/server/panel/vhost/nginx/ota.fanfh.cn.conf
ROUTE_INCLUDE=/etc/nginx/conf.d/ota.fanfh.cn.license-api.include
NGINX_PID_FILE=/www/server/nginx/logs/nginx.pid
HEALTH_URL=https://ota.fanfh.cn/health
STABLE_URL=https://ota.fanfh.cn/releases/win-x64/stable/RELEASES-win-x64-stable

die() { echo "BASELINE FAILED: $*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || die "run as root"
[ -f "$ACTIVE_VHOST" ] && [ ! -L "$ACTIVE_VHOST" ] || die "active vhost is missing or a symlink"

install -d -m 0700 -o root -g root "$BACKUP_ROOT"
[ "$(stat -c '%U:%G' "$BACKUP_ROOT")" = root:root ] || die "backup root must be root:root"
[ "$(stat -c '%a' "$BACKUP_ROOT")" = 700 ] || die "backup root must be 0700"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
BASELINE="$(mktemp -d "$BACKUP_ROOT/baseline-${STAMP}.XXXXXX")"
chmod 0700 "$BASELINE"

cp -a -- "$ACTIVE_VHOST" "$BASELINE/active-vhost.conf"
sha256sum -- "$ACTIVE_VHOST" | awk '{print $1}' >"$BASELINE/active-vhost.sha256"
if [ -e "$ROUTE_INCLUDE" ]; then
    [ -f "$ROUTE_INCLUDE" ] && [ ! -L "$ROUTE_INCLUDE" ] || die "route include is not a regular file"
    printf 'present\n' >"$BASELINE/include-state.txt"
    cp -a -- "$ROUTE_INCLUDE" "$BASELINE/route-include.conf"
    sha256sum -- "$ROUTE_INCLUDE" | awk '{print $1}' >"$BASELINE/route-include.sha256"
else
    printf 'absent\n' >"$BASELINE/include-state.txt"
fi

echo | openssl s_client -connect 127.0.0.1:443 -servername ota.fanfh.cn -verify_hostname ota.fanfh.cn 2>/dev/null |
    openssl x509 -outform DER >"$BASELINE/certificate.der"
sha256sum "$BASELINE/certificate.der" | awk '{print $1}' >"$BASELINE/certificate.sha256"
openssl x509 -inform DER -in "$BASELINE/certificate.der" -pubkey -noout |
    openssl pkey -pubin -outform DER 2>/dev/null | sha256sum | awk '{print $1}' >"$BASELINE/certificate-spki.sha256"

[ -r "$NGINX_PID_FILE" ] || die "Nginx PID file is unavailable"
tr -d '[:space:]' <"$NGINX_PID_FILE" >"$BASELINE/nginx-master.pid"
for unit in superpan-license.service superpan-ota-admin.service; do
    printf '%s\t%s\t%s\n' "$unit" "$(systemctl is-enabled "$unit" 2>/dev/null || true)" "$(systemctl is-active "$unit" 2>/dev/null || true)"
done >"$BASELINE/unit-state.tsv"
ss -ltnp >"$BASELINE/listeners.txt"

curl --resolve 'ota.fanfh.cn:443:127.0.0.1' --noproxy '*' --fail --silent --show-error --max-time 30 "$HEALTH_URL" |
    sha256sum | awk '{print $1}' >"$BASELINE/ota-health.sha256"
curl --resolve 'ota.fanfh.cn:443:127.0.0.1' --noproxy '*' --fail --silent --show-error --max-time 120 "$STABLE_URL" |
    sha256sum | awk '{print $1}' >"$BASELINE/stable.sha256"
for url in https://f.fanfh.cn/ http://f.fanfh.cn/; do
    printf '%s\t%s\n' "$url" "$(curl --noproxy '*' --silent --show-error --max-time 20 --output /dev/null --write-out '%{http_code}' "$url" || printf unreachable)"
done >"$BASELINE/f.fanfh.cn-probes.tsv"

(cd "$BASELINE" && find . -maxdepth 1 -type f ! -name manifest.sha256 -printf '%P\0' | sort -z | xargs -0 sha256sum) >"$BASELINE/manifest.sha256"
chown -R root:root "$BASELINE"
find "$BASELINE" -type f -exec chmod 0600 {} +
chmod 0700 "$BASELINE"
printf 'LICENSE_ROUTE_BASELINE=%s\n' "$BASELINE"
