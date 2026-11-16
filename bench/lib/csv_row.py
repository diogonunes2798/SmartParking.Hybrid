"""Appends one row to a spec §6 CSV (Phase 9a), e.g. the exit code of a load tool.

Usage:
  python bench/lib/csv_row.py --csv <results.csv> --scenario <s> --variant <v> --run <n> --metric <m> --value <x>
                              --unit <u> --config "<text>"
"""
import argparse
import sys

from benchcsv import append_rows


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--csv", required=True)
    p.add_argument("--scenario", required=True)
    p.add_argument("--variant", required=True, choices=["hybrid", "monolithic"])
    p.add_argument("--run", required=True, type=int)
    p.add_argument("--metric", required=True)
    p.add_argument("--value", required=True, type=float)
    p.add_argument("--unit", required=True)
    p.add_argument("--config", required=True)
    a = p.parse_args(argv)
    append_rows(a.csv, a.scenario, a.variant, a.run, a.config, [(a.metric, a.value, a.unit)])
    return 0


if __name__ == "__main__":
    sys.exit(main())
