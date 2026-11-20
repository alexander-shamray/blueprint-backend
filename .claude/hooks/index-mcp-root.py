#!/usr/bin/env python3
"""Refuse a code-index MCP call over an index the refresh hook would not open,
as the `cbx` gate does, unjudged included; and one made from another checkout
than the server reads, naming the `cbx` line that reads this one. Any other
call it cannot judge goes through, and it returns 0."""

from __future__ import annotations

import importlib.util
import json
import os
import shlex
import sys
from pathlib import Path

MCP = "mcp__codebase-index__"
CBX = "bash .claude/skills/codebase-index/scripts/cbx"
REFRESH = Path(__file__).resolve().parent / "refresh-index.py"

# Each tool's subcommand, its arguments in the CLI's positional order, and
# whether it takes a session tag, after SKILL.md's route table. `healthcheck`
# is absent and never refused for a mismatch, since its `root` is how a session
# sees one; it is refused, as every tool is, over an index that is not openable.
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

# The options a subcommand's own `--help` lists a flag for, as option: (flag,
# what it holds); an option missing here leaves its call the bare form.
OPTIONS = {
    "search": {"limit": ("--limit", "count"), "offset": ("--offset", "count"),
               "token_budget": ("--token-budget", "count"), "mode": ("--mode", ("hybrid", "fts", "symbol", "vector")),
               "raw": ("--raw", "switch")},
    "explain": {"token_budget": ("--token-budget", "count"), "raw": ("--raw", "switch")},
}


def flag(option: tuple, value: object) -> list[str] | None:
    """The words carrying one option's value, or None when it cannot."""
    name, holds = option
    if holds == "switch":
        return [name] if value is True else [] if value is False else None
    if holds == "count":
        return [name, str(value)] if isinstance(value, int) and not isinstance(value, bool) and value >= 0 else None
    return [name, value] if value in holds else None


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
    """The `cbx` line asking what the call asked, or its bare form where the
    call holds an argument the line does not carry, since a line dropping one
    would answer another question."""
    sub, positional, tagged = COMMANDS[tool]
    bare = f"`{CBX} {sub}` with the call's arguments and `--json`"
    kind = given.get("kind")
    options = OPTIONS.get(sub, {})
    known = {*positional, *options, *(("session",) if tagged else ()), *(("kind",) if sub == "refs" else ())}
    if any(value is not None and key not in known for key, value in given.items()) or (
            kind is not None and kind not in ("callers", "all")):
        return bare
    words = [CBX, sub]
    for key in positional:
        value = given.get(key)
        if not carried(value):
            return bare
        words.append(shlex.quote(value))
    if kind is not None:
        words += ["--kind", kind]
    for key, option in options.items():
        if given.get(key) is not None:
            carrying = flag(option, given[key])
            if carrying is None:
                return bare
            words += carrying
    session = given.get("session")
    if session is not None:
        if not carried(session):
            return bare
        words += ["--session", shlex.quote(session)]
    return "`" + " ".join(words + ["--json"]) + "`"


def unopenable(roots: list[Path]) -> str | None:
    """Why the index under one of `roots` may not be opened, asked of the
    refresh hook's own `index_openable`, which the `cbx` gate asks too; a
    question it could not ask is a refusal, as the gate's is."""
    try:
        sys.dont_write_bytecode = True  # no `__pycache__` under `.claude/hooks/`
        spec = importlib.util.spec_from_file_location("refresh_index", REFRESH)
        hook = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(hook)
        for root in roots:
            if not hook.index_openable(root):
                return (f"refusing to open the index under {root}: a link on its path, a rollback "
                        "journal beside it, or a header that is not WAL mode. Remove what the branch "
                        "tracked under .claude/cache/codebase-index/ and run it again.")
    except Exception as error:  # noqa: BLE001 - unjudged is refused, as the gate refuses it
        return (f"refusing to open the code index: whether it is openable could not be judged "
                f"({type(error).__name__}: {error}).")
    return None


def answer(event: dict) -> str | None:
    """Why to refuse this call, or None to let it run."""
    name, given, cwd = event.get("tool_name"), event.get("tool_input"), event.get("cwd")
    if not isinstance(name, str) or not name.startswith(MCP):
        return None
    project = os.environ.get("CLAUDE_PROJECT_DIR")
    here = checkout_root(Path(cwd)) if isinstance(cwd, str) and cwd else None
    served = checkout_root(Path(project)) if project else None
    roots = [root for root in (served, here) if root is not None]
    if len(roots) == 2 and same(*roots):
        roots = roots[:1]
    refused = unopenable(roots)
    if refused:
        return f"index-mcp-root: {refused}"
    if name[len(MCP):] not in COMMANDS or here is None or served is None or same(here, served):
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
