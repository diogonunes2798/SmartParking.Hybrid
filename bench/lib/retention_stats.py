"""Collectors of the retention scenario (Phase 10, RNF-05, ADR-0013). They only read and write CSV rows (spec §6).

Usage:
  python bench/lib/retention_stats.py storage --variant hybrid|monolithic --phase <p> --csv <f> --scenario <s> --run <n> --config "<t>"
  python bench/lib/retention_stats.py cpu-snapshot --out <json> [--pid name=<windows pid> ...]
  python bench/lib/retention_stats.py cpu-diff --before <json> --after <json> --phase <p> --events <n> --csv ... (labels)
  python bench/lib/retention_stats.py archive --path <dir> --csv ... (labels)
  python bench/lib/retention_stats.py count --variant hybrid|monolithic [--from <iso> --to <iso>]   (prints the number)

Storage (the stats of the guide, step 2):
  hybrid      after fsync (a checkpoint), db.occupancy_events.stats(): count, size, storageSize, freeStorageSize, totalIndexSize, indexSizes.<name>,
              and the block compressor of the collection (from wiredTiger.creationString, in the configuration column);
  monolithic  over the partitions of monolith.occupancy_events (DEFAULT included): rows, heap + TOAST
              (pg_table_size), indexes (pg_indexes_size), number of partitions.
  Rows: storage_<phase>_events, _data_bytes (hybrid: uncompressed BSON), _storage_bytes, _free_bytes (hybrid only),
  _index_bytes, _total_bytes (= storage + index), _index_<name>_bytes (hybrid), _partitions (monolithic).

CPU: usage_usec of the cgroup of each compose container (cpu.stat, cgroup v2), and the CPU time of the API and Workers
processes on the host (Get-Process). cpu-diff writes cpu_<phase>_<name>_seconds, cpu_<phase>_total_seconds and, with
--events > 0, cpu_<phase>_total_seconds_per_1e6_events.
"""
import argparse
import json
import os
import re
import subprocess
import sys

from benchcsv import append_rows

SERVICES = ["postgres", "mongo", "redis"]
MONGO_USER = os.environ.get("MONGO_USER", "smartparking")
MONGO_PASSWORD = os.environ.get("MONGO_PASSWORD", "smartparking")
PG_USER = os.environ.get("POSTGRES_USER", "smartparking")
PG_DB = os.environ.get("POSTGRES_DB", "smartparking")

# fsync first: WiredTiger updates the sizes of collStats at a checkpoint (about every 60 s); without it, a reading just
# after the load or the TTL deletes showed the sizes of the previous checkpoint (pilot of 17-11-2026, 09:49).
# Two fsyncs: WiredTiger writes changed pages to new blocks (copy-on-write), and the blocks of the previous checkpoint are
# free only once the next one completes. freeStorageSize is reported only with {freeStorage: 1}.
MONGO_STATS_JS = (
    "db.adminCommand({fsync: 1}); db.adminCommand({fsync: 1});"
    "const s = db.getSiblingDB('smartparking').occupancy_events.stats({freeStorage: 1});"
    "print(EJSON.stringify({count: s.count, size: s.size, storageSize: s.storageSize,"
    " freeStorageSize: s.freeStorageSize || 0, totalIndexSize: s.totalIndexSize, indexSizes: s.indexSizes,"
    " creationString: (s.wiredTiger || {}).creationString || ''}, {relaxed: true}));"
)

PG_STATS_SQL = (
    "SELECT json_build_object("
    "'rows', (SELECT count(*) FROM monolith.occupancy_events),"
    "'table_bytes', COALESCE(sum(pg_table_size(i.inhrelid)), 0),"
    "'index_bytes', COALESCE(sum(pg_indexes_size(i.inhrelid)), 0),"
    "'partitions', count(*))"
    " FROM pg_inherits i WHERE i.inhparent = 'monolith.occupancy_events'::regclass"
)


def run(args, stdin=None):
    return subprocess.run(args, check=True, capture_output=True, text=True, stdin=subprocess.DEVNULL).stdout


def mongo_eval(js):
    out = run(["docker", "compose", "exec", "-T", "mongo", "mongosh", "-u", MONGO_USER, "-p", MONGO_PASSWORD,
               "--authenticationDatabase", "admin", "--quiet", "--eval", js])
    return out.strip().splitlines()[-1]


def psql(sql):
    return run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", PG_USER, "-d", PG_DB, "-At", "-c", sql]).strip()


def compressor_of(creation_string):
    """block_compressor=zstd,... -> 'zstd' ('' when absent)."""
    m = re.search(r"block_compressor=([a-z]*)", creation_string or "")
    return m.group(1) if m else ""


def hybrid_storage_rows(phase, stats):
    p = f"storage_{phase}"
    rows = [
        (f"{p}_events", stats["count"], "events"),
        (f"{p}_data_bytes", stats["size"], "bytes"),
        (f"{p}_storage_bytes", stats["storageSize"], "bytes"),
        (f"{p}_free_bytes", stats.get("freeStorageSize", 0), "bytes"),
        (f"{p}_index_bytes", stats["totalIndexSize"], "bytes"),
        (f"{p}_total_bytes", stats["storageSize"] + stats["totalIndexSize"], "bytes"),
    ]
    for name, size in sorted((stats.get("indexSizes") or {}).items()):
        rows.append((f"{p}_index_{name}_bytes", size, "bytes"))
    return rows


