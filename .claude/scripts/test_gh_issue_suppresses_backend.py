"""This repository's own sweep held to the issue-set helpers: `/mutation-sweep`.

`test_gh_issue_suppresses.py` holds the payload's sweeps to the rule; this case holds the local one. `/mutation-sweep`
names `gh-issue-list.sh` mid-sentence, so the payload's enumeration control does not read it.
"""

import unittest

from review_helpers import setUpModule
import test_gh_issue_suppresses as payload


class MutationSweepReachesIssuesOnlyThroughTheHelpers(payload.TheSweepsReachIssuesOnlyThroughTheHelpers):
    SWEEPS = ("mutation-sweep.md",)


if __name__ == "__main__":
    unittest.main()
