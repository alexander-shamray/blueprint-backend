#!/usr/bin/env python3
"""Fail the build on a tracked path too long for a worktree to check out on Windows.

The subject is the tracked tree, not a list of long names; the README owns the claim and the arithmetic.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

WINDOWS_LONGEST_PATH = 259
WORKTREES_DIR = "C:/dev/ashamray/blueprint-backend/.claude/worktrees/"
LIMIT = 170


def tracked(root: Path) -> list[str]:
    # -z: without it git quotes a non-ASCII path, which would count its escapes.
    # Bytes, not text=True: a cp1252 default drops what it cannot decode.
    listing = subprocess.run(["git", "ls-files", "-z"], cwd=root, capture_output=True, check=True).stdout
    return sorted(name.decode("utf-8") for name in listing.split(b"\0") if name)


def too_long(paths: list[str], limit: int = LIMIT) -> list[str]:
    return [path for path in paths if len(path) > limit]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=REPO_ROOT)
    args = parser.parse_args(argv)

    paths = tracked(args.root)
    if not paths:
        print("path-length-gate: git lists no tracked path, so no path was judged", file=sys.stderr)
        return 1
    over = too_long(paths)
    for path in over:
        print(f"path-length-gate: {path}: {len(path)} characters, over the {LIMIT} a worktree's path leaves",
              file=sys.stderr)
    if over:
        return 1
    print(f"path-length-gate: {len(paths)} tracked paths, the longest {max(map(len, paths))} characters,"
          f" none over {LIMIT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
