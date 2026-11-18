"""Tests of the pure parts of bench/lib/retention_stats.py (Phase 10)."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "lib"))

import retention_stats as rs  # noqa: E402


class RetentionStatsTests(unittest.TestCase):
    def test_cpu_stat_usage_is_read_in_seconds(self):
        text = "usage_usec 2500000\nuser_usec 2000000\nsystem_usec 500000\n"
        self.assertEqual(rs.parse_cpu_stat(text), 2.5)

    def test_cpu_stat_without_usage_fails_loudly(self):
        with self.assertRaises(ValueError):
            rs.parse_cpu_stat("user_usec 1\n")

    def test_cpu_diff_sums_the_common_names_and_normalises_per_million_events(self):
        rows = rs.cpu_diff_rows("load", {"mongo": 10.0, "api": 1.0, "gone": 5.0}, {"mongo": 13.0, "api": 2.0}, 500_000)
        values = {m: v for m, v, _ in rows}
        self.assertEqual(values["cpu_load_mongo_seconds"], 3.0)
        self.assertEqual(values["cpu_load_api_seconds"], 1.0)
        self.assertEqual(values["cpu_load_total_seconds"], 4.0)
        self.assertEqual(values["cpu_load_total_seconds_per_1e6_events"], 8.0)
        self.assertNotIn("cpu_load_gone_seconds", values)

    def test_cpu_diff_net_subtracts_the_idle_rate_over_the_window_and_only_keeps_the_given_components(self):
        idle = ({"postgres": 0.0, "mongo": 0.0}, {"postgres": 3.0, "mongo": 30.0}, 30.0)  # 0.1/s and 1/s
        rows = rs.cpu_diff_rows("retention", {"postgres": 10.0, "mongo": 10.0}, {"postgres": 16.0, "mongo": 90.0},
                                1_000_000, only={"postgres"}, idle=idle, seconds=20.0)
        values = {m: v for m, v, _ in rows}
        self.assertEqual(values["cpu_retention_postgres_seconds"], 6.0)
        self.assertAlmostEqual(values["cpu_retention_postgres_net_seconds"], 4.0)
        self.assertAlmostEqual(values["cpu_retention_total_net_seconds"], 4.0)
        self.assertAlmostEqual(values["cpu_retention_total_net_seconds_per_1e6_events"], 4.0)
        self.assertNotIn("cpu_retention_mongo_seconds", values)

    def test_cpu_diff_without_events_has_no_normalised_row(self):
        rows = rs.cpu_diff_rows("retention", {"mongo": 1.0}, {"mongo": 2.0}, 0)
        self.assertEqual([m for m, _, _ in rows], ["cpu_retention_mongo_seconds", "cpu_retention_total_seconds"])

    def test_hybrid_storage_rows_add_storage_and_indexes_and_list_each_index(self):
        stats = {"count": 10, "size": 1000, "storageSize": 400, "freeStorageSize": 50, "totalIndexSize": 300,
                 "indexFreeStorageSize": 20, "indexSizes": {"ux_eventId": 200, "_id_": 100}}
        values = {m: v for m, v, _ in rs.hybrid_storage_rows("after_load", stats)}
        self.assertEqual(values["storage_after_load_total_bytes"], 700)
        self.assertEqual(values["storage_after_load_free_bytes"], 50)
        self.assertEqual(values["storage_after_load_index_free_bytes"], 20)
        self.assertEqual(values["storage_after_load_used_bytes"], 630)
        self.assertEqual(values["storage_after_load_index__id__bytes"], 100)
        self.assertEqual(values["storage_after_load_index_ux_eventId_bytes"], 200)

    def test_hybrid_storage_rows_fail_loudly_without_the_free_space(self):
        # Phase 10a: the mongosh stats() helper dropped freeStorageSize and a `|| 0` hid it as 0 (Phase 10b).
        stats = {"count": 10, "size": 1000, "storageSize": 400, "totalIndexSize": 300, "indexFreeStorageSize": 0,
                 "indexSizes": {}}
        with self.assertRaises(KeyError):
            rs.hybrid_storage_rows("after_load", stats)

    def test_mongo_stats_use_collstats_and_never_default_the_free_space(self):
        self.assertIn("$collStats", rs.MONGO_STATS_JS)
        self.assertNotIn(".stats(", rs.MONGO_STATS_JS)
        self.assertNotIn("|| 0", rs.MONGO_STATS_JS)

    def test_monolithic_storage_rows(self):
        values = {m: v for m, v, _ in rs.monolithic_storage_rows(
            "after_retention", {"rows": 5, "table_bytes": 800, "index_bytes": 200, "partitions": 3})}
        self.assertEqual(values["storage_after_retention_total_bytes"], 1000)
        self.assertEqual(values["storage_after_retention_partitions"], 3)

    def test_compressor_is_read_from_the_creation_string(self):
        self.assertEqual(rs.compressor_of("access_pattern_hint=none,block_compressor=zstd,cache_resident=false"), "zstd")
        self.assertEqual(rs.compressor_of(""), "")

    def test_archive_counts_only_the_archive_files(self):
        with tempfile.TemporaryDirectory() as folder:
            for name, size in [("occupancy-events-2026-11-12.ndjson.gz", 10), ("x.partial", 99), ("b.ndjson.gz", 5)]:
                with open(os.path.join(folder, name), "wb") as f:
                    f.write(b"\0" * size)
            values = {m: v for m, v, _ in rs.archive_rows(folder)}
        self.assertEqual(values, {"archive_files": 2, "archive_bytes": 15})
        self.assertEqual({m: v for m, v, _ in rs.archive_rows(os.path.join(folder, "missing"))},
                         {"archive_files": 0, "archive_bytes": 0})


if __name__ == "__main__":
    unittest.main()
