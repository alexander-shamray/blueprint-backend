"""git-trailer-pr.sh's refusal of `main`, which its `helper` row in `docs/harness-boundaries.md` names by this file,
and of an `origin` whose push goes anywhere but the repository it was added with.

The rest of the helper's cases are in `test_git_pr_trailer_helpers.py`, whose fixture this borrows.
"""

import unittest

from review_helpers import setUpModule  # noqa: F401
from test_git_pr_trailer_helpers import TRAILER, Fixture, sh

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


class TheHelperRefusesARedirectedPush(Fixture):
    def setUp(self):
        super().setUp()
        self.branch()
        self.commit("one", "feat(x): one")
        self.origin_url = self.git("config", "--get", "remote.origin.url")
        self.tip = self.git("rev-parse", "HEAD")

    def assert_refused(self, *expected):
        # Its own exit, since 7 is a push that failed after the rewrite, whose answer is a rerun. Refused ahead of the
        # rewrite too: a commit stamped and never published is what a rerun of that exit would then go on to force.
        out = self.run_helper(TRAILER)
        self.assertEqual(8, out.returncode, out.stderr)
        for line in expected:
            self.assertIn(line, out.stderr)
        self.assertEqual(self.tip, self.git("rev-parse", "HEAD"), "the commits were rewritten")
        # Asked of the directory, since a rewritten fetch URL would answer for another repository.
        on_origin = sh('git -C "$R" rev-parse refs/heads/feat/x', R=f"{self.root}/remote.git").stdout.strip()
        self.assertEqual(self.tip, on_origin, "the branch was forced")

    def test_a_configured_push_redirect_is_refused_before_anything_is_rewritten(self):
        for key, value in (("remote.origin.pushurl", "https://example.invalid/x.git"),
                           ("url.https://example.invalid/evil.git.pushinsteadof", self.origin_url)):
            with self.subTest(key=key):
                self.git("config", "--local", key, value)
                self.assert_refused("a redirected force is not published")
                self.git("config", "--local", "--unset-all", key)

    def test_a_remote_with_two_urls_is_refused_before_anything_is_rewritten(self):
        self.git("config", "--local", "--add", "remote.origin.url", f"{self.origin_url}-second")
        self.assert_refused("origin has 2 push URLs")

    def test_a_second_url_behind_a_single_push_url_is_refused(self):
        # `pushurl` names the second `url`, so one push URL matches the last `url` while the fetch reads the first.
        self.git("config", "--local", "--add", "remote.origin.url", f"{self.origin_url}-second")
        self.git("config", "--local", "remote.origin.pushurl", f"{self.origin_url}-second")
        self.assert_refused("origin has more than one url")

    def test_a_fetch_url_parted_from_the_push_url_is_refused(self):
        # `insteadOf` moves the fetch to a copy and `pushInsteadOf` puts the push back, so the push URL is the one the
        # remote was added with while the lease reads another repository's tip.
        copy = f"{self.root}/copy.git"
        self.assertEqual(0, sh('git clone -q --bare "$R/remote.git" "$C"', R=self.root, C=copy).returncode)
        self.git("config", "--local", f"url.{copy}.insteadof", self.origin_url)
        self.git("config", "--local", f"url.{self.origin_url}.pushinsteadof", self.origin_url)
        self.assert_refused("origin fetches from")

    def test_a_push_url_spelling_the_same_repository_differently_is_published(self):
        # The control for both refusals: the comparison is of the repository named, not of two strings.
        self.git("config", "--local", "remote.origin.pushurl", f"{self.origin_url}/")
        out = self.run_helper(TRAILER)
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual(["feat(x): one|#7"], self.subjects_and_trailers("origin/main..HEAD"))
        self.assertEqual(self.git("rev-parse", "HEAD"), self.git("ls-remote", "origin", "feat/x").split()[0])


class TheLeaseIsReadUnderANarrowedFetch(Fixture):
    def test_a_fetch_refspec_naming_only_main_still_reads_the_branch_tip(self):
        # A bare `git fetch origin main <branch>` writes no origin/<branch> that remote.origin.fetch does not map.
        self.branch()
        self.commit("one", "feat(x): one")
        self.git("config", "--local", "remote.origin.fetch", "+refs/heads/main:refs/remotes/origin/main")
        self.git("update-ref", "-d", "refs/remotes/origin/feat/x")
        out = self.run_helper(TRAILER)
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual(["feat(x): one|#7"], self.subjects_and_trailers("origin/main..HEAD"))
        self.assertEqual(self.git("rev-parse", "HEAD"), self.git("ls-remote", "origin", "feat/x").split()[0])


if __name__ == "__main__":
    unittest.main()
