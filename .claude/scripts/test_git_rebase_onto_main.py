"""git-rebase-onto-main.sh: a leased force push, and what bounds it.

A permission rule cannot pin a fact about the checkout, so the guards are the
script's, and its header lists them. The first class reads the flags, the
argument guards' runs it bare, and the rest against real repositories."""

import re
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    code_lines,
    copy_template,
    fixture_template,
    git_commands,
    setUpModule,
    run_bash as _run_bash,
)

HELPER = SCRIPTS / "git-rebase-onto-main.sh"


def run_bash(script, subject="", **env_extra):
    """`review_helpers.run_bash` with the caller's git configuration shut out.

    `rebase.backend`, `rebase.updateRefs` and `core.autocrlf` would each decide
    what the fixtures reach. `/dev/null` is spelled the same on every runner.
    """
    return _run_bash(script, subject,
                     GIT_CONFIG_GLOBAL="/dev/null",
                     GIT_CONFIG_SYSTEM="/dev/null",
                     GIT_CONFIG_NOSYSTEM="1",
                     **env_extra)


# A remote, a checkout of it, a branch with one commit, and a `main` that has
# moved since — the state every branch update starts from.
BUILD = '''
set -eu
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
'''

# Each test's own copy of that state, built once until a module cleanup removes it, which under the shard runner is
# per class: about twenty git processes a test become none, which is most of this suite's time on Windows. A build
# that fails raises, so no test runs against a `self.work` that is not there.
def template():
    return fixture_template(BUILD, "root", GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null",
                            GIT_CONFIG_NOSYSTEM="1")


# Both sides edit the same line, so the replay cannot proceed without a person.
CONFLICT = '''
git checkout -q main && echo mine > a.txt && git add -A
git commit -qm "main edits a.txt" && git push -q origin main
git checkout -q feat/x && echo theirs > a.txt && git add -A
git commit -qm "the branch edits a.txt" && git push -q origin feat/x
'''

# A push that fails after the replay has finished. The remote refuses a
# non-fast-forward rather than being broken, because the fetch has to succeed
# for the replay to happen at all. It is the remote's setting and not a
# `pre-push` hook, which the helper ignores along with the rest of the host's.
FAIL_PUSH = 'git --git-dir=../remote.git config receive.denyNonFastForwards true'
ALLOW_PUSH = 'git --git-dir=../remote.git config --unset receive.denyNonFastForwards || true'
# A rebase that fails before any state exists, on every platform: git wants the
# index lock and finds a file there. The helper runs no `pre-rebase` hook.
LOCK_INDEX = 'printf x > "$(git rev-parse --git-path index.lock)"'
UNLOCK_INDEX = 'rm -f "$(git rev-parse --git-path index.lock)"'

REBASE_RUNNING = ('test -d "$(git rev-parse --git-path rebase-merge)" '
                  '-o -d "$(git rev-parse --git-path rebase-apply)" '
                  '&& echo yes || echo no')


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
        pushes = [c for c in git_commands(self.source) if c.startswith("git push")]
        self.assertEqual(
            pushes, ['git push --force-with-lease="refs/heads/$branch:$lease" '
                     'origin "refs/heads/$branch:refs/heads/$branch"'],
            "one push, leased against the commit this run read, naming its remote and its refspec")

    def test_no_spelling_of_the_unleased_force_appears(self):
        # The commands only: the whole file holds `[ -f "$state/… ]` and every
        # prose mention of the deny. The trailing newline lets a flag ending
        # the last command match, so each spelling is listed in both forms.
        commands = "\n".join(git_commands(self.source)) + "\n"
        for spelling in ("--force ", "--force\n", "--force=", " -f ", " -f\n",
                         "--force-if-includes"):
            self.assertNotIn(spelling, commands, f"{spelling!r} would discard without a lease")
        # `+<refspec>` forces with no flag at all. Not anchored on a colon:
        # `+$branch` forces branch:branch without one, and a quote can sit
        # where the space was.
        for command in commands.splitlines():
            if command.startswith("git push") or " push " in command:
                self.assertNotRegex(command, r'''(?:^|\s)["']?\+\S''',
                                    "a `+` refspec forces without naming a flag")

    def test_the_scan_sees_what_a_line_scan_misses(self):
        """The scan's subject is the scan, not what it happened to find.

        Every line below is a second, unleased force push. Its flags must stay
        attached: a scan that cut one before `--force` would pass a count.
        """
        hidden = (
            'GIT_SSH_COMMAND=ssh git push --force origin "$branch"',
            'git rev-parse HEAD || { git push --force origin "$branch"; }',
            'if [ -n "$x" ]; then git push --force origin "$branch"; fi',
            'for r in a b; do git push --force origin "$r"; done',
            '! git push --force origin "$branch"',
            'eval git push --force origin "$branch"',
            'git push origin "${branch}" --force',
            'git push origin +$branch',
            'git push origin "+$branch:$branch"',
            'git push origin "$branch" -f',
            'git -C . push --force origin "$branch"',
            'git -c push.default=current push origin +$branch',
            'git --git-dir=.git push origin "+$branch:$branch"',
            'env git push --force origin "$branch"',
            'git --no-pager push origin +$branch',
            'git -c "k=a b" push --force origin "$branch"',
            'timeout 60 git push --force origin "$branch"',
            'timeout -k 5 60 git push --force origin "$branch"',
            'echo "$branch" | xargs git push --force origin',
            'echo "$branch" | xargs -n 1 git push origin +$branch',
            'sudo git push --force origin "$branch"',
            'nice -n 5 git push --force origin "$branch"',
            'stdbuf -oL git push --force origin "$branch"',
            'setsid git push --force origin "$branch"',
            'command -p git push --force origin "$branch"',
            'git' + chr(9) + 'push --force origin "$branch"',
            'git push origin ' + chr(92) + chr(10) + '  --force "$branch"',
        )
        baseline = git_commands(self.source)
        for line in hidden:
            with self.subTest(line=line):
                seen = git_commands(self.source + chr(10) + line)
                self.assertGreater(len(seen), len(baseline),
                                   "the scan does not see this command at all")
                added = seen[len(baseline):]
                self.assertTrue(
                    any("--force" in c or " -f" in c or "+" in c for c in added),
                    f"the scan truncated the command before its flags: {added!r}")

    def test_every_replay_spells_the_flags_that_decide_what_is_replayed(self):
        # Each `rebase.*` key behind these flags changes WHICH commits are
        # replayed or what the tree is while they are, and the result is
        # force-pushed. `--continue` replays what `start` planned and takes
        # none, so the replays are the rebases that name a base.
        replays = [c for c in git_commands(self.source)
                   if c.startswith("git rebase") and "--continue" not in c and "--abort" not in c]
        self.assertEqual(1, len(replays), replays)
        for flag in ("--merge", "--no-rebase-merges", "--no-update-refs", "--no-autosquash",
                     "--no-fork-point", "--no-autostash"):
            with self.subTest(flag=flag):
                self.assertIn(flag, replays[0])

    def test_the_push_leases_against_the_value_the_guard_approved(self):
        # Re-reading the remote ref in `publish` would lease against whatever a
        # fetch had since made of it, with the whole replay in between — the
        # lease would then name the commits the guard refused. A pattern, so a
        # second read spelled another way is still counted.
        self.assertIn('lease="$approved_lease"', self.source)
        reads = re.findall(r"\w*lease=\$\(\s*git rev-parse", self.source)
        self.assertEqual(1, len(reads),
                         "one place reads the remote tip into a lease, and it is the guard")
        # Narrowed to remote refs: `head=$(git rev-parse HEAD)` is in that
        # function too, and is not a lease.
        body = self.source.split("publish() {", 1)[1].split(chr(10) + "}", 1)[0]
        self.assertNotRegex(body, r'rev-parse\s+"?refs/remotes',
                            "publish re-reading a remote ref would lease against "
                            "whatever a fetch has since made of it")


