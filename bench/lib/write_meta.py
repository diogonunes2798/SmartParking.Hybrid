"""Writes the <stamp>.meta.json next to a results CSV (Phase 9a): what is needed to reproduce and trace the session.

  start  : command, scenario, variant, runs, warm-up, commit (git rev-parse HEAD), uncommitted changes, branch,
           image references of docker-compose.yml (tag + digest), .NET SDK, Docker, OS, CPU, memory, start time.
  finish : end time and the exit codes of every run (appended to the same file).

The machine preparation (closed applications, charger, updates) is manual and goes to docs/DIARIO.md.

Usage:
  python bench/lib/write_meta.py start --out <meta.json> --scenario <s> --variant <v> --runs <n> --warmup <s> --command "<cmd>"
  python bench/lib/write_meta.py run --out <meta.json> --run <n> --key <name> --value <text>
  python bench/lib/write_meta.py finish --out <meta.json> --status <text>
"""
import argparse
import ctypes
import datetime
import json
import os
import platform
import re
import subprocess
import sys


def sh(*cmd):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=60, stdin=subprocess.DEVNULL).stdout.strip()
    except (OSError, subprocess.SubprocessError) as e:
        return f"unavailable ({e.__class__.__name__})"


def compose_images(path="docker-compose.yml"):
    images = {}
    service = None
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
            if m:
                service = m.group(1)
            m = re.match(r"^\s+image:\s*(\S+)", line)
            if m and service:
                images[service] = m.group(1)
    return images


def memory_bytes():
    if os.name == "nt":
        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
        s = MEMORYSTATUSEX()
        s.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(s)):
            return s.ullTotalPhys
        return None
    try:
        return os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES")
    except (ValueError, OSError, AttributeError):
        return None


ENV_PREFIXES = ("ASPNETCORE_", "DOTNET_", "OTEL_", "SMOKE_", "BENCH_")
SECRET_MARKERS = ("CONNECTIONSTRINGS", "PASSWORD", "SECRET", "TOKEN", "KEY")


def relevant_environment():
    """Inherited variables that reach the API, the Workers or a scenario and change their behaviour (review of Phase
    9a, #7): ASPNETCORE_*, DOTNET_*, OTEL_*, the scenario overrides (SMOKE_*, BENCH_*) and any configuration override
    (a name with "__", e.g. Persistence__Mode). Values that may hold credentials are recorded only as "<set>"."""
    found = {}
    for key, value in os.environ.items():
        if key.upper().startswith(ENV_PREFIXES) or "__" in key:
            secret = any(m in key.upper() for m in SECRET_MARKERS)
            found[key] = "<set>" if secret else value
    return dict(sorted(found.items()))


def now():
    return datetime.datetime.now().astimezone().isoformat(timespec="seconds")


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save(path, meta):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(meta, f, indent=2, ensure_ascii=False)
        f.write("\n")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("start")
    s.add_argument("--out", required=True)
    s.add_argument("--scenario", required=True)
    s.add_argument("--variant", required=True)
    s.add_argument("--runs", required=True, type=int)
    s.add_argument("--warmup", required=True, type=int)
    s.add_argument("--command", required=True)
    r = sub.add_parser("run")
    r.add_argument("--out", required=True)
    r.add_argument("--run", required=True, type=int)
    r.add_argument("--key", required=True)
    r.add_argument("--value", required=True)
    f = sub.add_parser("finish")
    f.add_argument("--out", required=True)
    f.add_argument("--status", required=True)
    a = p.parse_args(argv)

    if a.cmd == "start":
        status = sh("git", "status", "--porcelain", "--", ".", ":!results")
        meta = {
            "scenario": a.scenario,
            "variant": a.variant,
            "runs": a.runs,
            "warmup_seconds": a.warmup,
            "command": a.command,
            "started": now(),
            "git": {
                "commit": sh("git", "rev-parse", "HEAD"),
                "branch": sh("git", "rev-parse", "--abbrev-ref", "HEAD"),
                "uncommitted_changes": status.splitlines() if status else [],
                # With --allow-dirty the file names alone do not say what was measured (review of Phase 9a, #7).
                # Untracked files are listed above but are not in the patch.
                "uncommitted_diff": sh("git", "diff", "HEAD", "--", ".", ":!results") if status else "",
            },
            "environment": relevant_environment(),
            "images": compose_images(),
            "tools": {
                "dotnet_sdk": sh("dotnet", "--version"),
                "docker": sh("docker", "version", "--format", "{{.Server.Version}}"),
                "docker_compose": sh("docker", "compose", "version", "--short"),
                "python": platform.python_version(),
            },
            "machine": {
                "os": platform.platform(),
                "processor": platform.processor(),
                "logical_cpus": os.cpu_count(),
                "memory_bytes": memory_bytes(),
                "docker_cpus": sh("docker", "info", "--format", "{{.NCPU}}"),
                "docker_memory_bytes": sh("docker", "info", "--format", "{{.MemTotal}}"),
            },
            "runs_detail": {},
        }
        save(a.out, meta)
    elif a.cmd == "run":
        meta = load(a.out)
        meta.setdefault("runs_detail", {}).setdefault(str(a.run), {})[a.key] = a.value
        save(a.out, meta)
    else:
        meta = load(a.out)
        meta["finished"] = now()
        meta["status"] = a.status
        save(a.out, meta)
    return 0


if __name__ == "__main__":
    sys.exit(main())
