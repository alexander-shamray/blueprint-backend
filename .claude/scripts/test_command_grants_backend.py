"""This repository's `/mutation-sweep` held to the payload's grant cases, under the mutation route.

`test_command_grants.py` holds the payload's commands and the chained ones `local_commands.py` declares; its classes
are reached through the module, never bound to a name here, or unittest would run the payload's class a second time.
"""

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


if __name__ == "__main__":
    unittest.main()
