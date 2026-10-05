"""git-worktree-fork.sh: the one worktree shape /branch step 5 creates.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import json
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
if [ "$HOOK" = yes ]; then
  mkdir -p "$root/origin/.claude/hooks"
  printf 'import os\\nopen("hook-ran", "w").write(os.getcwd())\\n' > "$root/origin/.claude/hooks/refresh-index.py"
  git -C "$root/origin" add .claude
  git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q -m hook
fi
git clone -q "$root/origin" "$root/checkout"
if [ "$IGNORE" = yes ]; then printf '.claude/worktrees/\\n' > "$root/checkout/.gitignore"; fi
mkdir -p "$root/checkout/src"
printf '%s\\n' "$root"
"""


class ForkShape(unittest.TestCase):
    """The helper forks `.claude/worktrees/<name>` from the main checkout only."""

    def fixture(self, ignore="yes", hook="no"):
        made = run_bash(FIXTURE, IGNORE=ignore, HOOK=hook)
        self.assertEqual(0, made.returncode, made.stderr)
        root = made.stdout.strip()
        self.addCleanup(lambda: run_bash('rm -rf "$TARGET"', TARGET=root))
        return root

    def fork(self, where, path, branch="feat/probe"):
        return run_bash('cd "$WHERE" && bash "$FORK" "$P" "$B"',
                        WHERE=where, FORK=str(FORK), P=path, B=branch)

    def local_settings(self, root, text):
        made = run_bash('mkdir -p "$C/.claude" && printf %s "$T" > "$C/.claude/settings.local.json"',
                        C=f"{root}/checkout", T=text)
        self.assertEqual(0, made.returncode, made.stderr)

    def probe_settings(self, root):
        return run_bash('cat "$W/.claude/settings.local.json"',
                        W=f"{root}/checkout/.claude/worktrees/probe")

    def checkout_file(self, root, name, text):
        made = run_bash('printf %s "$T" > "$C/$N"', C=f"{root}/checkout", N=name, T=text)
        self.assertEqual(0, made.returncode, made.stderr)

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

    def test_the_new_worktree_runs_its_own_index_refresh(self):
        # /branch enters the worktree mid-session, where no `SessionStart`
        # fires, so the fork starts the refresh that hook would have. The stub
        # is committed on origin/main, so the copy that runs is the worktree's.
        root = self.fixture(hook="yes")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        ran = run_bash(
            'for _ in $(seq 300); do '
            '[ -s "$W/hook-ran" ] && exec cat "$W/hook-ran"; sleep 0.1; '
            'done; exit 1',
            W=f"{root}/checkout/.claude/worktrees/probe")
        self.assertEqual(0, ran.returncode, "the fork never ran the refresh")
        self.assertTrue(
            ran.stdout.strip().replace("\\", "/").endswith("/.claude/worktrees/probe"),
            ran.stdout)

    def test_the_mcp_approval_crosses_and_no_permission_does(self):
        # A session started in the worktree reads that directory's local
        # settings, so the main checkout's approval is carried there alone.
        root = self.fixture()
        self.local_settings(root, json.dumps({
            "enableAllProjectMcpServers": True,
            "enabledMcpjsonServers": ["codebase-index"],
            "permissions": {"allow": ["Bash(ls:*)"]}}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        copied = self.probe_settings(root)
        self.assertEqual(0, copied.returncode, copied.stderr)
        self.assertEqual({"enabledMcpjsonServers": ["codebase-index"]},
                         json.loads(copied.stdout))

    def test_a_checkout_approving_nothing_gives_the_worktree_no_settings(self):
        root = self.fixture()
        self.local_settings(root, json.dumps({"permissions": {"allow": ["Bash(ls:*)"]}}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("could not copy", result.stderr)
        self.assertNotEqual(0, self.probe_settings(root).returncode)

    def test_an_unreadable_approval_still_forks_and_says_so(self):
        root = self.fixture()
        self.local_settings(root, "{not json")
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("could not copy the MCP approval", result.stderr)
        self.assertIn("JSONDecodeError", result.stderr)
        self.assertNotEqual(0, self.probe_settings(root).returncode)

    def test_an_approval_the_helper_cannot_copy_warns(self):
        # Every server approved at once, or a list naming none, would otherwise
        # leave the worktree's servers pending with nothing said.
        for text in (json.dumps({"enableAllProjectMcpServers": True}),
                     json.dumps({"enabledMcpjsonServers": "codebase-index"})):
            with self.subTest(text=text):
                root = self.fixture()
                self.local_settings(root, text)
                result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("could not copy the MCP approval", result.stderr)
                self.assertNotEqual(0, self.probe_settings(root).returncode)

    def test_a_json_module_at_the_checkout_root_is_not_imported(self):
        # The approval is read by a Python run from the checkout root, whose
        # untracked files must not shadow the standard library.
        root = self.fixture()
        self.checkout_file(root, "json.py", 'raise SystemExit("shadowed")')
        self.local_settings(root, json.dumps({"enabledMcpjsonServers": ["codebase-index"]}))
        result = self.fork(f"{root}/checkout", ".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        copied = self.probe_settings(root)
        self.assertEqual(0, copied.returncode, copied.stderr)
        self.assertEqual({"enabledMcpjsonServers": ["codebase-index"]},
                         json.loads(copied.stdout))

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
