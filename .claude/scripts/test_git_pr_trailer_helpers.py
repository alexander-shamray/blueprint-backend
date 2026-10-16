"""git-trailer-pr.sh and git-revert-pr.sh against real repositories, a bare remote and a checkout.
`gh` is a shim answering the one read the first helper makes. Each refusal is paired with a run
that succeeds on the same fixture, so a refusal is not a helper failing for another reason.
"""

import os
import shutil
import tempfile
import unittest
from pathlib import Path

from review_helpers import SCRIPTS, code_lines, run_bash, setUpModule  # noqa: F401

TRAILER = SCRIPTS / "git-trailer-pr.sh"
REVERT = SCRIPTS / "git-revert-pr.sh"

BUILD = """
set -eu
root=$(mktemp -d)
git init -q --bare --initial-branch=main "$root/remote.git"
git init -q --initial-branch=main "$root/work"
cd "$root/work"
git config user.email test@example.invalid
git config user.name Test
git remote add origin "$root/remote.git"
echo base > a.txt && git add -A && git commit -qm "chore: the base"
git push -q -u origin main
printf %s "$root"
"""

# A commit with a body and the trailers /commit keeps, so the new trailer has to land beside them.
COMMIT = """
cd "$WORK"
echo "$2" >> "$1.txt" && git add -A
printf '%s\\n\\nThe body.\\n\\nCo-Authored-By: C <c@example.invalid>\\n%s' "$3" "$4" | git commit -q -F -
"""


