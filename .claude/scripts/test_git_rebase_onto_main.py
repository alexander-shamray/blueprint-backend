"""git-rebase-onto-main.sh: the only force push, and what bounds it.

A permission rule matches the text of a command, so it can pin a flag and
cannot pin a fact about the checkout. Everything that makes this force push
safe is the second kind — the branch is the current one, it is not `main`, and
the remote holds nothing the push would discard — so the script is the
boundary and this suite is what watches it. The first class reads the flags;
the rest run the thing.
"""

import unittest

from review_helpers import (
    SCRIPTS,
    setUpModule,
    run_bash,
)

HELPER = SCRIPTS / "git-rebase-onto-main.sh"

# A remote, a checkout of it, a branch with one commit, and a `main` that has
# moved since — the state every branch update starts from.
FIXTURE = '''
set -eu
root=$(mktemp -d)
git init -q --bare --initial-branch=main "$root/remote.git"
git init -q --initial-branch=main "$root/work"
cd "$root/work"
git config user.email test@example.invalid
git config user.name Test
git remote add origin "$root/remote.git"
echo base > a.txt && git add -A && git commit -qm "the base"
git push -q -u origin main
git checkout -qb feat/x
echo work > b.txt && git add -A && git commit -qm "the branch work"
git push -q -u origin feat/x
git checkout -q main
echo moved > c.txt && git add -A && git commit -qm "main moved"
git push -q origin main
git checkout -q feat/x
printf %s "$root"
'''

# Both sides edit the same line, so the replay cannot proceed without a person.
CONFLICT = '''
git checkout -q main && echo mine > a.txt && git add -A
git commit -qm "main edits a.txt" && git push -q origin main
git checkout -q feat/x && echo theirs > a.txt && git add -A
git commit -qm "the branch edits a.txt" && git push -q origin feat/x
'''


class TheFlagsAreTheScriptsOwn(unittest.TestCase):
    """The grant buys `bash <this file>`, so the flags are never a caller's to
    choose. A prefix rule cannot exclude a trailing flag, which is why this
    repository answers such cases with a helper (`docs/harness-boundaries.md`).
    """

    source = HELPER.read_text(encoding="utf-8")

    def test_there_is_exactly_one_push_and_it_carries_an_expected_value(self):
        pushes = [ln.strip() for ln in self.source.splitlines() if ln.strip().startswith("git push")]
        self.assertEqual(
            pushes, ['git push --force-with-lease="$branch:$lease" origin "$branch"'],
            "one push, leased against the commit this run read, naming its remote and its refspec")

    def test_no_spelling_of_the_unleased_force_appears(self):
        # The commands only. Scanning the whole file catches `[ -f "$state/… ]`
        # and every prose mention of the deny this helper exists beside, which
        # is a check that fails on its own documentation.
        commands = "\n".join(ln.strip() for ln in self.source.splitlines() if ln.strip().startswith("git "))
        for spelling in ("--force ", "--force\n", "--force=", " -f ", "--force-if-includes"):
            self.assertNotIn(spelling, commands, f"{spelling!r} would discard without a lease")

    def test_main_is_refused_by_name_in_the_source(self):
        self.assertIn('[ "$branch" != main ]', self.source)


