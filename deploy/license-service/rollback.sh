#!/usr/bin/env bash
set -euo pipefail
if [ "$(id -u)" -ne 0 ]; then echo "rollback.sh must run as root" >&2; exit 1; fi
: "${BACKUP_ROOT:?BACKUP_ROOT is required}"
[ -d "$BACKUP_ROOT" ] || { echo "backup root missing" >&2; exit 1; }
systemctl stop superpan-license.service || true
if [ -f "$BACKUP_ROOT/superpan-license" ]; then install -m 0755 "$BACKUP_ROOT/superpan-license" /opt/superpan-license/superpan-license; fi
if [ -f "$BACKUP_ROOT/config.json" ]; then install -m 0640 "$BACKUP_ROOT/config.json" /etc/superpan-license/config.json; fi
systemctl daemon-reload
systemctl start superpan-license.service
echo "license service rollback complete"
