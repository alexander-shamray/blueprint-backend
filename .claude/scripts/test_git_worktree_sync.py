"""git-worktree-sync.sh: the one worktree /ship opens on an existing sync branch.

The shared harness is `review_helpers.py`'s. The fixture is a bare origin, as
`test_git_worktree_fork.py`'s is, because the helper fetches from it.
"""

import unittest

from review_helpers import (
    SCRIPTS,
    code_lines,
    setUpModule,
    temp_root,
    run_bash,
)


SYNC = SCRIPTS / "git-worktree-sync.sh"
BRANCH = "chore/harness-sync-abcdef0"
PATH = ".claude/worktrees/sync-abcdef0"

# A bare origin holding main and one sync branch one commit ahead of it, and a
# checkout cloned before that branch existed, so only a fetch makes it known.
FIXTURE = """
set -e
git init -q -b main "$root/seed"
git -C "$root/seed" -c user.name=t -c user.email=t@t commit -q --allow-empty -m base
git clone -q --bare "$root/seed" "$root/origin.git"
git clone -q "$root/origin.git" "$root/checkout"
git -C "$root/seed" remote add origin "$root/origin.git"
git -C "$root/seed" checkout -q -b "$SYNC_BRANCH"
printf one > "$root/seed/f"
git -C "$root/seed" add f
git -C "$root/seed" -c user.name=t -c user.email=t@t commit -q -m sync
git -C "$root/seed" push -q origin "$SYNC_BRANCH"
printf '.claude/worktrees/\\n' > "$root/checkout/.gitignore"
mkdir -p "$root/checkout/src"
"""


