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
    code_lines,
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

    # The executable lines only. An assertion made against the whole file
    # passes on a comment that mentions the guard, so deleting the guard would
    # not fail it — which is `code_lines`' own argument.
    source = "\n".join(code_lines(HELPER.read_text(encoding="utf-8")))

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

    def test_the_push_leases_against_the_value_the_guard_approved(self):
        # Re-reading the remote ref in `publish` would lease against whatever a
        # fetch had since made of it, with the whole replay in between — the
        # lease would then name the commits the guard refused.
        self.assertIn('lease="$approved_lease"', self.source)
        reads = [ln.strip() for ln in self.source.splitlines()
                 if 'rev-parse "refs/remotes/origin/$branch"' in ln]
        self.assertEqual(
            reads, ['approved_lease=$(git rev-parse "refs/remotes/origin/$branch")'],
            "the remote tip is read once, by the guard, and that value is what forces")


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

    def test_main_is_refused_however_it_is_spelled(self):
        # One string compare is one spelling, and this host's filesystem is
        # case-insensitive: `git branch Main` answers that it already exists.
        for spelling in ("main", "Main", "MAIN", "heads/main", "refs/heads/main",
                         "origin/main", "refs/remotes/origin/main"):
            result = self.helper(spelling)
            self.assertEqual(3, result.returncode, f"{spelling!r}: {result.stderr}")

    def test_naming_another_branch_does_not_act_on_it(self):
        # The guard is the refusal, not the exit code: `publish` exits 4 as
        # well, and would have rebased the other branch on the way there.
        self.at("git checkout -qb feat/other")
        other_before = self.at("git rev-parse feat/other").stdout.strip()
        result = self.helper("feat/x")
        self.assertEqual(4, result.returncode)
        self.assertIn("only ever touches the current branch", result.stderr)
        self.assertEqual(other_before, self.at("git rev-parse feat/other").stdout.strip(),
                         "the branch in hand was replayed on the way to the refusal")

    def test_a_detached_head_is_refused(self):
        self.at("git checkout -q --detach HEAD")
        result = self.helper("feat/x")
        self.assertEqual(4, result.returncode)
        self.assertIn("detached HEAD", result.stderr,
                      "the equality check below exits 4 too, so the code alone proves nothing")

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

    def test_a_rebase_this_helper_did_not_start_is_not_published(self):
        # An interactive rebase dropping the branch's commits passes every
        # other check in `continue`: the published tip is what it started
        # from, so the divergence guard is satisfied and the result — which
        # holds none of the branch's work — would be forced over it.
        published = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.at('GIT_SEQUENCE_EDITOR="sed -i 1s/^pick/break/" '
                'git rebase -i refs/remotes/origin/main')
        result = self.helper("feat/x", "continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("not started by this helper", result.stderr)
        self.assertEqual(published, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote still carries the branch's published work")
        self.at("git rebase --abort")

    def test_a_rebase_that_never_started_is_not_called_a_conflict(self):
        # Every other way `git rebase` can fail leaves no state, and reporting
        # it as a conflict sends the caller to a `continue` with nothing to do.
        self.at('printf "#!/bin/sh\\nexit 1\\n" > "$(git rev-parse --git-path hooks)/pre-rebase" '
                '&& chmod +x "$(git rev-parse --git-path hooks)/pre-rebase"')
        result = self.helper("feat/x")
        self.assertEqual(11, result.returncode, result.stderr)
        self.assertIn("did not start", result.stderr)


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

    def test_continue_fails_closed_when_the_starting_commit_is_unreadable(self):
        # The lease alone would not stop this: another session's commits that
        # this checkout has fetched satisfy it. Skipping the check that does
        # would leave the only force push in the repository unguarded.
        self.assertEqual(8, self.helper("start").returncode)
        self.at('rm -f "$(git rev-parse --git-path rebase-merge)"/orig-head '
                '"$(git rev-parse --git-path rebase-merge)"/head')
        self.at('echo resolved > a.txt && git add a.txt')
        result = self.helper("continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("divergence", result.stderr)

    def test_abort_puts_the_branch_back_and_publishes_nothing(self):
        before = self.at("git rev-parse HEAD").stdout.strip()
        remote_before = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.assertEqual(8, self.helper("start").returncode)
        self.assertEqual(0, self.helper("abort").returncode)

        self.assertEqual("no", self.rebase_running())
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip())
        self.assertEqual(remote_before, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())


class ALegacyMergeForwardIsNotSilentlyDropped(unittest.TestCase):
    """A rebase drops merge commits, and a branch made under the old policy
    has one. Where that merge carries content neither parent has, replaying
    loses it before the push, which no lease can see."""

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def merge_forward(self):
        self.at('git checkout -q main && echo later > d.txt && git add -A '
                '&& git commit -qm "main moved again" && git push -q origin main '
                '&& git checkout -q feat/x && git merge --no-edit -q main')

    def test_a_merge_carrying_its_own_content_stops_the_run(self):
        self.merge_forward()
        self.at('echo only-here > resolved-by-hand.txt && git add -A '
                '&& git commit -q --amend --no-edit && git push -q -f origin feat/x')
        before = self.at("git rev-parse HEAD").stdout.strip()

        result = self.helper()
        self.assertEqual(10, result.returncode, result.stderr)
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip(), "nothing was replayed")
        self.assertEqual("only-here\n", self.at("cat resolved-by-hand.txt").stdout)

    def test_an_ordinary_merge_forward_is_flattened_without_complaint(self):
        # Its content is in its parents, so dropping it loses nothing.
        self.merge_forward()
        self.at('git push -q -f origin feat/x')
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "the merge is gone and the branch is a line")


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
