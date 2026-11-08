#!/usr/bin/env python3
"""Refuse a command substitution in a command that runs a prefix-granted helper.

A grant matches the typed string while the shell runs `$(…)`, quoted or not; the protocol is `guard-index-argv.py`'s.
"""

import importlib.util
import json
import re
import sys
import traceback
from pathlib import Path

INDEX_GUARD = Path(__file__).with_name("guard-index-argv.py")
ASSIGNMENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*\+?=")
# Words that run the command after them, and the wrappers' options that take a separate argument; as `guard-sed-crlf.py`.
PREFIXES = frozenset({
    "command", "exec", "time", "builtin", "nohup",
    "!", "if", "then", "elif", "else", "while", "until", "do",
})
WRAPPERS = {
    "env": {"-u", "--unset", "-C", "--chdir"},
    "nice": {"-n", "--adjustment"},
    "timeout": {"-s", "--signal", "-k", "--kill-after"},
    "stdbuf": {"-i", "-o", "-e", "--input", "--output", "--error"},
}
GH_VALUE_FLAGS = frozenset({"-R", "--repo", "--hostname"})
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


def _command_word(run):
    """The words from the one this simple command runs: past assignments, prefix words and wrappers' options."""
    index = 0
    while index < len(run):
        word = run[index]
        if ASSIGNMENT.match(word) or word in PREFIXES:
            index += 1
        elif word in WRAPPERS:
            index += 1
            while index < len(run) and run[index].startswith("-") and run[index] != "-":
                option = run[index]
                index += 2 if option in WRAPPERS[word] else 1
                if option == "--":
                    break
            # `timeout` takes its duration before the command.
            index += 1 if word == "timeout" else 0
        else:
            break
    return run[index:]


def _python_script(run, index):
    """The script a python launcher's line names, past `py -3.12` and the interpreter's value flags, else ''."""
    position = 1
    if index._base_name(run[0]).removesuffix(".exe") == "py" and len(run) > 1 and run[1][1:2].isdigit():
        position = 2
    while position < len(run):
        word = run[position]
        if word in index._PYTHON_VALUE_FLAGS:
            position += 2
        elif word.startswith("-"):
            position += 1
        else:
            return word.replace("\\", "/")
    return ""


def _is_pr_create(run):
    """`gh pr create`, past gh's global flags."""
    position = 1
    while position < len(run) and run[position].startswith("-"):
        position += 2 if run[position] in GH_VALUE_FLAGS else 1
    return run[position:position + 2] == ["pr", "create"]


def _runs_a_helper(run, index):
    run = _command_word(run)
    if not run:
        return False
    if index._base_name(run[0]).removesuffix(".exe") == "gh":
        return _is_pr_create(run)
    if not index._is_python_launcher(run[0]):
        return False
    script = _python_script(run, index)
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
