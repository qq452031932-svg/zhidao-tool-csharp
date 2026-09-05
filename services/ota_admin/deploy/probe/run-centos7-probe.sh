#!/usr/bin/env bash
set -euo pipefail

readonly VERIFY_ROOT="/root/superpan-ota-admin-verify"
readonly UNIT_NAME="superpan-ota-admin-probe.service"
readonly UNIT_SOURCE="$VERIFY_ROOT/$UNIT_NAME"
readonly UNIT_TARGET="/etc/systemd/system/$UNIT_NAME"
readonly BINARY="$VERIFY_ROOT/superpan-ota-admin-linux-amd64"
readonly HEALTH_URL="http://127.0.0.1:18091/admin/health"

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "This isolated compatibility probe must run as root." >&2
  exit 1
fi
if [[ ! -d "$VERIFY_ROOT" || -L "$VERIFY_ROOT" || "$(readlink -f "$VERIFY_ROOT")" != "$VERIFY_ROOT" ]]; then
  echo "Refusing to run outside the exact isolated verification directory." >&2
  exit 1
fi
if [[ ! -x "$BINARY" || ! -f "$UNIT_SOURCE" ]]; then
  echo "The isolated probe binary or unit file is missing." >&2
  exit 1
fi

cleanup() {
  local status=$?
  systemctl disable --now "$UNIT_NAME" >/dev/null 2>&1 || true
  rm -f -- "$UNIT_TARGET"
  systemctl daemon-reload >/dev/null 2>&1 || true
  if [[ -d "$VERIFY_ROOT" && ! -L "$VERIFY_ROOT" && "$(readlink -f "$VERIFY_ROOT")" == "$VERIFY_ROOT" ]]; then
    rm -rf -- "$VERIFY_ROOT"
  fi
  exit "$status"
}
trap cleanup EXIT INT TERM

mkdir -p -- "$VERIFY_ROOT/oneshot"
probe_json="$($BINARY compat-probe --work-dir="$VERIFY_ROOT/oneshot")"
printf '%s\n' "$probe_json"
grep -q '"ok":true' <<<"$probe_json"

file "$BINARY"
file "$BINARY" | grep -Eq 'ELF 64-bit.*x86-64'
if ldd_output="$(ldd "$BINARY" 2>&1)"; then
  grep -Eq 'not a dynamic executable|statically linked' <<<"$ldd_output"
else
  grep -Eq 'not a dynamic executable|statically linked' <<<"$ldd_output"
fi
sha256sum "$BINARY"

install -m 0644 -- "$UNIT_SOURCE" "$UNIT_TARGET"
systemctl daemon-reload

wait_for_health() {
  local attempt response
  for attempt in {1..40}; do
    if response="$(curl --fail --silent --show-error "$HEALTH_URL")"; then
      printf '%s\n' "$response"
      grep -q '"ok":true' <<<"$response"
      return 0
    fi
    sleep 0.25
  done
  return 1
}

systemctl start "$UNIT_NAME"
wait_for_health
systemctl is-active --quiet "$UNIT_NAME"

systemctl stop "$UNIT_NAME"
if systemctl is-active --quiet "$UNIT_NAME"; then
  echo "Probe unit remained active after stop." >&2
  exit 1
fi

systemctl restart "$UNIT_NAME"
wait_for_health
systemctl is-active --quiet "$UNIT_NAME"
systemctl status --no-pager "$UNIT_NAME"
journalctl --no-pager -u "$UNIT_NAME"
