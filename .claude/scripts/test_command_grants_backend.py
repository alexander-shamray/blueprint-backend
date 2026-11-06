"""This repository's own commands held to the payload's grant cases: `/mutation-sweep` and `/ship`'s two local checks.

`test_command_grants.py` holds the payload's commands. Its classes are reached through the module and never bound to a
name here: unittest runs every TestCase in this namespace, so an alias would run the payload's class a second time.
"""

import unittest

import test_command_grants as payload


class MutationSweepDeniesEveryGhCommand(payload.TheSweepsDenyEveryGhCommand):
    SWEEPS = ("mutation-sweep.md",)


class TheLocalShipChecksDenyNoPush(unittest.TestCase):
    """The payload's case, over the commands this repository's Ship checks add to the chain."""

    CHAINED_BEFORE_A_PUSH = ("validate-blueprint.md", "check-links.md")
    PUSHES = payload.NothingShipChainsDeniesPush.PUSHES

    # The case itself rather than a subclass, so the payload's matcher controls are not run a second time.
    test_none_of_them_denies_push_or_the_shell = (
        payload.NothingShipChainsDeniesPush.test_none_of_them_denies_push_or_the_shell)


if __name__ == "__main__":
    unittest.main()
