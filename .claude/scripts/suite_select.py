"""Which `.claude/scripts` tests a change reaches, for `shard-harness-suite.py --selective`.

`docs/harness-boundaries.md`, *The suite's shard runner*, holds the argument: a changed path this cannot place selects
the whole suite, and CI runs the whole suite whatever a local run selected.
"""

import fnmatch
import importlib.util
import re
import subprocess
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
ROOT = SCRIPTS.parents[1]
BASE = "origin/main"

# The module a target keeps beside the suites under `local`, which only some targets have.
LOCAL_MODULE = "local_commands"

# A change to one of these selects the whole suite: every suite imports the first, a target's module pins classes for
# the runner and declares its commands to the suites, and the last two decide what runs at all.
WHOLE = frozenset({
    ".claude/scripts/review_helpers.py",
    f".claude/scripts/{LOCAL_MODULE}.py",
    ".claude/scripts/shard-harness-suite.py",
    ".claude/scripts/suite_select.py",
})

# The tests that read a tree by listing it rather than by naming its files, so no name in them selects them. Each
# pattern is a repository path, `*` within one segment and `**` across any; each selector a module or `module.Class`.
# `test_suite_select.py` fails on a module whose listing, in a spelling its pattern knows, no row names.
TREE_READERS = (
    ("*", ("test_git_argv_guard.TheListsTheGuardReads",)),
    ("**", ("test_harness_denies.HarnessControlSurfaceIsDenied", "test_harness_denies.TheRepositoryTracksNoSymbolicLink",
            "test_harness_legs.WhatTheDecisionLooksAtTests", "test_sweep_slices.TheCheckout",
            "test_sweep_slices.TheCheckoutReadsEachFileOnce")),
    (".claude/scripts/test_*.py", ("test_suite_select.TheTablesNameWhatTheSuitesHold",)),
    ("**/.gitignore", ("test_root_ignore_covers_nested",)),
    ("**/*.py", ("test_python_hygiene", "test_ship_sync.TheSyncRow")),
    (".claude/commands/*.md", (
        "test_command_grants",
        "test_command_runbooks.EachSplitCommandHasBothHalves",
        "test_gh_pr_create.TheCommandsOpenThroughTheHelper",
        "test_grants_name_existing_helpers",
        "test_harness_denies.HarnessControlSurfaceIsDenied",
        "test_pr_helpers.NoCommandReadsAPullRequestFeedUnfiltered",
        "test_ship_landing_method.TheGrantAndTheInvocationAgree",
    )),
    (".claude/agents/*.md", ("test_command_grants",)),
    ("docs/commands/*.md", ("test_command_runbooks.EachSplitCommandHasBothHalves",)),
    (".claude/scripts/*.sh", ("test_git_rebase_onto_main.TheArgumentGuardsEachHaveTheirOwnCase",
                              "test_here_string_band")),
    (".claude/scripts/pr-*.sh", ("test_pr_helpers.NoCommandReadsAPullRequestFeedUnfiltered",
                                 "test_pr_helpers.AStalledGhReadEndsAtTheBound")),
    (".claude/hooks/*.sh", ("test_here_string_band",)),
    (".claude/hooks/*", ("test_run_guard",)),
    (".claude/scripts/**", ("test_pr_helpers.NoHelperNeedsBash4",)),
    (".claude/hooks/**", ("test_pr_helpers.NoHelperNeedsBash4",)),
    (".claude/skills/**", ("test_pr_helpers.NoHelperNeedsBash4",)),
    (".claude/skills/codebase-index/**/*.md", ("test_cbx.CbxWrapper", "test_index_query_hint.TheSkillAgrees",
                                               "test_index_skill_examples")),
)

CODE_SUFFIXES = (".py", ".sh", ".ps1")


def matches(pattern, path):
    """Whether repository path `path` matches `pattern`, segment by segment, `**` spanning any number of them."""
    def walk(parts, pieces):
        if not parts:
            return not pieces
        head, rest = parts[0], parts[1:]
        if head == "**":
            return any(walk(rest, pieces[n:]) for n in range(len(pieces) + 1))
        return bool(pieces) and fnmatch.fnmatchcase(pieces[0], head) and walk(rest, pieces[1:])
    return walk(pattern.split("/"), path.split("/"))


