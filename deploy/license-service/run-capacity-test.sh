#!/usr/bin/env bash
set -euo pipefail

die(){ printf 'ERROR: %s\n' "$*" >&2; exit 1; }
BUNDLE= TOOLS= MANIFEST=
while [ "$#" -gt 0 ]; do
  case "$1" in
    --bundle) [ "$#" -ge 2 ] || die 'missing --bundle value'; BUNDLE=$2; shift 2 ;;
    --tools) [ "$#" -ge 2 ] || die 'missing --tools value'; TOOLS=$2; shift 2 ;;
    --manifest) [ "$#" -ge 2 ] || die 'missing --manifest value'; MANIFEST=$2; shift 2 ;;
    *) die 'unexpected argument' ;;
  esac
done
[ "$(id -u)" = 0 ] || die 'capacity orchestration requires root assertions'
ROOT=/var/lib/license-capacity-18093
CONFIG_ROOT=/etc/superpan-license-capacity-18093
DATABASE=$ROOT/state.db
CONFIG=$CONFIG_ROOT/config.json
SECRET_FILE=$CONFIG_ROOT/load-test.secret
EXPECTED_COUNTS=$ROOT/expected-counts.json
PRODUCTION_CONFIG=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/config.example.json
STOPPED_MARKER=$ROOT/instance.stopped
SAMPLES=$ROOT/process.samples
PUBLIC_INCLUDE=/etc/nginx/conf.d/ota.fanfh.cn.license-api.include
UNIT=superpan-license-loadtest.service
ORIGIN=http://127.0.0.1:18093

