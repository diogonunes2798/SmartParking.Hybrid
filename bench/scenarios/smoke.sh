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
smoke_events() { # <seconds> <seed> [extra args]
  local seconds="$1" seed="$2"
  shift 2
  sim_run --scenario bench/scenarios/d8-reference.json --rate "$SMOKE_EVENT_RATE" --peak-factor 1 \
    --duration "$seconds" --seed "$seed" --run "$RUN" "$@"
}

scenario_warmup() {
  local k6_code=0 sim_code=0
  k6_run availability-smoke.js "" -e RATE="$SMOKE_READ_RATE" -e DURATION="${WARMUP_SECONDS}s" &
  local k6_pid=$!
  smoke_events "$WARMUP_SECONDS" 4242 || sim_code=$?
  wait "$k6_pid" || k6_code=$?
  run_meta warmup_k6_exit "$k6_code"
  run_meta warmup_simulator_exit "$sim_code"
}

scenario_run() {
  local run="$1" k6_code=0 sim_code=0
  local summary="$OUT_DIR/$STAMP.run$run.k6.json"
  local sim_csv="$LOGS/simulator.run$run.csv"
  local before="$LOGS/e2e-before.run$run.json" after="$LOGS/e2e-after.run$run.json"

  e2e_snapshot "$before"
  k6_run availability-smoke.js "$summary" -e RATE="$SMOKE_READ_RATE" -e DURATION="${SMOKE_SECONDS}s" &
  local k6_pid=$!
  smoke_events "$SMOKE_SECONDS" 42 --summary-csv "$sim_csv" || sim_code=$?
  wait "$k6_pid" || k6_code=$?
  sleep "$SMOKE_DRAIN_SECONDS"
  e2e_snapshot "$after"

  run_meta k6_exit "$k6_code"
  run_meta simulator_exit "$sim_code"
  k6_collect "$summary" "$CONFIG_BASE;tool=k6;script=availability-smoke.js"
  [[ -f "$sim_csv" ]] && sim_collect "$sim_csv" "$CONFIG_BASE;tool=simulator"
  e2e_collect "$before" "$after" "$CONFIG_BASE;tool=e2e-histogram"
}
