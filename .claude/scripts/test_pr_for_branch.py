"""pr-for-branch.sh: the pull requests the branch-name filter keeps.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import json
import os
import subprocess
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    BASH,
    GIT,
    setUpModule,
    temp_root,
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
      {"number": 1, "state": "OPEN", "url": "u1", "headRefOid": "aaa1", "baseRefName": "main",
       "headRepository": {"nameWithOwner": "acme/widgets"}},
      {"number": 2, "state": "OPEN", "url": "u2",
       "headRepository": {"nameWithOwner": "mallory/widgets"}},
      {"number": 3, "state": "MERGED", "url": "u3",
       "headRepository": null},
      {"number": 4, "state": "CLOSED", "url": "u4",
       "headRepository": {"nameWithOwner": "acme/widgets-fork"}}
    ]"""

    def setUp(self):
        self.bin = Path(temp_root(self))
        stub = self.bin / "gh"
        stub.write_text(
            "#!/usr/bin/env bash\n"
            'if [ "$1 $2" = "repo view" ]; then printf "%s" "$STUB_OWNER"; exit 0; fi\n'
            'if [ "$1 $2" = "pr list" ]; then echo "$*" >> "$STUB_CALLS"; cat "$STUB_ROWS_FILE"; exit 0; fi\n'
            'echo "unexpected gh call: $*" >&2; exit 9\n',
            encoding="utf-8", newline="\n")
        stub.chmod(0o755)

    def run_helper(self, owner=None, rows=None, cwd=None, branch="some-branch"):
        # The rows travel in a file: a thousand of them overrun an environment block.
        (self.bin / "rows.json").write_text(self.ROWS if rows is None else rows, encoding="utf-8")
        env = {
            **os.environ,
            "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
            "STUB_OWNER": self.OWNER if owner is None else owner,
            "STUB_ROWS_FILE": str(self.bin / "rows.json"),
            "STUB_CALLS": str(self.bin / "calls"),
        }
        return subprocess.run(
            [BASH, str(self.HELPER), branch],
            capture_output=True, text=True, encoding="utf-8", env=env, cwd=cwd)

    def listing(self, rows, **kw):
        return self.run_helper(rows=json.dumps(rows), **kw)

    @staticmethod
    def row(number, state, base="main", owner="acme/widgets", **more):
        return {"number": number, "state": state, "url": f"u{number}", "headRefOid": f"h{number}",
                "baseRefName": base, "headRepository": {"nameWithOwner": owner}, **more}

    def numbers(self, result):
        self.assertEqual(0, result.returncode, result.stderr)
        return [r["number"] for r in json.loads(result.stdout)]

    def make_repo(self):
        path = temp_root(self)

        def git(*args):
            return subprocess.run(
                [GIT, "-C", path, "-c", "user.email=t@example.com", "-c", "user.name=t", *args],
                check=True, capture_output=True, text=True, encoding="utf-8").stdout.strip()

        git("init", "-q", "-b", "main")
        git("commit", "-q", "--allow-empty", "-m", "root")
        return path, git

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

    def test_the_shape_is_the_fixed_field_set(self):
        got = json.loads(self.run_helper().stdout)
        self.assertEqual({"number", "state", "url", "headRefOid", "baseRefName"}, set(got[0]))

    def test_the_head_is_the_rows_own(self):
        # /ship step 0 compares this value with the tip, so a projection that
        # named the field and dropped the value would keep every workspace.
        got = json.loads(self.run_helper().stdout)
        self.assertEqual("aaa1", got[0]["headRefOid"])
        self.assertEqual("main", got[0]["baseRefName"])

    def test_the_head_is_asked_for(self):
        # The stub answers any field list, so the request is read from the call it
        # logged: a field jq projects and `gh` was never asked for comes back null.
        self.run_helper()
        asked = (self.bin / "calls").read_text(encoding="utf-8").split("--json", 1)[1]
        self.assertIn("headRefOid", asked)
        self.assertIn("baseRefName", asked)

    def test_an_unresolvable_owner_stops_the_helper(self):
        result = self.run_helper(owner="")
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("mallory", result.stdout)


    def test_a_listing_that_fills_the_page_is_refused(self):
        # gh's default page is cut before the fork filter; a listing of exactly the limit may be a prefix, so it
        # is refused rather than returned.
        result = self.listing([self.row(n, "CLOSED") for n in range(1, 1001)])
        self.assertEqual(4, result.returncode)
        self.assertEqual("", result.stdout.strip())
        self.assertIn("truncated", result.stderr)

    def test_a_jq_that_does_not_finish_is_no_answer(self):
        # Git Bash reported a tool fed by a process substitution as `Child exited`. A `jq` dying on the
        # count, the repository filter, the merged-row read or the final shape stands in: each ends the run with
        # nothing printed, never an empty list `/ship` would read as "no pull request".
        for dies_on in ("length", "--arg", "-r", "--argjson"):
            with self.subTest(dies_on=dies_on):
                shim = Path(self.bin, "jq")
                shim.write_text(f'#!/usr/bin/env bash\ncase "$1" in {dies_on}) echo "Child exited" >&2; exit 148 ;; esac\n'
                                'PATH=${PATH#*:}\nexec jq "$@"\n', encoding="utf-8", newline="\n")
                shim.chmod(0o755)
                path, git = self.make_repo()
                git("branch", "some-branch")
                result = self.listing([self.row(1, "MERGED", mergeCommit={"oid": "a" * 40}), self.row(2, "OPEN")],
                                      cwd=path)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Child exited", result.stderr, "the stand-in jq was not the one the helper ran")
                self.assertEqual("", result.stdout)

    def test_an_answer_that_is_no_count_is_not_reported_as_truncation(self):
        result = self.run_helper(rows="")
        self.assertNotEqual(0, result.returncode)
        self.assertNotEqual(4, result.returncode)
        self.assertNotIn("truncated", result.stderr)

    def test_one_row_short_of_the_page_is_an_answer(self):
        # Without this the detector could refuse every listing and still pass.
        self.assertEqual(999, len(self.numbers(self.listing([self.row(n, "CLOSED") for n in range(1, 1000)]))))

    def test_the_listing_asks_for_the_limit_it_refuses_at(self):
        self.listing([self.row(1, "OPEN")])
        calls = (self.bin / "calls").read_text(encoding="utf-8")
        self.assertIn("--limit 1000", calls)
        self.assertIn("mergeCommit", calls)

    def test_a_newer_pr_into_another_base_does_not_mask_the_main_pr(self):
        develop = self.row(12, "OPEN", base="develop")
        self.assertEqual([9], self.numbers(self.listing([self.row(9, "OPEN"), develop])))
        self.assertEqual([], self.numbers(self.listing([develop])))

    def test_a_merged_pr_behind_the_reused_branch_is_not_its_pr(self):
        path, git = self.make_repo()
        git("switch", "-q", "-c", "feat/reused")
        git("commit", "-q", "--allow-empty", "-m", "the merged work")
        old_head = git("rev-parse", "HEAD")
        git("switch", "-q", "main")
        git("merge", "-q", "--no-ff", "-m", "Merge pull request #3", "feat/reused")
        row = self.row(3, "MERGED", headRefOid=old_head, mergeCommit={"oid": git("rev-parse", "HEAD")})

        def ask(*rows):
            return self.numbers(self.listing(list(rows), cwd=path, branch="feat/reused"))

        # The tip is the merged head: this is the pull request that landed.
        self.assertEqual([3], ask(row))
        # Commits added to the original branch after its merge keep the row: it does not carry the merge.
        git("switch", "-q", "feat/reused")
        git("commit", "-q", "--allow-empty", "-m", "after the merge, same branch")
        self.assertEqual([3], ask(row))
        # A branch recreated from a main that holds the merge is a new incarnation.
        git("switch", "-q", "main")
        git("branch", "-q", "-D", "feat/reused")
        git("switch", "-q", "-c", "feat/reused")
        git("commit", "-q", "--allow-empty", "-m", "new work, same name")
        self.assertEqual([], ask(row))
        # A closed row is a decision to stop on and an open row is the current pull request, whatever the tip;
        # the drop is per row, so an older incarnation goes and the newer row stays.
        closed = self.row(5, "CLOSED", headRefOid=old_head)
        self.assertEqual([5, 4], ask(closed, self.row(4, "OPEN")))
        self.assertEqual([4], ask(row, self.row(4, "OPEN")))

    def test_the_reuse_drop_still_answers_after_a_rebase_merge(self):
        # A rebase merge reports its last replayed commit as `mergeCommit`: it is on main, so a branch recreated
        # from main carries it and the original branch, left on the old shas, does not.
        path, git = self.make_repo()
        git("switch", "-q", "-c", "feat/reused")
        git("commit", "-q", "--allow-empty", "-m", "the work that landed")
        old_head = git("rev-parse", "HEAD")
        git("switch", "-q", "main")
        git("commit", "-q", "--allow-empty", "-m", "an unrelated PR lands first")
        git("switch", "-q", "-c", "landing", old_head)
        git("rebase", "-q", "--onto", "main", "main", "landing")
        git("switch", "-q", "main")
        git("merge", "-q", "--ff-only", "landing")
        git("branch", "-q", "-D", "landing")
        landed = git("rev-parse", "HEAD")
        self.assertNotEqual(old_head, landed)
        row = self.row(7, "MERGED", headRefOid=old_head, mergeCommit={"oid": landed})

        git("switch", "-q", "feat/reused")
        self.assertEqual([7], self.numbers(self.listing([row], cwd=path, branch="feat/reused")))
        git("switch", "-q", "main")
        git("branch", "-q", "-D", "feat/reused")
        git("switch", "-q", "-c", "feat/reused")
        git("commit", "-q", "--allow-empty", "-m", "new work, same name")
        self.assertEqual([], self.numbers(self.listing([row], cwd=path, branch="feat/reused")))

    def test_a_merge_commit_this_checkout_lacks_leaves_the_row(self):
        # With no local branch, or a merge commit not fetched, the stop is the safe answer.
        path, git = self.make_repo()
        row = self.row(3, "MERGED", mergeCommit={"oid": "1" * 40})
        self.assertEqual([3], self.numbers(self.listing([row], cwd=path, branch="feat/none")))
        git("switch", "-q", "-c", "feat/some")
        self.assertEqual([3], self.numbers(self.listing([row], cwd=path, branch="feat/some")))


if __name__ == "__main__":
    unittest.main()
