# Bench scenario "smoke" (Phase 9a): proves the measurement chain end to end, not a measurement for the thesis.
# Sourced by bench/run.sh (see its header for the contract). Usage:
#   ./bench/run.sh smoke --runs 2 [--variant monolithic]
#
# Measured part: for SMOKE_SECONDS (30 s), at the same time
#   - k6 reads UC-03 at SMOKE_READ_RATE requests/s (bench/k6/availability-smoke.js) -> k6_* rows;
#   - the simulator sends UC-01 events at SMOKE_EVENT_RATE events/s over the D8 topology -> sim_* rows;
#   - the D7 histogram is read before and after (plus SMOKE_DRAIN_SECONDS for events still in flight) -> e2e_* rows.
# The warm-up is the same load for $WARMUP_SECONDS with another simulator seed; nothing from it is collected.

SMOKE_SECONDS=${SMOKE_SECONDS:-30}
SMOKE_READ_RATE=${SMOKE_READ_RATE:-20}
SMOKE_EVENT_RATE=${SMOKE_EVENT_RATE:-5}
SMOKE_DRAIN_SECONDS=${SMOKE_DRAIN_SECONDS:-5}

scenario_config() {
  printf 'duration=%ss;reads=%s/s;events=%s/s;drain=%ss' \
    "$SMOKE_SECONDS" "$SMOKE_READ_RATE" "$SMOKE_EVENT_RATE" "$SMOKE_DRAIN_SECONDS"
}

# Simulator load over the D8 topology (bench/scenarios/d8-reference.json), at a constant low rate.
# --wait-read-model 0: the base data step already waited for the read model; waiting again would delay the start of the
# simulator's load relative to k6's (review of Phase 9a, #3). The start times of both tools go to meta.json.
smoke_events() { # <seconds> <seed> [extra args]
  local seconds="$1" seed="$2"
  shift 2
  sim_run --scenario bench/scenarios/d8-reference.json --rate "$SMOKE_EVENT_RATE" --peak-factor 1 \
    --duration "$seconds" --seed "$seed" --run "$RUN" --wait-read-model 0 "$@"
}

# The warm-up ends with every spot free (the simulator's default --free-at-end true), and then waits for the drain:
# events of the warm-up still in the outbox or the stream must not reach the D7 histogram after the "before" snapshot
# of the measured part (review of Phase 9a, #2).
scenario_warmup() {
  local k6_code=0 sim_code=0
  run_meta warmup_started "$(now_ms)"
  k6_run availability-smoke.js "" -e RATE="$SMOKE_READ_RATE" -e DURATION="${WARMUP_SECONDS}s" &
  local k6_pid=$!
  smoke_events "$WARMUP_SECONDS" 4242 || sim_code=$?
  wait "$k6_pid" || k6_code=$?
  sleep "$SMOKE_DRAIN_SECONDS"
  run_meta warmup_k6_exit "$k6_code"
  run_meta warmup_simulator_exit "$sim_code"
}

# --free-at-end false: the end-of-run burst of FREED events (one per occupied spot, all with the same timestamp) would
# fall inside the measured D7 window and the sim_* counts (review of Phase 9a, #1). Each run starts from
# `docker compose down -v`, so the next run does not inherit the occupied spots.
scenario_run() {
  local run="$1" k6_code=0 sim_code=0
  local summary="$OUT_DIR/$STAMP.run$run.k6.json"
  local sim_csv="$LOGS/simulator.run$run.csv"
  local before="$LOGS/e2e-before.run$run.json" after="$LOGS/e2e-after.run$run.json"
  local config="$CONFIG_BASE"

  e2e_snapshot "$before"
  run_meta measure_started "$(now_ms)"
  k6_run availability-smoke.js "$summary" -e RATE="$SMOKE_READ_RATE" -e DURATION="${SMOKE_SECONDS}s" &
  local k6_pid=$!
  smoke_events "$SMOKE_SECONDS" 42 --free-at-end false --summary-csv "$sim_csv" || sim_code=$?
  run_meta simulator_finished "$(now_ms)"
  wait "$k6_pid" || k6_code=$?
  run_meta k6_finished "$(now_ms)"
  sleep "$SMOKE_DRAIN_SECONDS"
  e2e_snapshot "$after"

  run_meta k6_exit "$k6_code"
  run_meta simulator_exit "$sim_code"
  csv_row k6_exit_code "$k6_code" code "$config;tool=k6"
  csv_row sim_exit_code "$sim_code" code "$config;tool=simulator"
  k6_collect "$summary" "$config;tool=k6;script=availability-smoke.js"
  if [[ -f "$sim_csv" ]]; then
    sim_collect "$sim_csv" "$config;tool=simulator"
  fi
  e2e_collect "$before" "$after" "$config;tool=e2e-histogram"
}
