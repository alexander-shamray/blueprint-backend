"""This repository's own commands held to the payload's grant cases: `/mutation-sweep`, and the two checks
`docs/harness-local.md`'s Ship checks has `/ship` run before it pushes.

`test_command_grants.py` is the payload's and holds the payload's commands; these cases, split out of it by
blueprint-harness#5, hold the local ones to the same rules, since only this repository has them. A payload class
is reached through its module and never bound to a name here: unittest runs every TestCase in this module's
namespace, so an alias would run the payload's class a second time.
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
