"""settings.json's deny list and the commands' `disallowed-tools`: the editing
boundaries the harness states.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import json
import re
import subprocess
import unittest

from review_helpers import (
    SCRIPTS,
    SETTINGS,
    COMMANDS,
    GIT,
    setUpModule,
)


class HarnessControlSurfaceIsDenied(unittest.TestCase):
    """The deny list covers the files that grant, not only the helpers.

    `commands/`, `agents/` and `settings.json` hand out the grants that
    `.claude/scripts/**` and `.claude/sandbox/**` protect, so the same
    reasoning applies one level up: a command carrying an unrestricted `Edit`
    while reading untrusted input reaches the frontmatter as well as the feed.
    """

    def deny(self):
        return json.loads(SETTINGS.read_text(encoding="utf-8"))["permissions"]["deny"]

    def test_every_control_surface_path_is_denied_in_both_spellings(self):
        deny = self.deny()
        # `hooks/**` is on the list because a hook runs on every Bash call: a
        # session able to rewrite one could delete its own guard and then act.
        for path in (".claude/scripts/**", ".claude/sandbox/**",
                     ".claude/commands/**", ".claude/agents/**",
                     ".claude/hooks/**", ".claude/skills/**",
                     ".claude/settings.json", ".claude/settings.local.json"):
            for prefix in ("", "./"):
                with self.subTest(path=path, prefix=prefix):
                    self.assertIn(f"Edit({prefix}{path})", deny)

    def test_the_rules_are_edit_and_never_write(self):
        # `Edit(path)` covers every file-editing tool, Write included. A
        # `Write(path)` rule matches nothing and makes Claude Code refuse to
        # start.
        for rule in self.deny():
            with self.subTest(rule=rule):
                self.assertFalse(rule.startswith("Write("))

    def test_every_loaded_settings_file_is_denied(self):
        """Both settings files Claude Code loads are denied, not just one.

        A deny on the exact file cannot cover a sibling, and .gitignore names
        `settings.local.json` as the per-developer override. Denying
        `.claude/**` wholesale is not the answer: `.claude/worktrees/` is
        where /branch puts working checkouts, so that blanket would deny
        editing the repository itself while a worktree run is live.
        """
        deny = self.deny()
        for name in ("settings.json", "settings.local.json"):
            for prefix in ("", "./"):
                with self.subTest(name=name, prefix=prefix):
                    self.assertIn(f"Edit({prefix}.claude/{name})", deny)

    def test_the_worktree_root_is_not_denied(self):
        # The other side: a control that over-reaches breaks the flow it is
        # meant to protect, so nothing may deny the worktree root.
        for rule in self.deny():
            with self.subTest(rule=rule):
                self.assertNotIn(".claude/worktrees", rule)
                self.assertNotEqual("Edit(.claude/**)", rule)
                self.assertNotEqual("Edit(./.claude/**)", rule)

    def test_the_deny_list_is_actually_read(self):
        # The positive control. Every assertion above would pass against a file
        # whose deny list this method could not find at all, if the lookup
        # silently yielded an empty list.
        self.assertGreater(len(self.deny()), 20)
        self.assertIn("Bash(git *--output*)", self.deny())


class CommandsEnforceTheEditingBoundariesTheyState(unittest.TestCase):
    """A command that promises not to edit a tree is denied it in frontmatter.

    `/validate-blueprint` says "never edit `src/`" and is step 2 of an
    unattended `/ship` whose entire input is prose in the branch under review;
    `/review-branch` says "do not fix the findings". A path-scoped
    `disallowed-tools` backs both: `Edit(src/**)` refuses an edit under `src/`
    while one under `docs/` succeeds in the same invocation, so it scopes
    rather than removing the tool.

    That list is a deny-list, so a tree added to the repository later is
    editable by both commands until someone adds it. The subject of these
    cases is what the list is looking at, not what it contains.
    """

    # The one tree each command's job is, exempt because denying it would break
    # the command rather than bound it. `.claude` is not exempt: settings denies
    # specific children rather than the tree, so a new `.claude/policies.md`
    # would be editable by both commands. It is denied in the two commands
    # instead, which leaves the worktree root writable.
    SUBJECTS = {
        "validate-blueprint.md": {"docs"},
        "review-branch.md": set(),
    }
    GLOBALLY_DENIED = frozenset()

    @classmethod
    def tracked_trees(cls):
        """Every top-level directory git actually tracks.

        Read from git rather than listed here, because a list in this file is
        the same copy that rots one directory over.
        """
        out = subprocess.run(
            [GIT, "ls-files"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True,
        )
        if out.returncode != 0:
            raise AssertionError(f"git ls-files failed: {out.stderr}")
        return {
            line.split("/")[0] for line in out.stdout.splitlines() if "/" in line
        }

    @staticmethod
    def disallowed(name):
        text = (COMMANDS / name).read_text(encoding="utf-8")
        found = re.findall(r"^disallowed-tools:\s*(.+)$", text, re.MULTILINE)
        if len(found) != 1:
            raise AssertionError(
                f"expected exactly one disallowed-tools line in {name}, "
                f"found {len(found)}"
            )
        return [entry.strip() for entry in found[0].split(",")]

    def test_the_tree_listing_is_not_vacuous(self):
        # The positive control, and it carries every case below: a listing that
        # silently came back empty would satisfy all of them.
        trees = self.tracked_trees()
        self.assertGreater(len(trees), 4)
        for expected in ("src", "tests", "docs", ".github"):
            self.assertIn(expected, trees)

    def test_every_tracked_tree_is_denied_in_both_spellings(self):
        # Every tree that exists rather than a listed few, so adding one is a
        # red build rather than a quiet widening.
        for name, subject in self.SUBJECTS.items():
            rules = self.disallowed(name)
            for tree in self.tracked_trees() - subject - self.GLOBALLY_DENIED:
                for prefix in ("", "./"):
                    with self.subTest(command=name, tree=tree, prefix=prefix):
                        self.assertIn(f"Edit({prefix}{tree}/**)", rules)

    def test_each_commands_own_subject_stays_editable(self):
        # The other side. A control that over-reaches breaks the flow it was
        # meant to protect — `/validate-blueprint` exists to amend chapters, so
        # denying `docs/**` would leave it able to find drift and not fix it.
        for name, subject in self.SUBJECTS.items():
            rules = self.disallowed(name)
            for tree in subject:
                for prefix in ("", "./"):
                    with self.subTest(command=name, tree=tree, prefix=prefix):
                        self.assertNotIn(f"Edit({prefix}{tree}/**)", rules)

    def test_the_rules_are_edit_and_never_write(self):
        # File permissions are checked against `Edit(path)` and `Read(path)`
        # only, so a `Write(path)` entry is accepted and never consulted — a
        # control that reads as present and matches nothing.
        for name in self.SUBJECTS:
            for rule in self.disallowed(name):
                with self.subTest(command=name, rule=rule):
                    self.assertFalse(
                        rule.startswith("Write("),
                        "a Write(path) rule is never consulted; use Edit(path)",
                    )

    @classmethod
    def tracked_root_files(cls):
        """Every tracked file at the repository root, read from git."""
        out = subprocess.run(
            [GIT, "ls-files"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True,
        )
        if out.returncode != 0:
            raise AssertionError(f"git ls-files failed: {out.stderr}")
        return {line for line in out.stdout.splitlines() if line and "/" not in line}

    def test_the_root_file_listing_is_not_vacuous(self):
        # The positive control for the case below.
        files = self.tracked_root_files()
        self.assertGreater(len(files), 4)
        for expected in ("CLAUDE.md", "Platform.slnx", "global.json"):
            self.assertIn(expected, files)

    def test_every_tracked_root_file_is_denied_in_both_spellings(self):
        # Denying directories leaves every root file writable, and `CLAUDE.md`,
        # `global.json`, `Directory.Build.props` and `Platform.slnx` all sit at
        # the root — a tree-only deny is a boundary with a hole exactly where
        # this repository keeps its build inputs.
        for name in self.SUBJECTS:
            rules = self.disallowed(name)
            for path in self.tracked_root_files():
                for prefix in ("", "./"):
                    with self.subTest(command=name, path=path, prefix=prefix):
                        self.assertIn(f"Edit({prefix}{path})", rules)

    # Every name MSBuild reads without being asked. Finite and documented,
    # unlike git's refspec grammar — which is what makes an enumeration the
    # right shape here and the wrong one there.
    AUTO_IMPORTED = (
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Build.rsp",
        "Directory.Packages.props",
        "Directory.Solution.props",
        "Directory.Solution.targets",
        "MSBuild.rsp",
        "nuget.config",
        "NuGet.config",
        "NuGet.Config",
        "global.json",
    )

    def test_the_msbuild_auto_import_surface_is_denied(self):
        # `tracked_root_files` reads `git ls-files`, so it enumerates what
        # exists and the dangerous file is one that does not. MSBuild imports
        # `Directory.Build.targets` into every build of every project beneath
        # it, so creating a root file no enumeration contains and then running
        # a build is host code execution — an `Exec` in an auto-imported
        # `.targets` runs and `dotnet build` reports success.
        for name in self.SUBJECTS:
            rules = self.disallowed(name)
            for target in self.AUTO_IMPORTED:
                with self.subTest(command=name, target=target):
                    self.assertIn(f"Edit({target})", rules)
                    self.assertIn(f"Edit(./{target})", rules)

    def test_the_auto_imported_names_are_not_all_tracked(self):
        # The positive control for the case above: at least one auto-imported
        # name is absent from the repository, so a test built on `git ls-files`
        # cannot reach it. If every name here became tracked this assertion
        # fails, which is the signal to re-derive the list rather than to
        # delete this test.
        tracked = self.tracked_root_files()
        absent = [n for n in self.AUTO_IMPORTED if n not in tracked]
        self.assertIn("Directory.Build.targets", absent)

    def test_no_command_grants_a_free_form_dotnet(self):
        # `dotnet test:*` admits an arbitrary project path and
        # `/p:CustomBeforeMicrosoftCommonTargets=<file>`, which imports
        # whatever it points at — `suggestions.md`, which /review-branch
        # writes, being a legal target. The executor is `dotnet-test.sh`, whose
        # only variable is one word out of two. Asserted over every command
        # rather than the one that held the grant, because a withdrawal that
        # names two files leaves a third.
        for path in sorted(COMMANDS.glob("*.md")):
            text = path.read_text(encoding="utf-8")
            granted = re.findall(r"^allowed-tools:\s*(.+)$", text, re.MULTILINE)
            for line in granted:
                with self.subTest(command=path.name):
                    self.assertNotIn("Bash(dotnet ", line)

    def test_the_test_runner_helper_takes_no_free_parameter(self):
        # The helper leaves nothing to steer: the solution, the filter and the
        # flags are literals, and the one argument is matched against a fixed
        # case rather than passed on.
        source = (SCRIPTS / "dotnet-test.sh").read_text(encoding="utf-8")
        self.assertIn("dotnet test Platform.slnx", source)
        self.assertNotIn('"$@"', source)
        self.assertNotIn("$mode\"", source.replace('"$mode" in', ""))

    # What `/validate-blueprint` actually audits, from its own opening lines.
    AUDITED_BY_VALIDATE_BLUEPRINT = {
        "backend-architecture",
        "roadmap.md",
        "testing.md",
    }

    def test_validate_blueprint_may_only_edit_what_it_audits(self):
        # `docs/` is exempt as a tree and the command audits three paths inside
        # it, so the rest of `docs/` — `superpowers/`, which `CLAUDE.md` calls
        # a frozen record, along with `runbooks/` and the loose files — is
        # denied entry by entry. The entries are read from git rather than
        # listed here, so a new file under `docs/` fails this until someone
        # decides which side it is on.
        out = subprocess.run(
            [GIT, "ls-files", "docs/"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True,
        )
        if out.returncode != 0:
            raise AssertionError(f"git ls-files failed: {out.stderr}")

        entries = {
            line.split("/")[1] for line in out.stdout.splitlines()
            if line.startswith("docs/") and len(line.split("/")) > 1
        }
        self.assertTrue(entries, "found no entries under docs/")
        self.assertTrue(
            self.AUDITED_BY_VALIDATE_BLUEPRINT <= entries,
            "the audited set names something docs/ does not hold: "
            f"{self.AUDITED_BY_VALIDATE_BLUEPRINT - entries}")

        rules = self.disallowed("validate-blueprint.md")
        for entry in sorted(entries - self.AUDITED_BY_VALIDATE_BLUEPRINT):
            with self.subTest(entry=entry):
                suffix = "/**" if "." not in entry else ""
                for prefix in ("", "./"):
                    self.assertIn(f"Edit({prefix}docs/{entry}{suffix})", rules)

    def test_gits_own_control_directory_is_denied(self):
        # `.git` is absent from `git ls-files`, so the coverage test cannot
        # reach it. With an unrestricted `Edit` a command could write
        # `.git/config`, set `diff.external`, and get host execution out of its
        # own approved `git diff`. Denied as a tree and as a file, because in a
        # worktree `.git` is a file pointing at the real directory.
        #
        # `/review-grok` is covered here and not in SUBJECTS: it holds `Edit`
        # for `src/`, `tests/` and `docs/` by design, so the tracked-tree cases
        # above are not its shape, but a site under `.git/` is a regular file a
        # crafted review can quote a real line from.
        for name in (*self.SUBJECTS, "review-grok.md"):
            rules = self.disallowed(name)
            for target in (".git/**", "./.git/**", ".git", "./.git"):
                with self.subTest(command=name, target=target):
                    self.assertIn(f"Edit({target})", rules)

    def test_the_repository_tracks_no_symbolic_link(self):
        # `/review-grok`'s site contract holds its path denies by spelling, and
        # a tracked symbolic link inside an allowed tree is a spelling the deny
        # never sees while its target can be anywhere. This case makes the
        # command's premise — that no such link is tracked — a gate on every
        # push rather than a sentence about one checkout. It is defence in
        # depth beside `.claude/hooks/guard-edit-target.py`, which decides at
        # edit time: this one goes red when a link is committed, the guard when
        # one is written through.
        out = subprocess.run(
            [GIT, "ls-files", "-s"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True,
        )
        if out.returncode != 0:
            raise AssertionError(f"git ls-files failed: {out.stderr}")
        entries = out.stdout.splitlines()
        # The positive control: the parse is over a real listing, so an empty
        # result would be a broken command rather than a clean tree.
        self.assertGreater(len(entries), 100, "git ls-files -s returned almost nothing")
        links = [e for e in entries if e.startswith("120000 ")]
        self.assertEqual([], links, f"tracked symbolic links: {links}")

    def test_the_git_directory_is_not_tracked(self):
        # The positive control, and the reason the case above cannot be folded
        # into the tracked-file test: `git ls-files` never reports `.git`, so
        # an inventory read from it is blind here by construction.
        self.assertNotIn(".git", self.tracked_root_files())
        self.assertNotIn(".git", self.tracked_trees())

    def test_the_one_legitimate_output_stays_writable(self):
        # The reason the root is enumerated rather than denied wholesale:
        # `suggestions.md` is `/review-branch`'s only output and is untracked,
        # so denying every tracked root file leaves it alone where a blanket
        # `Edit(**)` would take the command's own deliverable with it.
        self.assertNotIn("suggestions.md", self.tracked_root_files())
        for name in self.SUBJECTS:
            for rule in self.disallowed(name):
                with self.subTest(command=name, rule=rule):
                    self.assertNotIn("suggestions.md", rule)
                    self.assertNotEqual("Edit(**)", rule)
                    self.assertNotEqual("Edit(./**)", rule)
                    self.assertNotEqual("Edit(/*)", rule)



if __name__ == "__main__":
    unittest.main()
