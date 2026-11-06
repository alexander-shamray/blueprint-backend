"""The review loop's helper suites' harness; `docs/harness-boundaries.md` argues Claude Code's grants and refusals.

Negatives pair with positive controls; pattern cases run the scripts' own `grep -E` or `jq`, as `re` would be a second
specification, and call-site cases may use `re`. To run it, see the testing document `docs/harness-local.md` names.
"""

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
NEWLINE = chr(10)  # spelled this way so patch scripts cannot mangle it
SETTINGS = SCRIPTS.parent / "settings.json"
COMMANDS = SCRIPTS.parent / "commands"
BASH = shutil.which("bash")
GREP = shutil.which("grep")
GIT = shutil.which("git")
JQ = shutil.which("jq")


def setUpModule():
    # Not a skip: a skip on a missing tool reports a pass, the fail-open ADR-010
    # refuses. Absent any of these, this suite has established nothing and says
    # so.
    missing = [
        name for name, path in
        (("bash", BASH), ("grep", GREP), ("git", GIT), ("jq", JQ))
        if path is None
    ]
    if missing:
        raise RuntimeError(
            f"{', '.join(missing)} required and not on PATH: these tests exercise "
            "the same tools the scripts do, and asserting through Python "
            "equivalents instead would be testing a second specification"
        )


def run_bash(script, subject="", **env_extra):
    """Run a bash fragment with the subject on stdin and everything else in env.

    Nothing is passed as an argument: under MSYS an argv element crossing into
    `bash.exe` is re-parsed, so a pattern containing `"` arrives with its quotes
    eaten and silently matches nothing. Environment variables and stdin are not
    re-parsed, so they mean the same thing on both platforms.
    """
    env = dict(os.environ)
    env.update(env_extra)
    return subprocess.run(
        [BASH, "-c", script],
        input=subject,
        capture_output=True,
        text=True, encoding="utf-8",
        env=env,
    )


def python_as_py(env, directory):
    """`env` with a `py` first on PATH that runs this interpreter and drops the launcher's version argument.

    hint-bound.sh starts its hook as `py -3.12`; a runner without the Windows launcher has none, and the wrapper
    would report every hook as not run.
    """
    shim = Path(directory) / "py"
    shim.write_text('#!/usr/bin/env bash\nexec "' + Path(sys.executable).as_posix() + '" "${@:2}"\n', encoding="utf-8")
    shim.chmod(0o755)
    return dict(env, PATH=str(directory) + os.pathsep + env["PATH"])


def code_lines(text):
    """The executable lines of a shell script — comments and blanks dropped.

    An assertion that a line exists, made against the whole file, passes on a
    comment that mentions it, so deleting the executable line would not fail it.
    """
    return [
        line for line in text.splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    ]


# What can introduce a command, beyond the `VAR=value` run: a shell keyword or
# a negation. Stripped in a loop, because they stack, as in
# `if ! GIT_DIR=x git push …`.
COMMAND_LEAD = re.compile(
    r"^(?:\w+=\S*|then|else|elif|do|done|if|while|until|!|exec|time|eval|env|nohup|builtin)\s+")

# A wrapper that takes a command as its argument, with the options and the
# one numeric argument they may carry (`timeout -k 5 60`, `xargs -n 1`).
COMMAND_WRAPPER = re.compile(
    r"^(?:timeout(?:\s+-\S+\s+\d\S*|\s+-\S+)*\s+\d\S*"
    r"|(?:xargs|sudo|nice|stdbuf|setsid|command)(?:\s+-\S+(?:\s+\d\S*)?)*)\s+")

# A command can begin after any of these. `{` and `}` are not in the class,
# because they also spell `"${branch}"` and splitting there cuts a command off
# before its flags. A brace group's braces are space-delimited, so those are
# matched on their own.
COMMAND_SEPARATOR = re.compile(r"[\n;&|()`]+|(?<=\s)\{(?=\s)|(?<=\s)\}")

# git's own options sit between `git` and the subcommand, so `git -C . push`
# would not read as a push at all. Dropped, so the subcommand comes first.
GIT_GLOBAL_OPTIONS = re.compile(
    r"^git(?:\s+(?:-[Cc]\s+(?:\"[^\"]*\"|'[^']*'|\S+)"
    r"|--(?:git-dir|work-tree|namespace|exec-path)(?:=|\s+)\S+"
    r"|-{1,2}[A-Za-z][\w-]*))+\s+")


def git_commands(source):
    """The `git …` commands of a shell script that the leads and wrappers above introduce.

    The text is cut where a command can begin, whitespace is one space, and the
    assignments, keywords and wrappers in front of a command are stripped.
    """
    commands = []
    for piece in COMMAND_SEPARATOR.split(source.replace(chr(92) + chr(10), " ")):
        piece = re.sub(r"\s+", " ", piece).strip()
        while COMMAND_LEAD.match(piece) or COMMAND_WRAPPER.match(piece):
            piece = (COMMAND_LEAD if COMMAND_LEAD.match(piece) else COMMAND_WRAPPER).sub("", piece, count=1)
        piece = GIT_GLOBAL_OPTIONS.sub("git ", piece)
        if piece.startswith("git "):
            commands.append(piece)
    return commands
