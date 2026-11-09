#!/usr/bin/env python3
"""Which commits of a pull request the `commits` job builds; the README beside it owns why.

Prints `build <sha> <why>` or `skip <sha> <why>`, oldest first, for all but the head.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pipeline_gate


def _git(*arguments: str) -> str:
    return subprocess.run(
        ["git", *arguments],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="surrogateescape",
        check=True,
    ).stdout


def verdict(sha: str, filters: dict[str, list[str]]) -> str:
    """One commit's line: a skip only for a diff from one parent that no stage reads."""
    parents = _git("rev-list", "--parents", "-n", "1", sha).split()[1:]
    if len(parents) != 1:
        return f"build {sha} it has {len(parents)} parents, so no one diff says what it changed"

    # `-z` so a path git would quote is read as itself, and no renames so a move
    # out of a tree the build reads names that tree as well as where it went.
    changed = _git("diff", "--name-only", "--no-renames", "-z", parents[0], sha).split("\0")
    paths = [path for path in changed if path]
    if not paths:
        return f"build {sha} its diff reads as empty, which is no evidence for a skip"

    read = [path for path in paths if pipeline_gate.is_code(filters, path)]
    if read:
        return f"build {sha} a stage reads {read[0]}"
    return f"skip {sha} no stage reads any of its {len(paths)} changed path(s), the first {paths[0]}"


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print("usage: commit_builds.py <base-sha> <head-sha>", file=sys.stderr)
        return 2
    base, head = argv[1:]

    filters = pipeline_gate.read_filters_by_name(pipeline_gate.WORKFLOW.read_text(encoding="utf-8"))
    if problems := pipeline_gate.check_code_filters(filters):
        print("\n".join(problems), file=sys.stderr)
        return 1

    for sha in _git("rev-list", "--reverse", f"{base}..{head}").split():
        if sha != head:
            print(verdict(sha, filters))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
