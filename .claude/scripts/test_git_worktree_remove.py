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

# A refresh-index.py worker in miniature: it starts after a delay, takes the
# lock, says so through a marker file, takes the outstanding request, and lets
# go after the given number of seconds.
HOLDER = """
import os, sys, time
cache, delay, seconds, ready = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), sys.argv[4]
put_back, give_up = "put-back" in sys.argv[5:], "give-up" in sys.argv[5:]
time.sleep(delay)
handle = open(os.path.join(cache, "refresh.lock"), "a+b")
if sys.platform == "win32":
    import msvcrt
    def grab():
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
else:
    import fcntl
    def grab():
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
# Retried, because the helper's own poll holds the lock for an instant and a
# case timing a worker cannot also race that poll. The real work() returns at
# once on a failed grab and leaves its request, which `give-up` models.
for _ in range(1 if give_up else 100):
    try:
        grab()
        break
    except OSError:
        time.sleep(0.01)
else:
    sys.exit(0 if give_up else "the lock never came free")
open(ready, "w").close()
try:
    os.remove(os.path.join(cache, "refresh.pending"))
except FileNotFoundError:
    pass
time.sleep(seconds)
if put_back:
    open(os.path.join(cache, "refresh.pending"), "w").close()
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

    def cache(self, native):
        cache = native / "checkout" / ".claude" / "worktrees" / "probe" / ".claude" / "cache" / "codebase-index"
        cache.mkdir(parents=True, exist_ok=True)
        return cache

    def request(self, native):
        """What the hook leaves before it starts a worker."""
        (self.cache(native) / "refresh.pending").write_text("", encoding="utf-8")

    def hold(self, native, seconds, delay=0.0, wait=True, put_back=False, give_up=False, ready="ready"):
        ready = native / ready
        holder = subprocess.Popen([sys.executable, "-c", HOLDER, str(self.cache(native)), str(delay),
                                   str(seconds), str(ready), *(["put-back"] if put_back else []),
                                   *(["give-up"] if give_up else [])])
        self.addCleanup(holder.wait)
        self.addCleanup(holder.kill)
        if not wait:
            return holder
        for _ in range(300):
            if ready.exists():
                return holder
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

    def test_a_worker_started_but_not_yet_locked_is_waited_for(self):
        """The hook leaves its request and then starts the worker, so a free
        lock beside a request is a worker on its way, not an idle tree."""
        root, native = self.fixture()
        self.request(native)
        self.hold(native, 3, delay=1, wait=False)
        started = time.monotonic()
        result = self.remove(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue((native / "ready").exists(), "the tree went before the worker reached its lock")
        self.assertGreaterEqual(time.monotonic() - started, 3)
        self.assertFalse(self.present(root))

    def test_a_request_nobody_takes_is_dropped_after_the_grace(self):
        """A failed refresh puts its request back and exits, so no worker
        will ever take it, and waiting the whole bound would refuse for ever."""
        root, native = self.fixture()
        self.request(native)
        started = time.monotonic()
        result = self.remove(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertGreaterEqual(time.monotonic() - started, 2)
        self.assertLess(time.monotonic() - started, 25)
        self.assertFalse(self.present(root))

    def test_a_stand_in_that_gives_up_like_the_worker_leaves_its_request_to_be_dropped(self):
        """Modelled on work(), which exits on a held lock and leaves its request,
        so the tree is removed once the holder lets go and the grace passes."""
        root, native = self.fixture()
        self.hold(native, 5)
        held = time.monotonic()
        self.request(native)
        worker = self.hold(native, 0, wait=False, give_up=True, ready="worker-ready")
        self.assertEqual(0, worker.wait(timeout=30))
        self.assertFalse((native / "worker-ready").exists(), "the worker took a held lock")
        result = self.remove(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        # From the holder's grab: its 5 s hold, then the 2 s grace on the request it left.
        self.assertGreaterEqual(time.monotonic() - held, 6.9, "the request was dropped without its grace")
        self.assertFalse(self.present(root))

    def test_a_request_put_back_near_the_bound_still_gets_its_grace(self):
        """A refresh that fails late puts its request back and exits, which
        leaves nothing holding the tree, so the bound must not report one. The
        helper starts some time after the holder's grab, so a copy with a
        grace near the bound keeps the release inside it on a slow host."""
        root, native = self.fixture()
        text = REMOVE.read_text(encoding="utf-8")
        self.assertIn("bound, grace = 30, 2\n", text)
        copy = native / "git-worktree-remove.sh"
        copy.write_bytes(text.replace("bound, grace = 30, 2\n", "bound, grace = 10, 8\n").encode())
        self.hold(native, 9, put_back=True)
        result = run_bash('cd "$WHERE" && bash "$REMOVE" "$P"', WHERE=f"{root}/checkout", REMOVE=str(copy),
                          P=".claude/worktrees/probe")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.present(root))

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
