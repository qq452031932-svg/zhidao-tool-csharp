#!/usr/bin/env bash
set -euo pipefail

readonly VERIFY_ROOT="/root/superpan-ota-admin-verify"
readonly UNIT_NAME="superpan-ota-admin-probe.service"
readonly UNIT_TARGET="/etc/systemd/system/$UNIT_NAME"
readonly BINARY="$VERIFY_ROOT/superpan-ota-admin-linux-amd64"
readonly BINARY_SHA_FILE="$VERIFY_ROOT/superpan-ota-admin-linux-amd64.sha256"
readonly SOURCE_BUNDLE="$VERIFY_ROOT/superpan-ota-admin-source.zip"
readonly SOURCE_BUNDLE_SHA_FILE="$VERIFY_ROOT/superpan-ota-admin-source.zip.sha256"
readonly SOURCE_ROOT="$VERIFY_ROOT/source"
readonly BUNDLED_RUNNER="$SOURCE_ROOT/deploy/probe/run-centos7-probe.sh"
readonly BUNDLED_UNIT="$SOURCE_ROOT/deploy/probe/superpan-ota-admin-probe.service"
readonly SCRIPT_PATH="$(readlink -f "${BASH_SOURCE[0]}")"
readonly OWNER_MARKER="$VERIFY_ROOT/.runner-owner"
readonly OWNER_TOKEN_FILE="$OWNER_MARKER/token"
readonly GO_VERSION="1.27.1"
readonly GO_ARCHIVE="$VERIFY_ROOT/go1.27.1.linux-amd64.tar.gz"
readonly GO_ARCHIVE_PARTIAL="$VERIFY_ROOT/.go1.27.1.linux-amd64.tar.gz.partial"
readonly GO_ARCHIVE_SHA256="63d339f0da5ab53635a56f2490a7984dfe12dfcff22ad749f63edaf590168445"
readonly GO_URL="https://go.dev/dl/go1.27.1.linux-amd64.tar.gz"
readonly GO_EXE="$VERIFY_ROOT/go/bin/go"
readonly HEALTH_URL="http://127.0.0.1:18091/admin/health"
readonly STATIC_STRIPPED_FILE_PATTERN='^ELF 64-bit.*x86-64.*statically linked.*, stripped(,|$)'

unit_installed=false
unit_started=false
owner_token=""

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "This isolated compatibility probe must run as root." >&2
  exit 1
fi
if [[ ! -d "$VERIFY_ROOT" || -L "$VERIFY_ROOT" || "$(readlink -f "$VERIFY_ROOT")" != "$VERIFY_ROOT" ]]; then
  echo "Refusing to run outside the exact isolated verification directory." >&2
  exit 1
fi
if [[ "$SCRIPT_PATH" != "$VERIFY_ROOT/run-centos7-probe.sh" ]]; then
  echo "The probe runner must execute from the exact isolated verification root." >&2
  exit 1
fi
if [[ ! -x "$BINARY" || ! -f "$BINARY_SHA_FILE" || ! -f "$SOURCE_BUNDLE" ||
      ! -f "$SOURCE_BUNDLE_SHA_FILE" ]]; then
  echo "An isolated probe binary, hash, or source bundle is missing." >&2
  exit 1
fi

global_unit_is_absent() {
  local load_state
  if [[ -e "$UNIT_TARGET" || -L "$UNIT_TARGET" ]]; then
    return 1
  fi
  load_state="$(systemctl show "$UNIT_NAME" --property=LoadState 2>/dev/null || true)"
  [[ "$load_state" == "LoadState=not-found" ]]
}

refuse_existing_global_unit() {
  if ! global_unit_is_absent; then
    echo "Refusing to replace an existing or loaded global probe unit." >&2
    return 1
  fi
}

ownership_marker_is_intact() {
  [[ -d "$OWNER_MARKER" && ! -L "$OWNER_MARKER" &&
     -f "$OWNER_TOKEN_FILE" && ! -L "$OWNER_TOKEN_FILE" &&
     "$(<"$OWNER_TOKEN_FILE")" == "$owner_token" ]]
}

refuse_existing_global_unit
if ! mkdir -- "$OWNER_MARKER"; then
  echo "Another probe invocation owns the verification root." >&2
  exit 1
fi
owner_token="$$:$(date +%s):$RANDOM"
if ! (umask 077 && printf '%s\n' "$owner_token" >"$OWNER_TOKEN_FILE"); then
  rmdir -- "$OWNER_MARKER" >/dev/null 2>&1 || true
  echo "Could not create the probe ownership token." >&2
  exit 1
fi