class TheHelperRefusesBeforeItRewrites(unittest.TestCase):

    def setUp(self):
        self.root = copy_template(self, template())
        self.work = self.root + "/work"

    def helper(self, branch, mode="start"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def head(self):
        return self.at("git rev-parse HEAD").stdout.strip()

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
        result = self.helper("feat/x")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("the tree is dirty", result.stderr)
        self.assertIn("uncommitted", self.at("cat b.txt").stdout, "the edit survives the refusal")

    def test_an_untracked_file_is_dirty_whatever_status_shows(self):
        # status.showUntrackedFiles=no would read the planted file as a clean tree.
        self.at("git config status.showUntrackedFiles no && echo x > planted.txt")
        self.assertEqual("", self.at("git status --porcelain").stdout.strip())
        result = self.helper("feat/x")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("the tree is dirty", result.stderr)

    def test_an_assume_unchanged_file_is_refused_and_named(self):
        # Tag h in `git ls-files -v`: `git status` reads clean, and the replay's checkout would overwrite the edit.
        self.at("git update-index --assume-unchanged b.txt && echo local >> b.txt")
        self.assertEqual("", self.at("git status --porcelain").stdout.strip())
        before = self.head()
        result = self.helper("feat/x")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("assume-unchanged", result.stderr)
        self.assertIn("b.txt", result.stderr)
        self.assertEqual(before, self.head())
        self.assertIn("local", self.at("cat b.txt").stdout, "the edit survives the refusal")
        # No sparse checkout sets the flag, so saying there is one lifts nothing.
        self.at("git config core.sparseCheckout true")
        result = self.helper("feat/x")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("assume-unchanged", result.stderr)
        self.assertIn("b.txt", result.stderr)
        self.assertEqual(before, self.head())
        self.assertIn("local", self.at("cat b.txt").stdout, "the edit survives the refusal")

    def test_a_skip_worktree_file_is_refused_and_named(self):
        # `git status` reads clean here, so the dirty-tree guard cannot be what
        # fires; the message is what tells this guard from that one.
        self.at("git update-index --skip-worktree b.txt")
        self.assertEqual("", self.at("git status --porcelain").stdout.strip())
        before = self.head()
        result = self.helper("feat/x")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("skip-worktree", result.stderr)
        self.assertIn("b.txt", result.stderr)
        self.assertNotIn("the tree is dirty", result.stderr)
        self.assertEqual(before, self.head())
        self.assertEqual("", self.at('cat "$(git rev-parse --git-path claude-rebase-pending)" 2>/dev/null').stdout,
                         "a record was left for a replay that never started")

    def test_a_sparse_checkout_is_not_read_as_a_lift(self):
        self.at("git config core.sparseCheckout true && git update-index --skip-worktree b.txt")
        result = self.helper("feat/x")
        self.assertNotIn("skip-worktree", result.stderr)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_a_branch_the_remote_does_not_have_is_refused(self):
        self.at("git checkout -qb feat/unpublished")
        result = self.helper("feat/unpublished")
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertIn("origin has no feat/unpublished", result.stderr)

    def test_origin_without_main_is_refused_by_its_own_message(self):
        # Exit 6's other site. `start` fetches before it looks, so a branch
        # that was never pushed cannot reach it: the remote has to lose `main`
        # and the fetch has to prune it.
        run_bash('cd "$R/remote.git" && git symbolic-ref HEAD refs/heads/feat/x '
                 '&& git update-ref -d refs/heads/main', R=self.root)
        self.at("git config remote.origin.prune true")
        result = self.helper("feat/x")
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertIn("no refs/remotes/origin/main", result.stderr)

    def narrow_and_push(self, ref, change):
        # Only the other ref is fetched by default: a bare `git fetch origin` succeeds and leaves `ref`'s stale.
        other = "feat/x" if ref == "main" else "main"
        self.at(f"git config remote.origin.fetch +refs/heads/{other}:refs/remotes/origin/{other}")
        pushed = run_bash('cd "$R" && git clone -q remote.git third && cd third '
                          '&& git config user.email t@e.invalid && git config user.name T '
                          '&& git checkout -q "$F" && echo "$C" > "$C.txt" && git add -A '
                          '&& git commit -qm "$C" && git push -q origin "$F"', R=self.root, F=ref, C=change)
        self.assertEqual(0, pushed.returncode, pushed.stderr)
        return run_bash('cd "$R/remote.git" && git rev-parse "refs/heads/$F"', R=self.root, F=ref).stdout.strip()

    def test_a_narrowed_fetch_refspec_still_replays_onto_the_fresh_main(self):
        main = self.narrow_and_push("main", "again")
        result = self.helper("feat/x")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(0, self.at(f"git merge-base --is-ancestor {main} HEAD").returncode,
                         "the replay's base is a main origin no longer holds")

    def test_a_narrowed_fetch_refspec_still_sees_the_remote_branchs_new_commits(self):
        before = self.head()
        self.narrow_and_push("feat/x", "theirs")
        result = self.helper("feat/x")
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("carries commits this checkout did not start from", result.stderr)
        self.assertEqual(before, self.head(), "the branch was rewritten before the refusal")

    def test_commits_only_the_remote_has_stop_it(self):
        # A lease is satisfied by a commit this checkout has fetched, so it
        # would not stop a push that knowingly discards another session's work.
        # Exit 7 has three sites, and moving this guard after the replay would
        # still exit 7 with the branch rewritten; the message and the untouched
        # tip are what pin it.
        self.at('git clone -q ../remote.git ../second')
        run_bash('cd "$R/second" && git config user.email t@e.invalid && git config user.name T '
                 '&& git checkout -q feat/x && echo theirs > theirs.txt && git add -A '
                 '&& git commit -qm "another session" && git push -q origin feat/x', R=self.root)
        before = self.head()
        result = self.helper("feat/x")
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("carries commits this checkout did not start from", result.stderr)
        self.assertEqual(before, self.head(), "the branch was rewritten before the refusal")

    def test_continue_and_abort_refuse_when_no_rebase_is_running(self):
        # Exit 9 is shared across all four modes, so each message is the guard.
        finish = self.helper("feat/x", "continue")
        self.assertEqual(9, finish.returncode, finish.stderr)
        self.assertIn("'continue' has nothing to finish", finish.stderr)
        undo = self.helper("feat/x", "abort")
        self.assertEqual(9, undo.returncode, undo.stderr)
        self.assertIn("'abort' has nothing to undo", undo.stderr)

    def test_publish_refuses_when_no_replay_is_waiting(self):
        # The retry path, with nothing to retry, must not fall through to a push.
        result = self.helper("feat/x", "publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("no replay is waiting to be published", result.stderr)

    def interactive_rebase_by_hand(self):
        # A todo that stops at once and then drops the branch's commit, which
        # is the rebase the marker guard is for. Written by a shell function
        # rather than `sed -i`, whose in-place flag GNU and BSD spell apart.
        self.at("GIT_SEQUENCE_EDITOR='f() { { echo break; sed \"s/^pick/drop/\" \"$1\"; } "
                "> \"$1.new\" && mv \"$1.new\" \"$1\"; }; f' "
                "git rebase -i refs/remotes/origin/main")
        self.assertEqual("yes", self.at(REBASE_RUNNING).stdout.strip(),
                         "the hand-run rebase did not stop, so nothing below is reached")
        self.addCleanup(lambda: self.at("git rebase --abort"))

    def test_a_rebase_this_helper_did_not_start_is_not_published(self):
        # An interactive rebase dropping the branch's commits passes every
        # other check in `continue`: the published tip is what it started
        # from, so the divergence guard is satisfied and the result — which
        # holds none of the branch's work — would be forced over it.
        published = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.interactive_rebase_by_hand()
        result = self.helper("feat/x", "continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("not started by this helper", result.stderr)
        self.assertEqual(published, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote still carries the branch's published work")

    def test_a_rebase_this_helper_did_not_start_is_not_aborted(self):
        # The more destructive twin of the case above: `abort` would run
        # `git rebase --abort` and throw away a replay somebody is standing in.
        self.interactive_rebase_by_hand()
        result = self.helper("feat/x", "abort")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("not this helper's to undo", result.stderr)
        self.assertEqual("yes", self.at(REBASE_RUNNING).stdout.strip(),
                         "the foreign rebase was thrown away")

    def test_a_git_am_session_is_refused_as_itself(self):
        # It shares rebase-apply with a rebase, so without its own check each
        # mode names it as one: a rebase in progress, or one of no branch.
        self.at('git format-patch -1 --stdout refs/remotes/origin/main > ../main.patch '
                '&& echo other > c.txt && git add c.txt && git commit -qm "c.txt here first"; '
                'git am ../main.patch')
        self.assertEqual("yes", self.at(REBASE_RUNNING).stdout.strip(), "the am session did not stop")
        self.addCleanup(lambda: self.at("git am --abort"))
        for mode in ("start", "continue", "publish", "abort"):
            with self.subTest(mode=mode):
                result = self.helper("feat/x", mode)
                self.assertEqual(9, result.returncode, result.stderr)
                self.assertIn("a 'git am' session is in progress, not a rebase", result.stderr)
        self.assertEqual("yes", self.at(REBASE_RUNNING).stdout.strip(), "the am session was disturbed")

    def test_a_rebase_that_never_started_is_not_called_a_conflict(self):
        # Every other way `git rebase` can fail leaves no state, and reporting
        # it as a conflict sends the caller to a `continue` with nothing to do.
        self.at(LOCK_INDEX)
        result = self.helper("feat/x")
        self.assertEqual(11, result.returncode, result.stderr)
        self.assertIn("the rebase did not start", result.stderr)
        # A clean `git status` with git refusing to overwrite local changes is a
        # skip-worktree file; the refusal says how to see one.
        self.assertIn("git ls-files -v", result.stderr)
        self.assertIn("skip-worktree", result.stderr)


class TheArgumentGuardsEachHaveTheirOwnCase(unittest.TestCase):
    """Exit 2 covers five conditions, so each case pins its message too.

    No fixture, and that is the property: a guard that needed a repository
    would be one the session's location could switch off.
    """

    def refuse(self, branch, mode="start"):
        return run_bash('bash "$H" "$B" "$M"', H=str(HELPER), B=branch, M=mode)

    def test_it_takes_exactly_two_arguments(self):
        # Relaxed to `-ge 2`, the helper would ignore a trailing argument —
        # the trailing-flag case a helper exists to answer.
        for args in ("", '"$B"', '"$B" "$M" --onto'):
            with self.subTest(args=args or "(none)"):
                result = run_bash('bash "$H" ' + args,
                                  H=str(HELPER), B="feat/x", M="start")
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("usage: git-rebase-onto-main.sh", result.stderr)

    def test_an_unknown_mode_is_refused_by_its_own_message(self):
        result = self.refuse("feat/x", "skip")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("mode must be start, continue, publish or abort", result.stderr)

    def test_a_leading_dash_is_refused_before_it_can_be_a_flag(self):
        for branch in ("-D", "--onto", "-f"):
            with self.subTest(branch=branch):
                result = self.refuse(branch)
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("may not start with '-'", result.stderr)

    def test_a_range_is_refused(self):
        result = self.refuse("feat/a..feat/b")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("may not contain '..'", result.stderr)

    def test_anything_the_pattern_does_not_admit_is_refused(self):
        for branch in ("feat/x;id", "feat/x y", ".hidden", "feat/x$(id)", ""):
            with self.subTest(branch=branch):
                result = self.refuse(branch)
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("not a branch name this helper will take", result.stderr)

    def test_every_helper_that_takes_a_branch_takes_the_same_names(self):
        # One class, read from every helper that tests `"$branch"`: a name `/branch` can make and one helper refuses
        # is a branch stranded at that helper's step. The helpers are found by the test they carry rather than named,
        # so a further copy is held the day it is written, and the makers must be among them or the scan is blind.
        # `git-worktree-sync.sh` and `git-sync-head-drop.sh` take only a sync's head, so their pattern is pinned and
        # has to stay inside the class. `git-branch-drop.sh` asks it as well as the class.
        sync_head = r"^chore/harness-sync-[0-9a-f]{7}$"
        patterns = {}
        for path in sorted(SCRIPTS.glob("*.sh")):
            found = re.findall(r'\[\[ "\$branch" =~ (\S+) \]\]', path.read_text(encoding="utf-8"))
            if found:
                general = [pattern for pattern in found if pattern != sync_head] or found
                self.assertEqual(1, len(general), f"{path.name}: one branch-name pattern")
                patterns[path.name] = general[0]
        for named in ("git-branch-create.sh", "git-worktree-fork.sh", HELPER.name, "git-switch-existing.sh",
                      "git-worktree-remove.sh", "git-branch-drop.sh", "pr-for-branch.sh"):
            self.assertIn(named, patterns)
        for narrow in ("git-worktree-sync.sh", "git-sync-head-drop.sh"):
            self.assertEqual(sync_head, patterns.pop(narrow))
        self.assertRegex("chore/harness-sync-0123abc", patterns[HELPER.name])
        for name, pattern in patterns.items():
            with self.subTest(helper=name):
                self.assertEqual(patterns[HELPER.name], pattern)


class TheHostsHooksNeverRunInAReplay(unittest.TestCase):
    """The helper points `core.hooksPath` at an empty directory for the whole run,
    through the environment, so a `pre-rebase`, `post-rewrite`, `pre-push` or
    `reference-transaction` hook of the host's, which could veto or alter a
    replay, runs in no git the helper starts.
    """

    HOOKS = ("pre-rebase", "post-rewrite", "pre-push", "reference-transaction")

    def setUp(self):
        self.root = copy_template(self, template())
        self.work = self.root + "/work"

    def at(self, script, **env):
        return run_bash('cd "$W" && ' + script, W=self.work, **env)

    def helper(self, mode, **env):
        return run_bash('cd "$W" && bash "$H" feat/x "$M"',
                        W=self.work, H=str(HELPER), M=mode, **env)

    def plant_every_hook(self):
        # Each hook records that it ran and then FAILS, so a hook that fires
        # either leaves its marker or stops the replay it fires in.
        for name in self.HOOKS:
            planted = self.at('hooks="$(git rev-parse --git-path hooks)" && mkdir -p "$hooks" '
                              "&& printf '#!/bin/sh\\necho ran >> \"%s\"\\nexit 1\\n' \"$R/ran-$N\" "
                              '> "$hooks/$N" && chmod +x "$hooks/$N"',
                              R=self.root, N=name)
            self.assertEqual(0, planted.returncode, planted.stderr)

    def markers(self):
        return self.at('ls "$R" | grep "^ran-" || true', R=self.root).stdout.split()

    def test_a_hook_fires_here_when_nothing_ignores_it(self):
        # The positive control: the same hooks run under plain git in this
        # fixture, so their silence below is the helper's doing.
        self.plant_every_hook()
        self.at("git rebase -q origin/main || true")
        self.assertIn("ran-pre-rebase", self.markers())

    def test_a_clean_replay_ignores_every_hook_in_the_repository(self):
        self.plant_every_hook()
        result = self.helper("start")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.markers(), "a hook of the host's ran inside the replay")
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(),
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the replay was published")

    def test_a_replay_finished_by_continue_ignores_them_too(self):
        # `post-rewrite` fires when the sequencer finishes, which on a conflict
        # is under `continue` and not under `start`.
        self.at(CONFLICT)
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo resolved > a.txt && git add a.txt")
        self.plant_every_hook()
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.markers(), "a hook of the host's ran inside the replay")

    def test_configuration_the_caller_already_injected_is_kept(self):
        # The helper appends to `GIT_CONFIG_COUNT` rather than overwriting it,
        # so a caller's own entry survives and the hooks path is still set. The
        # entry is a key the helper does not spell, with an effect that shows on
        # the replayed commit: the committer name. A helper that overwrote entry
        # 0 would lose it and the replay would be committed as `Test`.
        self.plant_every_hook()
        result = self.helper("start", GIT_CONFIG_COUNT="1", GIT_CONFIG_KEY_0="user.name",
                             GIT_CONFIG_VALUE_0="Injected")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.markers())
        self.assertEqual("Injected", self.at("git log -1 --format=%cn").stdout.strip(),
                         "the caller's entry was dropped rather than kept beside the helper's")

    def test_the_path_is_set_before_the_first_command_that_can_run_a_hook(self):
        # The behaviour above is the proof; this keeps it covering the newest
        # command. A fetch updates refs, and a rebase and a push run hooks of
        # their own, so none of them may precede the export.
        text = "\n".join(code_lines(HELPER.read_text(encoding="utf-8")))
        export = text.find("GIT_CONFIG_KEY_")
        self.assertGreaterEqual(export, 0, "the hooks path is not set")
        self.assertIn("core.hooksPath", text)
        self.assertRegex(text, r"mktemp\s+-d", "the path is an empty directory made for the run")
        for command in ("git fetch origin", "git rebase", "git push"):
            with self.subTest(command=command):
                self.assertIn(command, text)
                self.assertLess(export, text.find(command))


class AConflictIsTheCaseRebaseIsHereFor(unittest.TestCase):
    """The resolution belongs in the replayed commit, not in a merge commit.

    So `start` leaves a conflicted rebase in progress: backing out would send
    the caller to the merge this repository stopped making.
    """

    def setUp(self):
        self.root = copy_template(self, template())
        self.work = self.root + "/work"
        self.at(CONFLICT)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode, branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def rebase_running(self):
        return self.at(REBASE_RUNNING).stdout.strip()

    def test_a_conflict_leaves_the_rebase_in_progress_and_names_the_paths(self):
        result = self.helper("start")
        self.assertEqual(8, result.returncode, result.stderr)
        # Joined to the helper's banner: git prints the conflicting commit's
        # title, which names a.txt, before the helper prints anything.
        self.assertIn("which is the point:\na.txt", result.stderr,
                      "the caller is told which files to resolve")
        self.assertEqual("yes", self.rebase_running(), "aborting here would mean a merge instead")

    def test_continue_refuses_while_anything_is_still_unmerged(self):
        self.assertEqual(8, self.helper("start").returncode)
        result = self.helper("continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("these are still unmerged", result.stderr)
        self.assertEqual("yes", self.rebase_running())

    def test_start_refuses_while_a_rebase_is_already_in_progress(self):
        self.assertEqual(8, self.helper("start").returncode)
        again = self.helper("start")
        self.assertEqual(9, again.returncode, again.stderr)
        self.assertIn("a rebase is already in progress", again.stderr)
        self.assertEqual("yes", self.rebase_running(), "the running rebase was disturbed")

    def test_continue_refuses_a_replay_whose_head_was_moved(self):
        # Checked out onto a branch mid-replay, `--continue` would commit the
        # rest there, move that branch, and publish a history never replayed.
        published = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.assertEqual(8, self.helper("start").returncode)
        self.at('echo resolved > a.txt && git add a.txt && git checkout -q -b feat/other')
        other = self.at("git rev-parse feat/other").stdout.strip()

        result = self.helper("continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("is not where the replay of feat/x stopped", result.stderr)
        self.assertIn("'abort' the replay", result.stderr)
        self.assertEqual(other, self.at("git rev-parse feat/other").stdout.strip(), "feat/other was moved")
        self.assertEqual(published, self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[0],
                         "origin was published over")

    def test_a_resolution_the_caller_committed_is_kept(self):
        # git allows committing mid-replay, and `--continue` keeps the commit;
        # refusing it and naming a move back would lose it.
        self.assertEqual(8, self.helper("start").returncode)
        self.at('echo resolved > a.txt && git add a.txt && git commit -qm "resolved by hand"')
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("resolved\n", self.at("git show refs/remotes/origin/feat/x:a.txt").stdout,
                         "the committed resolution did not reach origin")

    def test_more_than_the_callers_resolution_on_the_stop_is_refused(self):
        # A second commit on the stop is not a resolution of this replay, and
        # would ride into the publish.
        published = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.assertEqual(8, self.helper("start").returncode)
        self.at('echo resolved > a.txt && git add a.txt && git commit -qm "resolved by hand" '
                '&& echo other > other.txt && git add other.txt && git commit -qm "not this replay"')
        head = self.at("git rev-parse HEAD").stdout.strip()
        result = self.helper("continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("nor one commit on it", result.stderr)
        self.assertIn(head, result.stderr, "the commit abort would leave behind is not named")
        self.assertEqual(published, self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[0])

    def test_continue_sends_an_unrecorded_stop_to_abort(self):
        # An empty mark, as the previous version wrote, names nowhere to be.
        self.assertEqual(8, self.helper("start").returncode)
        self.at(': > "$(git rev-parse --git-path rebase-merge)/started-by-this-helper" '
                '&& echo resolved > a.txt && git add a.txt')
        result = self.helper("continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("'abort' the replay", result.stderr)

    def test_continue_refuses_a_todo_edited_since_the_stop(self):
        # Every other guard reads HEAD, the marker and the remote, and a hand
        # edit of what is left to replay changes none of them.
        published = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.assertEqual(8, self.helper("start").returncode)
        self.at("GIT_SEQUENCE_EDITOR='f() { echo \"exec true\" >> \"$1\"; }; f' git rebase --edit-todo")
        self.at('echo resolved > a.txt && git add a.txt')
        result = self.helper("continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("todo list is not the one it stopped with", result.stderr)
        self.assertEqual("yes", self.rebase_running())
        self.assertEqual(published, self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[0])

    def test_continue_sends_a_replay_whose_remote_branch_went_to_abort(self):
        # Mid-replay there is nothing to push normally, so the way out named
        # has to be one that works from here.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("git update-ref -d refs/remotes/origin/feat/x")
        self.at('echo resolved > a.txt && git add a.txt')
        result = self.helper("continue")
        self.assertEqual(6, result.returncode, result.stderr)
        self.assertIn("nothing to force over: 'abort' it", result.stderr)
        self.assertEqual("yes", self.rebase_running())

    def test_continue_refuses_an_apply_backend_replay_left_marked(self):
        # An earlier version's replay: marked as this helper's, on the backend
        # it no longer uses, where a stop can repeat for ever.
        self.at('git -c rebase.backend=apply rebase refs/remotes/origin/main; '
                ': > "$(git rev-parse --git-path rebase-apply)/started-by-this-helper"')
        self.assertEqual("yes", self.rebase_running(), "the apply replay did not stop")
        self.addCleanup(lambda: self.at("git rebase --abort"))
        self.at('echo resolved > a.txt && git add a.txt')
        result = self.helper("continue")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("apply backend, which this helper no longer uses", result.stderr)

    def test_continue_refuses_a_rebase_belonging_to_another_branch(self):
        self.assertEqual(8, self.helper("start").returncode)
        result = self.helper("continue", branch="feat/other")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("the rebase in progress is feat/x, not feat/other", result.stderr)
        self.assertEqual("yes", self.rebase_running())

    def test_publish_refuses_while_a_rebase_is_still_in_progress(self):
        self.assertEqual(8, self.helper("start").returncode)
        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("a rebase is still in progress", result.stderr)
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
        # would leave this force push unguarded.
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
        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("aborted;", result.stdout)

        self.assertEqual("no", self.rebase_running())
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip())
        self.assertEqual(remote_before, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())

    def test_abort_names_the_uncommitted_edits_it_discards(self):
        # Accepted rather than refused: discarding the stop's work is what an
        # abort is for, so the edits are listed rather than kept.
        self.assertEqual(8, self.helper("start").returncode)
        self.at('echo scratch > b.txt')
        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("aborting discards these uncommitted changes", result.stderr)
        self.assertIn("b.txt", result.stderr)
        self.assertEqual("work\n", self.at("cat b.txt").stdout, "the abort did not reset the tree")


class ALegacyMergeForwardIsNotSilentlyDropped(unittest.TestCase):
    """A rebase drops merge commits, and a branch made under the old policy
    has one. Where that merge carries content neither parent has, replaying
    loses it before the push, which no lease can see."""

    def setUp(self):
        self.root = copy_template(self, template())
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
        # The refusal teaches the route that works, not only that this one failed.
        self.assertIn("never merge main forward", result.stderr)
        self.assertIn("resolve it in the replayed commit", result.stderr)

    def test_a_merge_that_kept_one_side_against_a_clean_change_stops_the_run(self):
        # Every file matches one parent, so `--cc` shows nothing, and the
        # replay onto main would bring d.txt back.
        self.at('git checkout -q main && echo later > d.txt && git add -A '
                '&& git commit -qm "main moved again" && git push -q origin main '
                '&& git checkout -q feat/x && git merge --no-commit -q main; git rm -qf d.txt '
                '&& git commit -qm "merge main, keeping the branch side" && git push -q -f origin feat/x')
        self.assertEqual("", self.at("git log --merges --cc --format= origin/main..HEAD").stdout,
                         "the fixture is one `--cc` can see, so it tests nothing new")
        before = self.at("git rev-parse HEAD").stdout.strip()

        result = self.helper()
        self.assertEqual(10, result.returncode, result.stderr)
        self.assertIn(f"where it differs from a clean merge:\n{before} d.txt\n", result.stderr,
                      "the file the merge kept apart is not named")
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip(), "nothing was replayed")

    def test_a_side_taken_whole_over_a_clean_hunk_in_a_conflicted_file_stops_the_run(self):
        # Main edits lines 1 and 50 of m.txt and the branch line 50, and the
        # merge takes the branch's m.txt whole. It equals one parent, so `--cc`
        # is empty, and the replay would bring main's line 1 back. Each edit is
        # a `sed` into a new file: BSD's `sed -i` takes the script for a suffix.
        self.at('git checkout -q main && seq 1 60 > m.txt && git add -A && git commit -qm "m.txt" '
                '&& git push -q origin main && git checkout -q feat/x && git rebase -q main '
                '&& git push -q -f origin feat/x '
                '&& git checkout -q main && sed "1s/.*/main-one/;50s/.*/main-fifty/" m.txt > new.txt && mv new.txt m.txt '
                '&& git commit -qam "main edits m.txt" && git push -q origin main '
                '&& git checkout -q feat/x && sed "50s/.*/branch-fifty/" m.txt > new.txt && mv new.txt m.txt '
                '&& git commit -qam "the branch edits m.txt" '
                '&& { git merge -q main || true; } && git checkout -q --ours m.txt && git add m.txt '
                '&& git commit -q --no-edit && git push -q -f origin feat/x')
        self.assertEqual("", self.at("git log --merges --cc --format= origin/main..HEAD").stdout,
                         "the fixture is one `--cc` can see, so it tests nothing new")
        before = self.at("git rev-parse HEAD").stdout.strip()

        result = self.helper()
        self.assertEqual(10, result.returncode, result.stderr)
        self.assertIn(f"where it differs from a clean merge:\n{before} m.txt\n", result.stderr,
                      "the conflicted file that dropped a clean hunk is not named")
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip(), "nothing was replayed")

    def test_a_non_ascii_name_is_read_rather_than_passed_as_clean(self):
        # git quotes such a name by default, and a quoted name is one rev-parse cannot read.
        self.at('git checkout -q main && seq 1 60 > café.txt && git add -A && git commit -qm "café.txt" '
                '&& git push -q origin main && git checkout -q feat/x && git rebase -q main '
                '&& git push -q -f origin feat/x '
                '&& git checkout -q main && sed "1s/.*/main-one/;50s/.*/main-fifty/" café.txt > new.txt && mv new.txt café.txt '
                '&& git commit -qam "main edits café.txt" && git push -q origin main '
                '&& git checkout -q feat/x && sed "50s/.*/branch-fifty/" café.txt > new.txt && mv new.txt café.txt '
                '&& git commit -qam "the branch edits café.txt" '
                '&& { git merge -q main || true; } && git checkout -q --ours café.txt && git add café.txt '
                '&& git commit -q --no-edit && git push -q -f origin feat/x')
        before = self.at("git rev-parse HEAD").stdout.strip()

        result = self.helper()
        self.assertEqual(10, result.returncode, result.stderr)
        # Matched loosely around the é, which the runner may decode in another code page; quoted it would open "caf\.
        self.assertRegex(result.stderr, rf"{before} caf\S+\.txt\n", "the conflicted file is not named")
        self.assertNotIn('"caf\\', result.stderr, "the name reached the check still quoted")

    def test_a_binary_file_taken_whole_is_left_to_the_replay(self):
        # A binary has no clean hunk to drop, and merge-file refuses one, which must not stop every rebase.
        self.binary_taken_whole("a\\0base", "a\\0main", "a\\0branch")

    def test_a_file_whose_base_alone_is_binary_is_left_to_the_replay(self):
        # merge-file refuses a binary base as it does a binary side.
        self.binary_taken_whole("a\\0base", "main", "branch")

    def binary_taken_whole(self, base, on_main, on_branch):
        self.at(f'git checkout -q main && printf "{base}\\n" > b.bin && git add -A && git commit -qm "b.bin" '
                '&& git push -q origin main && git checkout -q feat/x && git rebase -q main '
                '&& git push -q -f origin feat/x '
                f'&& git checkout -q main && printf "{on_main}\\n" > b.bin && git commit -qam "main edits b.bin" '
                f'&& git push -q origin main && git checkout -q feat/x && printf "{on_branch}\\n" > b.bin '
                '&& git commit -qam "the branch edits b.bin" '
                '&& { git merge -q main || true; } && git checkout -q --ours b.bin && git add b.bin '
                '&& git commit -q --no-edit && git push -q -f origin feat/x')
        self.assertEqual("", self.at("git status --porcelain").stdout, "the merge was not committed")
        self.addCleanup(lambda: self.at("git rebase --abort"))

        result = self.helper()
        self.assertNotIn("cannot replay the clean hunks", result.stderr)
        self.assertEqual(8, result.returncode, result.stderr)

    def test_a_conflict_the_merge_resolved_to_one_side_is_left_to_the_replay(self):
        # A side taken whole that drops none of the other's clean hunks passes,
        # and here the replay meets the same conflict.
        self.at(CONFLICT + 'git merge -q main; echo theirs > a.txt && git add a.txt '
                '&& git commit -q --no-edit && git push -q -f origin feat/x')
        self.assertEqual("", self.at("git status --porcelain").stdout, "the merge was not committed")
        self.addCleanup(lambda: self.at("git rebase --abort"))
        result = self.helper()
        self.assertEqual(8, result.returncode, result.stderr)
        self.assertIn("which is the point", result.stderr)

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
        self.root = copy_template(self, template())
        self.work = self.root + "/work"

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def remote_tip(self):
        return self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()

    def fail_the_push(self):
        self.at(FAIL_PUSH)
        self.assertNotEqual(0, self.helper().returncode, "the push must fail for this to mean anything")

    def move_the_remote(self):
        # Another session's push, which this checkout has not fetched: its
        # remote-tracking ref still reads the leased tip.
        stale = self.remote_tip()
        moved = run_bash('git clone -q "$R/remote.git" "$R/second" && cd "$R/second" '
                         '&& git config user.email t@e.invalid && git config user.name T '
                         '&& git checkout -q feat/x && echo theirs > theirs.txt && git add -A '
                         '&& git commit -qm "another session" && git push -q origin feat/x',
                         R=self.root)
        self.assertEqual(0, moved.returncode, moved.stderr)
        self.assertEqual(stale, self.remote_tip(), "the tracking ref must still be stale")

    def test_a_clean_update_is_replayed_and_published(self):
        before = self.at("git rev-parse HEAD").stdout.strip()
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)

        after = self.at("git rev-parse HEAD").stdout.strip()
        self.assertNotEqual(before, after, "a replay onto a moved base makes new SHAs")
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "a rebased branch carries no merge commit")
        self.assertEqual(after, self.remote_tip())
        self.assertEqual("", self.at("git log --oneline HEAD..origin/main").stdout,
                         "and the branch now holds everything main does")

    def test_a_configured_push_redirect_is_refused_before_anything_is_forced(self):
        # Spelling the refspec pins the ref and not the URL, so a `pushurl` or a `pushInsteadOf` that sends the force
        # to another repository refuses the run.
        same_path_other_host = (
            '"https://evil.example/$(basename "$(dirname "$(git config --get remote.origin.url)")")/remote.git"')
        for key, value in (("remote.origin.pushurl", '"https://example.invalid/x.git"'),
                           ("remote.origin.pushurl", same_path_other_host),
                           ("url.https://example.invalid/evil.git.pushinsteadof",
                            '"$(git config --get remote.origin.url)"')):
            with self.subTest(key=key, value=value):
                self.at(f'git config --local "{key}" {value}')
                before = self.remote_tip()

                result = self.helper()

                self.assertEqual(7, result.returncode, result.stderr)
                self.assertIn("a redirected force is not published", result.stderr)
                self.assertEqual(before, self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[0])
                self.at(f'git config --local --unset-all "{key}"')
                self.at('rm -f "$(git rev-parse --git-path claude-rebase-pending)"')
                self.at("git reset -q --hard refs/remotes/origin/feat/x")

    def test_a_push_url_spelling_the_same_repository_differently_is_published(self):
        # The control for the refusal above: the comparison is of the repository named, not of two strings, which is
        # what lets a protocol rewrite on one host through.
        self.at('git config --local remote.origin.pushurl "$(git config --get remote.origin.url)/"')
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.at("git rev-parse HEAD").stdout.split(),
                         self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[:1])

    def test_repo_key_agrees_across_protocols_on_one_host_and_nowhere_else(self):
        # `repo_key` is lifted out of the file the force helpers source and run alone, so the spellings are exact. One
        # URL per call, through the environment: a subject on stdin arrives with CRLF on Windows, and a trailing CR is
        # part of the URL.
        script = 'eval "$(sed -n \'/^repo_key() {/,/^}/p\' "$H")"; repo_key "$U" || echo "(none)"'
        urls = [
            "https://github.com/o/r.git", "git@github.com:o/r.git", "ssh://git@github.com:22/o/r",
            "https://GitHub.com/o/r/", "https://evil.example/o/r", "https://github.com/r",
            "ext::sh -c evil", "C:/x/o/r.git", "/tmp/x/o/r.git",
            "https://github.com/x/y#/o/r", "https://github.com/x/y?/o/r", "ssh://github.com:o/r",
            "git@github.com:/o/r", "https://[::1]/o/r", "file:///o/r", "ext::sh -c evil/o/r",
            "https://github.com/evil/o/r", "https://github.com/x/../o/r", "https://github.com/o//r",
            "/evil/o/r", "https://github.com:443/o/r", "https://github.com:8443/o/r", "ssh://git@github.com:2222/o/r",
            "https://github.com:22/o/r", "git://github.com/o/r", "http://github.com/o/r", "/tmp/x/o/r",
        ]
        got = {u: run_bash(script, H=str(SCRIPTS / "git-push-url.sh"), U=u).stdout.strip() for u in urls}
        for same in (*urls[:4], "https://github.com:443/o/r"):
            self.assertEqual("github.com|o/r", got[same], same)
        # Another port is another server, each scheme's default aside.
        self.assertEqual("github.com:8443|o/r", got["https://github.com:8443/o/r"])
        self.assertEqual("github.com:2222|o/r", got["ssh://git@github.com:2222/o/r"])
        self.assertEqual("github.com:22|o/r", got["https://github.com:22/o/r"])
        self.assertEqual("evil.example|o/r", got["https://evil.example/o/r"])
        for refused in ("https://github.com/r", "ext::sh -c evil", "https://github.com/x/y#/o/r",
                        "https://github.com/x/y?/o/r", "ssh://github.com:o/r", "git@github.com:/o/r",
                        "https://[::1]/o/r", "file:///o/r", "ext::sh -c evil/o/r",
                        "https://github.com/x/../o/r", "https://github.com/o//r",
                        "git://github.com/o/r", "http://github.com/o/r"):
            self.assertEqual("(none)", got[refused], refused)
        # The whole path counts: a deeper path with the same last two components is another key.
        self.assertEqual("github.com|evil/o/r", got["https://github.com/evil/o/r"])
        # A local `r` and `r.git` are two directories, where a host serves one repository under both.
        self.assertEqual("|C:/x/o/r.git", got["C:/x/o/r.git"])
        self.assertEqual("|/tmp/x/o/r.git", got["/tmp/x/o/r.git"])
        self.assertNotEqual(got["/tmp/x/o/r"], got["/tmp/x/o/r.git"])
        self.assertNotEqual(got["/evil/o/r"], got["/tmp/x/o/r.git"])

    def test_a_remote_with_two_urls_is_refused_before_anything_is_forced(self):
        # A remote with several `url` values pushes to each: the force would reach a second repository the lease was
        # never read from.
        self.at('git config --local --add remote.origin.url "$(git config --get remote.origin.url)-second"')
        before = self.remote_tip()

        result = self.helper()

        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("origin has 2 push URLs", result.stderr)
        self.assertEqual(before, self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[0])

    def test_a_second_url_behind_a_single_push_url_is_refused(self):
        # `pushurl` names the second `url`, so one push URL matches the last `url` while the fetch and the lease read
        # the first: the force would go to a repository its lease was never read from.
        self.at('git config --local --add remote.origin.url "$(git config --get remote.origin.url)-second"')
        self.at('git config --local remote.origin.pushurl "$(git config --get remote.origin.url)"')
        before = self.remote_tip()

        result = self.helper()

        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("origin has more than one url", result.stderr)
        self.assertEqual(before, self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[0])

    def test_a_redirect_set_after_the_replay_is_refused_when_it_is_published(self):
        # `start` asked before the replay, so only `publish`'s own question stands between a redirect set since and the
        # force. A `continue` reaches the same function.
        self.fail_the_push()
        before = self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[0]
        self.at('git config --local remote.origin.pushurl "https://example.invalid/x.git"')
        self.at(ALLOW_PUSH)

        result = self.helper("publish")

        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("a redirected force is not published", result.stderr)
        self.assertEqual(before, self.at("git ls-remote origin refs/heads/feat/x").stdout.split()[0])

    def test_a_fetch_url_parted_from_the_push_url_is_refused_before_the_replay(self):
        # `insteadOf` moves the fetch to a copy and `pushInsteadOf` puts the push back, so the push URL is the one the
        # remote was added with while the lease reads another repository's tip. `start` asks before it fetches, so
        # nothing is read from there, the branch is not replayed and no record waits for a `publish`.
        origin = self.at("git config --get remote.origin.url").stdout.strip()
        copy = self.root + "/copy.git"
        self.assertEqual(0, self.at(f'git clone -q --bare "{origin}" "{copy}"').returncode)
        self.at(f'git config --local "url.{copy}.insteadof" "{origin}"')
        self.at(f'git config --local "url.{origin}.pushinsteadof" "{origin}"')
        head = self.at("git rev-parse HEAD").stdout.strip()

        result = self.helper()

        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("origin fetches from", result.stderr)
        self.assertEqual(head, self.at("git rev-parse HEAD").stdout.strip(), "the branch was replayed")
        self.assertEqual(head, self.at(f'git -C "{origin}" rev-parse refs/heads/feat/x').stdout.strip())
        self.assertNotEqual(0, self.at('test -e "$(git rev-parse --git-path claude-rebase-pending)"').returncode,
                            "a record waits for a publish")

    def test_configuration_cannot_change_what_the_replay_does(self):
        # `rebase.rebaseMerges` would keep the merge commits this helper
        # exists to be rid of.
        self.at("git config rebase.rebaseMerges true")
        self.at('git checkout -q main && echo later > d.txt && git add -A '
                '&& git commit -qm "main moved again" && git push -q origin main '
                '&& git checkout -q feat/x && git merge --no-edit -q main '
                '&& git push -q -f origin feat/x')
        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", self.at("git log --merges --format=%H origin/main..HEAD").stdout,
                         "rebase.rebaseMerges would have kept the merge")

    def test_a_branch_pointing_inside_the_replayed_range_is_not_moved(self):
        # `rebase.updateRefs` only moves a ref inside the replayed range, so
        # the case needs one there for `--no-update-refs` to be asserted.
        self.at("git branch marker")
        self.at('echo more > e.txt && git add -A && git commit -qm "a second commit" '
                '&& git push -q -f origin feat/x')
        self.at("git config rebase.updateRefs true")
        before = self.at("git rev-parse marker").stdout.strip()
        self.assertTrue(before, "the marker branch was not created")

        result = self.helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(before, self.at("git rev-parse marker").stdout.strip(),
                         "rebase.updateRefs force-moved a branch this helper does not own")

    def test_a_push_that_fails_leaves_a_retry_that_works(self):
        # The replay finishes and the push does not, taking the rebase state
        # with it: without the record nothing can reach the branch again.
        self.fail_the_push()
        rewritten = self.at("git rev-parse HEAD").stdout.strip()
        self.assertEqual("", self.at("git log --oneline HEAD..origin/main").stdout,
                         "the replay did finish; only the push did not")

        waiting = self.helper("start")
        self.assertEqual(9, waiting.returncode, waiting.stderr)
        self.assertIn("a replay of feat/x is waiting to be published", waiting.stderr)
        self.at(ALLOW_PUSH)
        result = self.helper("publish")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(rewritten, self.remote_tip())

    def test_a_scoped_branch_name_is_replayed_and_published(self):
        # `/branch` names a branch `type(scope)/kebab`, and the helpers that
        # fork one, switch to one and look one up all admit the parentheses.
        scoped = "feat(scope)/x"
        renamed = run_bash('cd "$W" && git branch -m "$B" && git push -q -u origin "$B"',
                           W=self.work, B=scoped)
        self.assertEqual(0, renamed.returncode, renamed.stderr)

        result = self.helper(branch=scoped)
        self.assertEqual(0, result.returncode, result.stderr)
        published = run_bash('cd "$W" && git rev-parse "refs/remotes/origin/$B"',
                             W=self.work, B=scoped).stdout.strip()
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(), published)
        self.assertEqual("", self.at("git log --oneline HEAD..origin/main").stdout,
                         "and the branch now holds everything main does")

    def test_publish_refuses_a_record_whose_replay_never_ran(self):
        # A conflicted rebase undone by hand leaves the two-field record, and
        # without a head `publish` would force whatever HEAD has since become.
        self.at(CONFLICT)
        self.assertEqual(8, self.helper().returncode, "the start must conflict")
        self.at("git rebase --abort")
        published = self.remote_tip()
        self.at('echo unrelated > later.txt && git add -A && git commit -qm "not a replay"')

        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("never finished", result.stderr)
        self.assertEqual(published, self.remote_tip(), "the remote still carries what it had")

    def test_a_commit_on_top_of_the_replay_is_published_with_it(self):
        # Refusing it strands the branch: `abort` then clears the record and
        # `start` reads the rewritten history as another session's commits.
        self.fail_the_push()
        self.at('echo extra > extra.txt && git add -A && git commit -qm "after the replay"')
        refused = self.helper("abort")
        self.assertEqual(9, refused.returncode, refused.stderr)
        self.assertIn("commits sit on the replay of feat/x", refused.stderr)

        self.at(ALLOW_PUSH)
        result = self.helper("publish")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(), self.remote_tip(),
                         "the replay and the commit on it are what origin carries")

    def test_a_retry_keeps_the_replayed_head_it_was_given(self):
        # A retry that failed with a commit on top must not record that commit
        # as the replay, or dropping it strands the replay the record named.
        self.fail_the_push()
        replayed = self.at("git rev-parse HEAD").stdout.strip()
        self.at('echo extra > extra.txt && git add -A && git commit -qm "after the replay"')
        self.assertNotEqual(0, self.helper("publish").returncode, "the retry must fail too")
        self.at("git reset -q --hard " + replayed)

        self.at(ALLOW_PUSH)
        result = self.helper("publish")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(replayed, self.remote_tip())

    def test_abort_clears_a_record_it_cannot_keep_without_asking_origin(self):
        # HEAD no longer descends from the replay, so the answer is decided
        # locally, and an origin that cannot be reached does not hold it up.
        self.fail_the_push()
        self.at('echo amended > amended.txt && git add -A && git commit -q --amend --no-edit '
                '&& git remote set-url origin "$(pwd)/../nowhere.git"')
        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("cleared the waiting replay", result.stdout)

    def test_publish_refuses_a_head_that_does_not_descend_from_the_replay(self):
        # An amended replay is a rewrite this helper did not make.
        self.fail_the_push()
        published = self.remote_tip()
        self.at('echo extra > extra.txt && git add -A && git commit -q --amend --no-edit')
        self.at(ALLOW_PUSH)

        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("does not descend from", result.stderr)
        self.assertIn("'abort' only clears the", result.stderr, "abort is offered as a way out it is not")
        self.assertEqual(published, self.remote_tip(),
                         "the amended commit was forced over the published one")

    def test_publish_refuses_a_record_naming_another_branch(self):
        self.fail_the_push()
        result = self.helper("publish", branch="feat/other")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("the waiting replay is feat/x, not feat/other", result.stderr)

    def test_publish_refuses_a_remote_that_moved_since_the_lease(self):
        # Asked of origin, not of the tracking ref, which still reads the lease:
        # read stale, the push is rejected on every retry.
        self.fail_the_push()
        self.move_the_remote()
        self.at(ALLOW_PUSH)
        result = self.helper("publish")
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("has moved since the replay was approved", result.stderr)

    def test_a_push_that_landed_unannounced_is_not_called_a_move(self):
        # The server took the push and the answer was lost: origin already
        # holds HEAD, which is the one move that needs nothing further.
        self.fail_the_push()
        self.at(ALLOW_PUSH)
        self.at('git push -q -f origin feat/x')
        result = self.helper("publish")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("origin already has feat/x", result.stdout)
        self.assertEqual(
            "", self.at('cat "$(git rev-parse --git-path claude-rebase-pending)" 2>/dev/null').stdout,
            "the record outlived the push it was kept for")

    def test_publish_refuses_when_origin_cannot_be_asked(self):
        self.fail_the_push()
        self.at('git remote set-url origin "$(pwd)/../nowhere.git"')
        result = self.helper("publish")
        self.assertEqual(12, result.returncode, result.stderr)
        self.assertIn("cannot ask origin where feat/x is", result.stderr)

    def test_publish_refuses_a_replay_that_ended_on_no_branch(self):
        # `publish` mode's own branch check, which comes before any read of the
        # replay. `publish()`'s checks behind it, on the branch and on an
        # empty lease, are defensive and have no case: every granted path
        # checks the branch first, and no mode writes a head without a lease.
        self.fail_the_push()
        self.at("git checkout -q --detach HEAD")
        result = self.helper("publish")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("on a detached HEAD, not feat/x", result.stderr)

    def test_an_unreadable_waiting_record_is_refused(self):
        # Refused with a message and an assigned code, in the path that exists
        # to recover from a failed run.
        self.fail_the_push()
        self.at('printf "\\n" > "$(git rev-parse --git-path claude-rebase-pending)"')
        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("the waiting record is unreadable", result.stderr)

    def test_abort_clears_an_unreadable_record(self):
        # The one way past it: left, it refuses every mode on every branch.
        self.fail_the_push()
        self.at('printf "\\n" > "$(git rev-parse --git-path claude-rebase-pending)"')
        refused = self.helper("start")
        self.assertEqual(9, refused.returncode, refused.stderr)
        self.assertIn("run 'abort' to clear it", refused.stderr)

        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("cleared an unreadable waiting record", result.stdout)
        self.assertEqual(
            "", self.at('cat "$(git rev-parse --git-path claude-rebase-pending)" 2>/dev/null').stdout,
            "the unreadable record is still there")

    def test_a_rebase_that_never_started_leaves_no_record(self):
        # Nothing was replayed, so there is no tip for a record to protect,
        # and one left behind refuses every later `start` on any branch.
        self.at(LOCK_INDEX)
        self.assertEqual(11, self.helper().returncode, "the rebase must not start")
        self.assertEqual(
            "", self.at('cat "$(git rev-parse --git-path claude-rebase-pending)" 2>/dev/null').stdout,
            "a record was left for a replay that never happened")
        self.at(UNLOCK_INDEX)
        self.assertEqual(0, self.helper().returncode, "a stale record refused the next start")

    def test_abort_says_so_when_a_record_names_no_tip_to_put_back(self):
        # A three-field record, as an earlier version wrote: nothing sits on
        # the replay, so the refusal must not claim that anything does.
        self.fail_the_push()
        self.at('f="$(git rev-parse --git-path claude-rebase-pending)"; read -r b l h o < "$f"; '
                'echo "$b $l $h" > "$f"')
        result = self.helper("abort")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("names no tip from before the replay", result.stderr)
        self.assertNotIn("commits sit on the replay", result.stderr)

    def test_abort_gives_a_replay_up_by_putting_the_branch_back(self):
        # A push origin keeps refusing would otherwise hold the record for
        # ever: clearing it strands the rewritten branch, and keeping it
        # refuses every later `start`. The pre-replay tip is neither.
        before = self.at("git rev-parse HEAD").stdout.strip()
        published = self.remote_tip()
        self.fail_the_push()
        self.assertNotEqual(before, self.at("git rev-parse HEAD").stdout.strip(), "nothing was replayed")

        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("put feat/x back at " + before, result.stdout)
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip())
        self.assertEqual(published, self.remote_tip(), "something was published")

        self.at(ALLOW_PUSH)
        again = self.helper()
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(), self.remote_tip(),
                         "the branch put back could not be replayed and published afresh")

    def test_abort_clears_a_record_publish_can_no_longer_finish(self):
        # `publish` sends a moved remote to `abort`, so `abort` has to take it
        # or each mode names the other and the record is reachable by neither.
        self.fail_the_push()
        self.move_the_remote()
        self.assertEqual(7, self.helper("publish").returncode)
        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("cleared the waiting replay", result.stdout)

    def test_abort_clears_a_record_whose_remote_branch_is_gone(self):
        # Gone from origin while the tracking ref still names it.
        self.fail_the_push()
        run_bash('git -C "$R/remote.git" update-ref -d refs/heads/feat/x', R=self.root)
        gone = self.helper("publish")
        self.assertEqual(6, gone.returncode, gone.stderr)
        self.assertIn("origin has no feat/x any more", gone.stderr)
        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("cleared the waiting replay", result.stdout)

    def test_abort_refuses_a_waiting_replay_belonging_to_another_branch(self):
        # The name passed and the branch in hand are two ways to reach the
        # wrong record, and clearing it strands a tip nothing else publishes.
        self.fail_the_push()
        self.at("git checkout -qb feat/other")

        named = self.helper("abort", branch="feat/other")
        self.assertEqual(4, named.returncode, named.stderr)
        self.assertIn("the waiting replay is feat/x, not feat/other", named.stderr)

        elsewhere = self.helper("abort", branch="feat/x")
        self.assertEqual(4, elsewhere.returncode, elsewhere.stderr)
        self.assertIn("only ever touches the current branch", elsewhere.stderr)

        self.at("git checkout -q feat/x")
        self.at(ALLOW_PUSH)
        self.assertEqual(0, self.helper("publish").returncode,
                         "the record did not survive both refusals")

    def test_a_second_run_changes_nothing_and_does_not_force(self):
        first = self.helper()
        self.assertEqual(0, first.returncode, first.stderr)
        settled = self.at("git rev-parse HEAD").stdout.strip()
        again = self.helper()
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertIn("nothing to force", again.stdout)
        self.assertEqual(settled, self.at("git rev-parse HEAD").stdout.strip())


