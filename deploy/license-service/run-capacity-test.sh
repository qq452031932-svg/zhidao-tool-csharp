#!/usr/bin/env bash
set -euo pipefail
: "${LOADTEST_BIN:?LOADTEST_BIN is required}"
: "${DBCHECK_BIN:?DBCHECK_BIN is required}"
: "${ORIGIN:=http://127.0.0.1:18093}"
case "$ORIGIN" in http://127.0.0.1:18093) ;; *) echo "origin must be disposable loopback" >&2; exit 1;; esac
"$LOADTEST_BIN" --origin "$ORIGIN"
"$DBCHECK_BIN" --database "${DATABASE:?DATABASE is required}" --root "${DISPOSABLE_ROOT:?DISPOSABLE_ROOT is required}"