class TheHelperRefusesBeforeItRewrites(unittest.TestCase):

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"

    def helper(self, branch, mode="start"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def head(self):
        return self.at("git rev-parse HEAD").stdout.strip()

    def test_it_takes_a_branch_and_a_mode(self):
        one = run_bash('cd "$W" && bash "$H" feat/x', W=self.work, H=str(HELPER))
        self.assertEqual(2, one.returncode)
        self.assertEqual(2, self.helper("feat/x", "publish").returncode, "an unknown mode is refused")

    def test_main_is_refused_even_while_main_is_checked_out(self):
        self.at("git checkout -q main")
        self.assertEqual(3, self.helper("main").returncode)

    def test_naming_another_branch_does_not_act_on_it(self):
        before = self.head()
        self.at("git checkout -qb feat/other")
        self.assertEqual(4, self.helper("feat/x").returncode)
        self.at("git checkout -q feat/x")
        self.assertEqual(before, self.head())

    def test_a_detached_head_is_refused(self):
        self.at("git checkout -q --detach HEAD")
        self.assertEqual(4, self.helper("feat/x").returncode)

    def test_a_dirty_tree_is_refused(self):
        self.at("echo uncommitted >> b.txt")
        self.assertEqual(5, self.helper("feat/x").returncode)
        self.assertIn("uncommitted", self.at("cat b.txt").stdout, "the edit survives the refusal")

    def test_a_branch_the_remote_does_not_have_is_refused(self):
        self.at("git checkout -qb feat/unpublished")
        self.assertEqual(6, self.helper("feat/unpublished").returncode)

    def test_commits_only_the_remote_has_stop_it(self):
        # A lease is satisfied by a commit this checkout has fetched, so it
        # would not stop a push that knowingly discards another session's work.
        # This is the guard that does.
        self.at('git clone -q ../remote.git ../second')
        run_bash('cd "$R/second" && git config user.email t@e.invalid && git config user.name T '
                 '&& git checkout -q feat/x && echo theirs > theirs.txt && git add -A '
                 '&& git commit -qm "another session" && git push -q origin feat/x', R=self.root)
        self.assertEqual(7, self.helper("feat/x").returncode)

    def test_continue_and_abort_refuse_when_no_rebase_is_running(self):
        self.assertEqual(9, self.helper("feat/x", "continue").returncode)
        self.assertEqual(9, self.helper("feat/x", "abort").returncode)


class AConflictIsTheCaseRebaseIsHereFor(unittest.TestCase):
    """The resolution belongs in the replayed commit, not in a merge commit.

    So `start` leaves a conflicted rebase in progress rather than aborting it:
    backing out would send the caller to the merge this repository stopped
    making, and the tree would stop being a line.
    """

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"
        self.at(CONFLICT)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode, branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def rebase_running(self):
        return self.at('test -d "$(git rev-parse --git-path rebase-merge)" '
                       '-o -d "$(git rev-parse --git-path rebase-apply)" '
                       '&& echo yes || echo no').stdout.strip()

    def test_a_conflict_leaves_the_rebase_in_progress_and_names_the_paths(self):
        result = self.helper("start")
        self.assertEqual(8, result.returncode)
        self.assertIn("a.txt", result.stderr, "the caller is told what to resolve")
        self.assertEqual("yes", self.rebase_running(), "aborting here would mean a merge instead")

    def test_continue_refuses_while_anything_is_still_unmerged(self):
        self.assertEqual(8, self.helper("start").returncode)
        self.assertEqual(9, self.helper("continue").returncode, "nothing was resolved")
        self.assertEqual("yes", self.rebase_running())

    def test_resolving_and_continuing_publishes_a_line(self):
        self.assertEqual(8, self.helper("start").returncode)
        self.at('echo resolved > a.txt && git add a.txt')
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)

        self.assertEqual("no", self.rebase_running())
        self.assertEqual("resolved\n", self.at("cat a.txt").stdout,
                         "the resolution is in the replayed commit")
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "and no merge commit was made — the whole point")
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(),
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote carries what was replayed")

    def test_abort_puts_the_branch_back_and_publishes_nothing(self):
        before = self.at("git rev-parse HEAD").stdout.strip()
        remote_before = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.assertEqual(8, self.helper("start").returncode)
        self.assertEqual(0, self.helper("abort").returncode)

        self.assertEqual("no", self.rebase_running())
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip())
        self.assertEqual(remote_before, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())


class TheHelperPublishesWhatItRebased(unittest.TestCase):

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def test_a_clean_update_is_replayed_and_published(self):
        before = self.at("git rev-parse HEAD").stdout.strip()
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)

        after = self.at("git rev-parse HEAD").stdout.strip()
        self.assertNotEqual(before, after, "a replay onto a moved base makes new SHAs")
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "a rebased branch carries no merge commit")
        self.assertEqual(after, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())
        self.assertEqual("", self.at("git log --oneline HEAD..origin/main").stdout,
                         "and the branch now holds everything main does")

    def test_a_second_run_changes_nothing_and_does_not_force(self):
        self.assertEqual(0, self.helper().returncode)
        settled = self.at("git rev-parse HEAD").stdout.strip()
        again = self.helper()
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertIn("nothing to force", again.stdout)
        self.assertEqual(settled, self.at("git rev-parse HEAD").stdout.strip())


if __name__ == "__main__":
    unittest.main()
