#!/usr/bin/env python3
"""Refuse a command substitution in a command that runs a prefix-granted helper.

A grant matches the typed string while the shell runs `$(…)`, quoted or not; the protocol is `guard-index-argv.py`'s.
"""

import importlib.util
import json
import sys
import traceback
from pathlib import Path

INDEX_GUARD = Path(__file__).with_name("guard-index-argv.py")
REASON = (
    "a command substitution in a command that runs a helper is refused: its prefix grant matches the typed string "
    "while the shell still runs `$(…)` and backticks, inside double quotes too. Put issue text in the file the "
    "helper reads, or in single quotes with each embedded quote written as '\\''; a pull request's body goes in --body-file. "
    "A value another command computes is a run of its own, read and then spelled on the helper's line."
)


def _index_guard():
    spec = importlib.util.spec_from_file_location("guard_index_argv", INDEX_GUARD)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _runs_a_helper(run, index):
    if run[:3] == ["gh", "pr", "create"]:
        return True
    if not index._is_python_launcher(run[0]):
        return False
    script = next((word for word in run[1:] if not word.startswith("-")), "").replace("\\", "/")
    return ".claude/scripts/" in script and script.endswith(".py")


def offence(command, index, git):
    if not index._has_substitutions(command, git):
        return None
    prepared = git.separate_lines(git.join_continuations(git.strip_comments(git.strip_heredocs(command))))
    for fragment in index._run_fragments(git.strip_redirections(prepared), git):
        try:
            run = index._tokenise(fragment)
        except ValueError:
            run = fragment.split()
        if run and _runs_a_helper(run, index):
            return REASON
    return None


def refuse(reason):
    json.dump({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                      "permissionDecisionReason": reason}}, sys.stdout)
    return 0


def main():
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8"))
    except UnicodeDecodeError:
        return refuse("this hook event is not UTF-8, so the command in it cannot be read; refusing it unjudged.")
    except ValueError:
        print("guard-helper-argv: unreadable hook event; not judging", file=sys.stderr)
        return 0
    tool_input = event.get("tool_input") if isinstance(event, dict) and event.get("tool_name") == "Bash" else None
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if not isinstance(command, str):
        return 0
    try:
        index = _index_guard()
        reason = offence(command, index, index._git_guard())
    except Exception:  # noqa: BLE001
        traceback.print_exc(file=sys.stderr)
        reason = "this command crashed the helper argv guard, so nothing about it is established; refusing it."
    return refuse(reason) if reason else 0


if __name__ == "__main__":
    sys.exit(main())
