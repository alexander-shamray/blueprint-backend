"""git-worktree-remove.sh: /ship's teardown of a /branch worktree.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import re
import subprocess
import sys
import time
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    setUpModule,
    run_bash,
)


REMOVE = SCRIPTS / "git-worktree-remove.sh"
SHIP = SCRIPTS.parent / "commands" / "ship.md"

# A checkout with origin/main and one /branch worktree at .claude/worktrees/probe
# holding a tracked file. It prints the root twice: as bash sees it, then as
# Python opens it, since under MSYS the two spellings differ.
FIXTURE = """
set -e
root=$(mktemp -d)
git init -q -b main "$root/origin"
printf '.claude/worktrees/\\n.claude/cache/\\n' > "$root/origin/.gitignore"
printf 'kept\\n' > "$root/origin/tracked.txt"
git -C "$root/origin" add .gitignore tracked.txt
git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q -m base
git clone -q "$root/origin" "$root/checkout"
git -C "$root/checkout" worktree add -q --no-track -b feat/probe .claude/worktrees/probe origin/main
printf '%s\\n' "$root"
(cd "$root" && pwd -W 2>/dev/null || pwd)
"""

# Holds a lock the way refresh-index.py's worker does, says so through a
# marker file, and lets go after the given number of seconds.
HOLDER = """
import sys, time
lock, seconds, ready = sys.argv[1], float(sys.argv[2]), sys.argv[3]
handle = open(lock, "a+b")
if sys.platform == "win32":
    import msvcrt
    handle.seek(0)
    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
else:
    import fcntl
    fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
open(ready, "w").close()
time.sleep(seconds)
"""


class RemoveShape(unittest.TestCase):
    def fixture(self):
        made = run_bash(FIXTURE)
        self.assertEqual(0, made.returncode, made.stderr)
        root, native = made.stdout.splitlines()
        self.addCleanup(lambda: run_bash('rm -rf "$TARGET"', TARGET=root))
        return root, Path(native)

    def remove(self, where, path=".claude/worktrees/probe"):
        return run_bash('cd "$WHERE" && bash "$REMOVE" "$P"', WHERE=where, REMOVE=str(REMOVE), P=path)

    def present(self, root, name="probe"):
        return run_bash('[ -f "$W/tracked.txt" ]', W=f"{root}/checkout/.claude/worktrees/{name}").returncode == 0

    def registered(self, root):
        listed = run_bash('git -C "$C" worktree list --porcelain', C=f"{root}/checkout")
        return "feat/probe" in listed.stdout

    def hold(self, native, seconds):
        cache = native / "checkout" / ".claude" / "worktrees" / "probe" / ".claude" / "cache" / "codebase-index"
        cache.mkdir(parents=True)
        ready = native / "ready"
        holder = subprocess.Popen([sys.executable, "-c", HOLDER, str(cache / "refresh.lock"), str(seconds),
                                   str(ready)])
        self.addCleanup(holder.wait)
        self.addCleanup(holder.kill)
        for _ in range(300):
            if ready.exists():
                return
            time.sleep(0.05)
        self.fail("the lock holder never took the lock")

    def test_a_clean_worktree_is_removed_and_unregistered(self):
        root, _ = self.fixture()
        result = self.remove(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.present(root))
        self.assertFalse(self.registered(root))

    def test_untracked_work_is_refused_and_kept(self):
        root, _ = self.fixture()
        run_bash('printf x > "$W/notes.txt"', W=f"{root}/checkout/.claude/worktrees/probe")
        result = self.remove(f"{root}/checkout")
        self.assertNotEqual(0, result.returncode)
        self.assertTrue(self.present(root))
        self.assertTrue(self.registered(root))

    def test_any_other_shape_is_refused_before_git_is_asked(self):
        root, _ = self.fixture()
        for path in ("-f", ".claude/worktrees/probe/", ".claude/worktrees/../probe",
                     ".claude/worktrees/a/b", f"{root}/checkout/.claude/worktrees/probe", "probe",
                     ".claude/worktrees/-f"):
            with self.subTest(path=path):
                result = self.remove(f"{root}/checkout", path)
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("path must be .claude/worktrees/<name>", result.stderr)
        self.assertTrue(self.present(root))

    def test_a_linked_worktree_cannot_remove_another(self):
        root, _ = self.fixture()
        result = self.remove(f"{root}/checkout/.claude/worktrees/probe")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("run from the main checkout", result.stderr)
        self.assertTrue(self.present(root))

    def test_a_subdirectory_is_refused(self):
        root, _ = self.fixture()
        run_bash('mkdir -p "$C/src"', C=f"{root}/checkout")
        result = self.remove(f"{root}/checkout/src")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("run from the checkout root", result.stderr)

    def test_a_directory_that_is_no_worktree_is_left_alone(self):
        root, _ = self.fixture()
        run_bash('mkdir -p "$C/.claude/worktrees/plain" && printf x > "$C/.claude/worktrees/plain/f"',
                 C=f"{root}/checkout")
        result = self.remove(f"{root}/checkout", ".claude/worktrees/plain")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("not a linked worktree of this repository", result.stderr)
        self.assertEqual(0, run_bash('[ -f "$C/.claude/worktrees/plain/f" ]', C=f"{root}/checkout").returncode)

    def test_a_missing_directory_is_refused(self):
        root, _ = self.fixture()
        result = self.remove(f"{root}/checkout", ".claude/worktrees/absent")
        self.assertEqual(3, result.returncode, result.stderr)

    def test_a_refresh_that_ends_is_waited_for(self):
        root, native = self.fixture()
        self.hold(native, 3)
        started = time.monotonic()
        result = self.remove(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertGreaterEqual(time.monotonic() - started, 2)
        self.assertFalse(self.present(root))
        self.assertFalse(self.registered(root))

    def test_a_refresh_past_the_bound_removes_nothing(self):
        root, native = self.fixture()
        self.hold(native, 60)
        result = self.remove(f"{root}/checkout")
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("nothing removed", result.stderr)
        self.assertTrue(self.present(root))
        self.assertTrue(self.registered(root))


class TheGrant(unittest.TestCase):
    def test_ship_grants_the_helper_and_not_the_raw_command(self):
        front = SHIP.read_text(encoding="utf-8").split("---")[1]
        tools = re.search(r"^allowed-tools: (.*)$", front, re.M).group(1)
        self.assertIn("Bash(bash .claude/scripts/git-worktree-remove.sh:*)", tools)
        self.assertNotIn("Bash(git worktree remove", tools)


if __name__ == "__main__":
    unittest.main()
