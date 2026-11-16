#!/usr/bin/env bash
# Phase 9a — measurement infrastructure (spec §6 and §9, step 9). One command per scenario:
#
#   ./bench/run.sh <scenario> [--variant hybrid|monolithic] [--runs 5] [--warmup 60] [--allow-dirty]
#
# For each run n = 1..N:
#   1. docker compose down -v and up -d --wait (clean state: no data from the previous run);
#   2. applies the migrations and starts the API and the Workers (Release build, ASPNETCORE_ENVIRONMENT=Benchmark,
#      Persistence__Mode of the variant), and waits for /health/ready of both;
#   3. loads the base data: the facilities, spots and sensors of scenario D8 (bench/scenarios/d8-reference.json), through
#      the simulator (RF-06 over HTTP), and waits until the read model knows every spot;
#   4. warm-up (--warmup seconds, 60 by default), not measured;
#   5. runs the measured part of the scenario and collects its metrics;
#   6. appends them to results/<scenario>/<YYYY-MM-DD_HHMM>.csv (columns of §6) and stops the API and the Workers.
# Next to the CSV: <stamp>.meta.json (commit, images, tools, machine, exit codes), <stamp>.run<n>.k6.json (raw k6
# summary, --summary-export) and <stamp>.logs/ (process output; not versioned).
#
# A scenario is bench/scenarios/<scenario>.sh, sourced by this script. It defines:
#   scenario_config         prints the configuration text of the CSV (the bench part; tools add their own)
#   scenario_warmup         runs the warm-up load for $WARMUP_SECONDS seconds
#   scenario_run <n>        runs the measured load of run n, collecting with the helpers below
# and may define scenario_seed (default: seed_d8). The script only collects; it does not interpret results.
#
# Refuses to run with uncommitted changes (the commit in meta.json would not describe the code measured) unless
# --allow-dirty is given; the list of changes is recorded in meta.json either way.

set -euo pipefail

usage() {
  sed -n '2,4p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

[[ $# -ge 1 ]] || usage
SCENARIO="$1"
shift
VARIANT=hybrid
RUNS=5
WARMUP_SECONDS=60
ALLOW_DIRTY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --variant) VARIANT="${2:-}"; shift 2 ;;
    --runs) RUNS="${2:-}"; shift 2 ;;
    --warmup) WARMUP_SECONDS="${2:-}"; shift 2 ;;
    --allow-dirty) ALLOW_DIRTY=1; shift ;;
    *) echo "unknown option: $1" >&2; usage ;;
  esac
done
case "$VARIANT" in
  hybrid) MODE=Hybrid ;;
  monolithic) MODE=Monolithic ;;
  *) echo "--variant must be hybrid or monolithic" >&2; exit 2 ;;