class TheApplyBackendIsNeverTheOneThatRuns(unittest.TestCase):
    """`rebase.backend=apply` is the caller's setting, and `start` spells
    `--merge` over it: the apply backend leaves a commit it never applied
    looking exactly like one a resolution emptied, so nothing could tell a
    drop that loses nothing from one that loses a commit.
    """

    def setUp(self):
        self.root = copy_template(self, template())
        self.work = self.root + "/work"
        self.at("git config rebase.backend apply")
        self.at(CONFLICT)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def state(self):
        return self.at('for d in rebase-merge rebase-apply; do '
                       'test -d "$(git rev-parse --git-path $d)" && echo $d; done').stdout.strip()

    def test_the_setting_does_not_choose_the_backend(self):
        result = self.helper("start")
        self.assertEqual(8, result.returncode, result.stderr)
        self.assertIn("which is the point:\na.txt", result.stderr)
        self.assertEqual("rebase-merge", self.state(), "the caller's setting chose the backend")

    def test_an_empty_resolution_is_dropped_by_git_and_published(self):
        # Taking origin/main's side verbatim empties the commit, which the
        # merge backend drops by itself rather than asking for `--skip`.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        result = self.helper("continue")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", self.state(), "the replay is still wedged in progress")
        self.assertEqual("mine\n", self.at("cat a.txt").stdout)
        self.assertEqual("work\n", self.at("cat b.txt").stdout,
                         "the branch's own commit was dropped along with the empty one")
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(),
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote carries what was replayed")

    def test_a_conflict_after_an_emptied_commit_is_reported(self):
        self.at('echo theirs-again > a.txt && git add -A '
                '&& git commit -qm "the branch edits a.txt again" '
                '&& git push -q -f origin feat/x')
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")

        result = self.helper("continue")
        self.assertEqual(8, result.returncode, result.stderr)
        self.assertIn("which is the point:\na.txt", result.stderr,
                      "the conflict after the dropped commit was not reported")
        self.assertEqual("rebase-merge", self.state(),
                         "the replay was not left in progress for the caller")

    def test_a_commit_an_untracked_file_blocks_is_applied_once_it_is_moved(self):
        # The case the apply backend lost a whole commit in: a file standing
        # where the next commit adds one. Refused, named, and then applied.
        self.at('echo added > new.txt && git add -A && git commit -qm "the branch adds new.txt" '
                '&& git push -q -f origin feat/x')
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo resolved > a.txt && git add a.txt && echo stray > new.txt")

        blocked = self.helper("continue")
        self.assertEqual(14, blocked.returncode, blocked.stderr)
        self.assertIn("stopped with nothing unmerged", blocked.stderr)
        self.assertIn("new.txt", blocked.stderr, "the caller is told what is in the way")
        self.assertEqual("stray\n", self.at("cat new.txt").stdout, "the untracked file was overwritten")

        self.at("rm new.txt")
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("added\n", self.at("cat new.txt").stdout,
                         "the commit the file blocked was dropped rather than applied")
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(),
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())

    def test_unstaged_work_survives_the_continue(self):
        # Nothing here resets the tree, so an edit made while resolving is
        # still there whatever the run then says.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        self.at("echo precious >> b.txt")
        self.helper("continue")
        self.assertEqual("work\nprecious\n", self.at("cat b.txt").stdout,
                         "the unstaged edit was reset away")


