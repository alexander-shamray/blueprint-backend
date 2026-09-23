"""git-rebase-onto-main.sh: the only force push, and what bounds it.

A permission rule matches the text of a command, so it can pin a flag and
cannot pin a fact about the checkout. The guards that make this force push
safe are mostly the second kind, and the script's own header enumerates them;
this docstring does not. The script is the boundary and this suite is what
watches it. The first class reads the flags, the last reads this file, and the
rest run the thing against real repositories.
"""

import ast
import re
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    code_lines,
    setUpModule,
    run_bash as _run_bash,
)

HELPER = SCRIPTS / "git-rebase-onto-main.sh"


def run_bash(script, subject="", **env_extra):
    """`review_helpers.run_bash` with the caller's git configuration shut out.

    `rebase.backend` picks the state directory the helper has to find,
    `rebase.updateRefs` is a setting it spells a flag against, and
    `core.autocrlf` rewrites the files a conflict is staged from, so ambient
    configuration would decide what the fixtures reach. `/dev/null` is read by
    git, which spells it the same way on every runner.
    """
    return _run_bash(script, subject,
                     GIT_CONFIG_GLOBAL="/dev/null",
                     GIT_CONFIG_SYSTEM="/dev/null",
                     GIT_CONFIG_NOSYSTEM="1",
                     **env_extra)


# What can introduce a command, beyond the `VAR=value` run: a shell keyword, a
# negation, or one of the wrappers that takes a command as its argument.
# Stripped in a loop, because they stack — `if ! GIT_DIR=x git push …`.
COMMAND_LEAD = re.compile(
    r"^(?:\w+=\S*|then|else|elif|do|done|if|while|until|!|command|exec|time|eval)\s+")

# A command can begin after any of these. `{` and `}` are not in the class,
# because they also spell `"${branch}"` and splitting there cuts a command off
# before its flags. A brace group's braces are space-delimited, so those are
# matched on their own.
COMMAND_SEPARATOR = re.compile(r"[\n;&|()`]+|(?<=\s)\{(?=\s)|(?<=\s)\}")


def git_commands(source):
    """Every `git …` command in a shell script, however it is introduced.

    A scan of line starts sees only a command opening a physical line, and the
    script already has `GIT_EDITOR=true git rebase --continue` and same-line
    brace groups. So the text is cut where a command can begin, and the
    assignments and keywords in front of it are stripped. It does not parse
    shell; `test_the_scan_sees_what_a_line_scan_misses` is what it answers to.
    """
    commands = []
    for piece in COMMAND_SEPARATOR.split(source):
        piece = piece.strip()
        while COMMAND_LEAD.match(piece):
            piece = COMMAND_LEAD.sub("", piece, count=1)
        if piece.startswith("git "):
            commands.append(piece)
    return commands


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

# A push that fails after the replay has finished. A hook rather than a broken
# remote, because the fetch has to succeed for the replay to happen at all.
HOOKS = 'h="$(git rev-parse --git-path hooks)"; mkdir -p "$h"'
FAIL_PUSH = HOOKS + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-push"; chmod +x "$h/pre-push"'
ALLOW_PUSH = HOOKS + '; rm -f "$h/pre-push"'

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
            pushes, ['git push --force-with-lease="$branch:$lease" origin "$branch"'],
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
            if command.startswith("git push"):
                self.assertNotRegex(command, r'(?:^|\s)"?\+\S',
                                    "a `+` refspec forces without naming a flag")

    def test_the_scan_sees_what_a_line_scan_misses(self):
        """The scan's subject is the scan, not what it happened to find.

        Every line below is a second, unleased force push, and a scan of line
        starts sees none of the first six. The assertion is that the force
        push is among what the scan now sees with its flags still attached,
        because a scan that saw the command and cut it before `--force` would
        pass a count.
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
        # `-i` with an attached suffix, which GNU and BSD sed both take. Bare
        # `-i` is GNU-only: on macOS the rebase would never start and the case
        # would assert on the wrong refusal.
        self.at('GIT_SEQUENCE_EDITOR="sed -i.bak 1s/^pick/break/" '
                'git rebase -i refs/remotes/origin/main')
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

    def test_a_rebase_that_never_started_is_not_called_a_conflict(self):
        # Every other way `git rebase` can fail leaves no state, and reporting
        # it as a conflict sends the caller to a `continue` with nothing to do.
        self.at(HOOKS + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-rebase"; chmod +x "$h/pre-rebase"')
        result = self.helper("feat/x")
        self.assertEqual(11, result.returncode, result.stderr)
        self.assertIn("the rebase did not start", result.stderr)


class TheArgumentGuardsEachHaveTheirOwnCase(unittest.TestCase):
    """Exit 2 covers five conditions, so each case pins its message too.

    No fixture, and that is the property: these refuse before anything reads
    the checkout, so a guard that needed a repository would be one the
    session's location could switch off.
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


