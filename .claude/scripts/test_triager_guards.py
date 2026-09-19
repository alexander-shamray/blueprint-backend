"""The `review-grok-triager` profile, its grant and the two hooks it carries.

`/ship` step 5 runs the `/review-grok` triage inside an agent, where a
command's frontmatter is not applied, so the boundary is the profile: a
`tools:` allowlist with no shell, and two `PreToolUse` hooks for what an
allowlist cannot say. `docs/harness-boundaries.md` owns the argument; these
cases pin the text and run the hooks the way the harness does.
"""
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
ROOT = SCRIPTS.parent.parent
COMMANDS = SCRIPTS.parent / "commands"
AGENTS = SCRIPTS.parent / "agents"
HOOKS = SCRIPTS.parent / "hooks"
SETTINGS = SCRIPTS.parent / "settings.json"
PROFILE = AGENTS / "review-grok-triager.md"


def frontmatter_list(text, key):
    return [item.strip() for line in
            re.findall(rf"^{key}:\s*(.+)$", text, re.MULTILINE)
            for item in line.split(",") if item.strip()]


def load(name):
    spec = importlib.util.spec_from_file_location(
        name.replace("-", "_"), HOOKS / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run_hook(name, event):
    payload = event if isinstance(event, str) else json.dumps(event)
    return subprocess.run(
        [sys.executable, str(HOOKS / f"{name}.py")],
        input=payload, capture_output=True, text=True)


class TheTriageRunsUnderAProfileOfItsOwn(unittest.TestCase):

    def test_the_profile_holds_no_shell_and_cannot_load_a_skill(self):
        # The profile reads review-grok.md rather than loading it, so the
        # command's frontmatter is not in play on /ship's path and the
        # no-shell boundary there is this `tools:` allowlist. `Skill` is
        # refused beside `Bash` because loading the command is the path
        # measured, in a general-purpose agent, to drop its deny.
        profile = PROFILE.read_text(encoding="utf-8")
        self.assertRegex(profile, r"(?m)^name:\s*review-grok-triager\s*$")
        self.assertEqual(
            {"Read", "Grep", "Glob", "Edit", "Write", "Agent"},
            set(frontmatter_list(profile, "tools")))
        self.assertNotRegex(profile, r"(?m)^skills:")
        self.assertIn(".claude/commands/review-grok.md", profile)

    def test_ship_grants_the_triager_by_exact_type_and_nothing_broader(self):
        ship = (COMMANDS / "ship.md").read_text(encoding="utf-8")
        allowed = frontmatter_list(ship, "allowed-tools")
        self.assertEqual(
            ["Agent(review-grok-triager)"],
            [t for t in allowed if t == "Agent" or t.startswith("Agent(")])
        self.assertIn("spawn a **`review-grok-triager`** agent", ship)

    def test_the_inline_deny_stays_on_the_command(self):
        # The agent path moves the triage; it does not retire the boundary
        # an inline `/review-grok` rests on.
        text = (COMMANDS / "review-grok.md").read_text(encoding="utf-8")
        denied = ", ".join(frontmatter_list(text, "disallowed-tools"))
        self.assertIsNotNone(
            re.search(r"(^|,\s*)Bash(\s*,|\s*$)", denied))

    def test_every_other_exact_agent_grant_denies_the_triager(self):
        # The subject is what the gate looks at: the profiles are read from
        # `.claude/agents/` each run, so one added tomorrow fails every
        # command that grants a type and does not name it. `/ship` is the
        # exception and states no deny list: it grants this one type, and
        # what the triager may dispatch is the dispatch hook's subject.
        profiles = set()
        for path in sorted(AGENTS.glob("*.md")):
            front = path.read_text(encoding="utf-8").split("\n---", 1)[0]
            names = re.findall(r"^name:\s*(\S+)\s*$", front, re.MULTILINE)
            with self.subTest(profile=path.name):
                self.assertEqual(1, len(names), "exactly one `name:`")
            profiles.update(names)
        self.assertIn("review-grok-triager", profiles)
        self.assertIn("review-adjudicator", profiles)
        granting = 0
        for path in sorted(COMMANDS.glob("*.md")):
            if path.name == "ship.md":
                continue
            text = path.read_text(encoding="utf-8")
            granted = {m for t in frontmatter_list(text, "allowed-tools")
                       for m in re.findall(r"^Agent\((.+)\)$", t)}
            if not granted:
                continue
            granting += 1
            denied = set(frontmatter_list(text, "disallowed-tools"))
            for name in sorted(profiles - granted):
                with self.subTest(command=path.name, agent=name):
                    self.assertIn(f"Agent({name})", denied)
        # The two sweeps and the triage: fewer means the grant pattern
        # stopped matching, and the loop above passed over nothing.
        self.assertGreaterEqual(granting, 3)

    def test_ship_pushes_a_copilot_fix_before_its_marker(self):
        # review-copilot.md pushes a committed fix before posting `done`, so
        # the marker names a commit on the remote, and ship.md step 6 must
        # push at the same point. Pinned together, because each file alone
        # reads correctly.
        ship = (COMMANDS / "ship.md").read_text(encoding="utf-8")
        copilot = (COMMANDS / "review-copilot.md").read_text(encoding="utf-8")
        self.assertRegex(
            ship, r"push the\s+branch by name, and only then let it post its"
                  r"\s+markers")
        self.assertRegex(copilot, r"before posting `done`")


class NothingShipChainsDeniesPush(unittest.TestCase):
    """A frontmatter deny lasts the rest of the turn, not the command.

    So `Bash(git push:*)` on a command `/ship` runs before it pushes refuses
    `/ship`'s own push a step later. Restoring it reads as hardening and
    breaks the chain silently, because the refusal lands on a later command
    than the one that carries it. `docs/harness-boundaries.md` owns the rule.
    """

    CHAINED_BEFORE_A_PUSH = ("branch.md", "validate-blueprint.md",
                             "check-links.md", "commit.md", "pr.md",
                             "review-copilot.md")

    def test_none_of_them_denies_push_or_the_shell(self):
        ship = (COMMANDS / "ship.md").read_text(encoding="utf-8")
        for name in self.CHAINED_BEFORE_A_PUSH:
            text = (COMMANDS / name).read_text(encoding="utf-8")
            denied = ", ".join(frontmatter_list(text, "disallowed-tools"))
            with self.subTest(command=name):
                # An exemption nobody needs quietly widens: the command has
                # to be one ship.md really names.
                self.assertIn(f"`/{name.removesuffix('.md')}`", ship)
                self.assertNotIn("Bash(git push", denied)
                self.assertIsNone(
                    re.search(r"(^|,\s*)Bash(\s*,|\s*$)", denied))


class TheTriagerDispatchesOnlyTheAdjudicator(unittest.TestCase):
    """`review-grok-triager` holds `Agent`, and the type list is ignored."""

    def dispatch(self, subagent_type, tool="Agent"):
        tool_input = {"description": "d", "prompt": "p"}
        if subagent_type is not None:
            tool_input["subagent_type"] = subagent_type
        return run_hook("guard-triager-dispatch",
                        {"tool_name": tool, "tool_input": tool_input})

    def test_the_adjudicator_passes(self):
        # The positive control: without it every refusal below passes against
        # a guard that refuses everything, and the triage could never start.
        for tool in ("Agent", "Task"):
            with self.subTest(tool=tool):
                out = self.dispatch("review-adjudicator", tool)
                self.assertEqual(0, out.returncode, out.stderr)
                self.assertEqual("", out.stdout)

    def test_every_other_dispatch_is_refused(self):
        # A missing type is the harness's default of general-purpose.
        for wanted in ("review-grok-triager", "general-purpose", "claude",
                       "Review-Adjudicator", "review-adjudicator ", "", None):
            for tool in ("Agent", "Task"):
                with self.subTest(subagent_type=wanted, tool=tool):
                    out = self.dispatch(wanted, tool)
                    self.assertEqual(0, out.returncode, out.stderr)
                    decision = json.loads(out.stdout)["hookSpecificOutput"]
                    self.assertEqual("deny", decision["permissionDecision"])

    def test_an_unreadable_event_blocks(self):
        # Fail closed: exit 2 is the only code that blocks a PreToolUse call.
        for event in ("not json", "[]", "\"Agent\""):
            with self.subTest(event=event):
                self.assertEqual(
                    2, run_hook("guard-triager-dispatch", event).returncode)

    def test_other_tools_are_not_judged(self):
        out = run_hook("guard-triager-dispatch",
                       {"tool_name": "Edit",
                        "tool_input": {"file_path": "docs/x.md"}})
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual("", out.stdout)

    def test_the_profile_wires_the_guard_on_its_dispatches(self):
        # The subject is the wiring, not the guard: a hook that exists and is
        # not registered on the profile refuses nothing.
        front = PROFILE.read_text(encoding="utf-8").split("\n---", 1)[0]
        self.assertRegex(front, r"(?m)^hooks:\s*$")
        self.assertRegex(front, r"(?m)^\s+PreToolUse:\s*$")
        self.assertRegex(front, r'matcher:\s*"Agent\|Task"')
        self.assertIn('/.claude/hooks/guard-triager-dispatch.py\\""', front)
        self.assertIn("${CLAUDE_PROJECT_DIR}", front)
        # And not session-wide, where it would refuse /ship's own dispatch.
        self.assertNotIn("guard-triager-dispatch",
                         SETTINGS.read_text(encoding="utf-8"))


class TheTriagerEditsNothingTheTriageDenies(unittest.TestCase):
    """`/review-grok`'s `Edit(...)` denies bind the triager in every turn.

    The patterns are read from `review-grok.md` rather than from a copy, so
    the subject is the list the guard reads and a path added there is covered
    here with no edit to this class.
    """

    def edit(self, path, tool="Edit", cwd=None):
        key = "notebook_path" if tool == "NotebookEdit" else "file_path"
        return run_hook("guard-triager-edit", {
            "tool_name": tool,
            "tool_input": {key: path},
            "cwd": str(cwd or ROOT),
        })

    def assert_refused(self, out):
        self.assertEqual(0, out.returncode, out.stderr)
        decision = json.loads(out.stdout)["hookSpecificOutput"]
        self.assertEqual("deny", decision["permissionDecision"])

    def assert_admitted(self, out):
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual("", out.stdout)

    def triage_denies(self):
        text = (COMMANDS / "review-grok.md").read_text(encoding="utf-8")
        return sorted({glob[2:] if glob.startswith("./") else glob
                       for rule in frontmatter_list(text, "disallowed-tools")
                       for glob in re.findall(r"^Edit\(([^)]*)\)$", rule)})

    @staticmethod
    def instance(glob):
        # One concrete path each glob must refuse: `**/` becomes a directory,
        # any other wildcard a name, so a nested pattern is tested nested.
        return (glob.replace("**/", "nested/dir/").replace("**", "inner/x")
                .replace("*", "x").replace("?", "x"))

    def test_the_list_it_reads_holds_the_machinery(self):
        # The subject test for the source: the trees the triager must never
        # write have to be in the list the guard reads, or every case below
        # passes against the wrong file.
        denies = self.triage_denies()
        for tree in (".claude/**", ".github/**", "deploy/**", ".git/**"):
            self.assertIn(tree, denies)

    def test_ordinary_edits_pass(self):
        # The positive control: a guard refusing everything passes every
        # refusal below, and the triage could fix nothing.
        for path in ("docs/harness-boundaries.md",
                     "src/Services/Catalog/Catalog.Api/Program.cs",
                     "tests/Catalog.Domain.Tests/x.cs",
                     str(ROOT / "docs" / "testing.md")):
            for tool in ("Edit", "Write", "MultiEdit"):
                with self.subTest(path=path, tool=tool):
                    self.assert_admitted(self.edit(path, tool))

    def test_every_triage_deny_is_refused(self):
        denies = self.triage_denies()
        self.assertTrue(denies)
        for glob in denies:
            path = self.instance(glob)
            for spelled in (path, "./" + path, path.upper(),
                            str(ROOT / path)):
                with self.subTest(glob=glob, spelled=spelled):
                    self.assert_refused(self.edit(spelled))

    def test_every_edit_tool_is_judged(self):
        for tool in ("Edit", "Write", "MultiEdit", "NotebookEdit"):
            with self.subTest(tool=tool):
                self.assert_refused(
                    self.edit(".github/workflows/ci.yml", tool))

    def test_spellings_windows_folds_are_refused(self):
        for spelled in (".github./workflows/x.yml",
                        ".github. :s/workflows/x.yml",
                        ".github/workflows/x.yml:stream",
                        "deploy /compose/x.yml",
                        "docs/../.github/workflows/x.yml"):
            with self.subTest(spelled=spelled):
                self.assert_refused(self.edit(spelled))

    def test_a_target_in_no_checkout_is_refused(self):
        with tempfile.TemporaryDirectory() as outside:
            self.assert_refused(self.edit(os.path.join(outside, "x.md")))
        # A scratchpad is such a target, so the record `/review-grok` keeps
        # in one has to travel another way, and both ends have to say so.
        self.assertRegex(PROFILE.read_text(encoding="utf-8"),
                         r"resolution record comes back in your report")
        self.assertRegex((COMMANDS / "ship.md").read_text(encoding="utf-8"),
                         r"resolution record comes back in its\s+report")

    def test_another_checkout_is_refused(self):
        # The root is the checkout holding cwd, not the one holding the
        # target: an ordinary path in a sibling checkout is still refused.
        with tempfile.TemporaryDirectory() as other:
            os.makedirs(os.path.join(other, ".git"))
            for spelled in (os.path.join(other, "src", "x.py"),
                            os.path.join(other, "docs", "x.md")):
                with self.subTest(spelled=spelled):
                    self.assert_refused(self.edit(spelled))
            # The positive control: the same path from inside that checkout
            # passes, so the refusal above is the anchor and nothing else.
            self.assert_admitted(self.edit(
                os.path.join(other, "docs", "x.md"), cwd=other))

    def test_a_cwd_in_no_checkout_refuses_everything(self):
        with tempfile.TemporaryDirectory() as outside:
            self.assert_refused(self.edit(str(ROOT / "docs" / "x.md"),
                                          cwd=outside))

    def test_an_unreadable_event_blocks(self):
        for event in ("not json", "[]", "\"Edit\"",
                      json.dumps({"tool_name": "Edit", "tool_input": {}}),
                      json.dumps({"tool_name": "Write", "tool_input": "x"})):
            with self.subTest(event=event):
                self.assertEqual(
                    2, run_hook("guard-triager-edit", event).returncode)

    def test_an_owner_without_edit_denies_blocks(self):
        # Fail closed on the source: a guard that finds no rules and admits
        # everything is no boundary.
        module = load("guard-triager-edit")
        with tempfile.TemporaryDirectory() as tmp:
            for body in ("no frontmatter\n", "---\nname: x\n---\n",
                         "---\ndisallowed-tools: Agent(claude)\n---\n",
                         "---\ndisallowed-tools: Edit(.github/**)\nno end\n"):
                owner = os.path.join(tmp, "owner.md")
                with open(owner, "w", encoding="utf-8") as handle:
                    handle.write(body)
                with self.subTest(body=body), \
                        mock.patch.object(module, "OWNER", owner):
                    self.assertIsNone(module.patterns())
            with mock.patch.object(module, "OWNER",
                                   os.path.join(tmp, "missing.md")):
                self.assertIsNone(module.patterns())
            # Undecodable bytes are a missing list, not a crash: a crash
            # exits 1, which does not block.
            owner = os.path.join(tmp, "binary.md")
            with open(owner, "wb") as handle:
                handle.write(b"---\ndisallowed-tools: Edit(\xff\xfe)\n---\n")
            with mock.patch.object(module, "OWNER", owner):
                self.assertIsNone(module.patterns())

    def test_a_bare_edit_deny_is_every_path(self):
        module = load("guard-triager-edit")
        with tempfile.TemporaryDirectory() as tmp:
            owner = os.path.join(tmp, "owner.md")
            for listed in ("Bash, Edit, Edit(.github/**)", "Edit",
                           "Agent(claude),Edit ,Bash"):
                with open(owner, "w", encoding="utf-8") as handle:
                    handle.write(f"---\ndisallowed-tools: {listed}\n---\n")
                with self.subTest(listed=listed):
                    rules = module.patterns(owner)
                    self.assertTrue(any(
                        pattern.match("docs/x.md") for _, pattern in rules))
            # The positive control: a scoped list leaves other paths alone.
            with open(owner, "w", encoding="utf-8") as handle:
                handle.write("---\ndisallowed-tools: Edit(.github/**)\n---\n")
            self.assertFalse(any(
                pattern.match("docs/x.md")
                for _, pattern in module.patterns(owner)))

    def test_a_newline_in_a_name_does_not_end_the_match(self):
        module = load("guard-triager-edit")
        pattern = module.compile_glob(".github/**")
        self.assertIsNotNone(pattern.match(".github/ci\n.yml"))
        self.assertIsNone(pattern.match("docs/ci\n.yml"))

    def test_the_edited_checkout_adds_its_own_denies(self):
        # A sibling worktree whose command denies one more tree than the
        # checkout holding the guard: both lists bind, neither replaces.
        with tempfile.TemporaryDirectory() as other:
            os.makedirs(os.path.join(other, ".git"))
            commands = os.path.join(other, ".claude", "commands")
            os.makedirs(commands)
            local = os.path.join(commands, "review-grok.md")
            with open(local, "w", encoding="utf-8") as handle:
                handle.write("---\ndisallowed-tools: Edit(docs/**)\n---\n")
            self.assert_refused(self.edit("docs/x.md", cwd=other))
            self.assert_refused(self.edit(".github/x.yml", cwd=other))
            self.assert_admitted(self.edit("src/x.py", cwd=other))
            with open(local, "w", encoding="utf-8") as handle:
                handle.write("---\nname: x\n---\n")
            self.assertEqual(2, self.edit("src/x.py", cwd=other).returncode)

    def test_a_crash_blocks(self):
        module = load("guard-triager-edit")
        with mock.patch.object(module, "main",
                               side_effect=RuntimeError("boom")):
            self.assertEqual(2, module.run())
        self.assertIn("sys.exit(run())",
                      (HOOKS / "guard-triager-edit.py").read_text(
                          encoding="utf-8"))

    def test_other_tools_are_not_judged(self):
        for tool in ("Read", "Grep", "Agent"):
            with self.subTest(tool=tool):
                self.assert_admitted(run_hook("guard-triager-edit", {
                    "tool_name": tool,
                    "tool_input": {"file_path": ".github/x.yml"}}))

    def test_the_profile_wires_the_guard_on_its_edits(self):
        # The subject is the wiring: a guard not registered on the profile
        # refuses nothing, and one registered session-wide would refuse the
        # session's own edits to the trees /ship's commits carry.
        front = PROFILE.read_text(encoding="utf-8").split("\n---", 1)[0]
        self.assertRegex(front, r'matcher:\s*"Edit\|Write\|MultiEdit\|'
                                r'NotebookEdit"')
        self.assertIn('/.claude/hooks/guard-triager-edit.py\\""', front)
        self.assertNotIn("guard-triager-edit",
                         SETTINGS.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