canonical_file(){ [ -f "$1" ] && [ ! -L "$1" ] && [ "$(readlink -f -- "$1")" = "$1" ] && [ "$(stat -c '%h' "$1")" = 1 ] || die 'untrusted input file'; }
canonical_dir(){ [ -d "$1" ] && [ ! -L "$1" ] && [ "$(readlink -f -- "$1")" = "$1" ] || die 'untrusted input directory'; }
read_systemd_property(){
  local property=$1 unit=$2 output
  output=$(systemctl show --property="$property" "$unit")
  [ "$(printf '%s\n' "$output" | wc -l)" -eq 1 ] || return 1
  case "$output" in "$property="*) printf '%s\n' "${output#*=}" ;; *) return 1 ;; esac
}
assert_public_absent(){ [ ! -e "$PUBLIC_INCLUDE" ] || die 'public include must remain absent'; }
assert_public_absent
canonical_dir "$BUNDLE"; canonical_dir "$TOOLS"; canonical_dir "$ROOT"; canonical_dir "$CONFIG_ROOT"
[ "$(stat -c '%U:%G:%a' "$ROOT")" = 'superpan-license:superpan-license:700' ] || die 'disposable root metadata mismatch'
[ "$(stat -c '%U:%G:%a' "$CONFIG_ROOT")" = 'root:superpan-license-admin:710' ] || die 'capacity config root metadata mismatch'
for file in "$BUNDLE/superpan-license" "$BUNDLE/superpan-license.sha256" "$BUNDLE/source-commit.txt" "$TOOLS/license-loadtest" "$TOOLS/license-loadtest.sha256" "$TOOLS/license-dbcheck" "$TOOLS/license-dbcheck.sha256" "$TOOLS/source-commit.txt" "$MANIFEST" "$SECRET_FILE" "$EXPECTED_COUNTS" "$CONFIG" "$CONFIG_ROOT/encryption.key" "$CONFIG_ROOT/lookup.key" "$CONFIG_ROOT/response-signing.key" "$CONFIG_ROOT/admin.secret" "$CONFIG_ROOT/response-signing.key-id" "$CONFIG_ROOT/response-signing.spki.der" "$CONFIG_ROOT/response-signing.spki.base64" "$CONFIG_ROOT/response-signing.spki.sha256" "$PRODUCTION_CONFIG"; do canonical_file "$file"; done
for binary in "$BUNDLE/superpan-license" "$TOOLS/license-loadtest" "$TOOLS/license-dbcheck"; do [ "$(stat -c '%U:%G:%a' "$binary")" = 'root:root:755' ] || die 'binary metadata mismatch'; done
[ "$(stat -c '%U:%G:%a' "$MANIFEST")" = 'superpan-license:superpan-license:600' ] || die 'manifest must be service-private'
[ "$(stat -c '%U:%G:%a:%s' "$SECRET_FILE")" = 'root:superpan-license:640:32' ] || die 'load credential metadata mismatch'
for file in "$CONFIG" "$CONFIG_ROOT/encryption.key" "$CONFIG_ROOT/lookup.key" "$CONFIG_ROOT/response-signing.key"; do [ "$(stat -c '%U:%G:%a' "$file")" = 'root:superpan-license:640' ] || die 'license-only capacity config metadata mismatch'; done
[ "$(stat -c '%U:%G:%a' "$CONFIG_ROOT/admin.secret")" = 'root:superpan-license-admin:640' ] || die 'shared capacity secret metadata mismatch'
for file in "$CONFIG_ROOT/response-signing.key-id" "$CONFIG_ROOT/response-signing.spki.der" "$CONFIG_ROOT/response-signing.spki.base64" "$CONFIG_ROOT/response-signing.spki.sha256"; do [ "$(stat -c '%U:%G:%a' "$file")" = 'root:root:644' ] || die 'public capacity identity metadata mismatch'; done
jq -e '.seed==20260910 and .rejection_code=="license_not_found" and .signing_spki_path=="/etc/superpan-license-capacity-18093/response-signing.spki.der" and .signing_key_id_path=="/etc/superpan-license-capacity-18093/response-signing.key-id"' "$MANIFEST" >/dev/null || die 'seeded manifest trust contract mismatch'
BUNDLE_COMMIT=$(tr -d '\n' <"$BUNDLE/source-commit.txt"); TOOLS_COMMIT=$(tr -d '\n' <"$TOOLS/source-commit.txt")
[[ "$BUNDLE_COMMIT" =~ ^[0-9a-f]{40}$ ]] || die 'invalid bundle commit'; [ "$TOOLS_COMMIT" = "$BUNDLE_COMMIT" ] || die 'tool and bundle commits differ'
(cd "$BUNDLE" && sha256sum -c superpan-license.sha256 >/dev/null); (cd "$TOOLS" && sha256sum -c license-loadtest.sha256 >/dev/null && sha256sum -c license-dbcheck.sha256 >/dev/null)
EXPECTED_SERVICE_HASH=$(cut -d' ' -f1 "$BUNDLE/superpan-license.sha256"); [ "$(sha256sum /opt/superpan-license/superpan-license | cut -d' ' -f1)" = "$EXPECTED_SERVICE_HASH" ] || die 'installed binary hash mismatch'
jq -e '.listen_address=="127.0.0.1:18093" and .license_database_path=="/var/lib/license-capacity-18093/state.db" and .load_test_enabled==true and .load_test_secret_path=="/etc/superpan-license-capacity-18093/load-test.secret" and .public_api_base_path=="/license-api/v1"' "$CONFIG" >/dev/null || die 'test-only load configuration missing'
[ "$(jq -S '{public_api_base_path,limits}' "$CONFIG")" = "$(jq -S '{public_api_base_path,limits}' "$PRODUCTION_CONFIG")" ] || die 'capacity configuration differs from production behavior'

