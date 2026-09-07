#!/usr/bin/env bash
set -euo pipefail
umask 077

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
readonly UNIT_REMOVAL_PATH="$VERIFY_ROOT/.unit-removal"
readonly GO_VERSION="1.27.1"
readonly GO_ARCHIVE="$VERIFY_ROOT/go1.27.1.linux-amd64.tar.gz"
readonly GO_ARCHIVE_PARTIAL="$VERIFY_ROOT/.go1.27.1.linux-amd64.tar.gz.partial"
readonly GO_ARCHIVE_SHA256="63d339f0da5ab53635a56f2490a7984dfe12dfcff22ad749f63edaf590168445"
readonly GO_URL="https://go.dev/dl/go1.27.1.linux-amd64.tar.gz"
readonly GO_ROOT="$VERIFY_ROOT/go"
readonly GO_EXE="$GO_ROOT/bin/go"
readonly GO_CACHE="$VERIFY_ROOT/go-build-cache"
readonly GO_MOD_CACHE="$VERIFY_ROOT/go-mod-cache"
readonly HEALTH_URL="http://127.0.0.1:18091/admin/health"
readonly STATIC_STRIPPED_FILE_PATTERN='^ELF 64-bit.*x86-64.*statically linked.*, stripped(,|$)'

unit_installed=false
unit_started=false
owner_token=""
cleanup_failed=false

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
if [[ -e "$GO_ROOT" || -L "$GO_ROOT" ]]; then
  echo "Refusing a pre-existing Go extraction target." >&2
  exit 1
fi
if [[ -e "$SOURCE_ROOT" || -L "$SOURCE_ROOT" ]]; then
  echo "Refusing a pre-existing source extraction target." >&2
  exit 1
fi
if [[ -e "$GO_CACHE" || -L "$GO_CACHE" || -e "$GO_MOD_CACHE" || -L "$GO_MOD_CACHE" ]]; then
  echo "Refusing a pre-existing Go cache target." >&2
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
  local unit_stopped=false
  trap - EXIT INT TERM
  if ! ownership_marker_is_intact; then
    echo "Ownership marker changed; refusing destructive cleanup." >&2
    cleanup_failed=true
    remove_root=false
  fi

  if [[ "$unit_installed" == true ]] && [[ "$cleanup_failed" == false ]]; then
    if [[ -f "$UNIT_TARGET" && ! -L "$UNIT_TARGET" && -f "$BUNDLED_UNIT" &&
          ! -L "$BUNDLED_UNIT" ]] && cmp -s -- "$UNIT_TARGET" "$BUNDLED_UNIT"; then
      if [[ "$unit_started" == true ]] || systemctl is-active --quiet "$UNIT_NAME"; then
        if systemctl stop "$UNIT_NAME"; then
          if systemctl is-active --quiet "$UNIT_NAME"; then
            echo "Owned probe unit remained active after cleanup stop." >&2
            cleanup_failed=true
            remove_root=false
          else
            unit_started=false
            unit_stopped=true
          fi
        else
          echo "Could not stop owned probe unit; preserving unit and verification root." >&2
          cleanup_failed=true
          remove_root=false
        fi
      else
        unit_stopped=true
      fi
      if [[ "$unit_stopped" == true ]]; then
        if [[ -e "$UNIT_REMOVAL_PATH" || -L "$UNIT_REMOVAL_PATH" ]]; then
          echo "Unit removal holding path already exists; preserving unit and verification root." >&2
          cleanup_failed=true
          remove_root=false
        elif mv -- "$UNIT_TARGET" "$UNIT_REMOVAL_PATH"; then
          if systemctl daemon-reload; then
            if ! rm -f -- "$UNIT_REMOVAL_PATH"; then
              echo "Could not remove owned unit holding file; preserving verification root." >&2
              cleanup_failed=true
              remove_root=false
            fi
          else
            echo "Could not reload systemd after removing owned unit; restoring and preserving it." >&2
            if ! mv -- "$UNIT_REMOVAL_PATH" "$UNIT_TARGET"; then
              echo "Could not restore owned unit; holding file preserved at $UNIT_REMOVAL_PATH." >&2
            fi
            cleanup_failed=true
            remove_root=false
          fi
        else
          echo "Could not move owned unit for safe removal; preserving it and the verification root." >&2
          cleanup_failed=true
          remove_root=false
        fi
      fi
    elif [[ -e "$UNIT_TARGET" || -L "$UNIT_TARGET" ]]; then
      echo "Global unit changed; preserving it and the verification root." >&2
      cleanup_failed=true
      remove_root=false
    else
      if [[ "$unit_started" == true ]] || systemctl is-active --quiet "$UNIT_NAME"; then
        if ! systemctl stop "$UNIT_NAME"; then
          echo "Could not stop owned loaded unit; preserving verification root." >&2
          cleanup_failed=true
          remove_root=false
        elif systemctl is-active --quiet "$UNIT_NAME"; then
          echo "Owned loaded unit remained active after cleanup stop." >&2
          cleanup_failed=true
          remove_root=false
        else
          unit_started=false
        fi
      fi
      if [[ "$cleanup_failed" == false ]] && ! systemctl daemon-reload; then
        echo "Could not reload systemd during owned cleanup; preserving verification root." >&2
        cleanup_failed=true
        remove_root=false
      fi
    fi
  elif [[ "$cleanup_failed" == false ]] && ! global_unit_is_absent; then
    echo "An unowned global unit appeared; preserving it and the verification root." >&2
    cleanup_failed=true
    remove_root=false
  fi

  if [[ "$remove_root" == true ]]; then
    if ownership_marker_is_intact; then
      rm -rf -- "$VERIFY_ROOT"
    else
      echo "Ownership marker changed during cleanup; preserving verification root." >&2
      cleanup_failed=true
    fi
  fi
  if [[ "$cleanup_failed" == true && "$status" -eq 0 ]]; then
    status=1
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
mkdir -- "$GO_ROOT"
tar -C "$GO_ROOT" --strip-components=1 -xzf "$GO_ARCHIVE"
mkdir -- "$GO_CACHE" "$GO_MOD_CACHE"
[[ "$(env GOCACHE="$GO_CACHE" GOMODCACHE="$GO_MOD_CACHE" "$GO_EXE" version)" == "go version go$GO_VERSION linux/amd64" ]]

mkdir -- "$SOURCE_ROOT"
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
env CGO_ENABLED=0 GOTOOLCHAIN=local GOCACHE="$GO_CACHE" GOMODCACHE="$GO_MOD_CACHE" \
  "$GO_EXE" -C "$SOURCE_ROOT" test -shuffle=on -count=10 ./...

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
