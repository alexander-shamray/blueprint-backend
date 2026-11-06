"""This repository's own sweep held to the issue-set helpers: `/mutation-sweep`.

`test_gh_issue_suppresses.py` is the payload's and holds its sweeps to the rule; this case, split out of it by
blueprint-harness#5, holds the local one to the same cases. `/mutation-sweep` names `gh-issue-list.sh`
mid-sentence rather than on a line of its own, so the payload's enumeration control does not read it.
"""

import unittest

from review_helpers import setUpModule
import test_gh_issue_suppresses as payload


class MutationSweepReachesIssuesOnlyThroughTheHelpers(payload.TheSweepsReachIssuesOnlyThroughTheHelpers):
    SWEEPS = ("mutation-sweep.md",)


if __name__ == "__main__":
    unittest.main()
