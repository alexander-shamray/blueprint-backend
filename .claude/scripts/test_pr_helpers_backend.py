"""This repository's own cases for pr-for-branch.sh and pr-locality.sh: the shipped class map, the locality gate
that reads it, and the local `/review-branch`'s grants.

`test_pr_helpers.py` holds every case a target without these files runs; these read files only this repository has.
"""

import json
import re
import subprocess
import sys
import unittest

from review_helpers import SCRIPTS, COMMANDS, setUpModule
import test_pr_helpers as payload


# The payload's helpers, reached through its module and never bound to a name here: unittest runs every TestCase in
# this module's namespace, so an alias would run the payload's class a second time.
def printing(body, files):
    return payload.NoCommandReadsAPullRequestFeedUnfiltered._gh_printing(body, files)


def run_locality(case, script):
    # The payload's runner, over this checkout's own map rather than the payload's fixture: the map is the subject.
    feed = payload.NoCommandReadsAPullRequestFeedUnfiltered
    return feed._run_helper_with_gh(case, "pr-locality.sh", "187", script, scripts=SCRIPTS)


class TheShippedClassMapIsTheGatesOwn(unittest.TestCase):

    def test_the_class_verdict_is_the_gates_own(self):
        # One payload through both readers of classes.yml: a path is
        # `outside-class` here exactly when the gate says it is outside the
        # class's tree set, whatever the row admits, on these paths over the shipped map.
        paths = [
            "src/Services/Ordering/A.cs", "tests/Ordering.Domain.Tests/A.cs", "docs/x.md",
            ".claude/scripts/x.sh", ".github/workflows/x.yml", "deploy/compose/x.yml",
            "tools/new-service/x.py", "Directory.Packages.props", "Platform.slnx",
            "src/Services/Ordering/Ordering.Api/Ordering.Api.csproj", "CLAUDE.md",
        ]
        root = SCRIPTS.parent.parent
        gate = root / ".github" / "locality-gate" / "locality_gate.py"
        for cls in ("A", "B", "C", "D", "E", "C+E", "A+D+E"):
            with self.subTest(cls=cls):
                body = f"| Class | {cls} |\n| Touch set | {', '.join(paths[:6])} |\n"
                r = run_locality(self, printing(body, "\n".join(paths) + "\n"))
                self.assertEqual(0, r.returncode, r.stderr)
                ours = {line.split(" ", 1)[1] for line in r.stdout.splitlines()
                        if line.startswith("outside-class ")}
                payload_json = json.dumps({"number": 1, "body": body, "files": paths, "changedFiles": len(paths)})
                g = subprocess.run(
                    [sys.executable, str(gate), "--root", str(root)], input=payload_json,
                    capture_output=True, text=True, encoding="utf-8")
                self.assertIn(g.returncode, (0, 1), g.stderr)
                theirs = set(re.findall(r"`([^`]+)` is outside class", g.stderr))
                self.assertEqual(theirs, ours)
                self.assertTrue(ours or cls == "A+D+E", "a class with nothing outside it proves nothing")


class ReviewBranchReadsThroughTheFixedHelpers(unittest.TestCase):
    """`/review-branch` holds the two grants the payload's commands are held to in `test_pr_helpers.py`."""

    def test_review_branch_grants_both_helpers(self):
        frontmatter = (COMMANDS / "review-branch.md").read_text(encoding="utf-8").split("---")[1]
        for helper in ("pr-for-branch.sh", "pr-locality.sh"):
            with self.subTest(helper=helper):
                self.assertIn(f"bash .claude/scripts/{helper}:*", frontmatter)


if __name__ == "__main__":
    unittest.main()
