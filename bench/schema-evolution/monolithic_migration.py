"""UC-05 in the monolithic variant (Phase 8, step 5): applies the AddEnvironmentalColumns migration in the middle of a
load run and records what it costs (RNF-04 term of comparison, Cap. 6.5).

The hybrid variant needs no migration for the generation-2 fields (ADR-0011); the monolithic one adds three columns to
the partitioned table monolith.occupancy_events (ADR-0012). This script:
  1. waits for the run to start (first growth of smartparking_ingestion_e2e_latency_seconds_count on the API's
     /metrics: in the monolithic variant the API writes the events itself);
  2. at t0 + --ramp-start + --offset, starts a sampler session that reads pg_stat_activity every ~10 ms for
     --sample-seconds (sessions waiting on a lock, and inserts into occupancy_events among them);
  3. one second later runs the migration SQL (generated once with
     `dotnet ef migrations script AddMonolithicSchema AddEnvironmentalColumns -o bench/schema-evolution/monolithic-add-environmental-columns.sql`)
     through psql in the postgres container, and times it;
  4. appends the results to --out (spec §6 format).

Run it next to pilot_latency.py (same --run and --out), started just before the simulator:
  python bench/schema-evolution/monolithic_migration.py --out results/schema-evolution-pilot/<yyyy-MM-dd_HHmm>.csv --run 5
The latency phases themselves (before, during and after the ramp) come from pilot_latency.py --variant monolithic.
"""
import argparse, csv, os, re, subprocess, threading, time, urllib.request

COUNT = re.compile(r'^smartparking_ingestion_e2e_latency_seconds_count(\{[^}]*\})? (\S+)$')
HEADER = ["cenario", "variante", "metrica", "valor", "unidade", "execucao", "configuracao"]

SAMPLER = """
CREATE TEMP TABLE samples (at timestamptz, waiting int, inserts_waiting int);
DO $$
DECLARE stop timestamptz := clock_timestamp() + interval '{seconds} seconds';
BEGIN
    WHILE clock_timestamp() < stop LOOP
        PERFORM pg_stat_clear_snapshot();
        INSERT INTO samples
        SELECT clock_timestamp(),
               count(*) FILTER (WHERE wait_event_type = 'Lock'),
               count(*) FILTER (WHERE wait_event_type = 'Lock' AND query ILIKE '%INSERT INTO monolith.occupancy_events%')
        FROM pg_stat_activity
        WHERE datname = current_database() AND pid <> pg_backend_pid();
        PERFORM pg_sleep(0.01);
    END LOOP;
END $$;
SELECT count(*),
       count(*) FILTER (WHERE waiting > 0),
       coalesce(max(waiting), 0),
       coalesce(max(inserts_waiting), 0),
       coalesce(extract(epoch FROM max(at) FILTER (WHERE waiting > 0) - min(at) FILTER (WHERE waiting > 0)), 0)
FROM samples;
"""


def scrape_count(url):
    total = 0.0
    with urllib.request.urlopen(url, timeout=5) as r:
        for line in r.read().decode().splitlines():
            m = COUNT.match(line)
            if m:
                total += float(m.group(2))
    return total


def psql(args, user, db, stdin):
    cmd = ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", user, "-d", db, "-v", "ON_ERROR_STOP=1"] + args
    return subprocess.run(cmd, input=stdin, capture_output=True, text=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--metrics", default="http://localhost:5080/metrics")
    p.add_argument("--ramp-start", type=float, default=300)
    p.add_argument("--offset", type=float, default=5, help="seconds after the ramp start at which the migration runs")
    p.add_argument("--sample-seconds", type=float, default=20)
    p.add_argument("--sql", default="bench/schema-evolution/monolithic-add-environmental-columns.sql")
    p.add_argument("--wait-start", type=float, default=900)
    p.add_argument("--user", default=os.environ.get("POSTGRES_USER", "smartparking"))
    p.add_argument("--db", default=os.environ.get("POSTGRES_DB", "smartparking"))
    p.add_argument("--run", type=int, default=1)
    p.add_argument("--scenario", default="schema-evolution-pilot")
    p.add_argument("--config", default="bench/scenarios/schema-evolution-pilot.json")
    p.add_argument("--out", required=True)
    a = p.parse_args()

    with open(a.sql, encoding="utf-8-sig") as f:  # dotnet ef writes a BOM
        migration_sql = f.read()

    base = scrape_count(a.metrics)
    deadline = time.time() + a.wait_start
    while scrape_count(a.metrics) <= base:
        if time.time() > deadline:
            raise SystemExit("run never started")
        time.sleep(0.2)
    t0 = time.time()
    print("run started", flush=True)

    time.sleep(max(0.0, t0 + a.ramp_start + a.offset - 1 - time.time()))
    sampler_result = {}

    def sample():
        sampler_result["r"] = psql(["-At", "-F", ","], a.user, a.db, SAMPLER.format(seconds=a.sample_seconds))

    sampler = threading.Thread(target=sample)
    sampler.start()
    time.sleep(1)

    started = time.time()
    migration = psql(["-q"], a.user, a.db, migration_sql)
    duration = time.time() - started
    offset = started - t0
    print(f"migration exit {migration.returncode} in {duration:.3f} s at t0+{offset:.1f} s", flush=True)
    if migration.returncode != 0:
        print(migration.stderr, flush=True)

    sampler.join()
    r = sampler_result["r"]
    if r.returncode != 0:
        raise SystemExit("sampler failed: " + r.stderr)
    samples, waiting_samples, max_waiting, max_inserts_waiting, span = r.stdout.strip().splitlines()[-1].split(",")

    new = not os.path.exists(a.out)
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)
    with open(a.out, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        if new:
            w.writerow(HEADER)
        row = lambda metric, value, unit: w.writerow([a.scenario, "monolithic", metric, value, unit, a.run, a.config])
        row("migration_offset_from_start", f"{offset:.1f}", "s")
        row("migration_wall_time", f"{duration:.3f}", "s")
        row("migration_exit_code", migration.returncode, "code")
        row("lock_sampler_samples", samples, "count")
        row("lock_wait_samples", waiting_samples, "count")
        row("lock_wait_span", f"{float(span):.3f}", "s")
        row("max_sessions_waiting_lock", max_waiting, "count")
        row("max_inserts_waiting_lock", max_inserts_waiting, "count")
        # Must be 0: rows in DEFAULT break the pruning claims (review of Phase 8, migrations finding 3).
        default_rows = psql(["-At", "-c", "SELECT count(*) FROM monolith.occupancy_events_default"], a.user, a.db, "")
        row("default_partition_rows", default_rows.stdout.strip() or "error", "count")
    print(f"samples {samples}, with lock waits {waiting_samples}, max waiting {max_waiting} "
          f"(inserts {max_inserts_waiting}), span {float(span):.3f} s", flush=True)


if __name__ == "__main__":
    main()
