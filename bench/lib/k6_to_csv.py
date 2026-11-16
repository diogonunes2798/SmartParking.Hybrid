"""Converts a k6 --summary-export JSON into rows of the spec §6 CSV (Phase 9a). Collects; does not interpret.

Every metric of the summary becomes rows named k6_<metric>[_<tag value>]_<stat>:
  - trend   (avg, min, med, max, p(90), p(95), p(99)) -> ms for k6's time trends, otherwise the --unit given;
  - counter (count, rate)                              -> <unit> and <unit>/s;
  - rate    (value, passes, fails)                     -> ratio, and counts of true / false samples;
  - gauge   (value, min, max).
A tagged sub-metric (e.g. http_req_duration{name:availability}, exported because a threshold names it) keeps the tag key and
value in its name: k6_http_req_duration_name_availability_p95. Threshold results are not copied (they are pass/fail
marks of the script, not measurements).

Usage:
  python bench/lib/k6_to_csv.py --summary <file.json> --csv <results.csv> --scenario <s> --variant <v> --run <n>
                                --config "<text>" [--unit my_trend=ms ...]
"""
import argparse
import json
import re
import sys

from benchcsv import append_rows

TIME_TRENDS = {"http_req_duration", "http_req_blocked", "http_req_connecting", "http_req_tls_handshaking",
               "http_req_sending", "http_req_waiting", "http_req_receiving", "iteration_duration", "group_duration"}
COUNTER_UNITS = {"http_reqs": "requests", "iterations": "iterations", "dropped_iterations": "iterations",
                 "data_sent": "bytes", "data_received": "bytes"}
GAUGE_UNITS = {"vus": "vus", "vus_max": "vus"}
TREND_STATS = ["avg", "min", "med", "max", "p(90)", "p(95)", "p(99)"]
SUBMETRIC = re.compile(r"^([A-Za-z0-9_]+)\{(.*)\}$")


def metric_name(key):
    """http_req_duration{name:availability,status:200} -> ("http_req_duration", "k6_http_req_duration_name_availability_status_200")."""
    m = SUBMETRIC.match(key)
    if not m:
        return key, f"k6_{key}"
    base, tags = m.group(1), m.group(2)
    parts = [t.replace(":", "_", 1) for t in tags.split(",") if t]  # key and value: "true" alone would be ambiguous
    suffix = "_".join(re.sub(r"[^A-Za-z0-9]+", "_", p).strip("_") for p in parts)
    return base, f"k6_{base}_{suffix}" if suffix else f"k6_{base}"


def stat_name(stat):
    return stat.replace("(", "").replace(")", "").replace(".", "_")


def rows_from_summary(summary, units=None):
    units = units or {}
    rows = []
    for key in sorted(summary.get("metrics", {})):
        data = summary["metrics"][key]
        base, name = metric_name(key)
        if "count" in data and "rate" in data:  # counter
            unit = units.get(base, COUNTER_UNITS.get(base, "count"))
            rows.append((f"{name}_count", data["count"], unit))
            rows.append((f"{name}_rate", data["rate"], f"{unit}/s"))
        elif "passes" in data and "fails" in data:  # rate
            rows.append((f"{name}_ratio", data.get("value"), "ratio"))
            rows.append((f"{name}_true", data["passes"], "samples"))
            rows.append((f"{name}_false", data["fails"], "samples"))
        elif "avg" in data or "med" in data or any(s.startswith("p(") for s in data):  # trend (a gauge also has min/max)
            unit = units.get(base, "ms" if base in TIME_TRENDS else "value")
            for stat in sorted((s for s in data if s != "thresholds"), key=_trend_order):
                rows.append((f"{name}_{stat_name(stat)}", data[stat], unit))
        elif "value" in data:  # gauge
            unit = units.get(base, GAUGE_UNITS.get(base, "value"))
            for stat in ("value", "min", "max"):
                if stat in data:
                    rows.append((f"{name}_{stat}", data[stat], unit))
        else:
            print(f"k6_to_csv: metric {key} has an unknown shape {sorted(data)}; skipped", file=sys.stderr)
    return rows


def _trend_order(stat):
    return (TREND_STATS.index(stat), stat) if stat in TREND_STATS else (len(TREND_STATS), stat)


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--summary", required=True)
    p.add_argument("--csv", required=True)
    p.add_argument("--scenario", required=True)
    p.add_argument("--variant", required=True, choices=["hybrid", "monolithic"])
    p.add_argument("--run", required=True, type=int)
    p.add_argument("--config", required=True)
    p.add_argument("--unit", action="append", default=[], help="metric=unit for a custom trend or counter")
    a = p.parse_args(argv)
    units = dict(u.split("=", 1) for u in a.unit)
    with open(a.summary, encoding="utf-8") as f:
        summary = json.load(f)
    rows = rows_from_summary(summary, units)
    n = append_rows(a.csv, a.scenario, a.variant, a.run, a.config, rows)
    print(f"k6_to_csv: {n} rows from {a.summary} appended to {a.csv}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