cleanup() {
  local status=$?
  local remove_root=true
  trap - EXIT INT TERM
  if ! ownership_marker_is_intact; then
    echo "Ownership marker changed; refusing destructive cleanup." >&2
    exit "$status"
  fi

  if [[ "$unit_installed" == true ]]; then
    if [[ -f "$UNIT_TARGET" && ! -L "$UNIT_TARGET" && -f "$BUNDLED_UNIT" &&
          ! -L "$BUNDLED_UNIT" ]] && cmp -s -- "$UNIT_TARGET" "$BUNDLED_UNIT"; then
      if [[ "$unit_started" == true ]] || systemctl is-active --quiet "$UNIT_NAME"; then
        systemctl stop "$UNIT_NAME" >/dev/null 2>&1 || true
        unit_started=false
      fi
      rm -f -- "$UNIT_TARGET"
      systemctl daemon-reload >/dev/null 2>&1 || true
    elif [[ -e "$UNIT_TARGET" || -L "$UNIT_TARGET" ]]; then
      echo "Global unit changed; preserving it and the verification root." >&2
      remove_root=false
    else
      if [[ "$unit_started" == true ]] || systemctl is-active --quiet "$UNIT_NAME"; then
        systemctl stop "$UNIT_NAME" >/dev/null 2>&1 || true
        unit_started=false
      fi
      systemctl daemon-reload >/dev/null 2>&1 || true
    fi
  elif ! global_unit_is_absent; then
    echo "An unowned global unit appeared; preserving it and the verification root." >&2
    remove_root=false
  fi

  if [[ "$remove_root" == true ]] && ownership_marker_is_intact; then
    rm -rf -- "$VERIFY_ROOT"
  fi
  exit "$status"
}
trap cleanup EXIT INT TERM

(cd "$VERIFY_ROOT" && sha256sum -c "$(basename "$BINARY_SHA_FILE")")
(cd "$VERIFY_ROOT" && sha256sum -c "$(basename "$SOURCE_BUNDLE_SHA_FILE")")

file_output="$(file -b "$BINARY")"
printf '%s\n' "$file_output"
if grep -Fq 'not stripped' <<<"$file_output"; then
  echo "Probe binary is not stripped." >&2
  exit 1
fi
grep -Eq "$STATIC_STRIPPED_FILE_PATTERN" <<<"$file_output"

verify_go_archive() {
  local archive_path="$1"
  printf '%s  %s\n' "$GO_ARCHIVE_SHA256" "$(basename "$archive_path")" |
    (cd "$VERIFY_ROOT" && sha256sum -c -)
}

if [[ -e "$GO_ARCHIVE" || -L "$GO_ARCHIVE" ]]; then
  if [[ ! -f "$GO_ARCHIVE" || -L "$GO_ARCHIVE" ]]; then
    echo "The preseeded Go archive is not a regular, non-symlink file." >&2
    exit 1
  fi
  if ! verify_go_archive "$GO_ARCHIVE"; then
    echo "The preseeded Go archive failed SHA-256 verification; refusing to overwrite it." >&2
    exit 1
  fi
else
  rm -f -- "$GO_ARCHIVE_PARTIAL"
  curl --fail --location --silent --show-error "$GO_URL" --output "$GO_ARCHIVE_PARTIAL"
  if ! verify_go_archive "$GO_ARCHIVE_PARTIAL"; then
    echo "The downloaded Go archive failed SHA-256 verification." >&2
    exit 1
  fi
  mv -- "$GO_ARCHIVE_PARTIAL" "$GO_ARCHIVE"
fi
tar -C "$VERIFY_ROOT" -xzf "$GO_ARCHIVE"
[[ "$($GO_EXE version)" == "go version go$GO_VERSION linux/amd64" ]]

mkdir -p -- "$SOURCE_ROOT"
unzip -q "$SOURCE_BUNDLE" -d "$SOURCE_ROOT"
if [[ ! -f "$BUNDLED_RUNNER" || -L "$BUNDLED_RUNNER" ||
      ! -f "$BUNDLED_UNIT" || -L "$BUNDLED_UNIT" ]]; then
  echo "Verified source bundle is missing canonical deployment files." >&2
  exit 1
fi
if ! cmp -s -- "$SCRIPT_PATH" "$BUNDLED_RUNNER"; then
  echo "Executing runner differs from the verified bundled runner." >&2
  exit 1
fi
env CGO_ENABLED=0 GOTOOLCHAIN=local "$GO_EXE" -C "$SOURCE_ROOT" test -shuffle=on -count=10 ./...

mkdir -p -- "$VERIFY_ROOT/oneshot"
probe_json="$($BINARY compat-probe --work-dir="$VERIFY_ROOT/oneshot")"
printf '%s\n' "$probe_json"
grep -q '"ok":true' <<<"$probe_json"
sha256sum "$BINARY"

refuse_existing_global_unit
if ! (umask 022 && set -o noclobber && cat "$BUNDLED_UNIT" >"$UNIT_TARGET"); then
  echo "Could not exclusively install the verified bundled unit." >&2
  exit 1
fi
unit_installed=true
chmod 0644 "$UNIT_TARGET"
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
unit_started=true
wait_for_health
systemctl is-active --quiet "$UNIT_NAME"

systemctl stop "$UNIT_NAME"
unit_started=false
if systemctl is-active --quiet "$UNIT_NAME"; then
  echo "Probe unit remained active after stop." >&2
  exit 1
fi

systemctl restart "$UNIT_NAME"
unit_started=true
wait_for_health
systemctl is-active --quiet "$UNIT_NAME"
systemctl status --no-pager "$UNIT_NAME"
journalctl --no-pager -u "$UNIT_NAME"
