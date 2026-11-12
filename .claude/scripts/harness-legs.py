#!/usr/bin/env python3
"""Whether a target's off-Linux harness legs test: prints `test=true` or `test=false` for `$GITHUB_OUTPUT`.

`docs/harness-boundaries.md`, *The harness legs decision*: only a clean merge reaching nothing the reach names skips.
"""

import json
import os
import subprocess
import sys
from pathlib import Path

from sync_pr import folded

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ".harness/manifest.json"
CONFIG = ".harness/config.json"

# Trees every target's suite reads or runs from whatever its manifest lists: the harness's own, which hold the
# target's `local` helpers and the settings, and the workflows, any of which may hold the legs' steps.
TREES = (".claude/", ".harness/", ".github/workflows/")
FILES = ("docs/harness-local.md",)
# At any depth: a nested ignore file is what `test_root_ignore_covers_nested.py` reads, and an attributes file sets
# the line endings a Windows checkout gets.
NAMES = (".gitignore", ".gitattributes")


def _git(root, *args):
    # A name git holds is bytes, so one that is not UTF-8 is carried through rather than raised.
    try:
        done = subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, encoding="utf-8",
                              errors="surrogateescape", check=False)
    except OSError:
        return None
    return done.stdout if done.returncode == 0 else None


def changed_paths(root):
    """What HEAD changes against its first parent, both sides of a move, or None unless HEAD is a merge read whole."""
    if _git(root, "rev-parse", "--verify", "--quiet", "HEAD^2") is None:
        return None
    out = _git(root, "diff", "--name-only", "--no-renames", "-z", "HEAD^1", "HEAD")
    return None if out is None else [path for path in out.split("\0") if path]


def _json(root, rel):
    try:
        return json.loads((root / rel).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def reach(root):
    """(managed paths, `local` globs), each read from the target, or None if either read failed."""
    manifest, config = _json(root, MANIFEST), _json(root, CONFIG)
    files = manifest.get("files") if isinstance(manifest, dict) else None
    local = config.get("local", []) if isinstance(config, dict) else None
    if not isinstance(files, dict) or not isinstance(local, list) or not all(isinstance(g, str) for g in local):
        return None
    return {path.lower() for path in files}, local


def reaches(path, managed, local):
    """Whether the reach names `path`, case folded as `apply` folds a local path, since a wrong skip loses a run."""
    lower = path.lower()
    return (lower.startswith(TREES) or lower in FILES or lower.rsplit("/", 1)[-1] in NAMES
            or lower in managed or folded(local, path))


def should_test(event, root):
    if event != "pull_request":
        return True
    paths = changed_paths(root)
    if not paths:
        return True
    read = reach(root)
    return read is None or any(reaches(path, *read) for path in paths)


def main(argv):
    if len(argv) != 1:
        print("usage: harness-legs.py        (it takes no arguments: GITHUB_EVENT_NAME says which event it decides)",
              file=sys.stderr)
        return 2
    print(f"test={'true' if should_test(os.environ.get('GITHUB_EVENT_NAME'), ROOT) else 'false'}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
