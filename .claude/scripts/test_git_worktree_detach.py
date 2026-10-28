"""git-worktree-detach.sh and git-worktree-drop.sh: the sweep worktree they
accept.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import subprocess
import unittest

from review_helpers import (
    SCRIPTS,
    BASH,
    setUpModule,
    run_bash,
)


DETACH = SCRIPTS / "git-worktree-detach.sh"
DROP = SCRIPTS / "git-worktree-drop.sh"


def bash_tmproot():
    """The temp root as BASH resolves it, which is not the one Python resolves.

    Under MSYS `/tmp` can be one directory for the shell and another for Python,
    so a fixture made with `pathlib` and handed to a shell script is a directory
    the script cannot see. The fixtures below are made by bash too.
    """
    return run_bash('cd "${TMPDIR:-/tmp}" && pwd -P').stdout.strip()


class SweepWorktreeShape(unittest.TestCase):
    """The sweep worktree check admits a direct child of the temp root only.

    A bash `case` does no pathname expansion, so `?` matches `/` and a pattern
    alone admits `$tmproot/secsweep-a/bbbb`.
    """

    def setUp(self):
        self.tmproot = bash_tmproot()

    def make_dir(self, relative):
        """Create the fixture through bash, so it lands where the script looks."""
        path = f"{self.tmproot}/{relative}"
        made = run_bash('mkdir -p "$TARGET"', TARGET=path)
        self.assertEqual(0, made.returncode, made.stderr)
        top = f"{self.tmproot}/{relative.split('/')[0]}"
        self.addCleanup(lambda: run_bash('rm -rf "$TARGET"', TARGET=top))
        return path

    def drop(self, path):
        return run_bash('bash "$DROP" "$TARGET"', DROP=str(DROP), TARGET=path)

    def test_a_nested_path_under_a_sweep_shaped_parent_is_refused(self):
        # Both match a `?`-pattern, because `?` matches `/` in a bash `case`.
        for nested in ("secsweep-a/bbbb", "secsweep-ab/ccc"):
            with self.subTest(nested=nested):
                path = self.make_dir(nested)
                result = self.drop(path)
                self.assertEqual(2, result.returncode)
                self.assertIn("direct child", result.stderr)

    def test_the_prefix_and_length_controls_still_refuse(self):
        for name in ("secsweep-abc12", "other-abc123", "secsweep-abc1234"):
            with self.subTest(name=name):
                path = self.make_dir(name)
                result = self.drop(path)
                self.assertEqual(2, result.returncode)
                self.assertIn("sweep-shaped", result.stderr)

    def test_the_detach_helper_takes_a_commit_and_nothing_else(self):
        # The path is made here, so no caller supplies one and neither sweep
        # needs a `Bash(mktemp:*)` grant, which takes an arbitrary template and
        # is a filesystem-write primitive.
        result = subprocess.run(
            [BASH, str(DETACH)], capture_output=True, text=True, cwd=str(SCRIPTS)
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("usage: git-worktree-detach.sh <commit-sha>", result.stderr)

        two_args = subprocess.run(
            [BASH, str(DETACH), "/tmp/anywhere", "0" * 40],
            capture_output=True,
            text=True,
            cwd=str(SCRIPTS),
        )
        self.assertEqual(2, two_args.returncode)

    def sweep_dirs(self):
        listing = run_bash('ls -d "$ROOT"/secsweep-* 2>/dev/null || true', ROOT=self.tmproot)
        return set(listing.stdout.split())

    def test_a_bad_commit_creates_no_directory(self):
        # The argument is checked before anything is made, so a refused call
        # leaves no litter behind for the next run to trip over.
        before = self.sweep_dirs()
        result = run_bash('bash "$DETACH" not-a-sha', DETACH=str(DETACH))
        self.assertEqual(2, result.returncode)
        self.assertEqual(before, self.sweep_dirs())

    def test_the_helper_returns_only_the_path_on_stdout(self):
        # `git worktree add` writes "HEAD is now at <sha> <subject>" to stdout,
        # so an unredirected call makes the return value a commit message
        # followed by a path. This runs the real round trip rather than grepping
        # the source, because any command leaking a line does the same:
        # detach at a real commit, capture stdout as `security-sweep.md` does,
        # and assert it is one line and a directory.
        repo = str(SCRIPTS.parent.parent)
        pinned = run_bash('cd "$REPO" && git rev-parse HEAD', REPO=repo).stdout.strip()
        self.assertRegex(pinned, r"^[0-9a-f]{40}$")

        made = run_bash(
            'cd "$REPO" && bash "$DETACH" "$SHA" 2>/dev/null',
            REPO=repo, DETACH=str(DETACH), SHA=pinned,
        )
        path = made.stdout.strip()
        # Registered before anything is asserted, so a failing assertion below
        # still tears the worktree down rather than leaving one for the next run.
        self.addCleanup(
            lambda: run_bash(
                'cd "$REPO" && bash "$DROP" "$TARGET" >/dev/null 2>&1 || true',
                REPO=repo, DROP=str(DROP), TARGET=path,
            )
        )
        self.assertEqual(0, made.returncode, made.stderr)
        self.assertEqual(
            path + "\n", made.stdout,
            "stdout must be the path and nothing else — no banner, no commit subject",
        )
        self.assertEqual(1, len(made.stdout.strip().split("\n")))
        self.assertEqual(
            "yes",
            run_bash('[ -d "$P" ] && echo yes || echo no', P=path).stdout.strip(),
            "the returned string must be usable as a directory by the caller",
        )
        # And it is the shape the drop helper will later require, which is the
        # agreement between the two ends of a sweep's lifetime.
        self.assertEqual(self.tmproot, run_bash('dirname "$P"', P=path).stdout.strip())
        self.assertRegex(run_bash('basename "$P"', P=path).stdout.strip(), r"^secsweep-.{6}$")


class StaleSweepWorktrees(unittest.TestCase):
    """A sweep's start drops the sweep worktrees interrupted sweeps left.

    A repository and a temp root of their own, so the drop can reach nothing of
    this checkout's, and no sweep another session is running.
    """

    def setUp(self):
        made = run_bash(
            'top=$(mktemp -d) && mkdir "$top/root" "$top/repo" && cd "$top/repo" && git init -q .'
            ' && git -c user.name=t -c user.email=t@example.invalid commit -q --allow-empty -m t'
            ' && echo "$top"'
        )
        self.assertEqual(0, made.returncode, made.stderr)
        top = made.stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$TOP"', TOP=top))
        self.repo, self.root = f"{top}/repo", f"{top}/root"

    def detach(self):
        return run_bash(
            'cd "$R" && bash "$DETACH" "$(git rev-parse HEAD)"',
            R=self.repo, DETACH=str(DETACH), TMPDIR=self.root,
        )

    def made(self):
        result = self.detach()
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def age(self, path):
        aged = run_bash('touch -d "2 days ago" "$P"', P=path)
        self.assertEqual(0, aged.returncode, aged.stderr)

    def names(self):
        listing = run_bash('cd "$R" && git worktree list --porcelain', R=self.repo)
        self.assertEqual(0, listing.returncode, listing.stderr)
        return {line.rsplit("/", 1)[-1] for line in listing.stdout.splitlines() if line.startswith("worktree ")}

    def test_a_day_old_sweep_worktree_goes_and_a_fresh_one_stays(self):
        old, fresh = self.made(), self.made()
        self.age(old)
        result = self.detach()
        self.assertEqual(0, result.returncode, result.stderr)
        new = result.stdout.strip()
        self.assertEqual(1, len(result.stdout.splitlines()), "stdout is still the path alone")
        names = self.names()
        self.assertNotIn(old.rsplit("/", 1)[-1], names)
        self.assertIn(fresh.rsplit("/", 1)[-1], names)
        self.assertIn(new.rsplit("/", 1)[-1], names)
        self.assertEqual("gone", run_bash('[ -e "$P" ] || echo gone', P=old).stdout.strip())

    def test_a_stale_worktree_holding_a_file_is_left_standing_and_named(self):
        # git's refusal to remove a tree holding anything is the guard, and a
        # sweep that refused its own teardown left it there for inspection.
        old = self.made()
        self.assertEqual(0, run_bash('echo x > "$P/kept.txt"', P=old).returncode)
        self.age(old)
        result = self.detach()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("left a stale sweep worktree standing", result.stderr)
        self.assertIn(old.rsplit("/", 1)[-1], self.names())

    def test_a_registration_whose_directory_went_is_pruned(self):
        old = self.made()
        self.assertEqual(0, run_bash('rm -rf "$P"', P=old).returncode)
        self.made()
        self.assertNotIn(old.rsplit("/", 1)[-1], self.names())


if __name__ == "__main__":
    unittest.main()
