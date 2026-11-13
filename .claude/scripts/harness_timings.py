#!/usr/bin/env python3
"""How long each harness run took: one JSON line a run, in a file outside every checkout, and the reader of it.

`docs/harness-boundaries.md`, *The suite's shard runner*, holds the argument. `record` is a check script's to call;
`report` prints p50, p90 and the maximum per script and mode, and the slowest classes."""

import argparse
import datetime
import json
import math
import os
import platform
import statistics
import subprocess
import sys
from pathlib import Path

# Set by CI to a path the job uploads, and by a test to a file of its own.
ENV = "HARNESS_TIMINGS"


def timings_path(environ=None):
    """Where the lines go: `HARNESS_TIMINGS`, or the user's local cache, which no checkout, clean or sync, touches."""
    environ = os.environ if environ is None else environ
    if environ.get(ENV):
        return Path(environ[ENV])
    if os.name == "nt" and environ.get("LOCALAPPDATA"):
        base = Path(environ["LOCALAPPDATA"])
    else:
        base = Path(environ.get("XDG_CACHE_HOME") or Path.home() / ".cache")
    return base / "blueprint-harness" / "timings.jsonl"


def _git(root, *args):
    try:
        done = subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, encoding="utf-8",
                              errors="replace", check=False)
    except OSError:
        return None
    return (done.stdout.strip() or None) if done.returncode == 0 else None


def checkout(root):
    """The repository, branch and commit `root` holds; a worktree is named for the repository it belongs to."""
    common = _git(root, "rev-parse", "--path-format=absolute", "--git-common-dir")
    return {
        "repository": Path(common).parent.name if common else Path(root).resolve().name,
        "branch": _git(root, "branch", "--show-current"),
        "commit": _git(root, "rev-parse", "HEAD"),
    }


def record(entry, path=None):
    """Append `entry`, with when and where it ran, as one line; whether it was written.

    A write that fails is said and swallowed: a run's exit is its checks', never its record's.
    """
    path = timings_path() if path is None else Path(path)
    line = {"at": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
            "host": platform.node(), "os": sys.platform, **entry}
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(line, sort_keys=True) + "\n")
    except OSError as refused:
        print(f"harness_timings.py: the run's time was not recorded in {path}: {refused}", file=sys.stderr)
        return False
    return True


def percentile(values, share):
    """The nearest-rank percentile: a value some run actually took, never one between two."""
    ordered = sorted(values)
    return ordered[max(0, math.ceil(share * len(ordered)) - 1)]


def entries(text):
    """`(lines, unreadable)`: each line that reads as a run, and how many did not."""
    runs, bad = [], 0
    for line in text.splitlines():
        if not line.strip():
            continue
        try:
            run = json.loads(line)
        except ValueError:
            bad += 1
            continue
        if isinstance(run, dict) and isinstance(run.get("wall"), (int, float)):
            runs.append(run)
        else:
            bad += 1
    return runs, bad


def report(runs, top=15):
    """The report's lines: wall time per repository, script and mode, then each class's median over the runs."""
    groups = {}
    for run in runs:
        key = (str(run.get("repository")), str(run.get("script")), str(run.get("mode", "-")))
        groups.setdefault(key, []).append(run)
    lines = [f"{'repository':<22} {'script':<26} {'mode':<10} {'runs':>5} {'p50 s':>8} {'p90 s':>8} {'max s':>8}"
             f" {'wait p90':>9} {'failed':>7}"]
    for (repository, script, mode), group in sorted(groups.items()):
        walls = [run["wall"] for run in group]
        waits = [run.get("lock_wait", 0) for run in group]
        failed = sum(1 for run in group if run.get("exit") not in (0, None))
        lines.append(f"{repository:<22} {script:<26} {mode:<10} {len(group):>5} {percentile(walls, .5):>8.0f}"
                     f" {percentile(walls, .9):>8.0f} {max(walls):>8.0f} {percentile(waits, .9):>9.0f} {failed:>7}")
    seconds = {}
    for run in runs:
        for cls, taken in (run.get("classes") or {}).items():
            seconds.setdefault(cls, []).append(taken)
    if seconds:
        lines += ["", f"The {min(top, len(seconds))} slowest classes, median seconds over the runs that timed them:"]
        ranked = sorted(((statistics.median(times), len(times), cls) for cls, times in seconds.items()), reverse=True)
        lines += [f"  {median:7.1f}  {count:>3} runs  {cls}" for median, count, cls in ranked[:top]]
    return lines


def main(argv):
    parser = argparse.ArgumentParser(prog="harness_timings.py")
    sub = parser.add_subparsers(dest="command", required=True)
    shown = sub.add_parser("report", help="p50, p90 and maximum per script and mode, and the slowest classes")
    shown.add_argument("--top", type=int, default=15)
    kept = sub.add_parser("record", help="append one run of a check script")
    kept.add_argument("--script", required=True)
    kept.add_argument("--mode", default="whole")
    kept.add_argument("--wall", type=float, required=True)
    kept.add_argument("--exit", type=int, required=True)
    args = parser.parse_args(argv[1:])

    path = timings_path()
    if args.command == "record":
        record({"script": args.script, "mode": args.mode, "wall": round(args.wall, 1), "exit": args.exit,
                **checkout(Path.cwd())}, path)
        return 0
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        print(f"harness_timings.py: no run has been recorded in {path} yet")
        return 0
    runs, bad = entries(text)
    print(f"{len(runs)} runs recorded in {path}" + (f", {bad} lines unreadable" if bad else ""))
    print("\n".join(report(runs, args.top)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