SAMPLER_PID= MAIN_PID= START_ATTEMPTED=false
cleanup_failure(){
  status=$?
  if [ -n "$SAMPLER_PID" ] && kill -0 "$SAMPLER_PID" 2>/dev/null; then kill -TERM "$SAMPLER_PID"; if ! wait "$SAMPLER_PID"; then status=1; fi; fi
  if [ "$START_ATTEMPTED" = true ] && ! systemctl stop "$UNIT"; then status=1; fi
  assert_public_absent
  exit "$status"
}
trap cleanup_failure EXIT INT TERM HUP
START_ATTEMPTED=true
systemctl start "$UNIT"
MAIN_PID=$(read_systemd_property MainPID "$UNIT"); [[ "$MAIN_PID" =~ ^[1-9][0-9]*$ ]] || die 'invalid MainPID'
[ "$(readlink -f -- "/proc/$MAIN_PID/exe")" = /opt/superpan-license/superpan-license ] || die 'MainPID executable mismatch'
[ "$(sha256sum "/proc/$MAIN_PID/exe" | cut -d' ' -f1)" = "$EXPECTED_SERVICE_HASH" ] || die 'MainPID binary hash mismatch'
[ "$(read_systemd_property User "$UNIT")" = superpan-license ] || die 'unit user mismatch'
[ "$(read_systemd_property Group "$UNIT")" = superpan-license ] || die 'unit group mismatch'
[ "$(read_systemd_property SupplementaryGroups "$UNIT")" = superpan-license-admin ] || die 'unit supplementary groups mismatch'
[ "$(read_systemd_property UMask "$UNIT")" = 0077 ] || die 'unit umask mismatch'
[ "$(read_systemd_property LimitNOFILE "$UNIT")" = 4096 ] || die 'unit limit mismatch'
EXPECTED_UID=$(id -u superpan-license); EXPECTED_GID=$(id -g superpan-license); ADMIN_GID=$(getent group superpan-license-admin | cut -d: -f3)
awk -v want="$EXPECTED_UID" '$1=="Uid:"{found=1;ok=($2==want&&$3==want&&$4==want&&$5==want)} END{exit !(found&&ok)}' "/proc/$MAIN_PID/status" || die 'runtime Uid mismatch'
awk -v want="$EXPECTED_GID" '$1=="Gid:"{found=1;ok=($2==want&&$3==want&&$4==want&&$5==want)} END{exit !(found&&ok)}' "/proc/$MAIN_PID/status" || die 'runtime Gid mismatch'
[ "$(awk '$1=="Umask:"{print $2}' "/proc/$MAIN_PID/status")" = 0077 ] || die 'runtime Umask mismatch'
[ "$(awk '$1=="Max"&&$2=="open"&&$3=="files"{print $4":"$5}' "/proc/$MAIN_PID/limits")" = 4096:4096 ] || die 'runtime NOFILE mismatch'
ACTUAL_GROUPS=$(sed -n 's/^Groups:[[:space:]]*//p' "/proc/$MAIN_PID/status" | tr ' ' '\n' | sed '/^$/d' | sort -n -u | tr '\n' ' '); EXPECTED_GROUPS=$(printf '%s\n%s\n' "$EXPECTED_GID" "$ADMIN_GID" | sort -n -u | tr '\n' ' ')
[ "$ACTUAL_GROUPS" = "$EXPECTED_GROUPS" ] || die 'runtime groups mismatch'
for attempt in $(seq 1 30); do curl --noproxy '*' --fail --silent --max-time 2 "$ORIGIN/internal/health" >/dev/null && break; [ "$attempt" = 30 ] && die 'health unavailable'; sleep 1; done

# Ordinary limiting is proven with the bypass header disabled (absent).
RATE_LIMIT_SEEN=false
for request in $(seq 1 200); do code=$(curl --noproxy '*' --silent --output /dev/null --write-out '%{http_code}' --max-time 2 -H 'Content-Type: application/json' --data '{}' "$ORIGIN/license-api/v1/activate"); [ "$code" = 429 ] && RATE_LIMIT_SEEN=true && break; done
[ "$RATE_LIMIT_SEEN" = true ] || die 'ordinary public rate limiting was not observed'
warm_up(){
  local rate=$1 expected=$2 report=
  exec 3<"$SECRET_FILE"
  if ! report="$(runuser -u superpan-license -- "$TOOLS/license-loadtest" --origin "$ORIGIN" --rate "$rate" --duration-seconds 600 --manifest "$MANIFEST" --load-secret-fd 3 --warmup)"; then exec 3<&-; die 'capacity warmup failed'; fi
  exec 3<&-
  case "$report" in "requests=$expected "*) ;; *) die 'capacity warmup request count mismatch' ;; esac
  printf 'warmup_rate=%s %s\n' "$rate" "$report"
}


