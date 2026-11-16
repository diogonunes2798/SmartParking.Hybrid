"""Tests of the Phase 9a collectors. Run from the repository root:
    python -m unittest discover -s bench/tests -v
"""
import csv
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "lib"))

import benchcsv  # noqa: E402
import e2e_latency  # noqa: E402
import k6_to_csv  # noqa: E402
import relabel_csv  # noqa: E402

# Shape of a k6 1.4 --summary-export (legacy format): trend, counter, rate and gauge metrics, plus a tagged
# sub-metric exported because a threshold names it.
K6_SUMMARY = {
    "metrics": {
        "http_req_duration": {"avg": 12.5, "min": 1.0, "med": 10.0, "max": 80.25, "p(90)": 20.0, "p(95)": 31.4159,
                              "p(99)": 70.0},
        "http_req_duration{name:availability}": {"avg": 11.0, "min": 1.0, "med": 9.0, "max": 60.0, "p(90)": 19.0,
                                                 "p(95)": 30.0, "p(99)": 55.0,
                                                 "thresholds": {"max>=0": False}},
        "http_reqs": {"count": 600, "rate": 19.98},
        "http_req_failed": {"passes": 3, "fails": 597, "value": 0.005},
        "vus": {"value": 1, "min": 1, "max": 4},
        "data_received": {"count": 123456, "rate": 4115.2},
    }
}


class FormatValueTests(unittest.TestCase):
    def test_three_decimals_without_trailing_zeros(self):
        self.assertEqual(benchcsv.format_value(12.0), "12")
        self.assertEqual(benchcsv.format_value(0.12345), "0.123")
        self.assertEqual(benchcsv.format_value(31.4159), "31.416")
        self.assertEqual(benchcsv.format_value(-0.0001), "0")
        self.assertEqual(benchcsv.format_value(float("inf")), "inf")
        self.assertEqual(benchcsv.format_value(None), "")


class K6ToCsvTests(unittest.TestCase):
    def setUp(self):
        self.rows = {m: (v, u) for m, v, u in k6_to_csv.rows_from_summary(K6_SUMMARY)}

    def test_trend_in_ms_with_every_stat(self):
        self.assertEqual(self.rows["k6_http_req_duration_p95"], (31.4159, "ms"))
        for stat in ("avg", "min", "med", "max", "p90", "p95", "p99"):
            self.assertIn(f"k6_http_req_duration_{stat}", self.rows)

    def test_submetric_keeps_tag_value_and_drops_thresholds(self):
        self.assertEqual(self.rows["k6_http_req_duration_name_availability_p95"], (30.0, "ms"))
        self.assertFalse(any("thresholds" in m for m in self.rows))

    def test_counter_rate_and_gauge(self):
        self.assertEqual(self.rows["k6_http_reqs_count"], (600, "requests"))
        self.assertEqual(self.rows["k6_http_reqs_rate"], (19.98, "requests/s"))
        self.assertEqual(self.rows["k6_data_received_count"], (123456, "bytes"))
        self.assertEqual(self.rows["k6_http_req_failed_ratio"], (0.005, "ratio"))
        self.assertEqual(self.rows["k6_http_req_failed_true"], (3, "samples"))
        self.assertEqual(self.rows["k6_vus_max"], (4, "vus"))

    def test_custom_trend_unit(self):
        summary = {"metrics": {"consistency_window": {"avg": 1.0, "p(95)": 2.0}}}
        rows = {m: u for m, _, u in k6_to_csv.rows_from_summary(summary, {"consistency_window": "ms"})}
        self.assertEqual(rows["k6_consistency_window_p95"], "ms")
        rows = {m: u for m, _, u in k6_to_csv.rows_from_summary(summary)}
        self.assertEqual(rows["k6_consistency_window_p95"], "value")


class CsvFileTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.csv = os.path.join(self.dir, "out", "2026-11-16_2200.csv")

    def tearDown(self):
        shutil.rmtree(self.dir)

    def read(self):
        with open(self.csv, newline="", encoding="utf-8") as f:
            return list(csv.reader(f))

    def test_k6_main_writes_section6_columns_and_appends(self):
        summary = os.path.join(self.dir, "s.json")
        with open(summary, "w", encoding="utf-8") as f:
            json.dump(K6_SUMMARY, f)
        args = ["--summary", summary, "--csv", self.csv, "--scenario", "smoke", "--variant", "hybrid",
                "--config", "variant=hybrid;commit=abc"]
        k6_to_csv.main(args + ["--run", "1"])
        k6_to_csv.main(args + ["--run", "2"])
        rows = self.read()
        self.assertEqual(rows[0], benchcsv.HEADER)
        self.assertEqual(sum(1 for r in rows if r == benchcsv.HEADER), 1)
        self.assertTrue(all(len(r) == 7 for r in rows))
        p95 = [r for r in rows if r[2] == "k6_http_req_duration_p95"]
        self.assertEqual(p95[0], ["smoke", "hybrid", "k6_http_req_duration_p95", "31.416", "ms", "1",
                                  "variant=hybrid;commit=abc"])
        self.assertEqual([r[5] for r in p95], ["1", "2"])

    def test_relabel_replaces_scenario_and_run_and_keeps_simulator_configuration(self):
        source = os.path.join(self.dir, "sim.csv")
        with open(source, "w", encoding="utf-8", newline="") as f:
            f.write("cenario,variante,metrica,valor,unidade,execucao,configuracao\n")
            f.write("d8,hybrid,sim_sent,150,events,7,name=d8;rate=5\n")
            f.write("d8,hybrid,sim_request_p95,12.5,ms,7,name=d8;rate=5\n")
        code = relabel_csv.main(["--from", source, "--csv", self.csv, "--scenario", "smoke", "--variant", "hybrid",
                                 "--run", "3", "--config", "commit=abc;tool=simulator"])
        self.assertEqual(code, 0)
        rows = self.read()[1:]
        self.assertEqual(rows[0], ["smoke", "hybrid", "sim_sent", "150", "events", "3",
                                   "commit=abc;tool=simulator;name=d8;rate=5"])
        self.assertEqual(rows[1][3:5], ["12.5", "ms"])


