"""git-push-force.sh: a force push of the branch in hand, and what bounds it.

The helper takes no argument and publishes the branch checked out under a
lease. A permission rule could pin its flags and nothing else; what makes it
safe is a set of facts about the checkout, so the first class reads the flags
and the rest run it against real repositories: a bare origin, a checkout of
it, and where a test needs one, a second clone standing for another session.
"""

import unittest

from review_helpers import (
    SCRIPTS,
    code_lines,
    git_commands,
    setUpModule,  # noqa: F401
    run_bash as _run_bash,
)

HELPER = SCRIPTS / "git-push-force.sh"

SPELLINGS = ("main", "Main", "MAIN", "heads/main", "refs/heads/main",
             "origin/main", "refs/remotes/origin/main")


def run_bash(script, **env):
    """`review_helpers.run_bash` with the caller's git configuration shut out."""
    return _run_bash("set -eu\n" + script,
                     GIT_CONFIG_GLOBAL="/dev/null",
                     GIT_CONFIG_SYSTEM="/dev/null",
                     GIT_CONFIG_NOSYSTEM="1",
                     **env)


# A remote, a checkout of it, and a branch with one commit, published.
BUILD = '''
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
printf %s "$root"
'''

# Another session: a clone of the same origin with a commit on the branch.
SECOND = '''
git clone -q "$R/remote.git" "$R/second"
cd "$R/second"
git config user.email other@example.invalid
git config user.name Other
git checkout -q feat/x
echo theirs > theirs.txt && git add -A && git commit -qm "another session"
'''


class TheFlagsAreTheScriptsOwn(unittest.TestCase):
    """The grant buys `bash <this file>`, so its flags are never a caller's."""

    # The executable lines only: an assertion made against the whole file
    # passes on a comment that mentions the guard.
    @classmethod
    def setUpClass(cls):
        cls.source = "\n".join(code_lines(HELPER.read_text(encoding="utf-8")))

    def test_there_is_exactly_one_push_and_it_leases(self):
        pushes = [c for c in git_commands(self.source) if c.startswith("git push")]
        self.assertEqual(
            pushes, ['git push --force-with-lease="refs/heads/$branch:$lease" '
                     'origin "HEAD:refs/heads/$branch"'],
            "one push, leased against the tip just fetched, naming its remote and both sides")

    def test_no_spelling_of_the_unleased_force_appears(self):
        commands = "\n".join(git_commands(self.source)) + "\n"
        for spelling in ("--force ", "--force\n", "--force=", " -f ", " -f\n",
                         "--force-if-includes"):
            self.assertNotIn(spelling, commands, f"{spelling!r} would discard without a lease")
        for command in commands.splitlines():
            if command.startswith("git push"):
                self.assertNotRegex(command, r'''(?:^|\s)["']?\+\S''',
                                    "a `+` refspec forces without naming a flag")

    def test_the_lease_is_read_once_and_from_the_fetch(self):
        # The lease is the tip the fetch just wrote, read once: a second read
        # after the reflog check would lease against whatever moved it since.
        reads = [line.strip() for line in self.source.splitlines()
                 if line.strip().startswith("lease=")]
        self.assertEqual(['lease=$(git rev-parse --verify "refs/remotes/origin/$branch^{commit}")'],
                         reads)
        self.assertLess(self.source.index("git fetch"), self.source.index("lease=$("))


class Fixture(unittest.TestCase):

    def setUp(self):
        built = run_bash(BUILD)
        self.assertEqual(0, built.returncode, built.stderr)
        self.root = built.stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"

    def at(self, script, where=None):
        result = run_bash('cd "$W"\n' + script, W=where or self.work, R=self.root)
        self.assertEqual(0, result.returncode, f"{script}: {result.stderr}")
        return result.stdout.strip()

    def helper(self, *args):
        quoted = " ".join("'" + a + "'" for a in args)
        return run_bash(f'cd "$W"\nbash "$H" {quoted}', W=self.work, H=str(HELPER))

    def remote(self, ref="feat/x"):
        return self.at(f"git rev-parse refs/heads/{ref}", where=self.root + "/remote.git")

    def head(self):
        return self.at("git rev-parse HEAD")

    def amend(self):
        self.at('git commit -q --amend -m "the branch work, reworded"')

    def second_session(self, push=True):
        self.at(SECOND + ("git push -q origin feat/x\n" if push else ""), where=self.root)
        return self.at("git rev-parse HEAD", where=self.root + "/second")


