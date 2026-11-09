"""git-worktree-remove.sh: /ship's teardown of a /branch worktree.

The shared harness and the reason it shells out rather than
re-implementing are `review_helpers.py`'s.
"""

import importlib.util
import os
import re
import shutil
import subprocess
import sys
import time
import unittest
from pathlib import Path

from review_helpers import (
    SCRIPTS,
    code_lines,
    setUpModule,
    temp_root,
    run_bash,
)


REMOVE = SCRIPTS / "git-worktree-remove.sh"
HOOK = SCRIPTS.parent / "hooks" / "refresh-index.py"
SHIP = SCRIPTS.parent / "commands" / "ship.md"

# A checkout with origin/main and one /branch worktree at .claude/worktrees/probe
# holding a tracked file.
FIXTURE = """
set -e
git init -q -b main "$root/origin"
printf '.claude/worktrees/\\n.claude/cache/\\n' > "$root/origin/.gitignore"
printf 'kept\\n' > "$root/origin/tracked.txt"
git -C "$root/origin" add .gitignore tracked.txt
git -C "$root/origin" -c user.name=t -c user.email=t@t commit -q -m base
git clone -q "$root/origin" "$root/checkout"
git -C "$root/checkout" worktree add -q --no-track -b feat/probe .claude/worktrees/probe origin/main
"""

# A file at $F, past Windows' 260 characters once checked out, committed in the origin by its index alone, so
# nothing writes the deep path there, then a second worktree at .claude/worktrees/deep holding it.
DEEP = """
set -e
long() { git -c core.longpaths=true "$@"; }
blob=$(printf 'deep\\n' | git -C "$O" hash-object -w --stdin)
git -C "$O" update-index --add --cacheinfo "100644,$blob,$F"
git -C "$O" -c user.name=t -c user.email=t@t commit -q -m deep
long -C "$C" fetch -q origin
long -C "$C" worktree add -q --no-track -b feat/deep .claude/worktrees/deep origin/main
"""

# A refresh-index.py worker in miniature: it starts after a delay, takes the
# lock and the outstanding request, says so through a marker file, and lets go
# after the given number of seconds. The two paths are the hook's own.
HOLDER = """
import os, sys, time
lock, pending = sys.argv[1], sys.argv[2]
delay, seconds, ready = float(sys.argv[3]), float(sys.argv[4]), sys.argv[5]
put_back, give_up = "put-back" in sys.argv[6:], "give-up" in sys.argv[6:]
time.sleep(delay)
os.makedirs(os.path.dirname(lock), exist_ok=True)
handle = open(lock, "a+b")
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
try:
    os.remove(pending)
except FileNotFoundError:
    pass
open(ready, "w").close()
time.sleep(seconds)
if put_back:
    open(pending, "w").close()
"""