class AConflictIsTheCaseRebaseIsHereFor(unittest.TestCase):
    """The resolution belongs in the replayed commit, not in a merge commit.

    So `start` leaves a conflicted rebase in progress rather than aborting it:
    backing out would send the caller to the merge this repository stopped
    making, and the tree would stop being a line.
    """

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
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
        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("aborted;", result.stdout)

        self.assertEqual("no", self.rebase_running())
        self.assertEqual(before, self.at("git rev-parse HEAD").stdout.strip())
        self.assertEqual(remote_before, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip())


class ALegacyMergeForwardIsNotSilentlyDropped(unittest.TestCase):
    """A rebase drops merge commits, and a branch made under the old policy
    has one. Where that merge carries content neither parent has, replaying
    loses it before the push, which no lease can see."""

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
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
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
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

    def test_publish_refuses_a_head_that_is_not_the_one_replayed(self):
        # The retry republishes the replay, and a commit made after it is a
        # different branch.
        self.fail_the_push()
        published = self.remote_tip()
        self.at('echo extra > extra.txt && git add -A && git commit -qm "after the replay"')
        self.at(ALLOW_PUSH)

        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("not the commit this helper replayed", result.stderr)
        self.assertEqual(published, self.remote_tip(),
                         "the commit made after the replay was not forced over it")

    def test_publish_refuses_a_record_naming_another_branch(self):
        self.fail_the_push()
        result = self.helper("publish", branch="feat/other")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("the waiting replay is feat/x, not feat/other", result.stderr)

    def test_publish_refuses_a_remote_that_moved_since_the_lease(self):
        self.fail_the_push()
        self.at("git update-ref refs/remotes/origin/feat/x refs/remotes/origin/main")
        result = self.helper("publish")
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertIn("has moved since the replay was approved", result.stderr)

    def test_publish_refuses_a_replay_that_ended_on_no_branch(self):
        # `publish()`'s own branch check, reached past every guard in the mode
        # above it. Its other refusal, an empty lease, has no case: no granted
        # mode can write a record carrying a head but no lease.
        self.fail_the_push()
        self.at("git checkout -q --detach HEAD")
        result = self.helper("publish")
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("the replay ended on a detached HEAD", result.stderr)

    def test_an_unreadable_waiting_record_is_refused(self):
        # Refused with a message and an assigned code, in the path that exists
        # to recover from a failed run.
        self.fail_the_push()
        self.at('printf "\\n" > "$(git rev-parse --git-path claude-rebase-pending)"')
        result = self.helper("publish")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("the waiting record is unreadable", result.stderr)

    def test_a_rebase_that_never_started_leaves_no_record(self):
        # Nothing was replayed, so there is no tip for a record to protect,
        # and one left behind refuses every later `start` on any branch.
        self.at(HOOKS + '; printf "#!/bin/sh\\nexit 1\\n" > "$h/pre-rebase"; chmod +x "$h/pre-rebase"')
        self.assertEqual(11, self.helper().returncode, "the rebase must not start")
        self.assertEqual(
            "", self.at('cat "$(git rev-parse --git-path claude-rebase-pending)" 2>/dev/null').stdout,
            "a record was left for a replay that never happened")
        self.at(HOOKS + '; rm -f "$h/pre-rebase"')
        self.assertEqual(0, self.helper().returncode, "a stale record refused the next start")

    def test_abort_refuses_a_replay_that_publish_can_still_finish(self):
        # Clearing it leaves the branch rewritten with nothing able to reach it.
        self.fail_the_push()
        rewritten = self.at("git rev-parse HEAD").stdout.strip()
        result = self.helper("abort")
        self.assertEqual(9, result.returncode, result.stderr)
        self.assertIn("refusing to strand it", result.stderr)

        self.at(ALLOW_PUSH)
        self.assertEqual(0, self.helper("publish").returncode)
        self.assertEqual(rewritten, self.remote_tip(),
                         "publish finished the replay the abort refused to discard")

    def test_abort_clears_a_record_publish_can_no_longer_finish(self):
        # `publish` sends a moved remote to `abort`, so `abort` has to take it
        # or each mode names the other and the record is reachable by neither.
        self.fail_the_push()
        self.at("git update-ref refs/remotes/origin/feat/x refs/remotes/origin/main")
        self.assertEqual(7, self.helper("publish").returncode)
        result = self.helper("abort")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("cleared the waiting replay", result.stdout)

    def test_abort_clears_a_record_whose_remote_ref_is_gone(self):
        # A missing remote-tracking ref is a dead lease, asked as absence
        # rather than inferred from a failed read.
        self.fail_the_push()
        self.at("git update-ref -d refs/remotes/origin/feat/x")
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


