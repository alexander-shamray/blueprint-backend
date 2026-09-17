#!/usr/bin/env python3
"""Refuse a codebase-index / cbx invocation that is more than one command.

Prefix grants auto-approve `Bash(… search:*)`, so
`codebase-index search x; rm -rf /` and `codebase-index search "$(evil)"`
keep the approved prefix. Matching is against the typed string; the shell
still executes operators and substitutions. This hook sees the typed string
and refuses substitutions, extra command runs, and subcommands the wrapper
itself refuses (`graph`, `clean`, `init`, `watch`). Honest traffic is a
single `cbx` / `codebase-index` invocation.

Protocol: PreToolUse, matcher `Bash`. Exit 0 and print nothing to allow;
print the deny JSON to refuse. A malformed event is allowed, as
`guard-git-argv.py` does, so a defect here cannot take the session down.
"""

import importlib.util
import json
import shlex
import sys
import traceback
from pathlib import Path

GIT_GUARD = Path(__file__).with_name("guard-git-argv.py")
ALLOWED = (
    "search", "explain", "architecture", "symbol", "refs", "impact",
    "diff-impact", "path", "describe", "verify", "stats", "doctor",
    "update", "index",
)
INDEX_NAMES = ("codebase-index", "cbx", "codebase-index.exe", "cbx.exe")


def _git_guard():
    spec = importlib.util.spec_from_file_location("guard_git_argv", GIT_GUARD)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _base_name(token):
    name = token.replace("\\", "/").rsplit("/", 1)[-1]
    return name.lower()


def _run_is_index(run):
    if not run:
        return False
    if _base_name(run[0]) in INDEX_NAMES:
        return True
    if _base_name(run[0]) in ("bash", "sh") and len(run) > 1:
        if _base_name(run[1]) in INDEX_NAMES:
            return True
    joined = " ".join(run[:8])
    return "codebase_index" in joined or "codebase-index" in joined


def _subcommand(run):
    words = list(run)
    if words and _base_name(words[0]) in ("bash", "sh"):
        words = words[1:]
    if words and _base_name(words[0]) in INDEX_NAMES:
        words = words[1:]
    if not words:
        return ""
    if words[0] == "-m" and len(words) > 1 and words[1] == "codebase_index":
        return words[2] if len(words) > 2 else ""
    return words[0].lstrip("-") if words else ""


def offence(command, git):
    if git.substitutions(command):
        if not _mentions_index(command):
            return None
        return (
            "command substitution in an index invocation is refused: the "
            "prefix grant matches the typed string while the shell still "
            "runs `$(…)` and backticks. Pass the query as a quoted word, "
            "not as a substitution."
        )
    resolved = git.separate_lines(
        git.join_continuations(git.strip_comments(git.strip_heredocs(command))))
    try:
        lexer = shlex.shlex(resolved, posix=True, punctuation_chars=True)
        lexer.commenters = ""
        lexer.whitespace_split = True
        tokens = list(lexer)
    except ValueError:
        if _mentions_index(command):
            return (
                "an index invocation could not be tokenised, so extra "
                "commands cannot be ruled out; refusing rather than "
                "admitting what could not be read."
            )
        return None

    runs = [run for run in git.command_runs(tokens) if run]
    index_runs = [run for run in runs if _run_is_index(run)]
    if not index_runs:
        return None
    if len(runs) > 1:
        return (
            "an index invocation may not share the line with another "
            "command: the prefix grant would auto-approve `search x; …`. "
            "Run it alone."
        )
    sub = _subcommand(index_runs[0])
    if sub and sub not in ALLOWED:
        return (
            f"`{sub}` is not an auto-approved index subcommand; "
            f"allowed: {' '.join(ALLOWED)}."
        )
    return None


def _mentions_index(command):
    return "codebase-index" in command or "codebase_index" in command or (
        "/cbx" in command.replace("\\", "/") or command.split()[:1] == ["cbx"]
        or " cbx " in f" {command} "
    )


def main():
    try:
        event = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        print("guard-index-argv: unreadable hook event; not judging",
              file=sys.stderr)
        return 0

    if event.get("tool_name") != "Bash":
        return 0
    command = (event.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        return 0

    try:
        git = _git_guard()
        reason = offence(command, git)
    except Exception:  # noqa: BLE001
        traceback.print_exc(file=sys.stderr)
        reason = (
            "this command crashed the index argv guard, so nothing about "
            "it has been established; refusing rather than admitting what "
            "could not be read. The traceback is on stderr."
        )
    if reason is None:
        return 0
    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            }
        },
        sys.stdout,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
