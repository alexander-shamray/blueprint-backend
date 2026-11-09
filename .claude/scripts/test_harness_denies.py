"""settings.json's deny list: the editing boundaries the harness states.

A command's own `disallowed-tools` boundary is a target's, in that target's
suite. The shared harness and why it shells out are `review_helpers.py`'s.
"""

import json
import subprocess
import unittest

import re

from review_helpers import (
    COMMANDS,
    SCRIPTS,
    SETTINGS,
    GIT,
    setUpModule,
)
from test_command_grants import bash_deny_matches


class HarnessControlSurfaceIsDenied(unittest.TestCase):
    """The deny list covers the files that grant, not only the helpers.

    `commands/`, `agents/` and `settings.json` hand out the grants that run the helpers in `.claude/scripts/`, so a
    command carrying an unrestricted `Edit` while reading untrusted input reaches the frontmatter as well as the feed.
    """

    def deny(self):
        return json.loads(SETTINGS.read_text(encoding="utf-8"))["permissions"]["deny"]

    def test_every_control_surface_path_is_denied_in_both_spellings(self):
        deny = self.deny()
        # `hooks/**` is on the list because a hook runs on every Bash call: a
        # session able to rewrite one could delete its own guard and then act.
        for path in (".claude/scripts/**", ".claude/commands/**", ".claude/agents/**",
                     ".claude/hooks/**", ".claude/skills/**",
                     ".claude/settings.json", ".claude/settings.local.json"):
            for prefix in ("", "./"):
                with self.subTest(path=path, prefix=prefix):
                    self.assertIn(f"Edit({prefix}{path})", deny)

    def test_the_harness_tree_is_denied_at_the_root_alone(self):
        # It holds the base settings these denies render from. A rule of one component reaches any depth, with or
        # without `./`, measured on 2.1.319 (`docs/harness-boundaries.md`, *Normalisation, residuals and the
        # sweeps*), and a bare spelling would refuse `payload/.harness/` in the repository that develops it.
        deny = self.deny()
        self.assertIn("Edit(/.harness/**)", deny)
        for rule in ("Edit(.harness/**)", "Edit(./.harness/**)"):
            with self.subTest(rule=rule):
                self.assertNotIn(rule, deny)

    def test_no_one_component_directory_deny_of_the_base_reaches_a_tracked_tree_below_the_root(self):
        # The subject is the tree, so a new rule of this shape meets whatever a checkout holds. A `**/` rule says it
        # means any depth, so it is left out, and the base is read and not the settings, since a target's own may too.
        base = json.loads((SCRIPTS.parent.parent / ".harness" / "settings.base.json").read_text(encoding="utf-8"))
        rules = [rule[len("Edit("):-len("/**)")].removeprefix("./") for rule in base["permissions"]["deny"]
                 if rule.startswith("Edit(") and rule.endswith("/**)")]
        names = {rule for rule in rules if rule and "/" not in rule and "*" not in rule}
        self.assertTrue(names, "no one-component directory rule was found to judge")
        tracked = subprocess.run([GIT, "ls-files"], cwd=SCRIPTS.parent.parent, capture_output=True,
                                 encoding="utf-8", check=True).stdout.splitlines()
        self.assertTrue(tracked, "the tracked listing went empty")
        for path in tracked:
            with self.subTest(path=path):
                self.assertFalse(names & set(path.split("/")[1:-1]))

    MACHINERY = (".git/**", ".git", ".mcp.json", ".codeindexignore")

    def test_every_editing_command_is_held_off_the_machinery_by_the_settings(self):
        # Edit and Write have no path scope, so a command granted either reaches the tree the checks and the
        # index run from unless the settings deny it; the deny is global because the grant is per command.
        deny = self.deny()
        editing = [path.name for path in sorted(COMMANDS.glob("*.md"))
                   if re.search(r"(^|,\s*)(Edit|Write)(\s*,|\s*$)",
                                " ".join(re.findall(r"^allowed-tools:\s*(.+)$",
                                                    path.read_text(encoding="utf-8").split("\n---", 1)[0],
                                                    re.MULTILINE)))]
        self.assertGreaterEqual(len(editing), 3, "found almost no editing commands")
        for name in editing:
            for path in self.MACHINERY:
                for prefix in ("", "./"):
                    with self.subTest(command=name, path=path, prefix=prefix):
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

        `.gitignore` names `settings.local.json` as the per-developer override.
        `.claude/**` is no answer: `/branch` puts live worktrees under it.
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

    # The raw forms `--admin` and `-f` come through, and the helpers `/ship` lands and tears down with.
    RAW = {"git worktree remove -f .claude/worktrees/x": "bash .claude/scripts/git-worktree-remove.sh "
                                                          ".claude/worktrees/x feature/x",
           "gh pr merge 7 --rebase --admin": "bash .claude/scripts/gh-pr-merge.sh 7 " + "0" * 40}

    def test_each_raw_form_is_denied_and_the_helper_is_not(self):
        # A deny judges the typed command and never one a script runs, so the helper stays reachable.
        deny = self.deny()
        for raw, helper in self.RAW.items():
            for command in (raw, raw.split(" -", 1)[0]):
                with self.subTest(refused=command):
                    self.assertTrue(any(bash_deny_matches(rule, command) for rule in deny))
            with self.subTest(reachable=helper):
                self.assertFalse(any(bash_deny_matches(rule, helper) for rule in deny))

    def test_the_deny_list_is_actually_read(self):
        # The positive control. Every assertion above would pass against a file
        # whose deny list this method could not find at all, if the lookup
        # silently yielded an empty list.
        self.assertGreater(len(self.deny()), 20)
        self.assertIn("Bash(git *--output*)", self.deny())


class TheRepositoryTracksNoSymbolicLink(unittest.TestCase):
    """What every path deny assumes of the tree it is written against."""

    def test_the_repository_tracks_no_symbolic_link(self):
        # A tracked symbolic link inside an allowed tree is a spelling no path deny sees while its target can be
        # anywhere. This goes red when one is committed, and `.claude/hooks/guard-edit-target.py` refuses one
        # written through at edit time.
        out = subprocess.run(
            [GIT, "ls-files", "-s"], cwd=str(SCRIPTS.parent.parent),
            capture_output=True, text=True, encoding="utf-8",
        )
        if out.returncode != 0:
            raise AssertionError(f"git ls-files failed: {out.stderr}")
        entries = out.stdout.splitlines()
        # The positive control: the parse is over a real listing, so an empty
        # result would be a broken command rather than a clean tree.
        self.assertGreater(len(entries), 100, "git ls-files -s returned almost nothing")
        links = [e for e in entries if e.startswith("120000 ")]
        self.assertEqual([], links, f"tracked symbolic links: {links}")


if __name__ == "__main__":
    unittest.main()
