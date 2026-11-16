"""The review loop's helper suites' harness; `docs/harness-boundaries.md` argues Claude Code's grants and refusals.

Negatives pair with positive controls; pattern cases run the scripts' own `grep -E` or `jq`, as `re` would be a second
specification, and call-site cases may use `re`. To run it, see the testing document `docs/harness-local.md` names.
"""

import importlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import NamedTuple
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
NEWLINE = chr(10)  # spelled this way so patch scripts cannot mangle it
SETTINGS = SCRIPTS.parent / "settings.json"
COMMANDS = SCRIPTS.parent / "commands"
HOOKS = SCRIPTS.parent / "hooks"
GUARD_LAUNCHER = 'sh "${CLAUDE_PROJECT_DIR}/.claude/hooks/run-guard.sh"'
BASH = shutil.which("bash")
GREP = shutil.which("grep")
GIT = shutil.which("git")
JQ = shutil.which("jq")

# The module a target keeps beside these suites, listed under `local`, to name its own commands to the payload's cases.
LOCAL_COMMANDS = "local_commands"


def assert_launched_on_the_floor(case, command, guard):
    """`command` runs `guard` through `run-guard.sh`, and the launcher and `run-guard.py` keep the 3.12 floor."""
    case.assertEqual(f"{GUARD_LAUNCHER} {guard}", command)
    launcher = code_lines((HOOKS / "run-guard.sh").read_text(encoding="utf-8"))
    prover = code_lines((HOOKS / "run-guard.py").read_text(encoding="utf-8"))
    case.assertTrue(any("sys.version_info < (3, 12)" in line for line in launcher), "no floor in the launcher's code")
    case.assertIn("FLOOR = (3, 12)", prover)


class Declared(NamedTuple):
    chained: tuple  # its commands its Ship checks run before /ship pushes
    ceilings: dict  # its split commands' runbook ceilings, in bytes


def local_commands(name=LOCAL_COMMANDS):
    """The target's declaration module, or None where it keeps none; a module whose own import fails is raised."""
    try:
        return importlib.import_module(name)
    except ModuleNotFoundError as error:
        if error.name != name:
            raise
        return None


def declared(module):
    """What `module` declares, refusing a shape that would read as something other than what it says."""
    chained = getattr(module, "CHAINED_BEFORE_A_PUSH", ())
    ceilings = getattr(module, "CEILINGS", {})
    if not isinstance(chained, (tuple, list)) or not all(
            isinstance(name, str) and name.endswith(".md") for name in chained):
        raise TypeError(f"CHAINED_BEFORE_A_PUSH must be a tuple of command file names, not {chained!r}")
    if not isinstance(ceilings, dict) or not all(
            isinstance(name, str) and name.endswith(".md") and type(size) is int and size > 0
            for name, size in ceilings.items()):
        raise TypeError(f"CEILINGS must map command file names to positive byte counts, not {ceilings!r}")
    return Declared(tuple(chained), dict(ceilings))


LOCAL = declared(local_commands())


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


def temp_root(case):
    """A directory bash and Python both open, gone after `case` whatever its fixture's build did.

    Python makes it: `mktemp -d` in a build gives an MSYS path Python on Windows cannot open, and a build that fails
    before it prints that path removes nothing. Its cleanup clears the read-only bit git sets on an object.
    """
    holder = tempfile.TemporaryDirectory()
    case.addCleanup(holder.cleanup)
    return Path(holder.name).as_posix()


# Each module's fixture repositories, built once and copied per test: on Windows a `git init`, `add` and `commit`
# cost about 210 ms where copying their result costs 19. Cleared by a module cleanup, which the shard runner runs
# between two classes, so a template never outlives the class that last read it there.
_templates = {}

# Appended to every config a template holds, so that no copy starts a file-system monitor or an automatic collection.
QUIET_GIT = "[core]\n\tfsmonitor = false\n[gc]\n\tauto = 0\n[maintenance]\n\tauto = false\n"


def no_maintenance(base):
    """The three variables that make a git started with `base` in its environment run no `maintenance --auto`.

    Its detached run holds `objects/maintenance.lock` past a commit, and a config added after the build is too late.
    A count `base` holds is extended; a push into a local bare remote is not covered, and `copy_template` skips it.
    """
    count = int(base.get("GIT_CONFIG_COUNT") or 0)
    return {"GIT_CONFIG_COUNT": str(count + 1), f"GIT_CONFIG_KEY_{count}": "maintenance.auto",
            f"GIT_CONFIG_VALUE_{count}": "false"}