class AStoppedReplayIsNotAlwaysAConflict(unittest.TestCase):
    """A replay can stop with nothing unmerged and a real change staged — a
    commit git would not make for a reason of its own. It is reported as
    itself and left in progress, because nothing here may drop a commit."""

    def setUp(self):
        self.root = copy_template(self, template())
        self.work = self.root + "/work"
        self.at(CONFLICT)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def test_a_replay_that_stopped_for_another_reason_is_reported_as_itself(self):
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo different > a.txt && git add a.txt")
        # An identity git will not commit under.
        self.at('git config user.name "" && git config user.email ""')

        result = self.helper("continue")
        self.assertEqual(14, result.returncode, result.stderr)
        self.assertIn("stopped with nothing unmerged", result.stderr)
        self.assertNotIn("resolve these", result.stderr,
                         "the caller is sent to resolve files that are named nowhere")
        self.assertEqual("yes", self.at(REBASE_RUNNING).stdout.strip(),
                         "the replay was thrown away rather than left to be answered")
        self.assertEqual("different\n", self.at("git show :a.txt").stdout,
                         "the staged change was dropped")

    def test_the_merge_backend_drops_an_empty_replay_on_its_own(self):
        # The reason no skip is needed: this backend drops an emptied commit
        # itself, without leaving a stop for the helper to judge.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("mine\n", self.at("cat a.txt").stdout)
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(),
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())


class TheTemplateOutlivesAModuleCleanup(unittest.TestCase):
    """`shard-harness-suite.py` runs each class as its own suite, so module cleanups run between two classes of one
    worker, and the next class must not be handed a template they removed."""

    def test_a_removed_template_is_built_again(self):
        first = template()
        self.assertTrue(Path(first, "work").is_dir(), "the control: the template holds its checkout")
        unittest.doModuleCleanups()
        self.assertFalse(Path(first).exists(), "the cleanup this case is about did not run")
        self.assertTrue(Path(template(), "work").is_dir())

    def test_a_copy_reaches_its_own_remote_and_not_the_templates(self):
        root = copy_template(self, template())
        url = run_bash('git -C "$W" remote get-url origin', W=f"{root}/work").stdout.strip()
        self.assertEqual(f"{root}/remote.git", url)

    def test_a_template_that_does_not_build_raises_rather_than_handing_over_nothing(self):
        with self.assertRaises(RuntimeError):
            fixture_template("false", "root")


if __name__ == "__main__":
    unittest.main()
