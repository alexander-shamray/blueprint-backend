"""The table of destructive classes in `docs/harness-boundaries.md`, held.

*Destructive classes* says, per class of operation the harness exists to
stop, an example and the layer that refuses it. A table nothing reads goes
stale where a gate would not, so this suite reads it:

- every `permissions.deny` entry of `.harness/settings.base.json` is matched
  by some `deny` row's example, so a deny added without a row fails and is
  named;
- every row's example is refused by the layer the row names: a `deny` row
  matches a base deny, and a guard row is fed to the real hook as the JSON a
  PreToolUse call carries and must come back `deny`;
- `git-push-force.sh` and `git-branch-drop.sh` each have a `helper` row,
  because each relaxes a deny and its refusal of `main` is what bounds it;
- the table is parsed, at least fifteen rows, so a renamed heading cannot
  leave the checks above vacuous.

A `ruleset` row is the server-side backstop. Its test is `ruleset-check`,
which the table names and this suite does not run. It is blueprint-harness's
`py -3.12 -m harness_sync ruleset-check`, which asks GitHub and is run by
that repository's `rulesets` workflow against every target's `main` ruleset,
so it is not in a target this suite is rendered into. A `helper` row is a
lease-bound helper's refusal of `main`, and its test is that helper's suite
and test, spelled `test_<helper>.py::test_main_is_refused_however_it_is_spelled`,
which this suite does not run either: it checks that the example invokes a
helper beside this file, that the suite is that helper's, and that the suite
defines the test. Every other row's *Test* must name a suite file that exists
beside this one.

The root is the directory two above this file: the rendered root in a
target, `payload/` in this repository, which has the same layout.

To add a *Refused by* value, add one entry to `REFUSERS`, one function that
returns `None` when the row is refused and the reason when it is not, and
the column's value to the section's prose.
"""

import ast
import contextlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
ROOT = SCRIPTS.parents[1]
DOC = ROOT / "docs" / "harness-boundaries.md"
BASE = ROOT / ".harness" / "settings.base.json"
HOOKS = ROOT / ".claude" / "hooks"

SECTION = "## Destructive classes"
COLUMNS = ["Class", "Example", "Refused by", "Test"]
# Well under the harness's 60 seconds, far over what one verdict takes.
HOOK_TIMEOUT = 20


class Row:
    """One row of the table, its cells with the backticks taken off."""

    def __init__(self, klass, example, refused_by, test):
        self.klass = klass
        self.example = example
        self.refused_by = refused_by
        self.test = test

    def __repr__(self):
        return f"Row({self.klass!r}, {self.example!r}, {self.refused_by!r})"


def parse_table(text):
    """The rows of the table under `## Destructive classes`, or `[]`."""
    lines = text.splitlines()
    try:
        start = lines.index(SECTION)
    except ValueError:
        return []
    rows = []
    header_seen = False
    for line in lines[start + 1:]:
        if line.startswith("## "):
            break
        if not line.startswith("|"):
            continue
        cells = [cell.strip().strip("`").strip()
                 for cell in line.strip().strip("|").split(" | ")]
        if cells == COLUMNS:
            header_seen = True
            continue
        if not header_seen or set(line) <= set("|-: "):
            continue
        if len(cells) != len(COLUMNS):
            raise AssertionError(f"a row of {len(cells)} cells: {line}")
        rows.append(Row(*cells))
    return rows


def _regex(text):
    """`text` as a regex in which a run of `*` matches any run."""
    return "".join(".*" if part.startswith("*") else re.escape(part)
                   for part in re.split(r"(\*+)", text) if part)


def deny_matches(rule, command):
    """Whether the permission `rule` matches `command`.

    `Bash(x:*)` matches `x` and `x …`; a `*` inside a rule matches any run; a
    rule without `:*` matches whole. `Edit(glob)` matches an example spelled
    `Edit(<path>)`, `./` taken off both, `*` and `**` any run.
    """
    name, _, rest = rule.partition("(")
    body = rest[:-1]
    if name == "Edit":
        if not (command.startswith("Edit(") and command.endswith(")")):
            return False
        path = command[len("Edit("):-1].removeprefix("./")
        return re.fullmatch(_regex(body.removeprefix("./")), path) is not None
    if name != "Bash":
        return False
    if body.endswith(":*"):
        pattern = _regex(body[:-2]) + r"(?:\s.*)?"
    else:
        pattern = _regex(body)
    return re.fullmatch(pattern, command, re.DOTALL) is not None


def base_denies():
    with open(BASE, encoding="utf-8") as handle:
        return json.load(handle)["permissions"]["deny"]


