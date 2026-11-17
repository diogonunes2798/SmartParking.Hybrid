# Bench scenario "retention" (Phase 10, RNF-05, ADR-0013). Sourced by bench/run.sh (see its header for the contract).
#   ./bench/run.sh retention --variant hybrid --runs 5 --warmup 0                              # snappy (MongoDB default)
#   RETENTION_COMPRESSOR=zstd ./bench/run.sh retention --variant hybrid --runs 5 --warmup 0
#   ./bench/run.sh retention --variant monolithic --runs 5 --warmup 0                          # daily partitions
#
# One run:
#   1. idle:      CPU of every process for RETENTION_IDLE_SECONDS without load (the baseline of the CPU rows);
#   2. load:      RETENTION_EVENTS events through the API (UC-01, the real path) at RETENTION_RATE events/s, with sensor
#                 timestamps compressed into RETENTION_DAYS whole UTC days in the past (simulator --sensor-start and
#                 --time-scale), then waits until the store stops growing. Rows: cpu_load_*, storage_after_load_*;
#   3. aggregate: aggregate-occupancy over the loaded days (UC-04: retention removes only aggregated days);
#   4. retention: the retention service of the Workers (every RETENTION_INTERVAL, as in production, so that its CPU is
#                 measured with the Workers process and the TTL guard keeps running) archives and removes the first half
#                 of the days as soon as they are aggregated. The run waits until no event is left before the horizon
#                 minus the guard margin (MongoDB keeps that margin by design; the monolithic DROP removes whole days).
#                 Rows: cpu_retention_*, storage_after_retention_*, archive_*, events_removed;
#   5. compact:   hybrid only: `compact` returns the space freed by the TTL deletes to the file system (the TTL monitor
#                 only frees it inside WiredTiger: storageSize does not shrink). Rows: storage_after_compact_*.
#                 Monolithic: the DROP of a partition already returns it, nothing to do.
# The aggregation worker of the Workers is off (Analytics__Enabled): the only aggregation is the measured command. The
# retention service is on from the start; while the days are not aggregated it only checks (blocked: no archive, no
# horizon, the TTL index at «never»), which is light and is part of the measured load like it would be in production.
# Found in the pilot of 17-11-2026: with apply-retention run once, the guard stopped and the TTL limit moved forward
# with the clock, and the CPU of the archive ran in an unmeasured process.

RETENTION_EVENTS=${RETENTION_EVENTS:-1000000}
RETENTION_RATE=${RETENTION_RATE:-500}
RETENTION_DAYS=${RETENTION_DAYS:-4}
RETENTION_COMPRESSOR=${RETENTION_COMPRESSOR:-}
RETENTION_IDLE_SECONDS=${RETENTION_IDLE_SECONDS:-30}
RETENTION_SETTLE_SECONDS=${RETENTION_SETTLE_SECONDS:-15}
RETENTION_TTL_WAIT=${RETENTION_TTL_WAIT:-1800}
RETENTION_INTERVAL_SECONDS=${RETENTION_INTERVAL_SECONDS:-15}
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
  RETENTION_KEPT_FROM=$(date -u -d "$RETENTION_HORIZON - $RETENTION_MARGIN_MINUTES minutes" +%Y-%m-%dT%H:%M:%SZ)
}

# Settings of the API and the Workers of this scenario (exported: run.sh starts both from this shell).
# - ArchiveDelay = (days from the horizon to today): exactly the days before the horizon are old enough to be archived,
#   whatever the hour of the run, so the purge horizon is min(now - RawEvents, archived) = the horizon above.
# - GuardMargin RETENTION_MARGIN_MINUTES: the guard runs every RETENTION_INTERVAL_SECONDS, so the TTL limit stays at the
#   horizon minus that margin; MongoDB keeps those minutes before the horizon (events_removed records it).
export Ingestion__MaxEventAge="$((RETENTION_DAYS + 3)).00:00:00"
export Analytics__Enabled=false
export Retention__Enabled=true
export Retention__Interval="00:00:$(printf '%02d' "$RETENTION_INTERVAL_SECONDS")"
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

# Waits until the number of stored events stops changing for RETENTION_SETTLE_SECONDS (the hybrid writes through the bus).
wait_settled() {
  local last=-1 current stable=0 deadline=$((SECONDS + 1800))
  while (( stable < RETENTION_SETTLE_SECONDS )); do
    current=$(count)
    if [[ "$current" == "$last" ]]; then stable=$((stable + 1)); else stable=0; last=$current; fi
    (( SECONDS < deadline )) || { echo "the store did not settle in 30 min" >&2; return 1; }
    sleep 1
  done
}

