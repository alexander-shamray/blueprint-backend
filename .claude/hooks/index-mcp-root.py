#!/usr/bin/env python3
"""Refuse a code-index MCP call made from another checkout than the one the
server reads, which is where the session started, and name the `cbx` command
that reads this one. A call it cannot judge goes through, and it returns 0."""

from __future__ import annotations

import json
import os
import shlex
import sys
from pathlib import Path

MCP = "mcp__codebase-index__"
CBX = "bash .claude/skills/codebase-index/scripts/cbx"

# Each tool's subcommand, its arguments in the CLI's positional order, and
# whether it takes a session tag, after SKILL.md's route table. `healthcheck`
# is absent and never refused: its `root` is how a session sees the mismatch.
COMMANDS = {
    "search_code": ("search", ("query",), True),
    "explain_code": ("explain", ("query",), True),
    "find_symbol": ("symbol", ("name",), False),
    "find_refs": ("refs", ("symbol",), False),
    "impact_of": ("impact", ("target",), False),
    "impact_of_diff": ("diff-impact", (), False),
    "path_between": ("path", ("source", "target"), False),
    "describe_symbol": ("describe", ("symbol",), False),
    "verify_evidence": ("verify", (), True),
    "architecture_overview": ("architecture", (), False),
    "index_stats": ("stats", (), False),
}


def checkout_root(start: Path) -> Path | None:
    """The checkout `start` sits in, by the `.git` at or above it."""
    try:
        start = start.absolute()
        for candidate in (start, *start.parents):
            if (candidate / ".git").exists():
                return candidate
    except OSError:
        return None
    return None


def same(one: Path, other: Path) -> bool:
    try:
        return os.path.samefile(one, other)
    except OSError:
        return os.path.normcase(str(one)) == os.path.normcase(str(other))


def carried(value: object) -> bool:
    """Whether a value fits one line inside the reason's code span."""
    return isinstance(value, str) and bool(value) and not any(c in value for c in "\n\r`")


def command(tool: str, given: dict) -> str:
    """The `cbx` line asking what the call asked, or its bare form where an
    argument cannot be carried."""
    sub, positional, tagged = COMMANDS[tool]
    words = [CBX, sub]
    for key in positional:
        value = given.get(key)
        if not carried(value):
            return f"`{CBX} {sub}` with the call's arguments and `--json`"
        words.append(shlex.quote(value))
    kind = given.get("kind")
    if sub == "refs" and kind in ("callers", "all"):
        words += ["--kind", kind]
    session = given.get("session")
    if tagged and carried(session):
        words += ["--session", shlex.quote(session)]
    return "`" + " ".join(words + ["--json"]) + "`"


def answer(event: dict) -> str | None:
    """Why to refuse this call, or None to let it run."""
    name, given, cwd = event.get("tool_name"), event.get("tool_input"), event.get("cwd")
    if not isinstance(name, str) or not name.startswith(MCP) or name[len(MCP):] not in COMMANDS:
        return None
    project = os.environ.get("CLAUDE_PROJECT_DIR")
    if not isinstance(cwd, str) or not cwd or not project:
        return None
    here, served = checkout_root(Path(cwd)), checkout_root(Path(project))
    if here is None or served is None or same(here, served):
        return None
    run = command(name[len(MCP):], given if isinstance(given, dict) else {})
    return (f"The codebase-index MCP server reads {served}, where this session started, and not {here}, "
            "where it now works, so its answers miss this checkout's changes. Load the codebase-index "
            f"skill, whose grant the CLI runs under, and run {run} from this checkout's root, alone on "
            "its line, with no pipe, `;` or `&&`, which the index guard refuses; the CLI reads this "
            "checkout's own index.")


def main() -> int:
    # Bytes, decoded here: Python reads a Windows pipe in the ANSI code page.
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
        found = answer(event) if isinstance(event, dict) else None
        if found:
            print(json.dumps({"hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": found}}))
    except Exception:  # noqa: BLE001 - a call it cannot judge goes through
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
