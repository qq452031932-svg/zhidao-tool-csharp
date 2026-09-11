#!/usr/bin/env bash
set -euo pipefail
if [ "$(id -u)" -ne 0 ]; then echo "install.sh must run as root" >&2; exit 1; fi
: "${BUNDLE:?BUNDLE is required}"
DEST=/opt/superpan-license; ETC=/etc/superpan-license; DATA=/var/lib/superpan-license
mkdir -p "$DEST" "$ETC" "$DATA"
chown -R superpan-license:superpan-license "$DEST" "$DATA" 2>/dev/null || true
install -m 0755 "$BUNDLE/superpan-license" "$DEST/superpan-license"
install -m 0640 "$BUNDLE/config.json" "$ETC/config.json"
systemctl daemon-reload
systemctl enable --now superpan-license.service
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:18092/internal/health >/dev/null
echo "license service installed on 127.0.0.1:18092"
