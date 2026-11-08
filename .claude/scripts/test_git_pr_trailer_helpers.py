"""git-trailer-pr.sh and git-revert-pr.sh against real repositories, a bare remote and a checkout.
`gh` is a shim answering the one read the first helper makes. Each refusal is paired with a run
that succeeds on the same fixture, so a refusal is not a helper failing for another reason.
"""

import os
import re
import tempfile
import unittest
from pathlib import Path

from review_helpers import SCRIPTS, code_lines, git_commands, run_bash, setUpModule, temp_root  # noqa: F401

TRAILER = SCRIPTS / "git-trailer-pr.sh"
REVERT = SCRIPTS / "git-revert-pr.sh"

BUILD = """
set -eu
git init -q --bare --initial-branch=main "$ROOT/remote.git"
git init -q --initial-branch=main "$ROOT/work"
cd "$ROOT/work"
git config user.email test@example.invalid
git config user.name Test
git remote add origin "$ROOT/remote.git"
echo base > a.txt && git add -A && git commit -qm "chore: the base"
git push -q -u origin main
"""

# A commit with a body and the trailers /commit keeps, so the new trailer has to land beside them.
COMMIT = """
cd "$WORK"
echo "$2" >> "$1.txt" && git add -A
printf '%s\\n\\nThe body.\\n\\nCo-Authored-By: C <c@example.invalid>\\n%s' "$3" "$4" | git commit -q -F -
"""


FAILING_REBASE = """\
sleep() {
  if [ "$1" = 1 ]; then
    echo sleep >> "$SHIM_TRACE"
  else
    # The watchdog backgrounds this and kills its pid, which `exec` keeps the real sleep's.
    exec sleep "$@"
  fi
}
git() {
  if [ "$1" = rebase ] && [ "$2" = --abort ]; then
    echo abort >> "$SHIM_TRACE"
    [ -z "$SHIM_STUCK" ] || return 1
  fi
  if [ "$1" = rebase ] && [ "$2" != --abort ]; then
    n=$(cat "$SHIM_COUNT" 2>/dev/null || echo 0)
    echo $((n + 1)) > "$SHIM_COUNT"
    if [ "$n" -lt "$SHIM_FAILS" ]; then
      if [ -n "$SHIM_STUCK" ]; then
        mkdir "$(command git rev-parse --git-path rebase-merge)"
        echo "fatal: Unable to create '/x/.git/index.lock': File exists." >&2
        return 1
      fi
      if [ -n "$SHIM_MESSAGE" ]; then
        echo "$SHIM_MESSAGE" >&2
        return 1
      fi
      lock=$(command git rev-parse --git-path index.lock)
      : > "$lock"
      command git "$@"
      status=$?
      rm -f "$lock"
      return "$status"
    fi
  fi
  command git "$@"
}
"""