def _refused_by_deny(row):
    if any(deny_matches(rule, row.example) for rule in base_denies()):
        return None
    return "no base deny matches it"


def _verdict(hook, event, project):
    """The hook's refusal reason, `None` for admitted; asserts the protocol."""
    result = subprocess.run(
        [sys.executable, str(HOOKS / hook)],
        input=json.dumps(event), capture_output=True, text=True,
        encoding="utf-8", timeout=HOOK_TIMEOUT,
        env={**os.environ, "CLAUDE_PROJECT_DIR": str(project)},
    )
    if result.returncode != 0:
        return f"the hook crashed ({result.returncode}): {result.stderr[-200:]}"
    if not result.stdout.strip():
        return None
    decision = json.loads(result.stdout)["hookSpecificOutput"]
    if decision["permissionDecision"] != "deny":
        return f"decision {decision['permissionDecision']!r}, not deny"
    return "refused"


def _refused_by_git_argv(row):
    event = {"hook_event_name": "PreToolUse", "cwd": str(ROOT),
             "tool_name": "Bash", "tool_input": {"command": row.example}}
    verdict = _verdict("guard-git-argv.py", event, ROOT)
    return None if verdict == "refused" else verdict or "the hook admitted it"


# Links the scratch checkout holds under `linked/`, each to the control surface
# an `Edit(linked/<name>/…)` example spells its way into.
EDIT_LINKS = {
    "scripts": ".claude/scripts",
    "commands": ".claude/commands",
    "agents": ".claude/agents",
    "hooks": ".claude/hooks",
    "skills": ".claude/skills",
    "claude": ".claude",
    "remember": ".remember",
}


def _link(target, linkpath):
    try:
        os.symlink(target, linkpath, target_is_directory=True)
    except (OSError, NotImplementedError):
        from _winapi import CreateJunction
        CreateJunction(target, linkpath)


@contextlib.contextmanager
def scratch_checkout():
    """A scratch checkout whose `linked/<name>` links reach `EDIT_LINKS`."""
    root = tempfile.mkdtemp(prefix="destructive-root-")
    try:
        os.makedirs(os.path.join(root, ".git"))
        os.makedirs(os.path.join(root, "linked"))
        for name, target in EDIT_LINKS.items():
            real = os.path.join(root, *target.split("/"))
            os.makedirs(real, exist_ok=True)
            _link(real, os.path.join(root, "linked", name))
        yield root
    finally:
        # `ignore_errors`, and `rmtree` does not follow a link, so the link
        # goes and the tree it points into stays inside the scratch root.
        shutil.rmtree(root, ignore_errors=True)


def _refused_by_edit_target(row):
    match = re.fullmatch(r"(Edit|Write)\((.+)\)", row.example)
    if match is None:
        return "an example for this hook is spelled `Edit(<path>)`"
    with scratch_checkout() as root:
        event = {"hook_event_name": "PreToolUse", "cwd": root,
                 "tool_name": match.group(1),
                 "tool_input": {"file_path": match.group(2)}}
        verdict = _verdict("guard-edit-target.py", event, root)
    return None if verdict == "refused" else verdict or "the hook admitted it"


def _backstop(row):
    """A server-side row is checked by `ruleset-check`, not here."""
    if row.test != "ruleset-check":
        return "a `ruleset` row names `ruleset-check` as its test"
    return None


HELPER_TEST = "test_main_is_refused_however_it_is_spelled"


def suite_of(row):
    """The suite file a row's *Test* names: a `helper` row's part before `::`."""
    if row.refused_by == "helper":
        return row.test.partition("::")[0]
    return row.test


def _refused_by_helper(row):
    """A helper's refusal of `main` is held by its own suite, not run here."""
    match = re.fullmatch(r"bash \.claude/scripts/([\w-]+\.sh)(?: .*)?", row.example)
    if match is None:
        return "a `helper` row's example is spelled `bash .claude/scripts/<helper>.sh …`"
    script = match.group(1)
    if not (SCRIPTS / script).is_file():
        return f"no helper {script} beside this suite"
    suite = "test_" + script[:-len(".sh")].replace("-", "_") + ".py"
    if row.test != f"{suite}::{HELPER_TEST}":
        return f"a `helper` row's test is `{suite}::{HELPER_TEST}`"
    if not (SCRIPTS / suite).is_file():
        return f"no suite {suite} beside this one"
    tree = ast.parse((SCRIPTS / suite).read_text(encoding="utf-8"))
    if not any(isinstance(node, ast.FunctionDef) and node.name == HELPER_TEST
               for node in ast.walk(tree)):
        return f"{suite} defines no {HELPER_TEST}"
    return None