esac
[[ "$RUNS" =~ ^[1-9][0-9]*$ ]] || { echo "--runs must be a positive integer" >&2; exit 2; }
[[ "$WARMUP_SECONDS" =~ ^[0-9]+$ ]] || { echo "--warmup must be a number of seconds" >&2; exit 2; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# Absolute path in the native form (C:/...) for arguments that .NET resolves itself: a relative --contentRoot is
# resolved against the DLL's folder, not the current directory.
ROOT_NATIVE="$(pwd -W 2> /dev/null || pwd)"
DEFINITION="bench/scenarios/$SCENARIO.sh"
[[ -f "$DEFINITION" ]] || { echo "no scenario definition $DEFINITION" >&2; exit 2; }

# results/ is left out: the CSV of an earlier session, not yet committed, does not change the code being measured.
if [[ -n "$(git status --porcelain -- . ':!results')" && $ALLOW_DIRTY -eq 0 ]]; then
  echo "uncommitted changes: commit them or pass --allow-dirty (they are recorded in meta.json)" >&2
  git status --short -- . ':!results' >&2
  exit 2
fi

API=http://localhost:5080
WORKERS=http://localhost:5090
API_FROM_K6=http://host.docker.internal:5080
BUILD=bin/Release/net10.0
GIT_SHORT="$(git rev-parse --short HEAD)"
PY=python

# A process left on 5080/5090 (an aborted session, a `dotnet run`) would answer /health/ready in place of the one
# started here, possibly in the other mode or from an older build, and be measured silently (review of Phase 9a, #4).
ports_free() {
  local url code
  for url in "$API" "$WORKERS"; do
    code=0
    curl -s -o /dev/null --max-time 2 "$url/" || code=$?
    if [[ $code -ne 7 ]]; then
      echo "something is already listening on $url: stop it before measuring" >&2
      return 1
    fi
  done
}
ports_free || exit 2

STAMP="$(date +%Y-%m-%d_%H%M)"
OUT_DIR="results/$SCENARIO"
# Rows go to <stamp>.csv.partial and the file is renamed to <stamp>.csv only when every run finished; a failed session
# leaves <stamp>.failed.csv, so an incomplete file never looks complete (review of Phase 9a, #6).
CSV_FINAL="$OUT_DIR/$STAMP.csv"
CSV="$CSV_FINAL.partial"
META="$OUT_DIR/$STAMP.meta.json"
LOGS="$OUT_DIR/$STAMP.logs"
if [[ -e "$CSV_FINAL" || -e "$CSV" || -e "$META" ]]; then
  echo "$CSV_FINAL already exists (one session per minute): wait a minute and run again" >&2
  exit 2
fi
mkdir -p "$OUT_DIR" "$LOGS"

# Npgsql pool of each process, as configured in the versioned appsettings.Benchmark.json (recorded in every CSV row).
pool_of() { grep -o 'Maximum Pool Size=[0-9]*' "$1/appsettings.Benchmark.json" | head -1 | cut -d= -f2; }
POOL="api=$(pool_of src/SmartParking.Api)/workers=$(pool_of src/SmartParking.Workers)"

log() { printf '[bench %s] %s\n' "$(date +%H:%M:%S)" "$*"; }
now_ms() { date +%H:%M:%S.%3N; }

python_meta() { "$PY" bench/lib/write_meta.py "$@" < /dev/null; }

COMMAND="./bench/run.sh $SCENARIO --variant $VARIANT --runs $RUNS --warmup $WARMUP_SECONDS"
(( ALLOW_DIRTY )) && COMMAND="$COMMAND --allow-dirty"
python_meta start --out "$META" --scenario "$SCENARIO" --variant "$VARIANT" --runs "$RUNS" \
  --warmup "$WARMUP_SECONDS" --command "$COMMAND"

# ---------------------------------------------------------------------------------------------------------------------
# Processes. Started from the Release DLLs (not `dotnet run`), so that the PID is the process itself; stopped with
# taskkill /F on Windows (Git Bash cannot signal a native process) or kill elsewhere.
API_PID=""
WORKERS_PID=""

start_process() { # <name> <dll dir> <dll> <url> -> sets LAST_PID
  local name="$1" dir="$2" dll="$3" url="$4"
  ASPNETCORE_ENVIRONMENT=Benchmark DOTNET_ENVIRONMENT=Benchmark Persistence__Mode="$MODE" ASPNETCORE_URLS="$url" \
    dotnet "$dir/$BUILD/$dll" --contentRoot "$ROOT_NATIVE/$dir/$BUILD" >"$LOGS/$name.run$RUN.log" 2>&1 < /dev/null &
  LAST_PID=$!
}

stop_pid() {
  local pid="$1"
  [[ -n "$pid" ]] || return 0
  if [[ -r "/proc/$pid/winpid" ]]; then
    taskkill //F //T //PID "$(cat "/proc/$pid/winpid")" > /dev/null 2>&1 || true
  else
    kill "$pid" 2> /dev/null || true
  fi
  wait "$pid" 2> /dev/null || true
}

stop_services() {
  stop_pid "$API_PID"
  stop_pid "$WORKERS_PID"
  API_PID=""
  WORKERS_PID=""
}

# On any exit: stops the API and the Workers, the background jobs (a `docker compose run` client of k6) and any k6
# container they left (review of Phase 9a, #9). A failed session keeps its rows as <stamp>.failed.csv.
cleanup() {
  local code=$? job
  stop_services
  for job in $(jobs -p); do
    kill "$job" 2> /dev/null || true
  done
  docker ps -q --filter label=com.docker.compose.project=smartparking-hybrid \
    --filter label=com.docker.compose.service=k6 | xargs -r docker rm -f > /dev/null 2>&1 || true
  if [[ $code -ne 0 ]]; then
    [[ -f "$CSV" ]] && mv "$CSV" "$OUT_DIR/$STAMP.failed.csv"
    python_meta finish --out "$META" --status "failed (exit $code)" || true
    log "failed with exit code $code; see $LOGS"
  fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM

wait_ready() { # <url> <seconds> <pid>
  local url="$1" deadline=$((SECONDS + $2)) pid="$3"
  until curl -fs -o /dev/null "$url/health/ready"; do
    if ! kill -0 "$pid" 2> /dev/null; then
      echo "the process for $url exited before it was ready (see $LOGS)" >&2
      return 1
    fi
    if (( SECONDS >= deadline )); then
      echo "$url/health/ready not ready after $2 s" >&2
      return 1
    fi
    sleep 1
  done
  # Ready, and still ours: the answer did not come from another process on the same port.
  kill -0 "$pid" 2> /dev/null || { echo "the process for $url is not running" >&2; return 1; }
}

# ---------------------------------------------------------------------------------------------------------------------
# Helpers for the scenario definitions.

# Runs a k6 script of bench/k6 in the pinned container. Usage: k6_run <script.js> <summary path under results/ or ""> [k6 args]
# Prints nothing; the k6 output goes to the logs. Returns k6's exit code.
k6_run() {
  local script="$1" summary="$2"
  shift 2
  local export_args=()
  if [[ -n "$summary" ]]; then
    export_args=(--summary-export "/${summary}")
  fi
  # MSYS_NO_PATHCONV: Git Bash would otherwise rewrite /scripts/... and /results/... into Windows paths.
  MSYS_NO_PATHCONV=1 docker compose --profile bench run --rm -T k6 run --quiet --no-color \
    --summary-trend-stats "avg,min,med,max,p(90),p(95),p(99)" "${export_args[@]}" \
    -e API="$API_FROM_K6" "$@" "/scripts/$script" >> "$LOGS/k6.run$RUN.log" 2>&1 < /dev/null
}

# Converts a k6 summary (path under results/) into CSV rows of this run.
k6_collect() { # <summary path> <configuration>
  "$PY" bench/lib/k6_to_csv.py --summary "$1" --csv "$CSV" --scenario "$SCENARIO" --variant "$VARIANT" \
    --run "$RUN" --config "$2" < /dev/null
}

# Runs the simulator (Release DLL) against the API. Usage: sim_run <simulator args...>; returns its exit code.
sim_run() {
  dotnet "tools/SensorSimulator/$BUILD/SensorSimulator.dll" --api "$API" --variant "$VARIANT" "$@" \
    >> "$LOGS/simulator.run$RUN.log" 2>&1 < /dev/null
}

# Appends the simulator's own summary CSV (written with --summary-csv) with this run's labels.
sim_collect() { # <tmp csv> <configuration>
  "$PY" bench/lib/relabel_csv.py --from "$1" --csv "$CSV" --scenario "$SCENARIO" --variant "$VARIANT" \
    --run "$RUN" --config "$2" < /dev/null
}

# D7 histogram of both processes (bench/lib/e2e_latency.py). e2e_snapshot <file>; e2e_collect <before> <after> <config>
e2e_snapshot() {
  "$PY" bench/lib/e2e_latency.py snapshot --url "$API/metrics" --url "$WORKERS/metrics" --out "$1" < /dev/null
}
e2e_collect() {
  "$PY" bench/lib/e2e_latency.py diff --before "$1" --after "$2" --csv "$CSV" --scenario "$SCENARIO" \
    --variant "$VARIANT" --run "$RUN" --config "$3" < /dev/null
}

# Records one value of this run in meta.json (exit codes, durations, start times).
run_meta() { python_meta run --out "$META" --run "$RUN" --key "$1" --value "$2"; }

# Writes one row of this run to the CSV, e.g. a tool's exit code, so that a run whose load was not clean (simulator
# exit 1 or 4, k6 exit 99) is visible in the CSV itself and not only in meta.json (review of Phase 9a, #6).
csv_row() { # <metric> <value> <unit> <configuration>
  "$PY" bench/lib/csv_row.py --csv "$CSV" --scenario "$SCENARIO" --variant "$VARIANT" --run "$RUN" \
    --metric "$1" --value "$2" --unit "$3" --config "$4" < /dev/null
}

# Base data of scenario D8 (step 3): provisioning only. The simulator needs a positive rate and duration, so it runs
# for 1 s at a rate low enough that it sends no event in practice; it ends with every spot free (--free-at-end).
seed_d8() {
  sim_run --scenario bench/scenarios/d8-reference.json --duration 1 --rate 0.001 --peak-factor 1 \
    --wait-read-model 600 --run "$RUN"
}

# shellcheck source=/dev/null
source "$DEFINITION"
declare -F scenario_run > /dev/null && declare -F scenario_warmup > /dev/null && declare -F scenario_config > /dev/null \
  || { echo "$DEFINITION must define scenario_config, scenario_warmup and scenario_run" >&2; exit 2; }
declare -F scenario_seed > /dev/null || scenario_seed() { seed_d8; }
CONFIG_BASE="variant=$VARIANT;mode=$MODE;warmup=${WARMUP_SECONDS}s;commit=$GIT_SHORT;pool=$POOL;$(scenario_config)"

# ---------------------------------------------------------------------------------------------------------------------
log "scenario $SCENARIO, variant $VARIANT, $RUNS run(s), warm-up ${WARMUP_SECONDS} s -> $CSV"
log "building (Release)"
for project in src/SmartParking.Api src/SmartParking.Workers tools/SensorSimulator; do
  dotnet build "$project" -c Release -nologo -v quiet > "$LOGS/build.log" 2>&1 < /dev/null \
    || { cat "$LOGS/build.log" >&2; exit 1; }
done

for ((RUN = 1; RUN <= RUNS; RUN++)); do
  log "run $RUN/$RUNS: clean state (docker compose down -v; up -d --wait)"
  docker compose down -v > "$LOGS/compose.run$RUN.log" 2>&1 < /dev/null
  docker compose up -d --wait >> "$LOGS/compose.run$RUN.log" 2>&1 < /dev/null

  log "run $RUN/$RUNS: migrations, API and Workers ($MODE)"
  ASPNETCORE_ENVIRONMENT=Benchmark dotnet ef database update --configuration Release --no-build \
    --project src/SmartParking.Infrastructure.Relational --startup-project src/SmartParking.Api \
    > "$LOGS/migrations.run$RUN.log" 2>&1 < /dev/null
  ports_free
  start_process api src/SmartParking.Api SmartParking.Api.dll "$API"
  API_PID=$LAST_PID
  start_process workers src/SmartParking.Workers SmartParking.Workers.dll "$WORKERS"
  WORKERS_PID=$LAST_PID
  wait_ready "$API" 120 "$API_PID"
  wait_ready "$WORKERS" 120 "$WORKERS_PID"

  log "run $RUN/$RUNS: base data (D8)"
  started=$SECONDS
  scenario_seed
  run_meta seed_seconds "$((SECONDS - started))"

  if (( WARMUP_SECONDS > 0 )); then
    log "run $RUN/$RUNS: warm-up ${WARMUP_SECONDS} s (not measured)"
    scenario_warmup
  fi

  log "run $RUN/$RUNS: measuring"
  scenario_run "$RUN"

  stop_services
  log "run $RUN/$RUNS: done"
done

mv "$CSV" "$CSV_FINAL"
python_meta finish --out "$META" --status ok
log "finished: $CSV_FINAL"