def sh(script, **env):
    result = run_bash(
        "set -eu\n" + script,
        GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null", GIT_CONFIG_NOSYSTEM="1", **env)
    return result


class Fixture(unittest.TestCase):
    def setUp(self):
        self.root = temp_root(self)
        built = run_bash(
            BUILD, ROOT=self.root, GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null", GIT_CONFIG_NOSYSTEM="1")
        self.assertEqual(0, built.returncode, built.stderr)
        self.work = f"{self.root}/work"
        stub = tempfile.TemporaryDirectory()
        self.addCleanup(stub.cleanup)
        shim = stub.name
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
        Path(self.work, "stray.txt").write_text("x\n", encoding="utf-8")

    def branch(self, name="feat/x"):
        self.git("checkout", "-q", "-b", name)

    def run_helper(self, helper, pr="7", row="OPEN\tfeat/x\tmain\tfalse", **env):
        return sh(
            f'cd "$WORK"\nbash "$HELPER" "$PR"',
            WORK=self.work, HELPER=str(helper), PR=pr, GH_ROW=row,
            PATH=self.shim + os.pathsep + os.environ["PATH"], **env)

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
        # The remote refuses a non-fast-forward, as a setting of its own: a `pre-receive` hook there would be
        # ignored, since the helper's empty hooks path is inherited by the receiving git on a local remote.
        deny = 'git --git-dir="$ROOT/remote.git" config receive.denyNonFastForwards true'
        self.assertEqual(0, sh(deny, ROOT=self.root).returncode)
        failed = self.run_helper(TRAILER)
        self.assertEqual(7, failed.returncode, failed.stderr)
        self.assertNotEqual(self.git('rev-parse', 'HEAD'), self.git('rev-parse', 'origin/feat/x'))
        self.assertEqual(0, sh(deny.replace(" true", " false"), ROOT=self.root).returncode)
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

    def failing_rebase(self, fails, message="", stuck=""):
        """The environment that fails the helper's first `fails` rebases and counts them all: a real `index.lock`
        held for the attempt, as a concurrent git reader holds it, or `message` where one is given, or with `stuck` a
        rewrite stopped midway whose abort fails too. A shell function through BASH_ENV, because the helper runs none
        of the repository's hooks and a `git` first on PATH loses to the one Git's bash launcher puts there."""
        shim = Path(self.root, "git-shim.sh")
        shim.write_text(FAILING_REBASE, encoding="utf-8", newline="\n")
        return {"BASH_ENV": shim.as_posix(), "SHIM_COUNT": f"{self.root}/count", "SHIM_FAILS": str(fails),
                "SHIM_MESSAGE": message, "SHIM_STUCK": stuck, "SHIM_TRACE": f"{self.root}/trace"}

    def rebases_run(self):
        return int(Path(self.root, "count").read_text(encoding="utf-8").strip())

    def trace(self):
        return Path(self.root, "trace").read_text(encoding="utf-8").split()

    def test_the_wait_comes_before_the_abort_which_wants_the_same_lock(self):
        # Also over gh-read-bound.sh's watchdog, which sleeps too, and which a runner with no `timeout` takes.
        for program in ("timeout", "no-such-timeout"):
            with self.subTest(program=program):
                Path(self.root, "trace").unlink(missing_ok=True)
                Path(self.root, "count").unlink(missing_ok=True)
                self.git("checkout", "-q", "main")
                self.branch(f"feat/{program}")
                self.commit("one", f"feat(x): {program}")
                out = self.run_helper(TRAILER, row=f"OPEN\tfeat/{program}\tmain\tfalse", GH_READ_TIMEOUT=program,
                                      **self.failing_rebase(1))
                self.assertEqual(0, out.returncode, out.stderr)
                self.assertEqual(["sleep", "abort"], self.trace())

    def test_an_abort_that_fails_too_is_not_called_the_branch_as_it_was(self):
        self.two_commit_branch()
        out = self.run_helper(TRAILER, **self.failing_rebase(99, stuck="1"))
        self.assertEqual(6, out.returncode, out.stderr)
        self.assertIn("the abort did too", out.stderr)
        self.assertNotIn("the branch is as it was", out.stderr)
        self.assertEqual(1, self.rebases_run(), "a retry over a rewrite still in progress")

    def test_a_single_index_lock_failure_is_retried_once_and_succeeds(self):
        self.two_commit_branch()
        out = self.run_helper(TRAILER, **self.failing_rebase(1))
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual(["docs: two|#7", "feat(x): one|#7"], self.subjects_and_trailers("origin/main..HEAD"))
        self.assertEqual(2, self.rebases_run())

    def test_a_persistent_index_lock_failure_stops_after_two_attempts_with_the_branch_as_it_was(self):
        self.two_commit_branch()
        before = self.git("rev-parse", "HEAD")
        out = self.run_helper(TRAILER, **self.failing_rebase(99))
        self.assertEqual(6, out.returncode, out.stderr)
        self.assertIn("index.lock", out.stderr)
        self.assertEqual(2, self.rebases_run())
        self.assertEqual(before, self.git("rev-parse", "HEAD"))

    def test_a_failure_that_is_not_a_lock_is_not_retried(self):
        self.two_commit_branch()
        out = self.run_helper(TRAILER, **self.failing_rebase(99, "boom"))
        self.assertEqual(6, out.returncode, out.stderr)
        self.assertIn("boom", out.stderr)
        self.assertEqual(1, self.rebases_run())

    def test_the_repositorys_own_hooks_do_not_run_inside_the_rewrite(self):
        # A `post-rewrite` hook in the repository runs after an amend or a rebase, and this helper rewrites published
        # history, so it points `core.hooksPath` at an empty directory for the run. The positive control comes first:
        # the same hook does fire on a plain amend in this fixture, so its silence afterwards is the helper's doing.
        self.two_commit_branch()
        hook = '"$WORK/.git/hooks/post-rewrite"'
        marker = '"$ROOT/hook-ran"'
        made = sh(f'printf "#!/bin/sh\\ntouch {marker}\\n" > {hook} && chmod +x {hook}', WORK=self.work, ROOT=self.root)
        self.assertEqual(0, made.returncode, made.stderr)
        self.git("commit", "-q", "--amend", "--no-edit")
        self.assertEqual(0, sh(f'test -e {marker}', ROOT=self.root).returncode, "the control hook never fired")
        self.assertEqual(0, sh(f'rm {marker}', ROOT=self.root).returncode)
        self.git("push", "-q", "--force", "origin", "HEAD")
        out = self.run_helper(TRAILER)
        self.assertEqual(0, out.returncode, out.stderr)
        self.assertEqual(1, sh(f'test -e {marker}', ROOT=self.root).returncode, "the host's hook ran inside the helper")

    def test_the_hooks_path_is_set_before_the_first_command_that_can_run_a_hook(self):
        code = chr(10).join(code_lines(TRAILER.read_text(encoding="utf-8")))
        export = code.find("GIT_CONFIG_KEY_")
        self.assertGreaterEqual(export, 0, "the hooks path is not set")
        self.assertIn("core.hooksPath", code)
        self.assertRegex(code, r"mktemp\s+-d", "the path is an empty directory made for the run")
        for command in ("git status", "git fetch", "git rebase", "git push"):
            with self.subTest(command=command):
                self.assertLess(export, code.find(command))

    @staticmethod
    def unleased(source):
        """Why each `git push` in the script is not a leased update, whatever else sits between its words."""
        found = []
        for command in git_commands("\n".join(code_lines(source))):
            if not command.startswith("git push"):
                continue
            tokens = command.split()
            if not any(t.startswith("--force-with-lease=") for t in tokens):
                found.append(f"no lease: {command}")
            found += [f"{t!r} forces without a lease: {command}" for t in tokens
                      if t in ("--force", "--force-if-includes") or t.startswith("--force=")
                      or re.fullmatch(r"-[A-Za-z]*f[A-Za-z]*", t) or re.match(r'''["']?\+''', t)]
        return found

    def test_the_helper_spells_its_rewrite_and_its_lease_itself(self):
        source = TRAILER.read_text(encoding="utf-8")
        self.assertEqual([], self.unleased(source))
        pushes = [c for c in git_commands("\n".join(code_lines(source))) if c.startswith("git push")]
        self.assertEqual(1, len(pushes), "one push, and it names its lease on the branch and the remote")
        self.assertIn('--force-with-lease="refs/heads/$branch:$remote"', pushes[0])

    def test_every_replay_spells_the_flags_that_decide_what_is_replayed(self):
        # Each `rebase.*` key behind these flags changes which commits are replayed, and the result is
        # force-pushed, so an inherited one is a rewrite nobody asked for.
        code = chr(10).join(code_lines(TRAILER.read_text(encoding="utf-8")))
        replays = [c for c in git_commands(code) if c.startswith("git rebase") and "--abort" not in c]
        self.assertEqual(1, len(replays), replays)
        for flag in ("--no-rebase-merges", "--no-update-refs", "--no-autosquash", "--no-fork-point",
                     "--no-autostash"):
            with self.subTest(flag=flag):
                self.assertIn(flag, replays[0])

    def test_the_scan_sees_an_unleased_force_in_the_helpers_own_shape(self):
        # A flag between `push` and `--force` must not hide the force, with a lease beside it or without.
        lease = '--force-with-lease="refs/heads/$branch:$remote"'
        for line in ('git push --quiet --force origin "HEAD:refs/heads/$branch"',
                     'git push -q -f origin "HEAD:refs/heads/$branch"',
                     'git push --quiet origin "HEAD:refs/heads/$branch"',
                     'timeout 60 git push --quiet --force origin "$branch"',
                     f'git push --quiet {lease} --force origin "HEAD:refs/heads/$branch"',
                     f'git push --quiet {lease} origin "+HEAD:refs/heads/$branch"',
                     f'git push -qf {lease} origin "HEAD:refs/heads/$branch"',
                     f"git push --quiet {lease} origin '+HEAD:refs/heads/$branch'",
                     f'git push --quiet {lease} origin ' + chr(92) + chr(10) + ' --force "HEAD:refs/heads/$branch"'):
            with self.subTest(line=line):
                source = TRAILER.read_text(encoding="utf-8") + chr(10) + line
                self.assertNotEqual([], self.unleased(source))
                if lease in line:
                    self.assertTrue(any("forces without a lease" in why for why in self.unleased(source)))


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
