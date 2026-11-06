"""git-trailer-pr.sh's refusal of `main`, which its `helper` row in `docs/harness-boundaries.md` names by this file.

The rest of the helper's cases are in `test_git_pr_trailer_helpers.py`, whose fixture this borrows.
"""

import unittest

from review_helpers import setUpModule  # noqa: F401
from test_git_pr_trailer_helpers import TRAILER, Fixture

SPELLINGS = ("main", "Main", "MAIN", "heads/main", "refs/heads/main",
             "origin/main", "refs/remotes/origin/main")


class TheHelperRefusesMain(Fixture):
    def test_main_is_refused_however_it_is_spelled(self):
        # `symbolic-ref` takes every spelling on every filesystem, where a checkout finds `Main` taken on one and
        # absent on another. The row names the same branch, so the name is the only thing that stops the helper.
        tip = self.git("rev-parse", "HEAD")
        for spelling in SPELLINGS:
            with self.subTest(spelling=spelling):
                self.git("update-ref", f"refs/heads/{spelling}", tip)
                self.git("symbolic-ref", "HEAD", f"refs/heads/{spelling}")
                out = self.run_helper(TRAILER, row=f"OPEN\t{spelling}\tmain\tfalse")
                self.assertEqual(3, out.returncode, f"{spelling!r}: {out.stderr}")
                self.assertIn("refusing to rewrite main", out.stderr)
        self.git("symbolic-ref", "HEAD", "refs/heads/main")
        self.assertEqual(tip, self.git("ls-remote", "origin", "main").split()[0])

    def test_a_branch_whose_name_only_holds_main_is_not_main(self):
        self.branch("feat/main")
        self.commit("one", "feat(x): one")
        out = self.run_helper(TRAILER, row="OPEN\tfeat/main\tmain\tfalse")
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual(["feat(x): one|#7"], self.subjects_and_trailers("origin/main..HEAD"))


if __name__ == "__main__":
    unittest.main()
