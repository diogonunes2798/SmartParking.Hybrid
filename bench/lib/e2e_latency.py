"""UC-01 end-to-end latency (D7, RNF-01) over a measurement window, from the Prometheus histogram
smartparking_ingestion_e2e_latency_seconds (Phase 9a). Collects; does not interpret.

Two steps, so that bench/run.sh can bracket exactly the measured part of a run (not the warm-up):
  python bench/lib/e2e_latency.py snapshot --url http://localhost:5080/metrics --url http://localhost:5090/metrics --out before.json
  ... load ...
  python bench/lib/e2e_latency.py diff --before before.json --after after.json --csv <results.csv>
                                       --scenario <s> --variant <v> --run <n> --config "<text>"

Both processes are scraped and summed because the histogram is recorded where the variant ends the flow: in the
Workers after the Redis update (hybrid) and at the commit in PostgreSQL (monolithic; ADR-0012, Phase 8 diary).
Percentiles are upper bounds of the histogram buckets (the metric name says so: *_bucket_upper); the mean is exact
(_sum / _count). With no events in the window, no rows are written and a notice goes to stderr.
"""
import argparse
import json
import re
import sys
import urllib.error
import urllib.request

from benchcsv import append_rows

NAME = "smartparking_ingestion_e2e_latency_seconds"
LINE = re.compile(r"^" + NAME + r"_(bucket|sum|count)(\{[^}]*\})? (\S+)$")
LE = re.compile(r'le="([^"]+)"')


def parse(text):
    """Exposition text -> {"buckets": {le: cumulative count}, "sum": s, "count": c}, summed over label sets."""
    result = {"buckets": {}, "sum": 0.0, "count": 0.0}
    for line in text.splitlines():
        m = LINE.match(line)
        if not m:
            continue
        kind, labels, value = m.group(1), m.group(2) or "", float(m.group(3))
        if kind == "bucket":
            le = LE.search(labels).group(1)
            result["buckets"][le] = result["buckets"].get(le, 0.0) + value
        else:
            result[kind] += value
    return result


def merge(snapshots):
    total = {"buckets": {}, "sum": 0.0, "count": 0.0}
    for s in snapshots:
        for le, v in s["buckets"].items():
            total["buckets"][le] = total["buckets"].get(le, 0.0) + v
        total["sum"] += s["sum"]
        total["count"] += s["count"]
    return total


def _bound(le):
    return float("inf") if le == "+Inf" else float(le)


def window(before, after):
    """Difference after - before (cumulative buckets stay cumulative)."""
    return {
        "buckets": {le: after["buckets"][le] - before["buckets"].get(le, 0.0) for le in after["buckets"]},
        "sum": after["sum"] - before["sum"],
        "count": after["count"] - before["count"],
    }


def quantile_upper(delta, q):
    """Smallest bucket upper bound whose cumulative count reaches q of the total (inf if only +Inf does)."""
    items = sorted(((_bound(le), c) for le, c in delta["buckets"].items()), key=lambda x: x[0])
    total = items[-1][1] if items else 0.0
    if total <= 0:
        return None
    for bound, cumulative in items:
        if cumulative >= q * total:
            return bound
    return float("inf")


def rows_from_window(delta):
    if delta["count"] <= 0:
        return []
    rows = [("e2e_events", delta["count"], "events"), ("e2e_mean", delta["sum"] / delta["count"] * 1000, "ms")]
    for q, label in ((0.5, "p50"), (0.95, "p95"), (0.99, "p99")):
        v = quantile_upper(delta, q)
        rows.append((f"e2e_{label}_bucket_upper", v * 1000 if v is not None else None, "ms"))
    return rows


def scrape(url):
    try:
        with urllib.request.urlopen(url, timeout=10) as r:
            return parse(r.read().decode())
    except (urllib.error.URLError, OSError) as e:
        print(f"e2e_latency: cannot scrape {url}: {e}", file=sys.stderr)
        raise


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("snapshot")
    s.add_argument("--url", action="append", required=True)
    s.add_argument("--out", required=True)
    d = sub.add_parser("diff")
    d.add_argument("--before", required=True)
    d.add_argument("--after", required=True)
    d.add_argument("--csv", required=True)
    d.add_argument("--scenario", required=True)
    d.add_argument("--variant", required=True, choices=["hybrid", "monolithic"])
    d.add_argument("--run", required=True, type=int)
    d.add_argument("--config", required=True)
    a = p.parse_args(argv)

    if a.cmd == "snapshot":
        snap = merge(scrape(u) for u in a.url)
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(snap, f)
        return 0

    with open(a.before, encoding="utf-8") as f:
        before = json.load(f)
    with open(a.after, encoding="utf-8") as f:
        after = json.load(f)
    rows = rows_from_window(window(before, after))
    if not rows:
        print("e2e_latency: no events in the window; no rows written", file=sys.stderr)
        return 0
    n = append_rows(a.csv, a.scenario, a.variant, a.run, a.config, rows)
    print(f"e2e_latency: {n} rows appended to {a.csv}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
