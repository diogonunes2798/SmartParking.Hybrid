# Bench scenario "retention" (Phase 10, RNF-05, ADR-0013). Sourced by bench/run.sh (see its header for the contract).
#   ./bench/run.sh retention --variant hybrid --runs 5 --warmup 0                              # snappy (MongoDB default)
#   RETENTION_COMPRESSOR=zstd ./bench/run.sh retention --variant hybrid --runs 5 --warmup 0
#   ./bench/run.sh retention --variant monolithic --runs 5 --warmup 0                          # daily partitions
#
# One run:
#   1. idle:      CPU of every process for RETENTION_IDLE_SECONDS without load (the baseline of the CPU rows; idle_seconds
#                 and the window lengths load_seconds, retention_seconds and compact_seconds allow subtracting it);
#   2. load:      RETENTION_EVENTS events through the API (UC-01, the real path) at RETENTION_RATE events/s, with sensor
#                 timestamps compressed into RETENTION_DAYS whole UTC days in the past (simulator --sensor-start and
#                 --time-scale), then waits until the store stops growing. Rows: cpu_load_*, storage_after_load_*;
#   3. aggregate: aggregate-occupancy over the loaded days (UC-04: retention removes only aggregated days);
#   4. retention: the Workers are restarted with their retention service on (every RETENTION_INTERVAL_SECONDS, as in
#                 production, so that its CPU is measured with the Workers process and the TTL guard keeps running), after
#                 an initial delay within which the CPU snapshot is taken (the start-up of the process is not counted).
#                 The service archives and removes the first half of the days. The run waits until no event is left
#                 before the horizon minus the guard margin minus one minute (MongoDB keeps that margin by design, and
#                 the guard rounds it up to the minute; the monolithic DROP
#                 removes whole days). Rows: cpu_retention_*, storage_after_retention_*, archive_*, events_*;
#   5. compact:   hybrid only: `compact` returns the space freed by the TTL deletes to the file system (the TTL monitor
#                 only frees it inside WiredTiger: storageSize does not shrink). Rows: cpu_compact_*, storage_after_compact_*.
#                 Monolithic: the DROP of a partition already returns it, nothing to do.
#
# Changes after the independent review of Phase 10 (17-11-2026; docs/revisoes/fase-10-revisao-codigo.md):
#   - the counts that poll the store run in a container of their own, every RETENTION_POLL_SECONDS, never with `exec`
#     in the database container (that CPU was measured as database CPU, much more for mongosh than for psql);
#   - the retention service is off during the load and the aggregation, so it cannot start before the snapshot of step 4
#     (with it on, the first aggregated day could be archived and removed while the aggregation still ran);
#   - the compact has its own CPU window;
#   - only the components of the variant are summed (monolithic: postgres, api, workers; mongo and redis still run, idle),
#     and every CPU window also has net rows (minus the idle rate x the window length);
#   - each run records the clock skew of the mongo container (the TTL monitor uses it) and whether it crossed midnight UTC.

RETENTION_EVENTS=${RETENTION_EVENTS:-1000000}
RETENTION_RATE=${RETENTION_RATE:-500}
RETENTION_DAYS=${RETENTION_DAYS:-4}
RETENTION_COMPRESSOR=${RETENTION_COMPRESSOR:-}
RETENTION_IDLE_SECONDS=${RETENTION_IDLE_SECONDS:-30}
RETENTION_POLL_SECONDS=${RETENTION_POLL_SECONDS:-15}
RETENTION_TTL_WAIT=${RETENTION_TTL_WAIT:-1800}
RETENTION_INTERVAL_SECONDS=${RETENTION_INTERVAL_SECONDS:-15}
RETENTION_INITIAL_DELAY_SECONDS=${RETENTION_INITIAL_DELAY_SECONDS:-30}
RETENTION_MARGIN_MINUTES=${RETENTION_MARGIN_MINUTES:-5}

(( RETENTION_DAYS >= 2 && RETENTION_DAYS % 2 == 0 )) || { echo "RETENTION_DAYS must be even and >= 2" >&2; exit 2; }
if [[ "$VARIANT" == monolithic && -n "$RETENTION_COMPRESSOR" ]]; then
  echo "RETENTION_COMPRESSOR applies to the hybrid variant only" >&2
  exit 2
fi

RETENTION_DURATION=$(awk -v n="$RETENTION_EVENTS" -v r="$RETENTION_RATE" 'BEGIN { printf "%.0f", n / r }')
RETENTION_SCALE=$(awk -v d="$RETENTION_DAYS" -v s="$RETENTION_DURATION" 'BEGIN { printf "%.4f", d * 86400 / s }')
RETENTION_REMOVED_DAYS=$((RETENTION_DAYS / 2))

