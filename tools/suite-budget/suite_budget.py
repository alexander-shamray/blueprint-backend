#!/usr/bin/env python3
"""Run each module of the review helpers' suite on its own, timed, and name every one that fails or overruns.

    py -3.12 tools/suite-budget/suite_budget.py [--dir .claude/scripts] [module ...]
"""

from __future__ import annotations

import argparse
import os
import signal
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WINDOWS = os.name == "nt"
SUITE = ".claude/scripts"
# Seconds per module on Windows: twice the figure docs/testing.md records for it, rounded up to ten seconds, so a
# loaded machine stays inside and a module grown well past double does not. One under half the default is not named.
CEILINGS = {
    "test_git_rebase_onto_main.py": 400,
    "test_git_argv_guard.py": 120,
    "test_git_pr_trailer_helpers.py": 80,
    "test_git_worktree_remove.py": 70,
    "test_pr_helpers.py": 60,
    "test_sweep_slices.py": 50,
    "test_index_search_hint.py": 40,
}
# A module this table does not name, a new one included, is still held to a budget rather than waved through.
DEFAULT_CEILING = 30
# A module still running at this multiple of its ceiling is stopped, so a hang is reported as one.
KILL_FACTOR = 3


@dataclass(frozen=True)
class Result:
    module: str
    seconds: float
    ceiling: float
    verdict: str
    output: str = ""


def modules(directory: Path) -> list[str]:
    return sorted(path.name for path in directory.glob("test_*.py"))


def verdict(seconds: float, returncode: int | None, ceiling: float) -> str:
    if returncode is None:
        return "hung"
    if returncode != 0:
        return "failed"
    return "over" if seconds > ceiling else "ok"


def stop(child: subprocess.Popen) -> None:
    # Its bash, git and helper processes still in its tree go too, or one left running skews the next module's figure.
    if WINDOWS:
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(child.pid)], capture_output=True)
    else:
        os.killpg(child.pid, signal.SIGKILL)
    child.wait()


def run(directory: Path, module: str, ceiling: float, kill_factor: float = KILL_FACTOR) -> Result:
    # discover from the module's directory, as the CI step does, so the helpers it imports resolve the same way.
    argv = [sys.executable, "-m", "unittest", "discover", "-s", str(directory), "-p", module]
    # A file rather than a pipe: a pipe a grandchild still holds would block the read and hang the runner itself.
    with tempfile.TemporaryFile() as output:
        started = time.monotonic()
        child = subprocess.Popen(argv, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT,
                                 start_new_session=not WINDOWS,
                                 creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if WINDOWS else 0)
        try:
            returncode = child.wait(timeout=ceiling * kill_factor)
        except subprocess.TimeoutExpired:
            stop(child)
            returncode = None
        seconds = time.monotonic() - started
        output.seek(0)
        text = output.read().decode("utf-8", "replace")
    return Result(module, seconds, ceiling, verdict(seconds, returncode, ceiling), text)


def report(results: list[Result]) -> str:
    lines = [f"{'module':<40} {'seconds':>8} {'ceiling':>8}  verdict"]
    lines += [f"{r.module:<40} {r.seconds:>8.1f} {r.ceiling:>8.0f}  {r.verdict}" for r in results]
    lines.append(f"{'total':<40} {sum(r.seconds for r in results):>8.1f}")
    for r in results:
        if r.verdict in ("failed", "hung"):
            lines += ["", f"--- {r.module} ({r.verdict})", r.output.rstrip()[-4000:]]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dir", default=SUITE, help="the suite's directory, relative to the repository root")
    parser.add_argument("module", nargs="*", help="run only these modules, by file name")
    args = parser.parse_args(argv)
    directory = ROOT / args.dir
    chosen = args.module or modules(directory)
    results = []
    for module in chosen:
        result = run(directory, module, CEILINGS.get(module, DEFAULT_CEILING))
        print(f"{result.module}: {result.seconds:.1f}s {result.verdict}", file=sys.stderr, flush=True)
        results.append(result)
    print(report(results))
    return 0 if all(r.verdict == "ok" for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