scenario_run() {
  local run="$1" config="$CONFIG_BASE" sim_code=0 code=0 started
  local sim_csv="$LOGS/simulator.run$run.csv" archive="$LOGS/archive"
  local c0="$LOGS/cpu-idle0.run$run.json" c1="$LOGS/cpu-load0.run$run.json" c2="$LOGS/cpu-load1.run$run.json"
  local c3="$LOGS/cpu-ret0.run$run.json" c4="$LOGS/cpu-ret1.run$run.json"
  retention_dates
  run_meta sensor_start "$RETENTION_START"
  run_meta horizon "$RETENTION_HORIZON"

  # 1. Idle baseline.
  cpu_snapshot "$c0"
  sleep "$RETENTION_IDLE_SECONDS"
  cpu_snapshot "$c1"
  stats cpu-diff --before "$c0" --after "$c1" --phase idle --csv "$CSV" --scenario "$SCENARIO" --variant "$VARIANT" \
    --run "$run" --config "$config;tool=cgroup+process;seconds=$RETENTION_IDLE_SECONDS"

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
  stats cpu-diff --before "$c1" --after "$c2" --phase load --events "$events" --csv "$CSV" --scenario "$SCENARIO" \
    --variant "$VARIANT" --run "$run" --config "$config;tool=cgroup+process"
  stats storage --variant "$VARIANT" --phase after_load --csv "$CSV" --scenario "$SCENARIO" --run "$run" \
    --config "$config;tool=engine-stats"

  # 3. Aggregation of the loaded days (UC-04).
  log "run $run: aggregating $RETENTION_START .. $RETENTION_END"
  started=$SECONDS
  workers_cmd aggregate-occupancy --from "$RETENTION_START" --to "$RETENTION_END" || code=$?
  csv_row aggregate_exit_code "$code" code "$config;tool=workers"
  csv_row aggregate_seconds "$((SECONDS - started))" s "$config;tool=workers"
  (( code == 0 )) || return 1

  # 4. Retention (the service of the Workers, already running): archive and remove the first half of the days.
  log "run $run: retention up to $RETENTION_HORIZON (kept from $RETENTION_KEPT_FROM in MongoDB)"
  local before_removed
  before_removed=$(count --from "$RETENTION_START" --to "$RETENTION_HORIZON")
  cpu_snapshot "$c3"
  started=$SECONDS
  local deadline=$((SECONDS + RETENTION_TTL_WAIT))
  until [[ "$(count --from "$RETENTION_START" --to "$RETENTION_KEPT_FROM")" == 0 ]]; do
    (( SECONDS < deadline )) || { echo "events before the horizon still stored after $RETENTION_TTL_WAIT s" >&2; return 1; }
    sleep 5
  done
  cpu_snapshot "$c4"
  local retention_seconds=$((SECONDS - started)) remaining
  remaining=$(count)
  csv_row retention_seconds "$retention_seconds" s "$config;tool=bench"
  csv_row events_eligible "$before_removed" events "$config;tool=bench"
  csv_row events_removed "$((events - remaining))" events "$config;tool=bench"
  csv_row events_remaining "$remaining" events "$config;tool=bench"
  stats cpu-diff --before "$c3" --after "$c4" --phase retention --events "$((events - remaining))" --csv "$CSV" \
    --scenario "$SCENARIO" --variant "$VARIANT" --run "$run" --config "$config;tool=cgroup+process"
  stats storage --variant "$VARIANT" --phase after_retention --csv "$CSV" --scenario "$SCENARIO" --run "$run" \
    --config "$config;tool=engine-stats"
  stats archive --path "$archive" --csv "$CSV" --scenario "$SCENARIO" --variant "$VARIANT" --run "$run" \
    --config "$config;tool=archive"

  # 5. Compact (hybrid).
  if [[ "$VARIANT" == hybrid ]]; then
    started=$SECONDS
    docker compose exec -T mongo mongosh -u "${MONGO_USER:-smartparking}" -p "${MONGO_PASSWORD:-smartparking}" \
      --authenticationDatabase admin --quiet \
      --eval "printjson(db.getSiblingDB('smartparking').runCommand({compact: 'occupancy_events'}))" \
      >> "$LOGS/compact.run$run.log" 2>&1 < /dev/null
    csv_row compact_seconds "$((SECONDS - started))" s "$config;tool=mongosh"
    stats storage --variant "$VARIANT" --phase after_compact --csv "$CSV" --scenario "$SCENARIO" --run "$run" \
      --config "$config;tool=engine-stats"
  fi

  rm -rf "$archive" # measured above; the files are not kept (tens of MB per run)
}
