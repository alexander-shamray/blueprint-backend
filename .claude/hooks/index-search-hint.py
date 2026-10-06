#!/usr/bin/env python3
"""Point a session's own code search at the code index while it has made no
lookup, once and then every few searches; the transcript is the state, so it
writes nothing, and it returns 0 whatever happens."""

from __future__ import annotations

import json
import os
import re
import shlex
import sys
from pathlib import Path

CBX = "bash .claude/skills/codebase-index/scripts/cbx"
MCP = "mcp__codebase-index__"

# Each `cbx` subcommand a hint can name and the MCP tool answering the same
# question; `test_index_query_hint.py` holds the prompt hint's routes to it.
TOOLS = {
    "search": "search_code",
    "explain": "explain_code",
    "symbol": "find_symbol",
    "refs": "find_refs",
    "impact": "impact_of",
}

# The first search is hinted, then every EVERY-th after it, so a session that
# declines the index is reminded without being told on every call.
EVERY = 5

# The subcommands and tools that answer a question; stats, health and upkeep
# read the index without asking it anything.
ASKING = ("search", "explain", "architecture", "symbol", "refs", "impact",
          "diff-impact", "path", "describe", "verify")
UPKEEP = {"healthcheck", "index_stats"}
CLI_LOOKUP = re.compile(
    r"\b(?:cbx(?:\.ps1)?|codebase[-_]index)[\"']?\s+(?:" + "|".join(map(re.escape, ASKING)) + r")\b")

SEPARATORS = set("|&;()")
REDIRECTS = set("<>")

# The options of grep, rg and git grep that consume the next word.
VALUED = {"-e", "-f", "-A", "-B", "-C", "-m", "-g", "-t", "-T", "-M",
          "--glob", "--type", "--type-not", "--max-count", "--max-depth",
          "--include", "--exclude", "--exclude-dir",
          "--regexp", "--file", "--context", "--before-context", "--after-context"}

# The prose half of the tree is a grep question: which chapter owns a rule is
# not one the index answers, as the skill says.
PROSE = re.compile(r"(?:^|[\\/])docs(?:[\\/]|$)|\.md$|^md$|^\*\.md$|^markdown$")

IDENTIFIER = re.compile(r"^[^\W\d]\w*(?:\.[^\W\d]\w*)*$")


def segments(command: str):
    """Each simple command in `command` as (argv, piped): quotes honoured,
    redirections and their targets dropped, and `piped` when it reads the
    output of the one before it."""
    for line in command.splitlines():
        try:
            lexer = shlex.shlex(line, posix=True, punctuation_chars="|&;<>()")
            lexer.whitespace_split = True
            tokens = list(lexer)
        except ValueError:
            tokens = line.split()
        argv, piped, skip = [], False, False
        for index, token in enumerate(tokens):
            following = tokens[index + 1] if index + 1 < len(tokens) else ""
            if skip:
                skip = False
            elif token and set(token) <= SEPARATORS:
                yield argv, piped
                argv, piped = [], token in ("|", "|&")
            elif token and set(token) <= SEPARATORS | REDIRECTS:
                skip = True
            elif not (token.isdigit() and following and set(following) & REDIRECTS
                      and set(following) <= SEPARATORS | REDIRECTS):
                argv.append(token)
        yield argv, piped


def prose_only(paths: list[str], filters: list[str]) -> bool:
    """True when the search reads only prose: its file filters, where it
    names any, or else the paths it names; a `!` filter excludes."""
    named = filters or paths
    return bool(named) and all(not item.startswith("!") and PROSE.search(item.strip("'\"")) for item in named)


def located(cwd: str, path: str) -> Path:
    """`path` as the shell that ran it would find it, Git Bash's `/c/…`
    drive form included."""
    text = os.path.expanduser(path.strip("'\""))
    drive = re.match(r"^/([A-Za-z])(?=/|$)", text)
    if sys.platform == "win32" and drive:
        text = f"{drive.group(1)}:/{text[3:]}"
    return Path(cwd or ".", text)


