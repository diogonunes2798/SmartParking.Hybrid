"""Appends the rows of a §6 CSV written by another tool (the simulator's --summary-csv) to the results CSV, with the
cenario, variante and execucao of the bench run (Phase 9a).

Why: the simulator names its rows after its own scenario file (e.g. "d8" for bench/scenarios/d8-reference.json),
but in bench/run.sh the scenario is the bench scenario (e.g. "ingest-scale"). The configuration column is kept as the
simulator wrote it (its Describe() string), with the bench configuration prefixed.

Usage:
  python bench/lib/relabel_csv.py --from <tmp.csv> --csv <results.csv> --scenario <s> --variant <v> --run <n> --config "<text>"
"""
import argparse
import csv
import sys

from benchcsv import HEADER, append_rows


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--from", dest="source", required=True)
    p.add_argument("--csv", required=True)
    p.add_argument("--scenario", required=True)
    p.add_argument("--variant", required=True, choices=["hybrid", "monolithic"])
    p.add_argument("--run", required=True, type=int)
    p.add_argument("--config", required=True)
    a = p.parse_args(argv)

    with open(a.source, newline="", encoding="utf-8") as f:
        reader = csv.reader(f)
        header = next(reader, None)
        if header != HEADER:
            print(f"relabel_csv: {a.source} does not have the §6 header: {header}", file=sys.stderr)
            return 2
        rows = []
        configuration = None
        for r in reader:
            if not r:
                continue
            rows.append((r[2], float(r[3]) if r[3] not in ("", "inf", "-inf", "nan") else r[3], r[4]))
            configuration = r[6]
    config = f"{a.config};{configuration}" if configuration else a.config
    n = append_rows(a.csv, a.scenario, a.variant, a.run, config, rows)
    print(f"relabel_csv: {n} rows from {a.source} appended to {a.csv}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