def monolithic_storage_rows(phase, stats):
    p = f"storage_{phase}"
    return [
        (f"{p}_events", stats["rows"], "events"),
        (f"{p}_storage_bytes", stats["table_bytes"], "bytes"),
        (f"{p}_index_bytes", stats["index_bytes"], "bytes"),
        (f"{p}_total_bytes", stats["table_bytes"] + stats["index_bytes"], "bytes"),
        (f"{p}_partitions", stats["partitions"], "partitions"),
    ]


def parse_cpu_stat(text):
    """usage_usec of a cgroup v2 cpu.stat, in seconds."""
    for line in text.splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[0] == "usage_usec":
            return int(parts[1]) / 1e6
    raise ValueError("no usage_usec in cpu.stat (cgroup v1?)")


def process_cpu_seconds(windows_pid):
    out = run(["powershell", "-NoProfile", "-Command",
               f"(Get-Process -Id {int(windows_pid)}).TotalProcessorTime.TotalSeconds.ToString([cultureinfo]::InvariantCulture)"])
    return float(out.strip())


def cpu_diff_rows(phase, before, after, events):
    rows, total = [], 0.0
    for name in sorted(set(before) & set(after)):
        delta = after[name] - before[name]
        total += delta
        rows.append((f"cpu_{phase}_{name}_seconds", delta, "s"))
    rows.append((f"cpu_{phase}_total_seconds", total, "s"))
    if events and events > 0:
        rows.append((f"cpu_{phase}_total_seconds_per_1e6_events", total * 1e6 / events, "s"))
    return rows


def archive_rows(path):
    files = [os.path.join(path, f) for f in os.listdir(path)] if os.path.isdir(path) else []
    files = [f for f in files if f.endswith(".ndjson.gz")]
    return [("archive_files", len(files), "files"), ("archive_bytes", sum(os.path.getsize(f) for f in files), "bytes")]


def count_events(variant, since=None, until=None):
    if variant == "hybrid":
        query = "{}" if since is None else (
            f"{{timestamp: {{$gte: ISODate('{since}'), $lt: ISODate('{until}')}}}}")
        return int(mongo_eval(f"print(db.getSiblingDB('smartparking').occupancy_events.countDocuments({query}))"))
    where = "" if since is None else f" WHERE \"timestamp\" >= '{since}' AND \"timestamp\" < '{until}'"
    return int(psql(f"SELECT count(*) FROM monolith.occupancy_events{where}"))


def labels(p):
    p.add_argument("--csv", required=True)
    p.add_argument("--scenario", required=True)
    p.add_argument("--variant", required=True, choices=["hybrid", "monolithic"])
    p.add_argument("--run", required=True, type=int)
    p.add_argument("--config", required=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    storage = sub.add_parser("storage")
    labels(storage)
    storage.add_argument("--phase", required=True)

    snap = sub.add_parser("cpu-snapshot")
    snap.add_argument("--out", required=True)
    snap.add_argument("--pid", action="append", default=[], help="name=<windows pid> of a host process")

    diff = sub.add_parser("cpu-diff")
    labels(diff)
    diff.add_argument("--before", required=True)
    diff.add_argument("--after", required=True)
    diff.add_argument("--phase", required=True)
    diff.add_argument("--events", type=float, default=0)

    arch = sub.add_parser("archive")
    labels(arch)
    arch.add_argument("--path", required=True)

    count = sub.add_parser("count")
    count.add_argument("--variant", required=True, choices=["hybrid", "monolithic"])
    count.add_argument("--from", dest="since")
    count.add_argument("--to", dest="until")

    a = parser.parse_args(argv)

    if a.command == "storage":
        config = a.config
        if a.variant == "hybrid":
            stats = json.loads(mongo_eval(MONGO_STATS_JS))
            config += f";compressor={compressor_of(stats.get('creationString')) or 'snappy(default)'}"
            rows = hybrid_storage_rows(a.phase, stats)
        else:
            rows = monolithic_storage_rows(a.phase, json.loads(psql(PG_STATS_SQL)))
        append_rows(a.csv, a.scenario, a.variant, a.run, config, rows)
    elif a.command == "cpu-snapshot":
        values = {}
        for service in SERVICES:
            values[service] = parse_cpu_stat(run(["docker", "compose", "exec", "-T", service, "cat", "/sys/fs/cgroup/cpu.stat"]))
        for item in a.pid:
            name, pid = item.split("=", 1)
            values[name] = process_cpu_seconds(pid)
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(values, f)
    elif a.command == "cpu-diff":
        with open(a.before, encoding="utf-8") as f:
            before = json.load(f)
        with open(a.after, encoding="utf-8") as f:
            after = json.load(f)
        append_rows(a.csv, a.scenario, a.variant, a.run, a.config, cpu_diff_rows(a.phase, before, after, a.events))
    elif a.command == "archive":
        append_rows(a.csv, a.scenario, a.variant, a.run, a.config, archive_rows(a.path))
    elif a.command == "count":
        print(count_events(a.variant, a.since, a.until))
    return 0


if __name__ == "__main__":
    sys.exit(main())