REFUSERS = {
    "deny": _refused_by_deny,
    "guard-git-argv": _refused_by_git_argv,
    "guard-edit-target": _refused_by_edit_target,
    "ruleset": _backstop,
    "helper": _refused_by_helper,
}


def refusal_problem(row):
    """`None` when the row's example is refused where it says, else why."""
    refuser = REFUSERS.get(row.refused_by)
    if refuser is None:
        return f"unknown Refused by {row.refused_by!r}"
    return refuser(row)


def table():
    return parse_table(DOC.read_text(encoding="utf-8"))


class DestructiveClasses(unittest.TestCase):
    def test_the_table_is_read(self):
        rows = table()
        self.assertGreaterEqual(
            len(rows), 15,
            f"{SECTION} parsed to {len(rows)} rows: renamed or emptied?")
        self.assertLessEqual(
            {row.refused_by for row in rows}, set(REFUSERS))

    def test_every_base_deny_has_a_row(self):
        examples = [row.example for row in table()
                    if row.refused_by == "deny"]
        missing = [rule for rule in base_denies()
                   if not any(deny_matches(rule, e) for e in examples)]
        self.assertEqual(
            [], missing,
            f"base deny entries with no `deny` row in {SECTION}")

    def test_every_row_is_refused(self):
        problems = []
        for row in table():
            problem = refusal_problem(row)
            if problem is not None:
                problems.append(f"{row.klass}: `{row.example}` "
                                f"({row.refused_by}): {problem}")
        self.assertEqual([], problems)

    def test_every_row_names_a_suite_that_exists(self):
        missing = [f"{row.klass}: {row.test}" for row in table()
                   if row.refused_by != "ruleset"
                   and not (SCRIPTS / suite_of(row)).is_file()]
        self.assertEqual([], missing)

    def test_each_helper_has_its_row(self):
        # The relaxation is bounded by these refusals, so a helper whose row
        # went missing is a boundary nobody reads.
        examples = {row.example for row in table() if row.refused_by == "helper"}
        self.assertLessEqual(
            {"bash .claude/scripts/git-push-force.sh",
             "bash .claude/scripts/git-branch-drop.sh main"}, examples)

    def test_a_helper_row_must_name_a_test_its_suite_defines(self):
        helper = "bash .claude/scripts/git-branch-drop.sh main"
        suite = "test_git_branch_drop.py"
        self.assertIsNone(refusal_problem(
            Row("control", helper, "helper", f"{suite}::{HELPER_TEST}")))
        for test in (f"{suite}::test_no_such_test",
                     f"test_git_push_force.py::{HELPER_TEST}",
                     suite,
                     f"test_no_such_suite.py::{HELPER_TEST}"):
            with self.subTest(test=test):
                self.assertIsNotNone(refusal_problem(Row("planted", helper, "helper", test)))
        self.assertIsNotNone(refusal_problem(Row(
            "planted", "bash .claude/scripts/no-such-helper.sh", "helper",
            f"test_no_such_helper.py::{HELPER_TEST}")))
        self.assertIsNotNone(refusal_problem(Row(
            "planted", "git branch -D main", "helper", f"{suite}::{HELPER_TEST}")))

    def test_a_planted_unrefused_row_fails(self):
        for layer in ("guard-git-argv", "deny"):
            planted = Row("planted", "git status", layer, "none")
            self.assertIsNotNone(
                refusal_problem(planted),
                f"a `git status` row passed as refused by {layer}")
        self.assertIsNotNone(refusal_problem(
            Row("planted", "Edit(linked/chapter.md)", "guard-edit-target",
                "none")))

    def test_deny_matches_follows_the_prefix_semantics(self):
        self.assertTrue(deny_matches("Bash(git clean:*)", "git clean"))
        self.assertTrue(deny_matches("Bash(git clean:*)", "git clean -fd"))
        self.assertFalse(deny_matches("Bash(git clean:*)", "git cleanup"))
        self.assertTrue(deny_matches("Bash(git *--exec*)", "git log --exec=x"))
        self.assertFalse(deny_matches("Bash(git branch)", "git branch -D x"))
        self.assertTrue(deny_matches("Edit(./.claude/hooks/**)",
                                     "Edit(.claude/hooks/x.py)"))
        self.assertFalse(deny_matches("Edit(.claude/hooks/**)",
                                      "Edit(docs/x.md)"))


if __name__ == "__main__":
    unittest.main()
