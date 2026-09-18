"""pr-for-branch.sh: the pull requests the branch-name filter keeps.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    BASH,
    setUpModule,
)


class OnlyThisCheckoutsPullRequestsSurvive(unittest.TestCase):
    """Only this checkout's pull requests survive the branch-name filter.

    `gh pr list --head <branch>` filters on the name, so an outside
    contributor's same-named branch is a candidate. /ship step 0 reads this to
    decide whether the branch landed and /pr to decide whether one is open, so
    the wrong row is the unattended flow acting on a stranger's pull request.

    Real rows go through the real `jq` pipeline behind a stubbed `gh`: a
    structural mention of `headRepository` cannot tell a working filter from a
    typo that drops the legitimate row or keeps the fork's.
    """

    HELPER = SCRIPTS / "pr-for-branch.sh"
    OWNER = "acme/widgets"

    ROWS = """[
      {"number": 1, "state": "OPEN", "url": "u1",
       "headRepository": {"nameWithOwner": "acme/widgets"}},
      {"number": 2, "state": "OPEN", "url": "u2",
       "headRepository": {"nameWithOwner": "mallory/widgets"}},
      {"number": 3, "state": "MERGED", "url": "u3",
       "headRepository": null},
      {"number": 4, "state": "CLOSED", "url": "u4",
       "headRepository": {"nameWithOwner": "acme/widgets-fork"}}
    ]"""

    def setUp(self):
        self.bin = Path(tempfile.mkdtemp(prefix="ghstub-"))
        stub = self.bin / "gh"
        stub.write_text(
            "#!/usr/bin/env bash\n"
            'if [ "$1 $2" = "repo view" ]; then printf "%s" "$STUB_OWNER"; exit 0; fi\n'
            'if [ "$1 $2" = "pr list" ]; then printf "%s" "$STUB_ROWS"; exit 0; fi\n'
            'echo "unexpected gh call: $*" >&2; exit 9\n',
            encoding="utf-8", newline="\n")
        stub.chmod(0o755)

    def tearDown(self):
        shutil.rmtree(self.bin, ignore_errors=True)

    def run_helper(self, owner=None, rows=None):
        env = {
            **os.environ,
            "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
            "STUB_OWNER": self.OWNER if owner is None else owner,
            "STUB_ROWS": self.ROWS if rows is None else rows,
        }
        return subprocess.run(
            [BASH, str(self.HELPER), "some-branch"],
            capture_output=True, text=True, env=env)

    def test_only_this_checkouts_pull_requests_survive(self):
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        got = json.loads(result.stdout)
        self.assertEqual([1], [row["number"] for row in got])

    def test_a_fork_with_the_same_branch_name_is_dropped(self):
        # Number 2 is `mallory/widgets` on the same branch name, and reaching
        # /ship step 0 with it means acting on a stranger's pull request.
        got = json.loads(self.run_helper().stdout)
        self.assertNotIn(2, [row["number"] for row in got])

    def test_a_null_head_repository_is_dropped_rather_than_crashing(self):
        # A deleted fork reports `headRepository: null`. `// ""` makes that a
        # non-match instead of an error, and non-match is the safe direction.
        got = json.loads(self.run_helper().stdout)
        self.assertNotIn(3, [row["number"] for row in got])

    def test_a_prefix_of_the_owner_is_not_the_owner(self):
        # `acme/widgets-fork` starts with `acme/widgets`, so the comparison is
        # equality rather than prefix.
        got = json.loads(self.run_helper().stdout)
        self.assertNotIn(4, [row["number"] for row in got])

    def test_the_shape_is_unchanged_for_callers(self):
        got = json.loads(self.run_helper().stdout)
        self.assertEqual({"number", "state", "url"}, set(got[0]))

    def test_an_unresolvable_owner_stops_the_helper(self):
        result = self.run_helper(owner="")
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("mallory", result.stdout)



if __name__ == "__main__":
    unittest.main()
