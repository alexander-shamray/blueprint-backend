#!/usr/bin/env python3
"""Refresh the code index after an edit, without making the edit wait.

Claude Code runs no `update` of its own, so between an edit and the query
that reports the index stale it describes a tree that has moved.

Spawned detached and never waited on, because it runs on every edit; its
streams go nowhere; and it returns 0 whatever happens, because an index
that cannot refresh is not a reason to fail the edit it followed.
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path


def main() -> int:
    root = os.environ.get("CLAUDE_PROJECT_DIR") or str(Path(__file__).resolve().parents[2])

    # CBX_NO_SKILL_AUTO_UPDATE, because an unpinned newer package rewrites the
    # tracked files under .claude/skills/codebase-index/ when it runs. The
    # `cbx` wrapper sets it; this calls the CLI directly, so it sets it here.
    environment = dict(os.environ, CBX_NO_SKILL_AUTO_UPDATE="1", PYTHONSAFEPATH="1")

    try:
        subprocess.Popen(
            ["codebase-index", "update"],
            cwd=root,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except OSError:
        # The CLI is not installed, or not on this PATH. Nothing to report to:
        # see the docstring on why this stays silent.
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