def unsigned(base):
    """The variables that make a git started with `base` in its environment sign no commit and no tag.

    A caller whose global config signs through a program the host lacks fails every commit a case makes. A count
    `base` holds is extended, as `no_maintenance` extends it.
    """
    count = int(base.get("GIT_CONFIG_COUNT") or 0)
    return {"GIT_CONFIG_COUNT": str(count + 2),
            f"GIT_CONFIG_KEY_{count}": "commit.gpgsign", f"GIT_CONFIG_VALUE_{count}": "false",
            f"GIT_CONFIG_KEY_{count + 1}": "tag.gpgsign", f"GIT_CONFIG_VALUE_{count + 1}": "false"}


def sign_nothing(case):
    """For the rest of `case`, every git it starts signs nothing, whatever the caller's config says."""
    case.enterContext(mock.patch.dict(os.environ, unsigned(os.environ)))

def repository_configs(root):
    """Every repository config under `root`: a checkout's `.git/config` and a bare repository's own."""
    return [config for config in Path(root).rglob("config")
            if config.is_file() and (config.parent.name == ".git" or (config.parent / "HEAD").is_file())]


def fixture_template(build, root_var="R", key=None, **env):
    """The directory `build` made once a module: a bash script run with `root_var` naming it and `env` beside, or a
    function handed it as a `Path`, with a `key` saying what it builds. `copy_template` re-points the paths its configs
    spell, so a build spells them with forward slashes; a build that fails raises, never handing over half a fixture.
    """
    if callable(build) and key is None:
        raise TypeError("a function's template takes a key, since the function alone does not say what it builds")
    key = ("function", key) if callable(build) else (build, root_var, tuple(sorted(env.items())))
    if key not in _templates:
        holder = tempfile.TemporaryDirectory(prefix="fixture-")
        unittest.addModuleCleanup(holder.cleanup)
        unittest.addModuleCleanup(_templates.clear)
        root = Path(holder.name).as_posix()
        if callable(build):
            saved = dict(os.environ)
            os.environ.update(no_maintenance(saved))
            try:
                build(Path(root))
            finally:
                os.environ.clear()
                os.environ.update(saved)
        else:
            built = run_bash("set -eu\n" + build, **{root_var: root, **env, **no_maintenance({**os.environ, **env})})
            if built.returncode != 0:
                raise RuntimeError(f"the fixture template did not build: {built.stderr}")
        for config in repository_configs(root):
            with config.open("a", encoding="utf-8", newline="\n") as handle:
                handle.write(QUIET_GIT)
        _templates[key] = root
    return _templates[key]


def _git_locks(template):
    """An `ignore` for `copytree` of `template`: the `*.lock` files a background git may still hold or remove
    mid-copy, inside a `.git` directory or a bare repository's `objects` below the template's root. A fixture's own
    `yarn.lock` is content and is copied."""
    def ignore(directory, names):
        here = Path(directory)
        below = here.relative_to(template).parts
        in_git = ".git" in below or any(
            part == "objects" and (here.parents[len(below) - 1 - n] / "HEAD").is_file()
            for n, part in enumerate(below))
        return [name for name in names if name.endswith(".lock")] if in_git else []
    return ignore


def copy_template(case, template, root=None):
    """A fresh copy of `template` at `root`, a `temp_root` of `case`'s by default, every git config re-pointed at it.

    A config is the one place git keeps an absolute path to another repository of the fixture, a remote's URL;
    a template holding a linked worktree, whose `.git` file and `gitdir` spell absolute paths too, is not one.
    """
    root = temp_root(case) if root is None else Path(root).as_posix()
    shutil.copytree(template, root, symlinks=True, dirs_exist_ok=True, ignore=_git_locks(template))
    for config in repository_configs(root):
        text = config.read_text(encoding="utf-8")
        if template in text:
            config.write_text(text.replace(template, root), encoding="utf-8", newline="\n")
    return root


def python_as_py(env, directory):
    """`env` with a `py` first on PATH that runs this interpreter and drops the launcher's version argument.

    hint-bound.sh and index-hook.sh start their hook as `py -3.12` wherever a `py` is on PATH, so the shim makes that
    this interpreter, whatever else the runner has.
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
# `if ! GIT_DIR=x git push …`. An assignment's value may be quoted and hold a
# space, as in `FOO="a b" git push`.
COMMAND_LEAD = re.compile(
    r"""^(?:\w+=(?:"[^"]*"|'[^']*'|[^\s"'])*|then|else|elif|do|done|if|while|until|!|exec|time|eval|env|nohup"""
    r"|builtin)\s+")

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
