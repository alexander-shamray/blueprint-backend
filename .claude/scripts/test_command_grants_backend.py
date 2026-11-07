"""This repository's own commands held to the payload's grant cases: `/mutation-sweep` and `/ship`'s two local checks.

`test_command_grants.py` holds the payload's commands. Its classes are reached through the module and never bound to a
name here: unittest runs every TestCase in this namespace, so an alias would run the payload's class a second time.
"""

import unittest

import local_commands
import test_command_grants as payload


class MutationSweepDeniesEveryGhCommand(payload.TheSweepsDenyEveryGhCommand):
    SWEEPS = ("mutation-sweep.md",)


class MutationSweepGrantsNoHelperThatTakesARoute(unittest.TestCase):
    """`gh-issue-create.sh` takes its route as an argument, so a grant on it could claim any route's last line."""

    def test_it_does_not_grant_that_helper(self):
        text = (payload.COMMANDS / "mutation-sweep.md").read_text(encoding="utf-8")
        allowed = payload.frontmatter_list(text, "allowed-tools")
        self.assertEqual([], [entry for entry in allowed if "gh-issue-create.sh" in entry])


class TheLocalShipChecksDenyNoPush(unittest.TestCase):
    """The payload's case, over the commands this repository's Ship checks add to the chain."""

    CHAINED_BEFORE_A_PUSH = local_commands.CHAINED_BEFORE_A_PUSH
    PUSHES = payload.NothingShipChainsDeniesPush.PUSHES

    # The case itself rather than a subclass, so the payload's matcher controls are not run a second time.
    test_none_of_them_denies_push_or_the_shell = (
        payload.NothingShipChainsDeniesPush.test_none_of_them_denies_push_or_the_shell)


if __name__ == "__main__":
    unittest.main()
