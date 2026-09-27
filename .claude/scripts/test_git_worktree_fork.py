"""git-worktree-fork.sh: the one worktree shape /branch step 5 creates.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import unittest

from review_helpers import (
    SCRIPTS,
    setUpModule,
    run_bash,
)


FORK = SCRIPTS / "git-worktree-fork.sh"

# A repository with an origin/main to fork from, made by bash so the paths are
# the ones the script sees. `IGNORE` decides whether .gitignore names the
# worktree directory, which is the check the ignored/unignored cases turn on.
FIXTURE = """
set -e
root=$(mktemp -d)
git init -q -b main "$root/origin"
git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q --allow-empty -m base
git clone -q "$root/origin" "$root/checkout"
if [ "$IGNORE" = yes ]; then printf '.claude/worktrees/\\n' > "$root/checkout/.gitignore"; fi
mkdir -p "$root/checkout/src"
printf '%s\\n' "$root"
"""


class ForkShape(unittest.TestCase):
    """The helper forks `.claude/worktrees/<name>` from the main checkout only."""

    def fixture(self, ignore="yes"):
        made = run_bash(FIXTURE, IGNORE=ignore)
        self.assertEqual(0, made.returncode, made.stderr)
        root = made.stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$TARGET"', TARGET=root))
        return root

    def fork(self, where, path, branch="feat/probe"):
        return run_bash('cd "$WHERE" && bash "$FORK" "$P" "$B"',
                        WHERE=where, FORK=str(FORK), P=path, B=branch)

    def test_the_documented_shape_forks_with_no_upstream(self):
        root = self.fixture()
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        upstream = run_bash(
            'git -C "$C" rev-parse --abbrev-ref feat/probe@{upstream}',
            C=f"{root}/checkout")
        self.assertNotEqual(0, upstream.returncode)
        # The worktree sits inside the checkout, and ignored is what keeps it
        # out of every `git status` the chain reads.
        status = run_bash('git -C "$C" status --porcelain', C=f"{root}/checkout")
        self.assertNotIn(".claude", status.stdout)

    def test_any_other_path_is_refused(self):
        root = self.fixture()
        for path in ("../sibling", ".claude/worktrees/../x", ".claude/worktrees/a/b",
                     "/tmp/x", ".claude/worktrees/-f"):
            with self.subTest(path=path):
                result = self.fork(f"{root}/checkout", path)
                self.assertEqual(2, result.returncode)
                self.assertIn("must be .claude/worktrees/<name>", result.stderr)

    def test_an_unignored_path_is_refused(self):
        root = self.fixture(ignore="no")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode)
        self.assertIn("is not ignored", result.stderr)

    def test_a_subdirectory_is_refused(self):
        root = self.fixture()
        result = self.fork(f"{root}/checkout/src", ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode)
        self.assertIn("checkout root", result.stderr)

    def test_a_linked_worktree_is_refused(self):
        root = self.fixture()
        first = self.fork(f"{root}/checkout", ".claude/worktrees/first", "feat/first")
        self.assertEqual(0, first.returncode, first.stderr)
        result = self.fork(f"{root}/checkout/.claude/worktrees/first",
                           ".claude/worktrees/second", "feat/second")
        self.assertEqual(2, result.returncode)
        self.assertIn("not a linked worktree", result.stderr)

    def test_an_existing_branch_is_refused_rather_than_reset(self):
        root = self.fixture()
        run_bash('git -C "$C" branch feat/probe', C=f"{root}/checkout")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(3, result.returncode)
        self.assertIn("branch already exists", result.stderr)

    def test_outside_a_repository_says_so(self):
        made = run_bash('mktemp -d')
        outside = made.stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$TARGET"', TARGET=outside))
        result = self.fork(outside, ".claude/worktrees/probe")
        self.assertEqual(2, result.returncode)
        self.assertIn("not in a git repository", result.stderr)


if __name__ == "__main__":
    unittest.main()