# The loaded days: [start, start + DAYS days), ending at the start of yesterday (UTC). The horizon is the start of the
# second half: [start, horizon) is archived and removed, [horizon, end) stays.
# Computed at the start of each run (a session of 5 runs may cross midnight UTC). A run that crosses midnight between the
# load and the retention archives one more day: events_removed records it.
day_utc() { date -u -d "$(date -u +%Y-%m-%d) $1 days" +%Y-%m-%dT00:00:00Z; }
retention_dates() {
  RETENTION_START=$(day_utc "-$((RETENTION_DAYS + 1))")
  RETENTION_HORIZON=$(day_utc "-$((RETENTION_DAYS + 1 - RETENTION_REMOVED_DAYS))")
  RETENTION_END=$(day_utc "-1")
  # One minute more than the margin: the guard rounds expireAfterSeconds up to the minute, so the TTL limit moves
  # between (horizon - margin - 1 min) and (horizon - margin). Waiting for an empty [start, horizon - margin) failed
  # with 1e6 events: about 3 events per sensor second, and some always stood in that last minute.
  RETENTION_KEPT_FROM=$(date -u -d "$RETENTION_HORIZON - $((RETENTION_MARGIN_MINUTES + 1)) minutes" +%Y-%m-%dT%H:%M:%SZ)
}

# Settings of the API and the Workers of this scenario (exported: run.sh starts both from this shell).
# - ArchiveDelay = (days from the horizon to today): exactly the days before the horizon are old enough to be archived,
#   whatever the hour of the run, so the purge horizon is min(now - RawEvents, archived) = the horizon above.
# - GuardMargin RETENTION_MARGIN_MINUTES: the guard runs every RETENTION_INTERVAL_SECONDS, so the TTL limit stays at the
#   horizon minus that margin (up to one minute earlier: rounding); MongoDB keeps those minutes before the horizon
#   (events_removed records it).
# - MaxEventAge (DAYS + 3 days) is above ArchiveDelay here: the load sends events of past days on purpose, and no event
#   arrives after its day is archived (review of Phase 10, finding 3: in production MaxEventAge <= ArchiveDelay).
export Ingestion__MaxEventAge="$((RETENTION_DAYS + 3)).00:00:00"
export Analytics__Enabled=false
export Retention__Enabled=false
export Retention__Interval="00:00:$(printf '%02d' "$RETENTION_INTERVAL_SECONDS")"
export Retention__InitialDelay="00:00:$(printf '%02d' "$RETENTION_INITIAL_DELAY_SECONDS")"
export Retention__ArchivePath="$ROOT_NATIVE/$LOGS/archive"
export Retention__RawEvents="1.00:00:00"
export Retention__ArchiveDelay="$((RETENTION_DAYS + 1 - RETENTION_REMOVED_DAYS)).00:00:00"
export Retention__GuardMargin="00:$(printf '%02d' "$RETENTION_MARGIN_MINUTES"):00"
export Persistence__Monolithic__PartitionInterval=Day
export Persistence__Monolithic__PartitionDaysBehind="$((RETENTION_DAYS + 3))"
if [[ -n "$RETENTION_COMPRESSOR" ]]; then
  export Mongo__EventsBlockCompressor="$RETENTION_COMPRESSOR"
fi

scenario_config() {
  printf 'events=%s;rate=%s/s;duration=%ss;days=%s;removed_days=%s;time_scale=%s;compressor=%s;raw=1d;archive_delay=%sd;guard_margin=%smin;retention_interval=%ss;partitions=%s' \
    "$RETENTION_EVENTS" "$RETENTION_RATE" "$RETENTION_DURATION" "$RETENTION_DAYS" "$RETENTION_REMOVED_DAYS" \
    "$RETENTION_SCALE" "${RETENTION_COMPRESSOR:-default}" "$((RETENTION_DAYS + 1 - RETENTION_REMOVED_DAYS))" \
    "$RETENTION_MARGIN_MINUTES" "$RETENTION_INTERVAL_SECONDS" \
    "$([[ $VARIANT == monolithic ]] && echo day || echo n/a)"
}

scenario_warmup() {
  log "retention: no warm-up (each run starts from an empty store; use --warmup 0)"
}

stats() { "$PY" bench/lib/retention_stats.py "$@" < /dev/null; }

winpid() { cat "/proc/$1/winpid"; }