class TheApplyBackendIsDrivenToo(unittest.TestCase):
    """`rebase.backend=apply` is the caller's setting, and the helper finds
    `rebase-apply` beside `rebase-merge` for it.

    It is also the only backend that reaches the empty-replay path: the merge
    backend drops such a commit itself and says nothing.
    """

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
        self.work = self.root + "/work"
        self.at("git config rebase.backend apply")
        self.at(CONFLICT)

    def at(self, script):
        return run_bash('cd "$W" && ' + script, W=self.work)

    def helper(self, mode="start", branch="feat/x"):
        return run_bash('cd "$W" && bash "$H" "$B" "$M"',
                        W=self.work, H=str(HELPER), B=branch, M=mode)

    def apply_running(self):
        return self.at('test -d "$(git rev-parse --git-path rebase-apply)" '
                       '&& echo yes || echo no').stdout.strip()

    def test_the_state_this_backend_leaves_is_the_one_the_helper_finds(self):
        result = self.helper("start")
        self.assertEqual(8, result.returncode, result.stderr)
        self.assertIn("which is the point:\na.txt", result.stderr)
        self.assertEqual("yes", self.apply_running(),
                         "this case is not driving the apply backend at all")
        self.at("echo resolved > a.txt && git add a.txt")
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)

    def test_an_empty_resolution_is_dropped_rather_than_called_a_conflict(self):
        # Taking origin/main's side verbatim leaves nothing to commit, and git
        # asks for `--skip`, which nothing grants a caller.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        result = self.helper("continue")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("left this commit of feat/x empty", result.stderr)
        self.assertEqual("no", self.apply_running(), "the replay is still wedged in progress")
        self.assertEqual("mine\n", self.at("cat a.txt").stdout)
        self.assertEqual("work\n", self.at("cat b.txt").stdout,
                         "the branch's own commit was dropped along with the empty one")
        self.assertEqual(self.at("git rev-parse HEAD").stdout.strip(),
                         self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the remote carries what was replayed")

    def test_a_conflict_after_the_drop_is_reported_rather_than_skipped_too(self):
        # A second commit on the same file, so dropping the first as empty
        # puts a conflicting one into the replay; the pass after a skip has
        # to answer as the first one did.
        self.at('echo theirs-again > a.txt && git add -A '
                '&& git commit -qm "the branch edits a.txt again" '
                '&& git push -q -f origin feat/x')
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")

        result = self.helper("continue")
        self.assertEqual(8, result.returncode, result.stderr)
        self.assertIn("left this commit of feat/x empty", result.stderr)
        self.assertIn("which is the point:\na.txt", result.stderr,
                      "the conflict after the drop was skipped instead of reported")
        self.assertEqual("yes", self.apply_running(),
                         "the replay was not left in progress for the caller")

    def test_unstaged_work_is_not_discarded_by_the_drop(self):
        # `--continue` refuses over an unstaged edit and leaves the index at
        # HEAD, so an emptiness test of the index alone passes, and the skip
        # would hard-reset the edit away and publish without it.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        self.at("echo precious >> b.txt")
        before = self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip()
        self.assertTrue(before, "the remote tip was not read")

        result = self.helper("continue")
        self.assertEqual(14, result.returncode, result.stderr)
        self.assertIn("unstaged changes in the tree", result.stderr)
        self.assertIn("b.txt", result.stderr, "the caller is told what is in the way")
        self.assertEqual("work\nprecious\n", self.at("cat b.txt").stdout,
                         "the unstaged edit was hard-reset away by the skip")
        self.assertEqual(before, self.at("git rev-parse refs/remotes/origin/feat/x").stdout.strip(),
                         "the branch was published without the work still in the tree")


class AStoppedReplayIsNotAlwaysAConflict(unittest.TestCase):
    """A replay can stop with nothing unmerged and a real change staged — a
    commit git would not make for a reason of its own. It is reported as
    itself rather than skipped, because dropping a commit that holds content
    is the one outcome the empty-replay path must never reach."""

    def setUp(self):
        self.root = run_bash(FIXTURE).stdout.strip()
        self.assertTrue(self.root, "the fixture produced no path")
        self.addCleanup(lambda: run_bash('rm -rf "$R"', R=self.root))
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
        self.assertIn("nothing unmerged and a change still staged", result.stderr)
        self.assertNotIn("resolve these", result.stderr,
                         "the caller is sent to resolve files that are named nowhere")
        self.assertEqual("yes", self.at(REBASE_RUNNING).stdout.strip(),
                         "the replay was thrown away rather than left to be answered")
        self.assertEqual("different\n", self.at("git show :a.txt").stdout,
                         "the staged change was dropped by a skip that must not have run")

    def test_the_merge_backend_drops_an_empty_replay_on_its_own(self):
        # Why the apply backend is where the empty-replay path lives: this
        # one never asks, so the helper's skip must not run here.
        self.assertEqual(8, self.helper("start").returncode)
        self.at("echo mine > a.txt && git add a.txt")
        result = self.helper("continue")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("mine\n", self.at("cat a.txt").stdout)
        self.assertNotIn("left this commit", result.stderr,
                         "the helper's skip path ran; this backend drops it itself")


class EveryFixtureSaysItWasBuilt(unittest.TestCase):
    """With a failed fixture `self.root` is "" and `self.work` is "/work", so
    every `cd` fails and the non-zero and empty-output assertions are
    satisfied by the failed `cd` rather than by the state they name.

    Read from the AST, not as text: a comment is not in it, and an assertion
    inside a lambda or a branch is not a top-level statement of `setUp`.
    """

    @staticmethod
    def builds_the_fixture(function):
        return any(isinstance(node, ast.Name) and node.id == "FIXTURE"
                   for node in ast.walk(function))

    @staticmethod
    def asserts_its_root(function):
        for statement in function.body:
            if not isinstance(statement, ast.Expr):
                continue
            call = statement.value
            if not (isinstance(call, ast.Call)
                    and isinstance(call.func, ast.Attribute)
                    and call.func.attr == "assertTrue"
                    and isinstance(call.func.value, ast.Name)
                    and call.func.value.id == "self"):
                continue
            subject = call.args[0] if call.args else None
            if (isinstance(subject, ast.Attribute)
                    and subject.attr == "root"
                    and isinstance(subject.value, ast.Name)
                    and subject.value.id == "self"):
                return True
        return False

    def test_every_class_that_builds_the_fixture_says_it_was_built(self):
        watched = []
        for node in ast.parse(Path(__file__).read_text(encoding="utf-8")).body:
            if not isinstance(node, ast.ClassDef):
                continue
            for member in node.body:
                if not isinstance(member, ast.FunctionDef) or member.name != "setUp":
                    continue
                if not self.builds_the_fixture(member):
                    continue
                watched.append(node.name)
                self.assertTrue(
                    self.asserts_its_root(member),
                    f"{node.name} builds the fixture and never says whether it "
                    "worked: a failed one makes self.work '/work' and every "
                    "assertion under it vacuous")
        # Exact, so a class the recogniser sees cannot arrive without this
        # number moving in the same edit, which is the moment to check it.
        self.assertEqual(6, len(watched),
                         f"this gate is watching {watched}: a fixture class was "
                         "added or renamed. Check that each one asserts its root, "
                         "then move this number deliberately")


if __name__ == "__main__":
    unittest.main()
