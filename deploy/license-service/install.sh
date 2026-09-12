#!/usr/bin/env bash
set -euo pipefail
umask 077

BUNDLE=""
SOURCE_COMMIT=""
OTA_BINARY_SHA256=""
LICENSE_BINARY_SHA256=""
SOURCE_MANIFEST_SHA256=""
LICENSE_CONFIG_SOURCE=""
LICENSE_CONFIG_SHA256=""
LICENSE_UNIT_SOURCE=""
LICENSE_UNIT_SHA256=""
OTA_CONFIG_SOURCE=""
OTA_CONFIG_SHA256=""
OTA_UNIT_SOURCE=""
OTA_UNIT_SHA256=""
PREFLIGHT_ONLY=false
declare -A SEEN_ARGS=()
SOURCE_COMMIT_TARGET="/opt/superpan-license/source-commit.txt"
OTA_INSTALLER="$(cd "$(dirname "${BASH_SOURCE[0]}")/../ota-admin" && pwd)/install.sh"

usage() {
    cat >&2 <<'USAGE'
Usage: install.sh --bundle ABSOLUTE_PATH --source-commit 40_LOWERCASE_HEX \
  --ota-binary-sha256 HEX --license-binary-sha256 HEX \
  --source-manifest-sha256 HEX \
  --license-config ABSOLUTE_PATH --license-config-sha256 HEX \
  --license-unit ABSOLUTE_PATH --license-unit-sha256 HEX \
  --ota-config ABSOLUTE_PATH --ota-config-sha256 HEX \
  --ota-unit ABSOLUTE_PATH --ota-unit-sha256 HEX [--preflight-only]
USAGE
}

die() {
    echo "ERROR: $*" >&2
    exit 1
}

require_value() {
    [ "$#" -eq 2 ] && [ -n "$2" ] || die "missing value for $1"
}

mark_seen() {
    local option="$1"
    if [ "${SEEN_ARGS[$option]+present}" = "present" ]; then
        usage
        die "option must be provided exactly once: $option"
    fi
    SEEN_ARGS["$option"]=1
}

require_seen() {
    local option="$1"
    if [ "${SEEN_ARGS[$option]+present}" != "present" ]; then
        usage
        die "missing required option: $option"
    fi
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --bundle) mark_seen "$1"; require_value "$1" "${2:-}"; BUNDLE="$2"; shift 2 ;;
        --source-commit) mark_seen "$1"; require_value "$1" "${2:-}"; SOURCE_COMMIT="$2"; shift 2 ;;
        --ota-binary-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_BINARY_SHA256="$2"; shift 2 ;;
        --license-binary-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_BINARY_SHA256="$2"; shift 2 ;;
        --source-manifest-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; SOURCE_MANIFEST_SHA256="$2"; shift 2 ;;
        --license-config) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_CONFIG_SOURCE="$2"; shift 2 ;;
        --license-config-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_CONFIG_SHA256="$2"; shift 2 ;;
        --license-unit) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_UNIT_SOURCE="$2"; shift 2 ;;
        --license-unit-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; LICENSE_UNIT_SHA256="$2"; shift 2 ;;
        --ota-config) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_CONFIG_SOURCE="$2"; shift 2 ;;
        --ota-config-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_CONFIG_SHA256="$2"; shift 2 ;;
        --ota-unit) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_UNIT_SOURCE="$2"; shift 2 ;;
        --ota-unit-sha256) mark_seen "$1"; require_value "$1" "${2:-}"; OTA_UNIT_SHA256="$2"; shift 2 ;;
        --preflight-only) mark_seen "$1"; PREFLIGHT_ONLY=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage; die "unknown argument: $1" ;;
    esac
done

for required in --bundle --source-commit --ota-binary-sha256 --license-binary-sha256 \
    --source-manifest-sha256 --license-config --license-config-sha256 \
    --license-unit --license-unit-sha256 --ota-config --ota-config-sha256 \
    --ota-unit --ota-unit-sha256; do
    require_seen "$required"
