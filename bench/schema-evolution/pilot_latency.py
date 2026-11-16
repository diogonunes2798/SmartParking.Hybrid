"""UC-05 pilot (Phase 7, step 5): D7 latency before, during and after the generation-2 ramp.

Reads the end-to-end latency histogram (smartparking_ingestion_e2e_latency_seconds, Workers /metrics) at the
phase boundaries themselves: t0 (first growth of the count, polled every 0.2 s), t0 + ramp start, t0 + ramp end
and t0 + duration + drain. The first version sampled every 15 s and took the last sample before each boundary,
which shifted the phases by about 15 s (Phase 7 code review, M2); the boundaries are now scheduled reads.

t0 is the first event processed, a fraction of a second after the simulator's first event; the reported
boundaries ("cut_*" rows) say where each phase really started. Percentiles are upper bucket bounds (Prometheus
histogram), so they are coarse: a pilot, not the Phase 9 measurement.

Rows are appended to --out (spec §6 format; header only for a new file), so the simulator's own summary
(--summary-csv) can go to the same file. Usage, started just before the simulator:
  python bench/schema-evolution/pilot_latency.py --out results/schema-evolution-pilot/<yyyy-MM-dd_HHmm>.csv --run 2
"""
import argparse, csv, os, re, time, urllib.request

LINE = re.compile(r'^smartparking_ingestion_e2e_latency_seconds_bucket\{[^}]*le="([^"]+)"\} (\S+)$')
HEADER = ["cenario", "variante", "metrica", "valor", "unidade", "execucao", "configuracao"]


def scrape(url):
    buckets = {}
    with urllib.request.urlopen(url, timeout=5) as r:
        for line in r.read().decode().splitlines():
            m = LINE.match(line)
            if m:
                le = float("inf") if m.group(1) == "+Inf" else float(m.group(1))
                buckets[le] = buckets.get(le, 0) + float(m.group(2))
    return dict(sorted(buckets.items()))


def diff(a, b):
    return {le: b[le] - a.get(le, 0) for le in b}


def quantile(delta, q):
    total = delta.get(float("inf"), 0)
    if total == 0:
        return None
    for le, c in delta.items():
        if c >= q * total:
            return le
    return float("inf")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--metrics", default="http://localhost:5090/metrics")
    p.add_argument("--duration", type=float, default=600)
    p.add_argument("--ramp-start", type=float, default=300)
    p.add_argument("--ramp-duration", type=float, default=60)
    p.add_argument("--drain", type=float, default=30, help="seconds after the run for closing events and the pipeline")
    p.add_argument("--wait-start", type=float, default=900, help="max seconds to wait for the run to start")
    p.add_argument("--run", type=int, default=1)
    p.add_argument("--scenario", default="schema-evolution-pilot")
    p.add_argument("--out", required=True)
    p.add_argument("--config", default="bench/scenarios/schema-evolution-pilot.json")
    # Phase 8: the monolithic variant records the histogram in the API (synchronous write), not in the Workers;
    # use --variant monolithic --metrics http://localhost:5080/metrics.
    p.add_argument("--variant", default="hybrid", choices=["hybrid", "monolithic"])
    a = p.parse_args()

    base = scrape(a.metrics)
    base_count = base.get(float("inf"), 0)
    deadline = time.time() + a.wait_start
    while True:
        time.sleep(0.2)
        snap = scrape(a.metrics)
        if snap.get(float("inf"), 0) > base_count:
            t0 = time.time()
            break
        if time.time() > deadline:
            raise SystemExit("run never started")
    print("run started", flush=True)

    boundaries = [a.ramp_start, a.ramp_start + a.ramp_duration, a.duration + a.drain]
    snaps = [(0.0, base)]
    for b in boundaries:
        wait = t0 + b - time.time()
        if wait > 0:
            time.sleep(wait)
        snaps.append((time.time() - t0, scrape(a.metrics)))
        print(f"read at t0 + {snaps[-1][0]:.2f} s (target {b} s)", flush=True)

    names = ["before_ramp", "ramp", "after_ramp"]
    phases = [(names[i], snaps[i], snaps[i + 1]) for i in range(3)] + [("whole_run", snaps[0], snaps[3])]

    new = not os.path.exists(a.out) or os.path.getsize(a.out) == 0
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)
    with open(a.out, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        if new:
            w.writerow(HEADER)
        row = lambda metric, value, unit: w.writerow([a.scenario, a.variant, metric, value, unit, a.run, a.config])
        for i, b in enumerate(boundaries):
            row(f"cut_{['ramp_start', 'ramp_end', 'run_end'][i]}", round(snaps[i + 1][0], 2), "s after t0")
        for name, (_, start), (_, stop) in phases:
            d = diff(start, stop)
            for label, q in (("p50", 0.5), ("p95", 0.95), ("p99", 0.99)):
                v = quantile(d, q)
                row(f"e2e_latency_{label}_{name}", "" if v is None else v, "s (bucket upper bound)")
                print(f"{name:12} {label} <= {v} s", flush=True)
            row(f"e2e_latency_count_{name}", int(d.get(float("inf"), 0)), "events")
            print(f"{name:12} n = {int(d.get(float('inf'), 0))}", flush=True)


if __name__ == "__main__":
    main()