def files_only(paths: list[str], cwd: str) -> bool:
    """True when every path the search names is one file, which a Read
    answers as well as the index would."""
    try:
        return bool(paths) and all(located(cwd, path).is_file() for path in paths)
    except (OSError, ValueError):
        return False


def checkout(cwd: str) -> Path | None:
    """The checkout `cwd` sits in, by the `.git` at or above it."""
    try:
        start = Path(cwd or ".").resolve()
        return next((place for place in (start, *start.parents) if (place / ".git").exists()), None)
    except OSError:
        return None


def elsewhere(paths: list[str], base: str, cwd: str) -> bool:
    """True when the search reads outside the checkout `cwd` sits in, whose
    index is the only one a hint can name."""
    root = checkout(cwd)
    try:
        return root is not None and any(
            not located(base, path).resolve().is_relative_to(root) for path in paths or ["."])
    except (OSError, ValueError):
        return False


def shell_search(command: str, cwd: str) -> str | None:
    """The pattern of a tree-wide grep, rg or git grep in `command`, or None.
    A grep with no recursion reads one file or a pipe, which the index does
    not replace, and neither does an rg filtering a pipe."""
    for argv, piped in segments(command):
        while argv and re.match(r"^[A-Za-z_]\w*=", argv[0]):
            argv = argv[1:]
        if not argv:
            continue
        program = argv[0].replace("\\", "/").rsplit("/", 1)[-1].removesuffix(".exe")
        base = cwd
        if program == "git" and "grep" in argv[1:4]:
            if argv[1] == "-C" and argv[2] != "grep":
                base = str(located(cwd, argv[2]))
            argv, tree = argv[argv.index("grep") + 1:], True
        elif program == "rg" and "--files" not in argv:
            argv, tree = argv[1:], not piped
        elif program in ("grep", "egrep", "fgrep"):
            argv, tree = argv[1:], False
        else:
            continue
        pattern, paths, filters, recursive = None, [], [], False
        index = 0
        while index < len(argv):
            word = argv[index]
            if word in VALUED and index + 1 < len(argv):
                value = argv[index + 1]
                if word in ("-e", "--regexp") and pattern is None:
                    pattern = value
                elif word in ("-g", "--glob", "-t", "--type", "--include"):
                    filters.append(value)
                index += 2
                continue
            if word.startswith("--include=") or word.startswith("--glob="):
                filters.append(word.split("=", 1)[1])
            elif word in ("--recursive", "--dereference-recursive") or re.match(r"^-[A-Za-z]*[rR]", word):
                recursive = True
            elif not word.startswith("-") or word == "-":
                if pattern is None:
                    pattern = word
                else:
                    paths.append(word)
            index += 1
        if program == "rg" and paths:
            tree = True
        if (tree or recursive) and pattern and not prose_only(paths, filters) \
                and not files_only(paths, base) and not elsewhere(paths, base, cwd):
            return pattern
    return None


def search(name: str, given: dict, cwd: str) -> str | None:
    """The pattern when this tool call is a code search, else None."""
    if name == "Grep":
        pattern = given.get("pattern")
        if not isinstance(pattern, str) or not pattern:
            return None
        paths = [given["path"]] if isinstance(given.get("path"), str) else []
        filters = [given[key] for key in ("glob", "type") if isinstance(given.get(key), str)]
        if prose_only(paths, filters) or files_only(paths, cwd) or elsewhere(paths, cwd, cwd):
            return None
        return pattern
    if name == "Bash" and isinstance(given.get("command"), str):
        return shell_search(given["command"], cwd)
    return None


def lookup(name: str, given: dict) -> bool:
    if name.startswith(MCP):
        return name[len(MCP):] not in UPKEEP
    return name == "Bash" and isinstance(given.get("command"), str) and bool(CLI_LOOKUP.search(given["command"]))