class TheHelperRefusesBeforeItPushes(Fixture):

    def test_it_takes_no_argument(self):
        result = self.helper("feat/x")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("usage: git-push-force.sh", result.stderr)

    def test_main_is_refused_however_it_is_spelled(self):
        # A rewritten local `main` that still has origin's tip in its reflog
        # passes every other guard, so only the name stops it. HEAD is pointed
        # at each spelling directly: on a case-insensitive filesystem `Main`
        # is `main`'s ref, and elsewhere it is unborn, and either way the
        # branch checked out is spelled that way.
        self.at("git checkout -q main")
        self.amend()
        before = self.remote("main")
        for spelling in SPELLINGS:
            with self.subTest(spelling=spelling):
                self.at('git symbolic-ref HEAD "refs/heads/$S"'.replace("$S", spelling))
                result = self.helper()
                self.assertEqual(3, result.returncode, f"{spelling!r}: {result.stderr}")
                self.assertIn("refusing to force-push main", result.stderr)
        self.assertEqual(before, self.remote("main"), "origin's main was rewritten")

    def test_a_branch_that_is_a_symbolic_ref_to_main_is_main(self):
        # HEAD names refs/heads/feat/alias, which is itself a symbolic ref to
        # main. `git branch --show-current` follows the chain, so the name the
        # helper folds is `main`; a read that stopped at the first link would
        # push main's rewritten tip as feat/alias.
        self.at("git checkout -q main")
        self.amend()
        before = self.remote("main")
        self.at("git symbolic-ref refs/heads/feat/alias refs/heads/main "
                "&& git symbolic-ref HEAD refs/heads/feat/alias")
        result = self.helper()
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("refusing to force-push main", result.stderr)
        self.assertEqual(before, self.remote("main"))
        self.assertEqual("", self.at("git ls-remote origin refs/heads/feat/alias"))

    def test_a_detached_head_is_refused(self):
        self.amend()
        self.at("git checkout -q --detach HEAD")
        result = self.helper()
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("detached HEAD", result.stderr)

    def test_a_branch_origin_lacks_is_refused(self):
        self.at("git checkout -qb feat/unpublished")
        result = self.helper()
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertIn("origin has no feat/unpublished", result.stderr)
        self.assertEqual("", self.at("git ls-remote origin refs/heads/feat/unpublished"),
                         "the refusal published the branch")

    def test_a_branch_origin_lost_is_refused_though_the_tracking_ref_remains(self):
        # The tracking ref outlives the remote branch, so the question is
        # origin's, not the checkout's.
        self.amend()
        self.at("git update-ref -d refs/heads/feat/x", where=self.root + "/remote.git")
        result = self.helper()
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertIn("origin has no feat/x", result.stderr)

    def test_an_origin_that_cannot_be_asked_is_refused(self):
        self.amend()
        self.at('git remote set-url origin "$R/nowhere.git"')
        result = self.helper()
        self.assertEqual(12, result.returncode, result.stderr)
        self.assertIn("cannot ask origin", result.stderr)

    def test_remote_commits_this_checkout_never_held_are_refused(self):
        # The lease alone passes this: the other session's commit has been
        # fetched, so the tracking ref names it and git would push over it.
        theirs = self.second_session()
        self.at("git fetch -q origin")
        self.amend()
        result = self.helper()
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("has never been at", result.stderr)
        self.assertEqual(theirs, self.remote(), "the other session's commit was discarded")

    def test_a_moved_remote_tip_refuses_the_push(self):
        # Origin moves after the helper's fetch and before its push, which is
        # the window the lease exists for. The checkout's tracking ref is set
        # back first so that the fetch writes it, and a reference-transaction
        # hook pushes the other session's commit at that moment, once. The
        # tip the helper fetched is one this branch has been at, so every
        # guard of its own passes and only git's lease is left to refuse.
        theirs = self.second_session(push=False)
        self.amend()
        self.at("git update-ref refs/remotes/origin/feat/x HEAD~1")
        hook = self.root + "/hook"
        self.at(
            'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"\n'
            'cat > "$h/reference-transaction" <<EOF\n'
            '#!/bin/sh\n'
            '[ "\\$1" = committed ] || exit 0\n'
            'grep -q " refs/remotes/origin/feat/x\\$" || exit 0\n'
            '[ -e "$HOOKMARK" ] && exit 0\n'
            ': > "$HOOKMARK"\n'
            'unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX\n'
            'cd "$R/second" && git push -q origin feat/x\n'
            'EOF\n'
            'chmod +x "$h/reference-transaction"\n'.replace("$HOOKMARK", hook + ".fired"))
        result = self.helper()
        self.assertEqual("", run_bash('test -e "$M" || echo missing', M=hook + ".fired").stdout.strip(),
                         "the hook never moved origin, so this test proved nothing")
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("origin refused the push", result.stderr)
        self.assertEqual(theirs, self.remote(), "the push landed over a tip it never fetched")


class TheHelperPublishesTheBranchInHand(Fixture):

    def test_an_amended_branch_is_published_under_its_lease(self):
        # The positive control for every refusal above.
        before = self.remote()
        self.amend()
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("published feat/x", result.stdout)
        self.assertNotEqual(before, self.head())
        self.assertEqual(self.head(), self.remote())

    def test_a_tip_this_branch_held_earlier_is_published_over(self):
        # Two rewrites in a row: origin's tip is the first, which the branch
        # was at before the second, so the reflog holds it.
        self.amend()
        self.assertEqual(0, self.helper().returncode)
        self.at('git commit -q --amend -m "reworded again"')
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.head(), self.remote())

    def test_a_branch_origin_already_holds_has_nothing_to_force(self):
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("nothing to force", result.stdout)


if __name__ == "__main__":
    unittest.main()