class SyncShape(unittest.TestCase):
    """The helper opens `.claude/worktrees/<name>` on origin's sync branch, from the main checkout only."""

    def fixture(self):
        root = temp_root(self)
        made = run_bash(FIXTURE, root=root, SYNC_BRANCH=BRANCH)
        self.assertEqual(0, made.returncode, made.stderr)
        return root

    def sync(self, where, path=PATH, branch=BRANCH):
        return run_bash('cd "$WHERE" && bash "$SYNC" "$P" "$B"',
                        WHERE=where, SYNC=str(SYNC), P=path, B=branch)

    def test_the_worktree_tracks_the_origin_tip(self):
        root = self.fixture()
        result = self.sync(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        read = run_bash(
            'git -C "$W" rev-parse --abbrev-ref "@{upstream}" && '
            '[ "$(git -C "$W" rev-parse HEAD)" = "$(git -C "$C" rev-parse "origin/$B")" ] && '
            'cat "$W/f"',
            W=f"{root}/checkout/{PATH}", C=f"{root}/checkout", B=BRANCH)
        self.assertEqual(0, read.returncode, read.stderr)
        self.assertEqual(f"origin/{BRANCH}\none", read.stdout.strip())
        status = run_bash('git -C "$C" status --porcelain', C=f"{root}/checkout")
        self.assertNotIn(".claude", status.stdout)

    def test_a_narrowed_fetch_refspec_never_opens_a_stale_origin_tip(self):
        # A bare `git fetch origin <branch>` leaves an unmapped origin/<branch> where it was; `--track` is what refuses it.
        root = self.fixture()
        checkout = f"{root}/checkout"
        made = run_bash(
            'git -C "$C" update-ref "refs/remotes/origin/$B" main && '
            'git -C "$C" config remote.origin.fetch "+refs/heads/main:refs/remotes/origin/main"',
            C=checkout, B=BRANCH)
        self.assertEqual(0, made.returncode, made.stderr)
        result = self.sync(checkout)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("is not a branch", result.stderr)
        listed = run_bash('git -C "$C" worktree list --porcelain', C=checkout)
        self.assertNotIn("sync-abcdef0", listed.stdout)

    def test_the_add_spells_core_longpaths_in_a_code_line(self):
        # Only a Windows run can fail on a path past 260 characters, so the spelling is held on every platform,
        # against code lines a comment cannot satisfy.
        self.assertIn("git -c core.longpaths=true worktree add",
                      "\n".join(code_lines(SYNC.read_text(encoding="utf-8"))))

    def test_only_a_sync_head_is_accepted(self):
        root = self.fixture()
        for branch in ("chore/harness-sync-abcdef", "chore/harness-sync-abcdef01", "chore/harness-sync-ABCDEF0",
                       "chore/harness-sync-abcdefg", "feat/probe", "xchore/harness-sync-abcdef0",
                       "chore/harness-sync-abcdef0x", "chore/harness-sync-abcdef0/more", "-f", ""):
            with self.subTest(branch=branch):
                result = self.sync(f"{root}/checkout", branch=branch)
                self.assertEqual(2, result.returncode)
                self.assertIn("not a sync branch", result.stderr)
        self.assertNotEqual(0, run_bash('[ -e "$W" ]', W=f"{root}/checkout/{PATH}").returncode)

    def test_main_is_refused_however_it_is_spelled(self):
        root = self.fixture()
        for branch in ("main", "origin/main", "refs/heads/main", "heads/main", "MAIN", "main\n"):
            with self.subTest(branch=branch):
                result = self.sync(f"{root}/checkout", branch=branch)
                self.assertEqual(2, result.returncode)
                self.assertIn("not a sync branch", result.stderr)
        listed = run_bash('git -C "$C" worktree list', C=f"{root}/checkout")
        self.assertEqual(1, len(listed.stdout.strip().splitlines()), listed.stdout)

    def test_an_existing_local_branch_is_refused(self):
        root = self.fixture()
        run_bash('git -C "$C" branch "$B"', C=f"{root}/checkout", B=BRANCH)
        result = self.sync(f"{root}/checkout")
        self.assertEqual(3, result.returncode)
        self.assertIn("branch already exists", result.stderr)

    def test_a_branch_origin_lacks_is_refused(self):
        root = self.fixture()
        result = self.sync(f"{root}/checkout", PATH.replace("abcdef0", "1234567"), "chore/harness-sync-1234567")
        self.assertEqual(4, result.returncode)
        self.assertIn("origin has no such branch", result.stderr)
        branches = run_bash('git -C "$C" branch --list', C=f"{root}/checkout")
        self.assertNotIn("1234567", branches.stdout)

    def test_the_upstream_is_the_only_thing_tracking_is_for(self):
        # A sync run pulls and pushes nothing through it, so the comment says what it does give and no more.
        text = SYNC.read_text(encoding="utf-8")
        self.assertNotIn("pushes of a rebase", text)
        self.assertIn("ahead/behind", text)

    def test_it_runs_only_from_the_main_checkout_root(self):
        root = self.fixture()
        result = self.sync(f"{root}/checkout/src")
        self.assertEqual(2, result.returncode)
        self.assertIn("checkout root", result.stderr)
        # From a linked worktree the relative path would nest a second one.
        first = self.sync(f"{root}/checkout")
        self.assertEqual(0, first.returncode, first.stderr)
        pushed = run_bash('git -C "$W" push -q origin HEAD:refs/heads/chore/harness-sync-1234567',
                          W=f"{root}/checkout/{PATH}")
        self.assertEqual(0, pushed.returncode, pushed.stderr)
        result = self.sync(f"{root}/checkout/{PATH}", ".claude/worktrees/sync-1234567",
                           "chore/harness-sync-1234567")
        self.assertEqual(2, result.returncode)
        self.assertIn("not a linked worktree", result.stderr)

    def test_any_other_path_is_refused(self):
        root = self.fixture()
        for path in ("../sibling", ".claude/worktrees/../x", ".claude/worktrees/a/b", "/tmp/x",
                     ".claude/worktrees/-f"):
            with self.subTest(path=path):
                result = self.sync(f"{root}/checkout", path)
                self.assertEqual(2, result.returncode)
                self.assertIn("must be .claude/worktrees/<name>", result.stderr)

    def test_an_unignored_or_existing_path_is_refused(self):
        root = self.fixture()
        run_bash('mkdir -p "$W"', W=f"{root}/checkout/{PATH}")
        result = self.sync(f"{root}/checkout")
        self.assertEqual(2, result.returncode)
        self.assertIn("path already exists", result.stderr)
        run_bash('rm -rf "$W" && : > "$C/.gitignore"', W=f"{root}/checkout/{PATH}", C=f"{root}/checkout")
        result = self.sync(f"{root}/checkout")
        self.assertEqual(2, result.returncode)
        self.assertIn("is not ignored", result.stderr)

    def test_the_arguments_are_exactly_two(self):
        root = self.fixture()
        result = run_bash('cd "$WHERE" && bash "$SYNC" "$P"', WHERE=f"{root}/checkout", SYNC=str(SYNC), P=PATH)
        self.assertEqual(2, result.returncode)
        self.assertIn("usage:", result.stderr)


if __name__ == "__main__":
    unittest.main()
