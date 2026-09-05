#!/usr/bin/env bash
set -euo pipefail

readonly VERIFY_ROOT="/root/superpan-ota-admin-verify"
readonly UNIT_NAME="superpan-ota-admin-probe.service"
readonly UNIT_SOURCE="$VERIFY_ROOT/$UNIT_NAME"
readonly UNIT_TARGET="/etc/systemd/system/$UNIT_NAME"
readonly BINARY="$VERIFY_ROOT/superpan-ota-admin-linux-amd64"
readonly BINARY_SHA_FILE="$VERIFY_ROOT/superpan-ota-admin-linux-amd64.sha256"
readonly SOURCE_BUNDLE="$VERIFY_ROOT/superpan-ota-admin-source.zip"
readonly SOURCE_BUNDLE_SHA_FILE="$VERIFY_ROOT/superpan-ota-admin-source.zip.sha256"
readonly SOURCE_ROOT="$VERIFY_ROOT/source"
readonly GO_VERSION="1.27.1"
readonly GO_ARCHIVE="$VERIFY_ROOT/go1.27.1.linux-amd64.tar.gz"
readonly GO_ARCHIVE_SHA256="63d339f0da5ab53635a56f2490a7984dfe12dfcff22ad749f63edaf590168445"
readonly GO_URL="https://go.dev/dl/go1.27.1.linux-amd64.tar.gz"
readonly GO_EXE="$VERIFY_ROOT/go/bin/go"
readonly HEALTH_URL="http://127.0.0.1:18091/admin/health"

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "This isolated compatibility probe must run as root." >&2
  exit 1
fi
if [[ ! -d "$VERIFY_ROOT" || -L "$VERIFY_ROOT" || "$(readlink -f "$VERIFY_ROOT")" != "$VERIFY_ROOT" ]]; then
  echo "Refusing to run outside the exact isolated verification directory." >&2
  exit 1
fi
if [[ ! -x "$BINARY" || ! -f "$BINARY_SHA_FILE" || ! -f "$SOURCE_BUNDLE" ||
      ! -f "$SOURCE_BUNDLE_SHA_FILE" || ! -f "$UNIT_SOURCE" ]]; then
  echo "An isolated probe binary, hash, source bundle, or unit file is missing." >&2
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

(cd "$VERIFY_ROOT" && sha256sum -c "$(basename "$BINARY_SHA_FILE")")
(cd "$VERIFY_ROOT" && sha256sum -c "$(basename "$SOURCE_BUNDLE_SHA_FILE")")

file_output="$(file -b "$BINARY")"
printf '%s\n' "$file_output"
grep -Eq 'ELF 64-bit.*x86-64.*statically linked.*stripped' <<<"$file_output"

curl --fail --location --silent --show-error "$GO_URL" --output "$GO_ARCHIVE"
printf '%s  %s\n' "$GO_ARCHIVE_SHA256" "$(basename "$GO_ARCHIVE")" |
  (cd "$VERIFY_ROOT" && sha256sum -c -)
tar -C "$VERIFY_ROOT" -xzf "$GO_ARCHIVE"
[[ "$($GO_EXE version)" == "go version go$GO_VERSION linux/amd64" ]]

mkdir -p -- "$SOURCE_ROOT"
unzip -q "$SOURCE_BUNDLE" -d "$SOURCE_ROOT"
env CGO_ENABLED=0 GOTOOLCHAIN=local "$GO_EXE" -C "$SOURCE_ROOT" test -shuffle=on -count=10 ./...

mkdir -p -- "$VERIFY_ROOT/oneshot"
probe_json="$($BINARY compat-probe --work-dir="$VERIFY_ROOT/oneshot")"
printf '%s\n' "$probe_json"
grep -q '"ok":true' <<<"$probe_json"

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