def _git(root, *args):
    try:
        done = subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, encoding="utf-8",
                              errors="surrogateescape", check=False)
    except OSError:
        return None
    return done.stdout if done.returncode == 0 else None


def changed(root=ROOT, base=BASE):
    """What the checkout holds that `base` does not: its commits, its edits and its untracked files; None if unread."""
    fork = _git(root, "merge-base", base, "HEAD")
    if fork is None:
        return None
    tracked = _git(root, "diff", "--name-only", "--no-renames", "-z", fork.strip())
    untracked = _git(root, "ls-files", "--others", "--exclude-standard", "-z")
    if tracked is None or untracked is None:
        return None
    return sorted({path for path in (tracked + untracked).split("\0") if path})


def _legs():
    """`harness-legs.py`, whose reach is what the suites may read; its name is not an importable module's."""
    spec = importlib.util.spec_from_file_location("harness_legs", SCRIPTS / "harness-legs.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _names(text, path):
    """Whether `text` names `path`: its file name, or a Python module's own name as a word."""
    name = path.rsplit("/", 1)[-1]
    if name in text:
        return True
    return name.endswith(".py") and re.search(rf"\b{re.escape(name[:-3])}\b", text) is not None


def _is_code(root, path):
    if path.endswith(CODE_SUFFIXES):
        return True
    try:
        with open(Path(root) / path, "rb") as handle:
            return handle.read(2) == b"#!"
    except OSError:
        return False


def sources(root, code):
    """Each of `code`'s paths to its text, leaving out one that cannot be read."""
    texts = {}
    for path in code:
        try:
            texts[path] = (Path(root) / path).read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
    return texts


def callers(paths, texts):
    """`paths` and every code file in `texts` that names one: what runs them, one step out.

    One step, not the closure: helpers name each other in messages and comments as well as in calls, and the closure
    of a helper reached most of `.claude/` in four steps. A change that breaks a caller's caller is CI's to find.
    """
    return set(paths) | {path for path, text in texts.items() if any(_names(text, target) for target in paths)}


def select(paths, modules, root=ROOT, reach=None, code=None):
    """`(selectors, why)`: the modules and classes `paths` reach, or None and the reason the whole suite runs.

    `modules` maps each test module to its source. A path outside the reach is placed as any path is, and never
    unplaced; one inside it that nothing names and no narrower listing reads selects everything.
    """
    if paths is None:
        return None, f"the change against {BASE} could not be read"
    if not paths:
        return None, f"nothing differs from {BASE}"
    whole = sorted(set(paths) & WHOLE)
    if whole:
        return None, f"{whole[0]} is read by every suite"
    if reach is None:
        legs = _legs()
        read = legs.reach(Path(root))
        if read is None:
            return None, "the reach could not be read from .harness/"
        reach = lambda path: legs.reaches(path, *read)  # noqa: E731
    if code is None:
        listed = _git(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", ".claude")
        if listed is None:
            return None, "the harness's own files could not be listed"
        code = [path for path in listed.split("\0") if path and not path.rsplit("/", 1)[-1].startswith("test_")
                and _is_code(root, path)]
    texts = sources(root, code)

    selectors = set()
    for path in paths:
        own = path.rsplit("/", 1)[-1]
        listed = {selector for pattern, readers in TREE_READERS if matches(pattern, path) for selector in readers}
        if path.startswith(".claude/scripts/test_") and own.endswith(".py") and own[:-3] in modules:
            selectors |= listed | {own[:-3]}
            continue
        reached = callers([path], texts)
        named = {module for module, text in modules.items() if any(_names(text, target) for target in reached)}
        # A suite listing every path, or every root path, reads each one's name and not what it says, so its
        # selection places nothing.
        placing = {selector for pattern, readers in TREE_READERS
                   if pattern not in ("*", "**") and matches(pattern, path) for selector in readers}
        if not placing and not named and reach(path):
            return None, f"no suite names {path}, or anything that runs it"
        selectors |= listed | named
    return selectors, None


def chosen(test_id, selectors):
    """Whether `selectors` hold `test_id`: by its module, by its class, or as a module that failed to import."""
    module, _, rest = test_id.partition(".")
    cls = rest.rpartition(".")[0]
    return module in selectors or f"{module}.{cls}" in selectors or not module.startswith("test_")
