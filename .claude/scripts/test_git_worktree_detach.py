"""git-worktree-detach.sh and git-worktree-drop.sh: the sweep worktree they
accept.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import subprocess
import unittest
from pathlib import Path

from review_helpers import (
    COMMANDS,
    SCRIPTS,
    BASH,
    code_lines,
    setUpModule,
    temp_root,
    run_bash,
)


DETACH = SCRIPTS / "git-worktree-detach.sh"
DROP = SCRIPTS / "git-worktree-drop.sh"
# A modification time long past the sweep's day, in `touch -t`'s POSIX form, which GNU, BSD and MSYS all read.
OLD_TOUCH = 'touch -t 200001010000 "$P"'


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
            [BASH, str(DETACH)], capture_output=True, text=True, encoding="utf-8", cwd=str(SCRIPTS)
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("usage: git-worktree-detach.sh <commit-sha>", result.stderr)

        two_args = subprocess.run(
            [BASH, str(DETACH), "/tmp/anywhere", "0" * 40],
            capture_output=True,
            text=True, encoding="utf-8",
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
        top = temp_root(self)
        made = run_bash(
            'mkdir "$top/root" "$top/repo" && cd "$top/repo" && git init -q .'
            ' && git -c user.name=t -c user.email=t@example.invalid commit -q --allow-empty -m t',
            top=top,
        )
        self.assertEqual(0, made.returncode, made.stderr)
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
        # `-t`, which POSIX defines: `-d "2 days ago"` is GNU's, and BSD's `touch` on macOS refuses it.
        aged = run_bash(OLD_TOUCH, P=path)
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
        # git's refusal to remove a tree holding anything modified or untracked
        # is the guard, and a teardown it refused leaves the tree for inspection.
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


GIT_LEAVING_A_DIRECTORY = """\
git() {
  if [ "$1" = -c ] && [ "$3" = worktree ] && [ "$4" = remove ]; then
    command git -c "$2" worktree remove "$5" || return
    case "$SHIM_LEAVES" in
      directory) mkdir -p "$5" ;;
      file) mkdir -p "$5" && : > "$5/held" ;;
    esac
    return "$SHIM_EXIT"
  fi
  command git "$@"
}
"""

# A file at $F, past Windows' 260 characters once checked out, committed by its index alone, so nothing writes the deep
# path in the repository itself, then a sweep-shaped worktree at $P holding it.
DEEP = """
set -e
long() { git -c core.longpaths=true "$@"; }
blob=$(printf 'deep\\n' | git -C "$R" hash-object -w --stdin)
git -C "$R" update-index --add --cacheinfo "100644,$blob,$F"
git -C "$R" -c user.name=t -c user.email=t@example.invalid commit -q -m deep
long -C "$R" worktree add -q --detach "$P" HEAD
"""


class ARemovalGitLeftHalfDone(unittest.TestCase):
    """On Windows a process holding the tree fails git's delete of it, and git then drops the registration anyway.
    The helper's `set -e` then ended it before the lists beside the tree were reached, and nothing named the
    directory. A `git` function through BASH_ENV removes for real and then does that, since a shim first on PATH loses
    to the directories Git's bash launcher puts there.
    """

    def setUp(self):
        top = temp_root(self)
        made = run_bash(
            'mkdir "$top/root" "$top/repo" && cd "$top/repo" && git init -q .'
            ' && git -c user.name=t -c user.email=t@example.invalid commit -q --allow-empty -m t',
            top=top,
        )
        self.assertEqual(0, made.returncode, made.stderr)
        self.repo, self.root = f"{top}/repo", f"{top}/root"
        shim = Path(top, "git-shim.sh")
        shim.write_text(GIT_LEAVING_A_DIRECTORY, encoding="utf-8", newline="\n")
        self.shim = shim.as_posix()

    def sweep(self):
        """A sweep worktree with its slice list and mutation report beside it."""
        made = run_bash(
            'cd "$R" && bash "$DETACH" "$(git rev-parse HEAD)"', R=self.repo, DETACH=str(DETACH), TMPDIR=self.root)
        self.assertEqual(0, made.returncode, made.stderr)
        path = made.stdout.strip()
        beside = run_bash('mkdir "$P.slices" "$P.mutation" && : > "$P.slices/list" && : > "$P.mutation/report"', P=path)
        self.assertEqual(0, beside.returncode, beside.stderr)
        return path

    def drop(self, path, leaves, git_exit):
        return run_bash(
            'cd "$R" && bash "$DROP" "$P"', R=self.repo, DROP=str(DROP), P=path, TMPDIR=self.root,
            BASH_ENV=self.shim, SHIM_LEAVES=leaves, SHIM_EXIT=str(git_exit))

    def exists(self, path):
        return run_bash('[ -e "$P" ]', P=path).returncode == 0

    def registered(self, path):
        listed = run_bash('cd "$R" && git worktree list --porcelain', R=self.repo)
        self.assertEqual(0, listed.returncode, listed.stderr)
        return path.rsplit("/", 1)[-1] in listed.stdout

    def assert_the_lists_are_gone(self, path):
        self.assertFalse(self.exists(f"{path}.slices"), "the slice lists stayed")
        self.assertFalse(self.exists(f"{path}.mutation"), "the mutation report stayed")

    def test_an_empty_directory_git_left_goes_and_the_lists_go_with_it(self):
        # Whether git then exits non-zero, as a failed delete does, or zero.
        for git_exit in (255, 0):
            with self.subTest(git_exit=git_exit):
                path = self.sweep()
                result = self.drop(path, "directory", git_exit)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertFalse(self.exists(path), "the empty directory is still there")
                self.assert_the_lists_are_gone(path)
                self.assertFalse(self.registered(path))

    def test_a_directory_git_left_holding_something_is_named_and_the_lists_go(self):
        # Git has already unregistered it, so no later sweep's start can find it, and `rmdir` is all this helper will
        # delete it with: what a held tree still holds is left for the owner, by name.
        for git_exit in (255, 0):
            with self.subTest(git_exit=git_exit):
                path = self.sweep()
                self.addCleanup(run_bash, 'rm -rf "$P"', P=path)
                result = self.drop(path, "file", git_exit)
                self.assertEqual(4, result.returncode, result.stderr)
                self.assertIn("unregistered but not deleted", result.stderr)
                self.assertIn(path.rsplit("/", 1)[-1], result.stderr)
                self.assertTrue(self.exists(f"{path}/held"), "what the directory held was removed")
                self.assert_the_lists_are_gone(path)
                self.assertFalse(self.registered(path))

    def test_a_leftover_under_a_temp_root_inside_another_repository_is_still_a_leftover(self):
        # With no `.git` of its own the directory answers with whatever repository holds the temp root, by walking
        # up. That is not the worktree it was, so it is not git's refusal.
        self.assertEqual(0, run_bash('git init -q "$R"', R=self.root).returncode)
        path = self.sweep()
        self.addCleanup(run_bash, 'rm -rf "$P"', P=path)
        result = self.drop(path, "file", 255)
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("unregistered but not deleted", result.stderr)
        self.assert_the_lists_are_gone(path)

    def test_a_tree_git_deleted_before_it_failed_is_done_and_the_lists_go(self):
        path = self.sweep()
        result = self.drop(path, "nothing", 255)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.exists(path))
        self.assert_the_lists_are_gone(path)

    def test_a_worktree_holding_a_path_past_260_characters_is_removed(self):
        # Windows' limit: without long paths git refuses a worktree it cannot read, or fails to delete the deep file
        # and leaves the directory unregistered. Long paths are set off above every config file, which only the
        # helper's -c outranks. Only a Windows run can fail on the removal itself, so the spelling is held everywhere.
        path = f"{self.root}/secsweep-deep00"
        deep = "/".join(["d" * 60] * 5) + "/deep.txt"
        made = run_bash(DEEP, R=self.repo, P=path, F=deep)
        self.assertEqual(0, made.returncode, made.stderr)
        self.assertGreater(len(f"{path}/{deep}"), 260)
        result = run_bash(
            'cd "$R" && bash "$DROP" "$P"', R=self.repo, DROP=str(DROP), P=path, TMPDIR=self.root,
            GIT_CONFIG_COUNT="1", GIT_CONFIG_KEY_0="core.longpaths", GIT_CONFIG_VALUE_0="false")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.exists(path))
        self.assertFalse(self.registered(path))
        self.assertIn("git -c core.longpaths=true worktree remove",
                      "\n".join(code_lines(DROP.read_text(encoding="utf-8"))))

    def test_a_tree_git_refused_keeps_its_work_and_its_lists_and_git_says_why(self):
        # Untracked work is git's refusal: still registered, so nothing here deletes anything.
        path = self.sweep()
        self.assertEqual(0, run_bash(': > "$P/untracked.txt"', P=path).returncode)
        result = self.drop(path, "nothing", 0)
        self.assertNotEqual(0, result.returncode, result.stderr)
        self.assertNotEqual(4, result.returncode, result.stderr)
        self.assertIn("modified or untracked", result.stderr)
        for kept in ("/untracked.txt", ".slices/list", ".mutation/report"):
            self.assertTrue(self.exists(path + kept), kept)
        self.assertTrue(self.registered(path))


class ATrackedLinkIsRefusedBeforeAnythingIsMade(unittest.TestCase):
    """A git worktree preserves tracked symbolic links, so a link in the pinned
    commit is a path under `$work` whose target is not, and `Read`, `Grep` and
    `Glob` follow it. The sweep's path rule is about spelling and cannot see it,
    so the commit is asked before the worktree exists.
    """

    def setUp(self):
        top = temp_root(self)
        # The index records mode 120000 without any OS symlink privilege.
        made = run_bash(
            'mkdir "$top/root" "$top/repo" && cd "$top/repo" && git init -q . '
            '&& blob=$(printf %s ../../outside | git hash-object -w --stdin) '
            '&& git update-index --add --cacheinfo "120000,$blob,leak" '
            '&& git -c user.name=t -c user.email=t@example.invalid commit -q -m link '
            '&& git rm -q --cached leak '
            '&& git -c user.name=t -c user.email=t@example.invalid commit -q -m plain',
            top=top,
        )
        self.assertEqual(0, made.returncode, made.stderr)
        self.repo, self.root = f"{top}/repo", f"{top}/root"

    def detach(self, revision):
        return run_bash(
            'cd "$R" && bash "$DETACH" "$(git rev-parse "$REV")"',
            R=self.repo, DETACH=str(DETACH), TMPDIR=self.root, REV=revision,
        )

    def sweep_dirs(self):
        return run_bash('ls "$ROOT"', ROOT=self.root).stdout.split()

    def test_a_commit_carrying_a_link_is_refused_and_named(self):
        result = self.detach("HEAD~1")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("tracked symbolic links", result.stderr)
        self.assertIn("leak", result.stderr)
        self.assertEqual("", result.stdout)
        self.assertEqual([], self.sweep_dirs(), "a directory was made for a commit that was refused")

    def test_a_link_whose_name_holds_a_space_is_named_whole(self):
        made = run_bash(
            'cd "$R" && blob=$(printf %s ../../outside | git hash-object -w --stdin) '
            '&& git update-index --add --cacheinfo "120000,$blob,my link" '
            '&& git -c user.name=t -c user.email=t@example.invalid commit -q -m spaced',
            R=self.repo)
        self.assertEqual(0, made.returncode, made.stderr)
        result = self.detach("HEAD")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("my link", result.stderr)
        self.assertEqual([], self.sweep_dirs())

    def test_the_refusal_names_five_links_and_no_more(self):
        made = run_bash(
            'cd "$R" && blob=$(printf %s ../../outside | git hash-object -w --stdin) '
            '&& for n in lnk1 lnk2 lnk3 lnk4 lnk5 lnk6 lnk7 lnk8; do '
            'git update-index --add --cacheinfo "120000,$blob,$n"; done '
            '&& git -c user.name=t -c user.email=t@example.invalid commit -q -m many',
            R=self.repo)
        self.assertEqual(0, made.returncode, made.stderr)
        result = self.detach("HEAD")
        self.assertEqual(5, result.returncode, result.stderr)
        for named in ("lnk1", "lnk2", "lnk3", "lnk4", "lnk5"):
            self.assertIn(named, result.stderr)
        for past in ("lnk6", "lnk7", "lnk8"):
            self.assertNotIn(past, result.stderr)

    def test_the_same_repository_at_a_commit_without_the_link_is_detached(self):
        # The positive control: the refusal is the commit's, not the repository's.
        result = self.detach("HEAD")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len(self.sweep_dirs()))

    def test_a_refused_commit_drops_no_stale_worktree(self):
        old = self.detach("HEAD").stdout.strip()
        self.assertEqual(0, run_bash(OLD_TOUCH, P=old).returncode)
        result = self.detach("HEAD~1")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertEqual("yes", run_bash('[ -d "$P" ] && echo yes', P=old).stdout.strip(),
                         "a refused call dropped a worktree")

    def test_both_sweeps_state_the_half_that_is_closed(self):
        for name in ("security-sweep.md", "bug-sweep.md"):
            with self.subTest(command=name):
                text = " ".join((COMMANDS / name).read_text(encoding="utf-8").split())
                self.assertIn("`git-worktree-detach.sh` now refuses a", text)
                self.assertIn("tracked link", text)


if __name__ == "__main__":
    unittest.main()