def calls(transcript: Path):
    """Every tool call the transcript records, as (id, name, input, cwd). Only
    the lines naming a tool call are parsed: a long session's transcript runs
    to tens of megabytes, and this runs inside the agent's turn."""
    with transcript.open("rb") as handle:
        for line in handle:
            if b'"tool_use"' not in line:
                continue
            try:
                entry = json.loads(line)
            except ValueError:
                continue
            if not isinstance(entry, dict):
                continue
            message, cwd = entry.get("message"), entry.get("cwd")
            content = message.get("content") if isinstance(message, dict) else None
            for block in content if isinstance(content, list) else ():
                if isinstance(block, dict) and block.get("type") == "tool_use":
                    given = block.get("input")
                    yield (block.get("id"), str(block.get("name")), given if isinstance(given, dict) else {},
                           cwd if isinstance(cwd, str) else None)


def in_worktree(cwd: str) -> bool:
    """True when `cwd` is in a linked worktree: a `.git` file whose gitdir
    carries a `commondir`. The MCP server keeps the root it started in."""
    try:
        start = Path(cwd).resolve()
        for candidate in (start, *start.parents):
            marker = candidate / ".git"
            if marker.is_dir():
                return False
            if marker.is_file():
                text = marker.read_text(encoding="utf-8").strip()
                if not text.startswith("gitdir:"):
                    return False
                git_dir = (candidate / text[len("gitdir:"):].strip()).resolve()
                return (git_dir / "commondir").is_file()
    except (OSError, ValueError):
        return False
    return False


def subject(pattern: str) -> tuple[str, bool]:
    """What to ask the index for, and whether that is a named symbol. A call
    form, `Name(` or `Name\\(`, and word boundaries are dropped first."""
    text = re.sub(r"\\b|\\<|\\>", "", pattern).strip()
    text = re.sub(r"\s*(?:\\\(|\()$", "", text)
    if IDENTIFIER.match(text):
        return text, True
    query = " ".join(re.sub(r"\W+", " ", text).split())
    return query or "X", False


def hint(pattern: str, worktree: bool) -> str:
    target, symbol = subject(pattern)
    target = target[:80]
    if symbol:
        tools = (TOOLS["refs"], TOOLS["symbol"])
        ask = (f"`{MCP}{TOOLS['refs']}` for what calls or uses `{target}`, "
               f"`{MCP}{TOOLS['symbol']}` for where it is defined")
        command = f'{CBX} refs "{target}" --json'
    else:
        tools = (TOOLS["search"],)
        ask = f"`{MCP}{TOOLS['search']}` with `limit: 3` and one session tag"
        command = f'{CBX} search "{target}" --limit 3 --session <tag> --json'
    load = ",".join(MCP + tool for tool in tools)
    where = (" This session is in a linked worktree and the MCP server serves"
             " the checkout it started in, so for code this branch changed use"
             " the CLI form, which answers from this worktree's own index."
             if worktree else "")
    return (
        "That was a code search, and this session has made no code-index "
        f"lookup yet. Ask the index: {ask}. The MCP tools are deferred, so "
        f"load them first with ToolSearch `select:{load}`; or load the "
        f"codebase-index skill and run `{command}`.{where} Grep stays right "
        "for text, error messages and docs/.")


def answer(event: dict) -> str | None:
    name, given = event.get("tool_name"), event.get("tool_input")
    if not isinstance(name, str) or not isinstance(given, dict):
        return None
    cwd = event.get("cwd") if isinstance(event.get("cwd"), str) else ""
    pattern = search(name, given, cwd)
    transcript = event.get("transcript_path")
    if pattern is None or not isinstance(transcript, str) or not transcript:
        return None
    current = event.get("tool_use_id")
    searches = 1
    for call_id, called, called_with, called_in in calls(Path(transcript)):
        if lookup(called, called_with):
            return None
        if call_id != current and search(called, called_with, called_in or cwd) is not None:
            searches += 1
    if (searches - 1) % EVERY:
        return None
    return hint(pattern, bool(cwd) and in_worktree(cwd))


def main() -> int:
    # Bytes, decoded here: Python reads a Windows pipe in the ANSI code page.
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
        found = answer(event) if isinstance(event, dict) else None
        if found:
            print(json.dumps({"hookSpecificOutput": {
                "hookEventName": "PostToolUse",
                "additionalContext": found}}))
    except Exception:  # noqa: BLE001 - a hint that fails says nothing
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