# Components of the variant (the monolithic variant does not use mongo and redis, which still run).
if [[ "$VARIANT" == monolithic ]]; then RETENTION_COMPONENTS=postgres,api,workers; else RETENTION_COMPONENTS=postgres,mongo,redis,api,workers; fi

cpu_window() { # <phase> <before> <after> <seconds> <events> <run> <config>
  stats cpu-diff --before "$2" --after "$3" --phase "$1" --events "$5" --only "$RETENTION_COMPONENTS" \
    --idle-before "$IDLE0" --idle-after "$IDLE1" --idle-seconds "$RETENTION_IDLE_SECONDS" --seconds "$4" \
    --csv "$CSV" --scenario "$SCENARIO" --variant "$VARIANT" --run "$6" --config "$7"
}

cpu_snapshot() { # <file>
  stats cpu-snapshot --out "$1" --pid "api=$(winpid "$API_PID")" --pid "workers=$(winpid "$WORKERS_PID")"
}

# Workers command (Release DLL) with the same environment as the Workers process.
workers_cmd() {
  ASPNETCORE_ENVIRONMENT=Benchmark DOTNET_ENVIRONMENT=Benchmark Persistence__Mode="$MODE" \
    dotnet "src/SmartParking.Workers/$BUILD/SmartParking.Workers.dll" "$1" \
    --contentRoot "$ROOT_NATIVE/src/SmartParking.Workers/$BUILD" "${@:2}" >> "$LOGS/workers-command.run$RUN.log" 2>&1 < /dev/null
}

count() { stats count --variant "$VARIANT" "$@"; }

# Waits until two counts RETENTION_POLL_SECONDS apart are equal (the hybrid writes through the bus).
wait_settled() {
  local last=-1 current deadline=$((SECONDS + 1800))
  while true; do
    current=$(count)
    [[ "$current" == "$last" ]] && return 0
    last=$current
    (( SECONDS < deadline )) || { echo "the store did not settle in 30 min" >&2; return 1; }
    sleep "$RETENTION_POLL_SECONDS"
  done
}

# Restarts the Workers with the retention service on (run.sh's start_process and wait_ready; the new PID replaces the
# old one, so run.sh still stops it). The service waits Retention__InitialDelay before its first run.
restart_workers_with_retention() {
  stop_pid "$WORKERS_PID"
  WORKERS_PID=""
  Retention__Enabled=true start_process "workers-retention" src/SmartParking.Workers SmartParking.Workers.dll "$WORKERS"
  WORKERS_PID=$LAST_PID
  wait_ready "$WORKERS" 120 "$WORKERS_PID"
}