sample_process(){ trap 'exit 0' TERM; while kill -0 "$MAIN_PID" 2>/dev/null; do read -r _ _ _ _ _ _ _ _ _ _ _ _ _ utime stime _ <"/proc/$MAIN_PID/stat" || exit 1; rss=$(awk '/^VmRSS:/{print $2}' "/proc/$MAIN_PID/status") || exit 1; mem=$(awk '/^MemAvailable:/{print $2}' /proc/meminfo) || exit 1; [ -n "$rss" ] && [ -n "$mem" ] || exit 1; printf '%s %s %s %s\n' "$(date +%s)" "$((utime+stime))" "$rss" "$mem" >>"$SAMPLES" || exit 1; sleep 1; done; exit 1; }
run_phase(){
  local rate=$1 duration=$2 sample_count coverage load_ok=false
  SAMPLES=$ROOT/process-$rate.samples; : >"$SAMPLES"; chmod 600 "$SAMPLES"; chown superpan-license:superpan-license "$SAMPLES"
  sample_process & SAMPLER_PID=$!
  exec 3<"$SECRET_FILE"
  if runuser -u superpan-license -- "$TOOLS/license-loadtest" --origin "$ORIGIN" --rate "$rate" --duration-seconds "$duration" --manifest "$MANIFEST" --load-secret-fd 3; then load_ok=true; fi
  exec 3<&-
  kill -0 "$MAIN_PID" 2>/dev/null || die 'capacity process exited during phase'
  kill -TERM "$SAMPLER_PID"; wait "$SAMPLER_PID"; SAMPLER_PID=
  [ "$load_ok" = true ] || die 'load phase failed'
  sample_count=$(wc -l <"$SAMPLES"); [ "$sample_count" -ge 590 ] || die 'phase sample count too low'
  read -r START_TIME START_TICKS RSS_START _ <"$SAMPLES"; read -r END_TIME END_TICKS RSS_END _ < <(tail -n 1 "$SAMPLES"); coverage=$((END_TIME-START_TIME)); [ "$coverage" -ge 589 ] || die 'phase sample coverage too short'
  CLK_TCK=$(getconf CLK_TCK); CPU_AVG=$(awk -v ticks="$((END_TICKS-START_TICKS))" -v hz="$CLK_TCK" -v seconds="$coverage" 'BEGIN{printf "%.3f",100*ticks/hz/seconds}')
  MEMORY_FLOOR=$(awk 'NR==1{m=$4}$4<m{m=$4}END{print m}' "$SAMPLES"); RSS_GROWTH=$(awk -v start="$RSS_START" -v end="$RSS_END" 'BEGIN{if(start==0){print 100}else{printf "%.3f",100*(end-start)/start}}')
  awk -v value="$CPU_AVG" 'BEGIN{exit !(value<70)}' || die 'CPU_AVG threshold failed'; [ "$MEMORY_FLOOR" -gt 1048576 ] || die 'MEMORY_FLOOR threshold failed'; awk -v value="$RSS_GROWTH" 'BEGIN{exit !(value<10)}' || die 'RSS_GROWTH threshold failed'
  printf 'rate=%s samples=%s coverage_seconds=%s cpu_avg_pct=%s rss_start_kib=%s rss_end_kib=%s rss_growth_pct=%s memory_floor_kib=%s\n' "$rate" "$sample_count" "$coverage" "$CPU_AVG" "$RSS_START" "$RSS_END" "$RSS_GROWTH" "$MEMORY_FLOOR"
}
warm_up 50 3000
run_phase 50 600
sleep 10
warm_up 100 6000
run_phase 100 600

systemctl stop superpan-license-loadtest.service
kill -0 "$MAIN_PID" 2>/dev/null && die 'exact capacity PID survived stop'
START_ATTEMPTED=false
printf 'stopped\n' >"$STOPPED_MARKER"; chown superpan-license:superpan-license "$STOPPED_MARKER"; chmod 600 "$STOPPED_MARKER"
runuser -u superpan-license -- "$TOOLS/license-dbcheck" --database "$DATABASE" --root "$ROOT" --stopped-marker "$STOPPED_MARKER" --expected-counts "$EXPECTED_COUNTS"
assert_public_absent
for file in "$STOPPED_MARKER" "$ROOT/process-50.samples" "$ROOT/process-100.samples" "$EXPECTED_COUNTS" "$ROOT/state.db-wal" "$ROOT/state.db-shm" "$ROOT/state.db-journal" "$DATABASE"; do [ ! -e "$file" ] || rm -f -- "$file"; done
for file in "$CONFIG" "$CONFIG_ROOT/encryption.key" "$CONFIG_ROOT/lookup.key" "$CONFIG_ROOT/response-signing.key" "$CONFIG_ROOT/admin.secret" "$SECRET_FILE" "$CONFIG_ROOT/response-signing.key-id" "$CONFIG_ROOT/response-signing.spki.der" "$CONFIG_ROOT/response-signing.spki.base64" "$CONFIG_ROOT/response-signing.spki.sha256"; do [ ! -e "$file" ] || rm -f -- "$file"; done
rmdir -- "$ROOT"
rmdir -- "$CONFIG_ROOT"
trap - EXIT INT TERM HUP
printf 'capacity_acceptance=passed source_commit=%s\n' "$BUNDLE_COMMIT"
