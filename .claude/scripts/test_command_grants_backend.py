"""This repository's own commands' grants: `/mutation-sweep`'s mutation route, and the writers' memory denies.

`test_command_grants.py` holds the payload's commands and the chained ones `local_commands.py` declares; its classes
are reached through the module, never bound to a name here, or unittest would run the payload's class a second time.
"""

import json
import re
import unittest

import test_command_grants as payload


class MutationSweepDeniesEveryGhCommand(payload.TheSweepsDenyEveryGhCommand):
    SWEEPS = ("mutation-sweep.md",)
    # Its own entry in the sweep entry's place: it denies `gh-issue-sweep.sh` by name, an entry it does not hold.
    HELPERS = ("bash .claude/scripts/gh-issue-mutation.sh low code,test", "bash .claude/scripts/gh-issue-list.sh")


class MutationSweepFilesUnderTheMutationRoute(unittest.TestCase):
    """The payload's route rule for a sweep, with `gh-issue-mutation.sh` as the entry (`docs/harness-boundaries.md`)."""

    ROUTE = payload.EachCommandFilesUnderOneRoute
    TEXT = (payload.COMMANDS / "mutation-sweep.md").read_text(encoding="utf-8")

    def test_it_is_granted_the_mutation_entry_and_denies_every_other_by_name(self):
        allowed = payload.frontmatter_list(self.TEXT, "allowed-tools")
        denied = payload.frontmatter_list(self.TEXT, "disallowed-tools")
        self.assertIn(self.ROUTE.MUTATION, allowed)
        for other in (self.ROUTE.CREATE, self.ROUTE.SWEEP, self.ROUTE.HAND):
            with self.subTest(entry=other):
                self.assertNotIn(other, allowed)
                self.assertIn(other, denied)

    def test_it_files_through_the_entry_it_is_granted(self):
        body = self.TEXT.split("---", 2)[2]
        self.assertIn("bash .claude/scripts/gh-issue-mutation.sh low code,test", body)
        self.assertNotIn("gh-issue-create.sh", body)


class TheLocalWritingCommandsDenyTheUsersMemoryAndOutputStyles(unittest.TestCase):
    """A memory file or an output style instructs every later session, and these commands read a branch's text.

    The set is read from the tree, so a new local command that writes is a red build rather than a quiet widening.
    """

    REQUIRED = ("Edit(~/.claude/projects/**)", "Edit(~/.claude/output-styles/**)")
    MANIFEST = payload.COMMANDS.parent.parent / ".harness" / "manifest.json"

    @staticmethod
    def writes(tool):
        return re.match(r"(Write|Edit)(\(|$)", tool) is not None

    def writers(self):
        managed = json.loads(self.MANIFEST.read_text(encoding="utf-8"))["files"]
        paths = [path for path in payload.COMMANDS.glob("*.md") if f".claude/commands/{path.name}" not in managed]
        return [path for path in paths
                if any(self.writes(tool)
                       for tool in payload.frontmatter_list(path.read_text(encoding="utf-8"), "allowed-tools"))]

    def test_the_local_writing_commands_are_found(self):
        # The positive control: a set that found none would pass the case below. `/mutation-sweep` is local and holds
        # neither tool, and `/ship` writes and is the payload's, so each side of the filter is held.
        names = {path.name for path in self.writers()}
        self.assertLessEqual(
            {"review-branch.md", "check-links.md", "new-adr.md", "new-chapter.md", "validate-blueprint.md"}, names)
        self.assertNotIn("mutation-sweep.md", names)
        self.assertNotIn("ship.md", names)

    def test_every_local_command_granting_write_or_edit_denies_both_paths(self):
        for path in self.writers():
            rules = payload.frontmatter_list(path.read_text(encoding="utf-8"), "disallowed-tools")
            for required in self.REQUIRED:
                with self.subTest(command=path.name, rule=required):
                    self.assertIn(required, rules)


if __name__ == "__main__":
    unittest.main()