EXPOSITION_BEFORE = """# TYPE smartparking_ingestion_e2e_latency_seconds histogram
smartparking_ingestion_e2e_latency_seconds_bucket{otel_scope_name="x",le="0.05"} 10
smartparking_ingestion_e2e_latency_seconds_bucket{otel_scope_name="x",le="0.1"} 10
smartparking_ingestion_e2e_latency_seconds_bucket{otel_scope_name="x",le="0.25"} 10
smartparking_ingestion_e2e_latency_seconds_bucket{otel_scope_name="x",le="+Inf"} 10
smartparking_ingestion_e2e_latency_seconds_sum{otel_scope_name="x"} 0.3
smartparking_ingestion_e2e_latency_seconds_count{otel_scope_name="x"} 10
smartparking_other_seconds_bucket{le="0.05"} 999
"""
EXPOSITION_AFTER = """smartparking_ingestion_e2e_latency_seconds_bucket{otel_scope_name="x",le="0.05"} 60
smartparking_ingestion_e2e_latency_seconds_bucket{otel_scope_name="x",le="0.1"} 105
smartparking_ingestion_e2e_latency_seconds_bucket{otel_scope_name="x",le="0.25"} 110
smartparking_ingestion_e2e_latency_seconds_bucket{otel_scope_name="x",le="+Inf"} 110
smartparking_ingestion_e2e_latency_seconds_sum{otel_scope_name="x"} 5.3
smartparking_ingestion_e2e_latency_seconds_count{otel_scope_name="x"} 110
"""


class E2eLatencyTests(unittest.TestCase):
    def test_window_excludes_events_before_the_first_snapshot(self):
        delta = e2e_latency.window(e2e_latency.parse(EXPOSITION_BEFORE), e2e_latency.parse(EXPOSITION_AFTER))
        rows = {m: (v, u) for m, v, u in e2e_latency.rows_from_window(delta)}
        self.assertEqual(rows["e2e_events"], (100, "events"))
        self.assertAlmostEqual(rows["e2e_mean"][0], 50.0)  # (5.3 - 0.3) / 100 s
        self.assertEqual(rows["e2e_p50_bucket_upper"], (50.0, "ms"))  # 50 of 100 in le=0.05
        self.assertEqual(rows["e2e_p95_bucket_upper"], (100.0, "ms"))  # 95 of 100 in le=0.1
        self.assertEqual(rows["e2e_p99_bucket_upper"], (250.0, "ms"))

    def test_two_processes_are_summed(self):
        one = e2e_latency.parse(EXPOSITION_AFTER)
        both = e2e_latency.merge([one, one])
        self.assertEqual(both["count"], 220)
        self.assertEqual(both["buckets"]["0.05"], 120)

    def test_no_events_gives_no_rows(self):
        snap = e2e_latency.parse(EXPOSITION_BEFORE)
        self.assertEqual(e2e_latency.rows_from_window(e2e_latency.window(snap, snap)), [])
        self.assertEqual(e2e_latency.rows_from_window(e2e_latency.parse("")), [])


class MetaTests(unittest.TestCase):
    def test_environment_keeps_overrides_and_hides_credentials(self):
        import write_meta
        saved = dict(os.environ)
        try:
            os.environ["Persistence__Mode"] = "Monolithic"
            os.environ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317"
            os.environ["ConnectionStrings__Postgres"] = "Host=x;Password=secret"
            os.environ["UNRELATED_VARIABLE"] = "x"
            # Windows upper-cases the names in os.environ (and .NET configuration ignores case), so compare upper-cased.
            env = {k.upper(): v for k, v in write_meta.relevant_environment().items()}
        finally:
            os.environ.clear()
            os.environ.update(saved)
        self.assertEqual(env["PERSISTENCE__MODE"], "Monolithic")
        self.assertEqual(env["OTEL_EXPORTER_OTLP_ENDPOINT"], "http://localhost:4317")
        self.assertEqual(env["CONNECTIONSTRINGS__POSTGRES"], "<set>")
        self.assertNotIn("UNRELATED_VARIABLE", env)


class CsvRowTests(unittest.TestCase):
    def test_one_row_with_section6_columns(self):
        import csv_row
        d = tempfile.mkdtemp()
        try:
            path = os.path.join(d, "r.csv")
            csv_row.main(["--csv", path, "--scenario", "smoke", "--variant", "monolithic", "--run", "2",
                          "--metric", "sim_exit_code", "--value", "4", "--unit", "code", "--config", "c=1"])
            with open(path, newline="", encoding="utf-8") as f:
                rows = list(csv.reader(f))
            self.assertEqual(rows, [benchcsv.HEADER, ["smoke", "monolithic", "sim_exit_code", "4", "code", "2", "c=1"]])
        finally:
            shutil.rmtree(d)


if __name__ == "__main__":
    unittest.main()
