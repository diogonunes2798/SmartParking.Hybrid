"""Shared CSV writer of the Phase 9 measurement scripts (spec §6).

Columns: cenario, variante, metrica, valor, unidade, execucao, configuracao. Same format as the simulator's
SummaryCsvWriter: header only when the file is new, values with at most three decimals, invariant culture, LF.
"""
import csv
import math
import os

HEADER = ["cenario", "variante", "metrica", "valor", "unidade", "execucao", "configuracao"]


def format_value(value):
    """0.###, like the simulator (C# "0.###"): 12.0 -> "12", 0.12345 -> "0.123", inf -> "inf"."""
    if value is None:
        return ""
    value = float(value)
    if math.isinf(value):
        return "inf" if value > 0 else "-inf"
    if math.isnan(value):
        return "nan"
    text = f"{round(value, 3):.3f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text


def append_rows(path, scenario, variant, run, configuration, rows):
    """rows: iterable of (metric, value, unit). Appends to path (creates it, with the header, when new)."""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    is_new = not os.path.exists(path) or os.path.getsize(path) == 0
    count = 0
    with open(path, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        if is_new:
            w.writerow(HEADER)
        for metric, value, unit in rows:
            w.writerow([scenario, variant, metric, format_value(value), unit, run, configuration])
            count += 1
    return count
