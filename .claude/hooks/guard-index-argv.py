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
# Interpreter flags that consume the next token. Clustered forms (`-Wignore`)
# are one token and do not belong here.
_PYTHON_VALUE_FLAGS = ("-W", "-X", "-Q", "--check-hash-based-pycs")


def _git_guard():
    spec = importlib.util.spec_from_file_location("guard_git_argv", GIT_GUARD)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _base_name(token):
    name = token.replace("\\", "/").rsplit("/", 1)[-1]
    return name.lower()


def _is_python_launcher(token):
    name = _base_name(token)
    if name.endswith(".exe"):
        name = name[:-4]
    if name in ("python", "python3", "py"):
        return True
    return name.startswith("python3.") or name.startswith("python2.")


def _python_module_argv(words):
    """Tokens after `python [opts] -m codebase_index`, else None.

    Only a Python/py launcher, and only while its interpreter options are
    still open: `echo -m codebase_index` is echo, and
    `python script.py -m codebase_index` is a script argument.
    """
    i = 1
    name = _base_name(words[0])
    if name.endswith(".exe"):
        name = name[:-4]
    if name == "py" and i < len(words):
        sel = words[i]
        if sel.startswith("-") and len(sel) > 1 and sel[1].isdigit():
            i += 1
    while i < len(words):
        tok = words[i]
        if tok == "-m":
            if i + 1 < len(words) and words[i + 1] == "codebase_index":
                return words[i + 2:]
            return None
        if tok in _PYTHON_VALUE_FLAGS:
            i += 2
            continue
        if (
            not tok.startswith("-")
            or tok in ("-", "--")
            or tok.startswith("-c")
        ):
            return None
        i += 1
    return None


def _index_argv(run):
    """Tokens after the index executable, or None if this run is not one.

    Only the executable position counts: `rg codebase-index` is grep, not
    an index invocation. `python -m codebase_index` is the module form,
    and only when the executable is a Python/py launcher.
    """
    if not run:
        return None
    words = list(run)
    if _base_name(words[0]) in ("bash", "sh"):
        words = words[1:]
    if not words:
        return None
    if _base_name(words[0]) in INDEX_NAMES:
        return words[1:]
    if _is_python_launcher(words[0]):
        return _python_module_argv(words)
    return None


def _run_is_index(run):
    return _index_argv(run) is not None


def _subcommand(run):
    rest = _index_argv(run)
    if not rest:
        return ""
    return rest[0].lstrip("-")


def offence(command, git):
    has_sub = git.substitutions(command)
    resolved = git.separate_lines(
        git.join_continuations(git.strip_comments(git.strip_heredocs(command))))
    try:
        lexer = shlex.shlex(resolved, posix=True, punctuation_chars=True)
        lexer.commenters = ""
        lexer.whitespace_split = True
        tokens = list(lexer)
    except ValueError:
        if not _run_is_index(command.split()):
            return None
        if has_sub:
            return (
                "command substitution in an index invocation is refused: the "
                "prefix grant matches the typed string while the shell still "
                "runs `$(…)` and backticks. Pass the query as a quoted word, "
                "not as a substitution."
            )
        return (
            "an index invocation could not be tokenised, so extra "
            "commands cannot be ruled out; refusing rather than "
            "admitting what could not be read."
        )

    runs = [run for run in git.command_runs(tokens) if run]
    index_runs = [run for run in runs if _run_is_index(run)]
    if not index_runs:
        return None
    if has_sub:
        return (
            "command substitution in an index invocation is refused: the "
            "prefix grant matches the typed string while the shell still "
            "runs `$(…)` and backticks. Pass the query as a quoted word, "
            "not as a substitution."
        )
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


def main():
    try:
        event = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        print("guard-index-argv: unreadable hook event; not judging",
              file=sys.stderr)
        return 0

    if not isinstance(event, dict):
        print("guard-index-argv: malformed hook event; not judging",
              file=sys.stderr)
        return 0

    if event.get("tool_name") != "Bash":
        return 0
    tool_input = event.get("tool_input")
    if not isinstance(tool_input, dict):
        return 0
    command = tool_input.get("command")
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
