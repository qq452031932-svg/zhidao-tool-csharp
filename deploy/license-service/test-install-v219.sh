#!/usr/bin/env bash
set -euo pipefail
if [ "$(id -u)" -ne 0 ]; then echo "test harness must run as root" >&2; exit 1; fi
: "${BUNDLE:?BUNDLE is required}"
[ -x "$BUNDLE/superpan-license" ]
[ -f "$BUNDLE/config.json" ]
grep -q '127.0.0.1:18092' "$BUNDLE/config.json"
echo "v219 fixture checks passed"
