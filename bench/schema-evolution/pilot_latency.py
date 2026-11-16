"""UC-05 pilot (Phase 7, step 5): D7 latency before, during and after the generation-2 ramp.

Scrapes the end-to-end latency histogram (smartparking_ingestion_e2e_latency_seconds, Workers /metrics) every
--interval seconds while the simulator runs. The run starts (t0) at the first scrape where the count grows; the
phases are cut from t0 using the scenario times (ramp from --ramp-start for --ramp-duration, run of --duration).
Percentiles are upper bucket bounds (Prometheus histogram), so they are coarse: a pilot, not the Phase 9 measurement.

Usage (with the simulator started right after):
  python bench/schema-evolution/pilot_latency.py --out results/schema-evolution-pilot/<date>.csv
"""
import argparse, csv, os, re, time, urllib.request

LINE = re.compile(r'^smartparking_ingestion_e2e_latency_seconds_bucket\{[^}]*le="([^"]+)"\} (\S+)$')


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
    p.add_argument("--interval", type=float, default=15)
    p.add_argument("--duration", type=float, default=600)
    p.add_argument("--ramp-start", type=float, default=300)
    p.add_argument("--ramp-duration", type=float, default=60)
    p.add_argument("--wait-start", type=float, default=900, help="max seconds to wait for the run to start")
    p.add_argument("--out", required=True)
    p.add_argument("--config", default="bench/scenarios/schema-evolution-pilot.json")
    a = p.parse_args()

    base = scrape(a.metrics)
    base_count = base.get(float("inf"), 0)
    deadline = time.time() + a.wait_start
    while True:
        time.sleep(1)
        snap = scrape(a.metrics)
        if snap.get(float("inf"), 0) > base_count:
            t0 = time.time() - 0.5
            break
        if time.time() > deadline:
            raise SystemExit("run never started")
    print(f"run started; scraping every {a.interval} s", flush=True)

    snaps = [(0.0, base)]
    end = t0 + a.duration + 30  # closing FREED events and drain
    while time.time() < end:
        time.sleep(a.interval)
        snaps.append((time.time() - t0, scrape(a.metrics)))

    def at(t):  # last snapshot not after t (first one for t = 0)
        best = snaps[0][1]
        for ts, s in snaps:
            if ts <= t:
                best = s
        return best

    phases = [
        ("before_ramp", 0, a.ramp_start),
        ("ramp", a.ramp_start, a.ramp_start + a.ramp_duration),
        ("after_ramp", a.ramp_start + a.ramp_duration, a.duration + 30),
        ("whole_run", 0, a.duration + 30),
    ]
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["cenario", "variante", "metrica", "valor", "unidade", "execucao", "configuracao"])
        for name, start, stop in phases:
            d = diff(at(start), at(stop))
            n = d.get(float("inf"), 0)
            for label, q in (("p50", 0.5), ("p95", 0.95), ("p99", 0.99)):
                v = quantile(d, q)
                w.writerow(["schema-evolution-pilot", "hybrid", f"e2e_latency_{label}_{name}",
                            "" if v is None else v, "s (bucket upper bound)", 1, a.config])
                print(f"{name:12} {label} <= {v} s", flush=True)
            w.writerow(["schema-evolution-pilot", "hybrid", f"e2e_latency_count_{name}", int(n), "events", 1, a.config])
            print(f"{name:12} n = {int(n)}", flush=True)


if __name__ == "__main__":
    main()
