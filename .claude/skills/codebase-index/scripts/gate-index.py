#!/usr/bin/env python3
"""Refuse to start the code-index CLI over an index the refresh hook would not open, asking the hook's own
`index_openable` of the working directory and of the checkout git puts it in. Exit 3 is a refusal, a git that
cannot answer included, and 0 lets the CLI run: `docs/harness-boundaries.md`, *Index refresh and worktree
seeding*."""

import importlib.util
import os
import subprocess
import sys

HOOK = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "hooks", "refresh-index.py")


class GitUnanswered(Exception):
    """git did not say whether the working directory is in a checkout."""


def toplevel():
    """The checkout the working directory sits in, or None outside one.

    Only git's own "not a git repository" means outside: a git that is missing,
    stalls or fails otherwise has said nothing, and raises `GitUnanswered`.
    """
    environment = {name: value for name, value in os.environ.items()
                   if name.upper() not in ("GIT_DIR", "GIT_WORK_TREE")}
    environment["LC_ALL"] = "C"
    environment["LANGUAGE"] = "C"
    try:
        done = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"], capture_output=True,
            encoding="utf-8", errors="replace", timeout=10, env=environment)
    except (OSError, subprocess.SubprocessError) as error:
        raise GitUnanswered(str(error)) from error
    line = done.stdout.strip()
    if done.returncode == 0 and line:
        return line
    if "not a git repository" in done.stderr:
        return None
    raise GitUnanswered(done.stderr.strip() or f"exit {done.returncode}")


def main():
    # No `__pycache__` under `.claude/hooks/`: one more file deciding what runs.
    # And no `git` from the working directory, which Windows searches first.
    sys.dont_write_bytecode = True
    os.environ["NoDefaultCurrentDirectoryInExePath"] = "1"
    roots = [os.getcwd()]
    try:
        top = toplevel()
    except GitUnanswered as error:
        print(f"gate-index: git did not say which checkout {roots[0]} is in ({error}), so the index "
              "it would open cannot be judged; refusing to start the CLI.", file=sys.stderr)
        return 3
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
