#!/usr/bin/env python3
"""Refuse to start the code-index CLI over an index the refresh hook would not
open, asking the hook's own `index_openable` of the working directory and of
the checkout git puts it in. Exit 3 is a refusal and 0 lets the CLI run:
`docs/harness-boundaries.md`, *Index refresh and worktree seeding*."""

import importlib.util
import os
import subprocess
import sys

HOOK = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "hooks", "refresh-index.py")


def toplevel():
    """The checkout the working directory sits in, or None outside one."""
    environment = {name: value for name, value in os.environ.items()
                   if name.upper() not in ("GIT_DIR", "GIT_WORK_TREE")}
    try:
        done = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"], capture_output=True,
            encoding="utf-8", errors="replace", timeout=10, env=environment)
    except (OSError, subprocess.SubprocessError):
        return None
    line = done.stdout.strip()
    return line if done.returncode == 0 and line else None


def main():
    # No `__pycache__` under `.claude/hooks/`: one more file deciding what runs.
    # And no `git` from the working directory, which Windows searches first.
    sys.dont_write_bytecode = True
    os.environ["NoDefaultCurrentDirectoryInExePath"] = "1"
    roots = [os.getcwd()]
    top = toplevel()
    if top is not None:
        roots.append(top)
    spec = importlib.util.spec_from_file_location("refresh_index", HOOK)
    hook = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(hook)
    for root in dict.fromkeys(os.path.normpath(root) for root in roots):
        if not hook.index_openable(root):
            print(f"gate-index: refusing to open the index under {root}: a link on its path, a "
                  "rollback journal beside it, or a header that is not WAL mode. Remove what the "
                  "branch tracked under .claude/cache/codebase-index/ and run it again.",
                  file=sys.stderr)
            return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