def sh(script, **env):
    result = run_bash(
        "set -eu\n" + script,
        GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null", GIT_CONFIG_NOSYSTEM="1", **env)
    return result


class Fixture(unittest.TestCase):
    def setUp(self):
        built = run_bash(BUILD, GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null", GIT_CONFIG_NOSYSTEM="1")
        self.assertEqual(0, built.returncode, built.stderr)
        self.root = built.stdout.strip()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.work = f"{self.root}/work"
        shim = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, shim, ignore_errors=True)
        Path(shim, "gh").write_text('#!/usr/bin/env bash\nprintf "%s\\n" "$GH_ROW"\n', encoding="utf-8")
        os.chmod(Path(shim, "gh"), 0o755)
        self.shim = shim

    def git(self, *args, check=True):
        quoted = " ".join("'" + a.replace("'", "'\\''") + "'" for a in args)
        result = sh(f'cd "$WORK"\ngit {quoted}', WORK=self.work)
        if check:
            self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def commit(self, name, subject, trailer="", push=True):
        sh(COMMIT.replace("$1", name).replace("$2", name).replace("$3", subject).replace("$4", trailer), WORK=self.work)
        if push:
            self.git("push", "-q", "-u", "origin", "HEAD")

    def stray(self):
        # Written by the shell: the checkout's path is an MSYS one that Python on Windows cannot open.
        self.assertEqual(0, sh('cd "$WORK"\necho x > stray.txt', WORK=self.work).returncode)

    def branch(self, name="feat/x"):
        self.git("checkout", "-q", "-b", name)

    def run_helper(self, helper, pr="7", row="OPEN\tfeat/x\tmain\tfalse"):
        return sh(
            f'cd "$WORK"\nbash "$HELPER" "$PR"',
            WORK=self.work, HELPER=str(helper), PR=pr, GH_ROW=row,
            PATH=self.shim + os.pathsep + os.environ["PATH"])

    def subjects_and_trailers(self, rev):
        return self.git("log", "--format=%s|%(trailers:key=PR,valueonly,separator=,)", rev).splitlines()


class TrailerHelper(Fixture):
    def two_commit_branch(self):
        self.branch()
        self.commit("one", "feat(x): one")
        self.commit("two", "docs: two")

    def test_every_commit_gains_the_trailer_beside_the_others_and_is_published(self):
        self.two_commit_branch()
        out = self.run_helper(TRAILER)
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual(["docs: two|#7", "feat(x): one|#7"], self.subjects_and_trailers("origin/main..HEAD"))
        self.assertEqual(self.git("rev-parse", "HEAD"), self.git("rev-parse", "origin/feat/x"))
        message = self.git("log", "-1", "--format=%B")
        self.assertIn("Co-Authored-By: C", message)
        self.assertEqual(
            "Co-Authored-By: C <c@example.invalid>\nPR: #7",
            "\n".join(message.splitlines()[-2:]))

    def test_a_rerun_after_a_failed_push_publishes_the_rewrite(self):
        self.two_commit_branch()
        hook = '$ROOT/remote.git/hooks/pre-receive'
        self.assertEqual(0, sh(f'printf "#!/bin/sh\\nexit 1\\n" > "{hook}" && chmod +x "{hook}"', ROOT=self.root).returncode)
        failed = self.run_helper(TRAILER)
        self.assertEqual(7, failed.returncode, failed.stderr)
        self.assertNotEqual(self.git('rev-parse', 'HEAD'), self.git('rev-parse', 'origin/feat/x'))
        self.assertEqual(0, sh(f'rm "{hook}"', ROOT=self.root).returncode)
        again = self.run_helper(TRAILER)
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertEqual(self.git('rev-parse', 'HEAD'), self.git('ls-remote', 'origin', 'feat/x').split()[0])

    def test_a_remote_with_other_intermediate_trees_refuses_though_the_tip_tree_matches(self):
        self.two_commit_branch()
        self.git('reset', '-q', '--hard', 'main')
        self.commit('two', 'docs: two', push=False)
        self.commit('one', 'feat(x): one', push=False)
        out = self.run_helper(TRAILER)
        self.assertEqual(4, out.returncode, out.stderr)

    def test_a_second_run_changes_nothing_and_publishes_nothing(self):
        self.two_commit_branch()
        self.assertEqual(0, self.run_helper(TRAILER).returncode)
        tip = self.git("rev-parse", "HEAD")
        again = self.run_helper(TRAILER)
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertIn("already carries", again.stdout)
        self.assertEqual(tip, self.git("rev-parse", "HEAD"))

    def test_only_the_commits_without_it_are_rewritten(self):
        self.branch()
        self.commit("one", "feat(x): one", trailer="PR: #7\n")
        self.commit("two", "docs: two")
        first = self.git("rev-parse", "HEAD~1")
        self.assertEqual(0, self.run_helper(TRAILER).returncode)
        self.assertEqual(first, self.git("rev-parse", "HEAD~1"))
        self.assertEqual(["docs: two|#7", "feat(x): one|#7"], self.subjects_and_trailers("origin/main..HEAD"))

    def test_a_commit_naming_another_pull_request_refuses_and_changes_nothing(self):
        self.branch()
        self.commit("one", "feat(x): one", trailer="PR: #9\n")
        self.commit("two", "docs: two")
        tip = self.git("rev-parse", "HEAD")
        out = self.run_helper(TRAILER)
        self.assertEqual(5, out.returncode, out.stderr)
        self.assertIn("not 'PR: #7'", out.stderr)
        self.assertEqual(tip, self.git("rev-parse", "HEAD"))

    def test_a_pull_request_for_another_branch_refuses(self):
        self.two_commit_branch()
        out = self.run_helper(TRAILER, row="OPEN\tfeat/other\tmain\tfalse")
        self.assertEqual(3, out.returncode)
        self.assertIn("is for feat/other", out.stderr)

    def test_a_closed_pull_request_refuses(self):
        self.two_commit_branch()
        self.assertEqual(3, self.run_helper(TRAILER, row="MERGED\tfeat/x\tmain\tfalse").returncode)

    def test_main_refuses(self):
        self.assertEqual(3, self.run_helper(TRAILER).returncode)

    def test_a_dirty_tree_refuses(self):
        self.two_commit_branch()
        self.stray()
        out = self.run_helper(TRAILER)
        self.assertEqual(3, out.returncode)
        self.assertIn("not clean", out.stderr)

    def test_an_unpushed_commit_refuses(self):
        self.two_commit_branch()
        self.commit("three", "docs: three", push=False)
        out = self.run_helper(TRAILER)
        self.assertEqual(4, out.returncode, out.stderr)

    def test_a_remote_ahead_of_the_checkout_refuses(self):
        self.two_commit_branch()
        self.git("reset", "-q", "--hard", "HEAD~1")
        out = self.run_helper(TRAILER)
        self.assertEqual(4, out.returncode, out.stderr)
        self.assertNotIn("PR: #7", self.git("log", "-1", "--format=%B"))

    def test_a_merge_commit_refuses(self):
        self.branch()
        self.commit("one", "feat(x): one")
        self.git("checkout", "-q", "-b", "side", "main")
        self.commit("side", "docs: side")
        self.git("checkout", "-q", "feat/x")
        self.git("merge", "-q", "--no-ff", "-m", "docs: merge", "side")
        self.git("push", "-q", "origin", "HEAD")
        out = self.run_helper(TRAILER)
        self.assertEqual(5, out.returncode, out.stderr)

    def test_the_arguments_are_checked_first(self):
        for bad in ("", "x7", "7 8", "-1"):
            with self.subTest(pr=bad):
                self.assertEqual(2, self.run_helper(TRAILER, pr=bad).returncode)

    def test_the_helper_spells_its_rewrite_and_its_lease_itself(self):
        code = "\n".join(code_lines(TRAILER.read_text(encoding="utf-8")))
        self.assertIn('--force-with-lease="refs/heads/$branch:$remote"', code)
        self.assertNotIn("git push --force ", code)
        self.assertNotIn("git push -f", code)


class RevertHelper(Fixture):
    def land(self, name, pr):
        self.commit(name, f"docs: {name}", trailer=f"PR: #{pr}\n")

    def test_the_commits_of_that_pull_request_are_reverted_newest_first(self):
        self.land("a", 5)
        self.land("b", 6)
        self.land("c", 5)
        self.git("checkout", "-q", "-b", "chore/undo")
        out = self.run_helper(REVERT, pr="5")
        self.assertEqual(0, out.returncode, out.stderr)
        subjects = self.git("log", "--format=%s", "main..HEAD").splitlines()
        self.assertEqual(['Revert "docs: a"', 'Revert "docs: c"'], subjects)
        trailers = self.git("log", "--format=%(trailers:key=Reverts,valueonly)", "main..HEAD").split()
        self.assertEqual(["#5", "#5"], trailers)
        self.assertEqual("", self.git("ls-files", "c.txt"))
        self.assertEqual("b", self.git("show", "HEAD:b.txt"))

    def test_a_pull_request_with_no_commits_refuses(self):
        self.land("a", 5)
        self.git("checkout", "-q", "-b", "chore/undo")
        out = self.run_helper(REVERT, pr="55")
        self.assertEqual(4, out.returncode)
        self.assertIn("no commit on origin/main", out.stderr)

    def test_a_number_that_is_a_prefix_of_another_selects_only_its_own(self):
        self.land("a", 50)
        self.git("checkout", "-q", "-b", "chore/undo")
        self.assertEqual(4, self.run_helper(REVERT, pr="5").returncode)

    def test_a_line_quoted_in_a_body_is_not_a_trailer(self):
        self.branch("feat/q")
        sh(COMMIT.replace("$1", "q").replace("$2", "q").replace("$3", "docs: q").replace("$4", ""), WORK=self.work)
        self.git("commit", "-q", "--amend", "-m", "docs: q\n\nQuoted below, not a trailer:\n\nPR: #5\n\nMore prose.")
        self.git("push", "-q", "origin", "HEAD:main")
        self.git("checkout", "-q", "-b", "chore/undo")
        self.assertEqual(4, self.run_helper(REVERT, pr="5").returncode)

    def test_a_conflicting_revert_stops_and_leaves_a_clean_tree(self):
        self.land("a", 5)
        sh(COMMIT.replace("$1", "a").replace("$2", "later").replace("$3", "docs: later").replace("$4", "PR: #6\n"),
           WORK=self.work)
        self.git("push", "-q", "origin", "HEAD:main")
        self.git("checkout", "-q", "-b", "chore/undo")
        out = self.run_helper(REVERT, pr="5")
        self.assertEqual(5, out.returncode, out.stderr)
        self.assertEqual("", self.git("status", "--porcelain"))

    def test_main_and_a_dirty_tree_refuse(self):
        self.land("a", 5)
        self.assertEqual(3, self.run_helper(REVERT, pr="5").returncode)
        self.git("checkout", "-q", "-b", "chore/undo")
        self.stray()
        self.assertEqual(3, self.run_helper(REVERT, pr="5").returncode)

    def test_the_argument_is_checked_first(self):
        for bad in ("", "x5", "5 6"):
            with self.subTest(pr=bad):
                self.assertEqual(2, self.run_helper(REVERT, pr=bad).returncode)


if __name__ == "__main__":
    unittest.main()