scenario_run() {
  local run="$1" config="$CONFIG_BASE" sim_code=0 code=0 started
  local sim_csv="$LOGS/simulator.run$run.csv" archive="$LOGS/archive"
  local c0="$LOGS/cpu-idle0.run$run.json" c1="$LOGS/cpu-load0.run$run.json" c2="$LOGS/cpu-load1.run$run.json"
  local c3="$LOGS/cpu-ret0.run$run.json" c4="$LOGS/cpu-ret1.run$run.json"
  local c5="$LOGS/cpu-compact0.run$run.json" c6="$LOGS/cpu-compact1.run$run.json"
  retention_dates
  local day0
  day0=$(date -u +%F)
  IDLE0="$c0"
  IDLE1="$c1"
  run_meta sensor_start "$RETENTION_START"
  run_meta horizon "$RETENTION_HORIZON"
  run_meta clock_skew_mongo_ms_start "$(stats clock-skew --service mongo)"

  # 1. Idle baseline.
  cpu_snapshot "$c0"
  sleep "$RETENTION_IDLE_SECONDS"
  cpu_snapshot "$c1"
  csv_row idle_seconds "$RETENTION_IDLE_SECONDS" s "$config;tool=bench"
  stats cpu-diff --before "$c0" --after "$c1" --phase idle --only "$RETENTION_COMPONENTS" --csv "$CSV" \
    --scenario "$SCENARIO" --variant "$VARIANT" --run "$run" --config "$config;tool=cgroup+process"

  # 2. Load (the idle snapshot c1 is also the start of the load).
  log "run $run: loading $RETENTION_EVENTS events at $RETENTION_RATE/s, sensor time $RETENTION_START .. $RETENTION_END"
  run_meta load_started "$(now_ms)"
  started=$SECONDS
  sim_run --scenario bench/scenarios/d8-reference.json --rate "$RETENTION_RATE" --peak-factor 1 \
    --duration "$RETENTION_DURATION" --sensor-start "$RETENTION_START" --time-scale "$RETENTION_SCALE" \
    --seed 42 --run "$run" --wait-read-model 0 --free-at-end false --summary-csv "$sim_csv" || sim_code=$?
  wait_settled
  cpu_snapshot "$c2"
  local load_seconds=$((SECONDS - started)) events
  events=$(count)
  run_meta simulator_exit "$sim_code"
  csv_row sim_exit_code "$sim_code" code "$config;tool=simulator"
  [[ -f "$sim_csv" ]] && sim_collect "$sim_csv" "$config;tool=simulator"
  csv_row load_seconds "$load_seconds" s "$config;tool=bench"
  csv_row events_stored "$events" events "$config;tool=bench"
  cpu_window load "$c1" "$c2" "$load_seconds" "$events" "$run" "$config;tool=cgroup+process"
  stats storage --variant "$VARIANT" --phase after_load --csv "$CSV" --scenario "$SCENARIO" --run "$run" \
    --config "$config;tool=engine-stats"

  # 3. Aggregation of the loaded days (UC-04), retention still off.
  log "run $run: aggregating $RETENTION_START .. $RETENTION_END"
  started=$SECONDS
  workers_cmd aggregate-occupancy --from "$RETENTION_START" --to "$RETENTION_END" || code=$?
  csv_row aggregate_exit_code "$code" code "$config;tool=workers"
  csv_row aggregate_seconds "$((SECONDS - started))" s "$config;tool=workers"
  (( code == 0 )) || return 1

  # 4. Retention: Workers restarted with the service on; snapshot within its initial delay.
  log "run $run: retention up to $RETENTION_HORIZON (kept from $RETENTION_KEPT_FROM in MongoDB)"
  local before_removed
  before_removed=$(count --from "$RETENTION_START" --to "$RETENTION_HORIZON")
  restart_workers_with_retention
  cpu_snapshot "$c3"
  started=$SECONDS
  run_meta retention_snapshot "$(now_ms)"
  local deadline=$((SECONDS + RETENTION_TTL_WAIT))
  until [[ "$(count --from "$RETENTION_START" --to "$RETENTION_KEPT_FROM")" == 0 ]]; do
    (( SECONDS < deadline )) || { echo "events before the horizon still stored after $RETENTION_TTL_WAIT s" >&2; return 1; }
    sleep "$RETENTION_POLL_SECONDS"
  done
  cpu_snapshot "$c4"
  local retention_seconds=$((SECONDS - started)) remaining
  remaining=$(count)
  csv_row retention_seconds "$retention_seconds" s "$config;tool=bench"
  csv_row events_eligible "$before_removed" events "$config;tool=bench"
  csv_row events_removed "$((events - remaining))" events "$config;tool=bench"
  csv_row events_remaining "$remaining" events "$config;tool=bench"
  cpu_window retention "$c3" "$c4" "$retention_seconds" "$((events - remaining))" "$run" "$config;tool=cgroup+process"
  csv_row crossed_midnight "$([[ "$(date -u +%F)" == "$day0" ]] && echo 0 || echo 1)" bool "$config;tool=bench"
  stats storage --variant "$VARIANT" --phase after_retention --csv "$CSV" --scenario "$SCENARIO" --run "$run" \
    --config "$config;tool=engine-stats"
  stats archive --path "$archive" --csv "$CSV" --scenario "$SCENARIO" --variant "$VARIANT" --run "$run" \
    --config "$config;tool=archive"

  # 5. Compact (hybrid), from a client container of its own, with its own CPU window.
  if [[ "$VARIANT" == hybrid ]]; then
    cpu_snapshot "$c5"
    started=$SECONDS
    docker compose run --rm --no-deps -T mongo mongosh \
      "mongodb://${MONGO_USER:-smartparking}:${MONGO_PASSWORD:-smartparking}@mongo:27017/smartparking?authSource=admin" \
      --quiet --eval "printjson(db.runCommand({compact: 'occupancy_events'}))" >> "$LOGS/compact.run$run.log" 2>&1 < /dev/null
    cpu_snapshot "$c6"
    local compact_seconds=$((SECONDS - started))
    csv_row compact_seconds "$compact_seconds" s "$config;tool=mongosh"
    cpu_window compact "$c5" "$c6" "$compact_seconds" 0 "$run" "$config;tool=cgroup+process"
    stats storage --variant "$VARIANT" --phase after_compact --csv "$CSV" --scenario "$SCENARIO" --run "$run" \
      --config "$config;tool=engine-stats"
  fi

  run_meta clock_skew_mongo_ms_end "$(stats clock-skew --service mongo)"
  rm -rf "$archive" # measured above; the files are not kept (tens of MB per run)
}