class RemoveShape(unittest.TestCase):
    def fixture(self, inside=""):
        root = temp_root(self)
        if inside:
            root = f"{root}/{inside}"
            os.mkdir(root)
        made = run_bash(FIXTURE, root=root)
        self.assertEqual(0, made.returncode, made.stderr)
        return root, Path(root)

    def remove(self, where, path=".claude/worktrees/probe", branch="feat/probe", script=REMOVE, **env):
        return run_bash('cd "$WHERE" && bash "$REMOVE" "$P" "$B"', WHERE=where, REMOVE=str(script), P=path,
                        B=branch, **env)

    def timed(self, native, bound, grace):
        """A copy of the helper with a shorter bound and grace, so a test waits
        seconds rather than the production half minute."""
        text = REMOVE.read_text(encoding="utf-8")
        self.assertIn("bound, grace = 30, 2\n", text)
        copy = native / "git-worktree-remove.sh"
        copy.write_bytes(text.replace("bound, grace = 30, 2\n", f"bound, grace = {bound}, {grace}\n").encode())
        return copy

    def present(self, root, name="probe"):
        return run_bash('[ -f "$W/tracked.txt" ]', W=f"{root}/checkout/.claude/worktrees/{name}").returncode == 0

    def registered(self, root):
        listed = run_bash('git -C "$C" worktree list --porcelain', C=f"{root}/checkout")
        return "feat/probe" in listed.stdout

    def state(self, native):
        """The lock and the marker the hook keeps for the probe worktree, as
        the hook itself names them: the helper has to look where it does."""
        spec = importlib.util.spec_from_file_location("refresh_index", HOOK)
        hook = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(hook)
        hook.home = lambda: native / "checkout"
        probe = native / "checkout" / ".claude" / "worktrees" / "probe"
        return hook.lock_path(probe), hook.pending_path(probe)

    def request(self, native):
        """What the hook leaves before it starts a worker."""
        pending = self.state(native)[1]
        pending.parent.mkdir(parents=True, exist_ok=True)
        pending.write_text("", encoding="utf-8")

    def hold(self, native, seconds, delay=0.0, wait=True, put_back=False, give_up=False, ready="ready"):
        ready = native / ready
        lock, pending = self.state(native)
        holder = subprocess.Popen([sys.executable, "-c", HOLDER, str(lock), str(pending), str(delay),
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

    def test_a_worktree_holding_a_path_past_260_characters_is_removed(self):
        # Windows' limit: without long paths git refuses a worktree it cannot read, or fails to delete the deep file
        # and leaves the directory unregistered. The fixture's git commands ask for long paths, so only the helper's
        # removal is tested, with long paths set off above every config file, which only the helper's -c outranks. Only
        # a Windows run can fail on the removal itself, so the helper's spelling is held on every platform too.
        self.assertIn("git -c core.longpaths=true worktree remove",
                      "\n".join(code_lines(REMOVE.read_text(encoding="utf-8"))))
        root, _ = self.fixture()
        deep = "/".join(["d" * 60] * 5) + "/deep.txt"
        made = run_bash(DEEP, C=f"{root}/checkout", O=f"{root}/origin", F=deep)
        self.assertEqual(0, made.returncode, made.stderr)
        self.assertGreater(len(f"{root}/checkout/.claude/worktrees/deep/{deep}"), 260)
        result = self.remove(f"{root}/checkout", ".claude/worktrees/deep", "feat/deep", GIT_CONFIG_COUNT="1",
                             GIT_CONFIG_KEY_0="core.longpaths", GIT_CONFIG_VALUE_0="false")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(run_bash('[ -e "$W" ]', W=f"{root}/checkout/.claude/worktrees/deep").returncode == 0)
        self.assertNotIn("feat/deep", run_bash('git -C "$C" worktree list --porcelain', C=f"{root}/checkout").stdout)

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

    def fork(self, root, name, branch):
        """A second /branch worktree, as the fixture's first one was made."""
        made = run_bash('git -C "$C" worktree add -q --no-track -b "$B" ".claude/worktrees/$N" origin/main',
                        C=f"{root}/checkout", B=branch, N=name)
        self.assertEqual(0, made.returncode, made.stderr)

    def test_the_path_alone_is_refused(self):
        # Registration is not ownership: a path without the branch it was forked for would remove any clean
        # forked worktree, another run's included.
        root, _ = self.fixture()
        result = run_bash('cd "$WHERE" && bash "$REMOVE" .claude/worktrees/probe', WHERE=f"{root}/checkout",
                          REMOVE=str(REMOVE))
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("usage: git-worktree-remove.sh <path> <branch>", result.stderr)
        self.assertTrue(self.present(root))

    def test_main_and_anything_shaped_like_a_flag_are_refused_as_the_branch(self):
        root, _ = self.fixture()
        for branch in ("main", "Main", "MAIN", "-f", "--force", "feat/..x", "feat/a b", "", "/feat"):
            with self.subTest(branch=branch):
                result = self.remove(f"{root}/checkout", branch=branch)
                self.assertEqual(2, result.returncode, result.stderr)
        self.assertTrue(self.present(root))
        self.assertTrue(self.registered(root))

    def test_a_worktree_is_removed_only_with_the_branch_it_holds(self):
        root, _ = self.fixture()
        self.fork(root, "other", "feat/other")
        for path, branch in ((".claude/worktrees/probe", "feat/other"),
                             (".claude/worktrees/other", "feat/probe"),
                             (".claude/worktrees/probe", "feat/absent"),
                             (".claude/worktrees/probe", "feat/prob")):
            with self.subTest(path=path, branch=branch):
                result = self.remove(f"{root}/checkout", path, branch)
                self.assertEqual(3, result.returncode, result.stderr)
                self.assertIn("not the worktree of this repository holding", result.stderr)
                self.assertTrue(self.present(root))
                self.assertTrue(self.present(root, "other"))
        self.assertEqual(0, self.remove(f"{root}/checkout", ".claude/worktrees/other", "feat/other").returncode)
        self.assertFalse(self.present(root, "other"))
        self.assertTrue(self.present(root))

    def test_a_detached_worktree_holds_no_branch_and_is_refused(self):
        root, _ = self.fixture()
        detached = run_bash('git -C "$W" checkout -q --detach', W=f"{root}/checkout/.claude/worktrees/probe")
        self.assertEqual(0, detached.returncode, detached.stderr)
        result = self.remove(f"{root}/checkout")
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("not the worktree of this repository holding feat/probe", result.stderr)
        self.assertTrue(self.present(root))
        listed = run_bash('git -C "$C" worktree list --porcelain', C=f"{root}/checkout").stdout
        self.assertIn("/.claude/worktrees/probe", listed.replace(chr(92), "/"), "the refusal unregistered it")

    def test_a_branch_with_parentheses_is_still_removed(self):
        # The pattern is the one git-branch-create.sh and git-worktree-fork.sh use, because `/branch` cuts
        # `feat(<scope>)/` names, so a forked worktree carries one. The directory name is the first words of the change
        # and not the branch's.
        root, _ = self.fixture()
        self.fork(root, "masstransit", "feat(template)/masstransit-registration")
        result = self.remove(f"{root}/checkout", ".claude/worktrees/masstransit",
                             "feat(template)/masstransit-registration")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.present(root, "masstransit"))
        self.assertTrue(self.present(root))

    def leftover(self, root, git_exit, content, held=False):
        """Run the helper over a git that does what Git for Windows does when a delete fails: it removes the
        worktree and its registration, leaves the directory behind, and exits as given. `content` says whether
        a file is left in it too, as a file another process holds open is, and `held` has `rmdir` refuse the
        empty directory, as Windows refuses one that is a session's working directory."""
        native = Path(root)
        shim = native / "shim"
        shim.mkdir()
        if held:
            (shim / "rmdir").write_bytes(b"#!/usr/bin/env bash" + chr(10).encode() + b"exit 1" + chr(10).encode())
            os.chmod(shim / "rmdir", 0o755)
        real = Path(shutil.which("git")).as_posix()
        (shim / "git").write_bytes((
            "#!/usr/bin/env bash" + chr(10)
            + f'"{real}" "$@" || exit' + chr(10)
            + 'if [ "$3" = worktree ] && [ "$4" = remove ]; then' + chr(10)
            + '  mkdir -p "$5"; [ -z "$SHIM_FILE" ] || : > "$5/held"; exit "${SHIM_EXIT:-0}"' + chr(10)
            + "fi" + chr(10)
        ).encode("utf-8"))
        os.chmod(shim / "git", 0o755)
        # On PATH inside bash, not before it: Git's bash launcher puts Git's own directories first, so a shim
        # placed first from outside came second and the real git ran. In bash's own form, because a drive
        # colon splits a PATH entry.
        shim_dir = shim.resolve()
        if os.name == "nt":
            shim_dir = "/" + shim_dir.drive[0].lower() + shim_dir.as_posix()[2:]
        return run_bash('cd "$WHERE" && PATH="$SHIM:$PATH" bash "$REMOVE" .claude/worktrees/probe feat/probe',
                        WHERE=f"{root}/checkout", REMOVE=str(REMOVE), SHIM=str(shim_dir),
                        SHIM_EXIT=str(git_exit), SHIM_FILE="1" if content else "")

    def test_a_directory_git_unregistered_but_left_holding_something_is_reported(self):
        # Git for Windows, failing to delete a file another process holds open, has already unregistered the
        # worktree. A teardown read as done leaves a directory nobody looks at, and the next /branch on that slug
        # stops on it. Whether git then exits non-zero, as a failed delete does, or zero.
        for git_exit in (255, 0):
            with self.subTest(git_exit=git_exit):
                root, _ = self.fixture()
                result = self.leftover(root, git_exit, content=True)
                self.assertEqual(4, result.returncode, result.stderr)
                self.assertIn("unregistered but not deleted", result.stderr)
                self.assertIn(".claude/worktrees/probe", result.stderr)
                self.assertEqual(0, run_bash('[ -f "$W/held" ]', W=f"{root}/checkout/.claude/worktrees/probe").returncode,
                                 "what the directory held was removed")
                self.assertFalse(self.registered(root), "git still registers it")

    def test_an_empty_directory_git_left_behind_is_removed_and_the_removal_succeeded(self):
        # Only the empty folder is left, as when a process held it while git deleted the files. It is the
        # helper's to remove with `rmdir`, which refuses anything that holds a file, and the removal then succeeded.
        for git_exit in (255, 0):
            with self.subTest(git_exit=git_exit):
                root, _ = self.fixture()
                result = self.leftover(root, git_exit, content=False)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(1, run_bash('[ -e "$W" ]', W=f"{root}/checkout/.claude/worktrees/probe").returncode,
                                 "the empty directory is still there")
                self.assertFalse(self.registered(root))

    def test_an_empty_directory_a_process_holds_is_named_apart_from_one_holding_files(self):
        # A /ship launched inside its worktree stands in the directory it removes, and Windows refuses the delete.
        # Told only "remove it by hand", the owner looks for work that is not there and removes nothing they can.
        root, _ = self.fixture()
        result = self.leftover(root, 0, content=False, held=True)
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("is empty and held by a process", result.stderr)
        self.assertIn(".claude/worktrees/probe", result.stderr)
        self.assertNotIn("remove it by hand", result.stderr)
        self.assertFalse(self.registered(root))

    def test_a_held_directory_with_a_file_in_it_is_not_called_empty(self):
        root, _ = self.fixture()
        result = self.leftover(root, 0, content=True, held=True)
        self.assertEqual(4, result.returncode, result.stderr)
        self.assertIn("remove it by hand", result.stderr)
        self.assertNotIn("is empty", result.stderr)

    def test_the_dirty_refusal_names_gits_own_reason_and_keeps_the_work(self):
        # A worktree git still registers is the dirty-tree refusal, which keeps git's own status and message.
        root, _ = self.fixture()
        run_bash('printf x > "$W/notes.txt"', W=f"{root}/checkout/.claude/worktrees/probe")
        result = self.remove(f"{root}/checkout")
        self.assertNotIn(result.returncode, (0, 4), result.stderr)
        self.assertNotIn("not deleted", result.stderr)
        self.assertIn("contains modified or untracked files", result.stderr)
        self.assertTrue(self.registered(root))

    def test_a_missing_directory_is_refused(self):
        root, _ = self.fixture()
        result = self.remove(f"{root}/checkout", ".claude/worktrees/absent")
        self.assertEqual(3, result.returncode, result.stderr)

    def test_a_refresh_is_waited_for_in_a_checkout_whose_path_is_not_ascii(self):
        """The name crosses a pipe from Python, which on Windows writes the
        ANSI code page: only the key may cross, and the path is bash's own."""
        root, native = self.fixture("\u043f\u0440\u043e\u0435\u043a\u0442")
        self.hold(native, 3)
        started = time.monotonic()
        result = self.remove(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertGreaterEqual(time.monotonic() - started, 2)
        self.assertFalse(self.present(root))

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
        helper starts some time after the holder's grab: the release, 3 s from
        the grab, lands a second inside the 4 s bound, and the 3 s grace still
        runs at the bound for a start up to 2 s late."""
        root, native = self.fixture()
        self.hold(native, 3, put_back=True)
        result = self.remove(f"{root}/checkout", script=self.timed(native, 4, 3))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.present(root))

    def test_a_refresh_past_the_bound_removes_nothing(self):
        root, native = self.fixture()
        self.hold(native, 20)
        result = self.remove(f"{root}/checkout", script=self.timed(native, 1, 2))
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("nothing removed", result.stderr)
        self.assertTrue(self.present(root))
        self.assertTrue(self.registered(root))

    def test_the_worktrees_lock_and_marker_are_removed_with_it(self):
        """A directory inode a later worktree is given would inherit a lock
        file left under the old one's name, and the pid recorded in it."""
        root, native = self.fixture()
        lock, pending = self.state(native)
        self.request(native)
        self.hold(native, 1)
        self.assertTrue(lock.exists())
        result = self.remove(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.present(root))
        self.assertFalse(lock.exists())
        self.assertFalse(pending.exists())

    def test_a_worktree_that_never_had_a_lock_is_not_given_one(self):
        root, native = self.fixture()
        lock, _ = self.state(native)
        neighbour = lock.with_name("neighbour.lock")
        neighbour.parent.mkdir(parents=True, exist_ok=True)
        neighbour.write_text("", encoding="utf-8")
        result = self.remove(f"{root}/checkout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(lock.exists())
        self.assertTrue(neighbour.exists(), "another worktree's lock went")

    def test_a_name_that_cannot_be_computed_removes_nothing(self):
        """Without the name the lock cannot be waited on, and a refresh may
        hold the tree: the state directory is the sign that one could."""
        root, native = self.fixture()
        self.hold(native, 1)
        shim = native / "shim"
        shim.mkdir()
        for name in ("py", "python3"):
            (shim / name).write_bytes(("#!/usr/bin/env bash" + chr(10) + "exit 1" + chr(10)).encode("utf-8"))
            os.chmod(shim / name, 0o755)
        shim_dir = shim.resolve()
        if os.name == "nt":
            shim_dir = "/" + shim_dir.drive[0].lower() + shim_dir.as_posix()[2:]
        result = run_bash('cd "$WHERE" && PATH="$SHIM:$PATH" bash "$REMOVE" .claude/worktrees/probe feat/probe',
                          WHERE=f"{root}/checkout", REMOVE=str(REMOVE), SHIM=str(shim_dir))
        self.assertEqual(5, result.returncode, result.stderr)
        self.assertIn("nothing removed", result.stderr)
        self.assertTrue(self.present(root))

    def test_a_state_file_that_will_not_go_does_not_change_the_exit(self):
        root, native = self.fixture()
        lock, _ = self.state(native)
        self.hold(native, 1)
        shim = native / "shim"
        shim.mkdir()
        (shim / "rm").write_bytes(("#!/usr/bin/env bash" + chr(10) + "exit 1" + chr(10)).encode("utf-8"))
        os.chmod(shim / "rm", 0o755)
        shim_dir = shim.resolve()
        if os.name == "nt":
            shim_dir = "/" + shim_dir.drive[0].lower() + shim_dir.as_posix()[2:]
        result = run_bash('cd "$WHERE" && PATH="$SHIM:$PATH" bash "$REMOVE" .claude/worktrees/probe feat/probe',
                          WHERE=f"{root}/checkout", REMOVE=str(REMOVE), SHIM=str(shim_dir))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.present(root))
        self.assertTrue(lock.exists(), "the shim did not stop the removal of the state file")


class TheGrant(unittest.TestCase):
    def test_ship_grants_the_helper_and_not_the_raw_command(self):
        front = SHIP.read_text(encoding="utf-8").split("---")[1]
        tools = re.search(r"^allowed-tools: (.*)$", front, re.M).group(1)
        self.assertIn("Bash(bash .claude/scripts/git-worktree-remove.sh:*)", tools)
        self.assertNotIn("Bash(git worktree remove", tools)


if __name__ == "__main__":
    unittest.main()