done
[ "$(id -u)" -eq 0 ] || die "install.sh must run as root"
case "$BUNDLE" in /*) ;; *) die "bundle path must be absolute and canonical" ;; esac
BUNDLE_CANONICAL="$(readlink -f -- "$BUNDLE")" || die "bundle path cannot be canonicalized"
[ "$BUNDLE_CANONICAL" = "$BUNDLE" ] || die "bundle path must be absolute and canonical"
[ -d "$BUNDLE" ] && [ ! -L "$BUNDLE" ] || die "bundle must be a real directory"
[ "$(stat -c '%U:%G' "$BUNDLE")" = "root:root" ] || die "bundle owner must be root:root"
BUNDLE_MODE="$(stat -c '%a' "$BUNDLE")"
[ "$BUNDLE_MODE" = "700" ] || die "bundle directory mode must be exactly 0700"

for hash in "$OTA_BINARY_SHA256" "$LICENSE_BINARY_SHA256" "$SOURCE_MANIFEST_SHA256" \
    "$LICENSE_CONFIG_SHA256" "$LICENSE_UNIT_SHA256" "$OTA_CONFIG_SHA256" "$OTA_UNIT_SHA256"; do
    [[ "$hash" =~ ^[0-9a-f]{64}$ ]] || die "every expected SHA-256 must be 64 lowercase hexadecimal characters"
done
[[ "$SOURCE_COMMIT" =~ ^[0-9a-f]{40}$ ]] || die "source commit must be exactly 40 lowercase hexadecimal characters"

EXPECTED_BUNDLE_NAMES=$'ota-admin\nota-admin.sha256\nsource-commit.txt\nsource-manifest.sha256\nsource-manifest.sha256.sha256\nsuperpan-license\nsuperpan-license.sha256'
ACTUAL_BUNDLE_NAMES="$(find "$BUNDLE" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)"
[ "$ACTUAL_BUNDLE_NAMES" = "$EXPECTED_BUNDLE_NAMES" ] || die "bundle is incomplete or contains unexpected files"
[ -z "$(find "$BUNDLE" -mindepth 1 -maxdepth 1 -type l -print -quit)" ] || die "bundle contains a symlink"
[ -z "$(find "$BUNDLE" -mindepth 1 -maxdepth 1 ! -type f -print -quit)" ] || die "bundle entries must be regular files"
[ -z "$(find "$BUNDLE" -mindepth 1 -maxdepth 1 -type f -links +1 -print -quit)" ] || die "bundle contains a hard-linked file"
[ -z "$(find "$BUNDLE" -mindepth 1 -maxdepth 1 -type f -perm /022 -print -quit)" ] || die "bundle files must not be group/world writable"

verify_sha256() {
    local source="$1" expected="$2" actual
    [ -f "$source" ] && [ ! -L "$source" ] || die "trusted input must be a regular non-symlink file: $source"
    [ "$(stat -c '%h' "$source")" = "1" ] || die "trusted input must have exactly one link: $source"
    actual="$(sha256sum -- "$source" | awk '{print $1}')"
    [ "$actual" = "$expected" ] || die "SHA-256 mismatch for $source"
}

verify_sidecar() {
    local payload="$1" sidecar="$2" expected="$3" name
    name="$(basename "$payload")"
    printf '%s  %s\n' "$expected" "$name" | cmp -s - "$sidecar" ||
        die "sidecar does not match the explicit expected SHA-256 as one canonical line: $sidecar"
}

for source in "$LICENSE_CONFIG_SOURCE" "$LICENSE_UNIT_SOURCE" "$OTA_CONFIG_SOURCE" "$OTA_UNIT_SOURCE"; do
    case "$source" in /*) ;; *) die "trusted template paths must be absolute" ;; esac
    [ "$(readlink -f -- "$source")" = "$source" ] || die "trusted template paths must be canonical"
    [ "$(stat -c '%U:%G' "$source")" = "root:root" ] || die "trusted templates must be owned by root:root"
    mode="$(stat -c '%a' "$source")"
    (( (8#$mode & 8#022) == 0 )) || die "trusted templates must not be group/world writable"
done

verify_sidecar "$BUNDLE/ota-admin" "$BUNDLE/ota-admin.sha256" "$OTA_BINARY_SHA256"
verify_sidecar "$BUNDLE/superpan-license" "$BUNDLE/superpan-license.sha256" "$LICENSE_BINARY_SHA256"
verify_sidecar "$BUNDLE/source-manifest.sha256" "$BUNDLE/source-manifest.sha256.sha256" "$SOURCE_MANIFEST_SHA256"
verify_sha256 "$BUNDLE/ota-admin" "$OTA_BINARY_SHA256"
verify_sha256 "$BUNDLE/superpan-license" "$LICENSE_BINARY_SHA256"
verify_sha256 "$BUNDLE/source-manifest.sha256" "$SOURCE_MANIFEST_SHA256"
verify_sha256 "$LICENSE_CONFIG_SOURCE" "$LICENSE_CONFIG_SHA256"
verify_sha256 "$LICENSE_UNIT_SOURCE" "$LICENSE_UNIT_SHA256"
verify_sha256 "$OTA_CONFIG_SOURCE" "$OTA_CONFIG_SHA256"
verify_sha256 "$OTA_UNIT_SOURCE" "$OTA_UNIT_SHA256"

[ "$(wc -l <"$BUNDLE/source-commit.txt")" -eq 1 ] || die "source-commit.txt must contain one canonical line"
BUNDLE_SOURCE_COMMIT="$(cat "$BUNDLE/source-commit.txt")"
[[ "$BUNDLE_SOURCE_COMMIT" =~ ^[0-9a-f]{40}$ ]] || die "source-commit.txt is malformed"
[ "$BUNDLE_SOURCE_COMMIT" = "$SOURCE_COMMIT" ] || die "source-commit.txt does not match --source-commit"
printf '%s\n' "$SOURCE_COMMIT" | cmp -s - "$BUNDLE/source-commit.txt" || die "source-commit.txt is not canonical"

[ -f "$OTA_INSTALLER" ] && [ ! -L "$OTA_INSTALLER" ] || die "OTA installer is missing or unsafe"
bash "$OTA_INSTALLER" --loopback-license-transaction \
    --preflight-only \
    --binary "$BUNDLE/ota-admin" --binary-sha256 "$OTA_BINARY_SHA256" \
    --config "$OTA_CONFIG_SOURCE" --config-sha256 "$OTA_CONFIG_SHA256" \
    --unit "$OTA_UNIT_SOURCE" --unit-sha256 "$OTA_UNIT_SHA256" \
    --source-commit "$SOURCE_COMMIT"

# LICENSE_INSTALL_PREFLIGHT_COMPLETE: every input byte and source record is authenticated.
if [ "$PREFLIGHT_ONLY" = true ]; then
    echo "license installation preflight complete for source commit $SOURCE_COMMIT"
    exit 0
fi

DEST=/opt/superpan-license
ETC=/etc/superpan-license
DATA=/var/lib/superpan-license
if ! getent group superpan-license >/dev/null; then groupadd --system superpan-license; fi
if ! id -u superpan-license >/dev/null 2>&1; then
    useradd --system --gid superpan-license --home-dir "$DATA" --shell /sbin/nologin superpan-license
fi
BACKUP_DIR=/var/backups/superpan-license
install -d -m 0755 -o root -g root "$DEST"
install -d -m 0750 -o root -g superpan-license "$ETC"
install -d -m 0700 -o superpan-license -g superpan-license "$DATA"
install -m 0755 -o root -g root "$BUNDLE/superpan-license" "$DEST/superpan-license"
install -m 0640 -o root -g superpan-license "$LICENSE_CONFIG_SOURCE" "$ETC/config.json"
install -m 0644 -o root -g root "$LICENSE_UNIT_SOURCE" /etc/systemd/system/superpan-license.service
install -m 0644 -o root -g root "$BUNDLE/source-commit.txt" "$SOURCE_COMMIT_TARGET"
systemctl daemon-reload
systemctl enable --now superpan-license.service
curl --fail --silent --show-error --max-time 5 http://127.0.0.1:18092/internal/health >/dev/null
echo "license service installed on 127.0.0.1:18092"
