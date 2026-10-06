"""What `.claude/hooks/refresh-index.py` refreshes, and what it leaves alone.

The hook suppresses every failure on purpose, so its exit status says
nothing: a wrong checkout, an absent CLI and a hook nothing calls all look
like success. The invariant held here is that the tree refreshed is the one
the edit landed in, exactly one refresh owns it, or none of them run.
"""

import importlib.util
import io
import json
import os
import shlex
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
HOOK = SCRIPTS.parent / "hooks" / "refresh-index.py"
SETTINGS = SCRIPTS.parent / "settings.json"
EXAMPLE = (SCRIPTS.parent / "skills" / "codebase-index" / "examples" / "hooks"
           / "settings.json")
CACHE = Path(".claude") / "cache" / "codebase-index"

# What settings.json has to run, spelled once. Matched whole, because
# `not-refresh-index.py` and `refresh-index.py.disabled` both contain the
# file's name and neither of them runs it — and the hook swallows every
# failure, so a registration broken that way is green everywhere else.
COMMAND = ("py -3.12 -P -c \"import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR']"
           " + '/.claude/hooks/refresh-index.py', run_name='__main__')\"")


def _load():
    spec = importlib.util.spec_from_file_location("refresh_index", HOOK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def entries_running_the_hook(settings: Path) -> dict:
    """The entries of `settings` that run this hook, keyed by hook event.

    Every event, because a read naming one compares two registrations that
    agree there and are free to disagree anywhere else.
    """
    document = json.loads(settings.read_text(encoding="utf-8"))
    running = {}
    for event, entries in (document.get("hooks") or {}).items():
        matched = [
            entry
            for entry in entries
            if any(hook.get("type") == "command" and hook.get("command") == COMMAND
                   for hook in entry.get("hooks", []))
        ]
        if matched:
            running[event] = matched
    return running


class _Completed:
    """`subprocess.run`'s answer, with only the field the hook reads."""

    def __init__(self, returncode):
        self.returncode = returncode


class _Stdin:
    """`sys.stdin` for one call as Windows hands it to `py -3.12`: the bytes
    as sent, and text decoded in the ANSI code page."""

    def __init__(self, body):
        self.buffer = io.BytesIO(body.encode("utf-8"))

    def read(self):
        return self.buffer.getvalue().decode("cp1252", "surrogateescape")


class Base(unittest.TestCase):
    """Every patch below is undone by addCleanup, and that is load-bearing.

    `subprocess`, `os` and `sys` inside the hook are the interpreter's own
    modules rather than copies of them, so an attribute set on one and left
    there reaches every other suite in the same discover run.
    """

    def setUp(self):
        self.mod = _load()
        self.spawned = []
        self.ran = []
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        # Kept before the patch below, because the hook's `subprocess` is the
        # interpreter's own: a test that needs to start a real process has to
        # hold the real function rather than the stand-in.
        self.start_process = subprocess.Popen
        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen",
            side_effect=lambda *a, **k: self.spawned.append((a, k))))
        self.run_status = 0
        self.patch(mock.patch.object(
            self.mod.subprocess, "run", side_effect=self.record_run))

    def record_run(self, *arguments, **keywords):
        """The update as the hook meets it: recorded, and answered."""
        self.ran.append((arguments, keywords))
        return _Completed(self.run_status)

    def patch(self, patcher):
        patcher.start()
        self.addCleanup(patcher.stop)
        return patcher

    def checkout(self, name, indexed=True):
        """A directory that looks like a checkout, with or without an index."""
        root = self.tmp / name
        (root / ".git").mkdir(parents=True)
        if indexed:
            (root / CACHE).mkdir(parents=True)
        return root

    def run_hook(self, body, project_dir=None):
        self.patch(mock.patch.dict(self.mod.os.environ, {}, clear=False))
        if project_dir is None:
            self.mod.os.environ.pop("CLAUDE_PROJECT_DIR", None)
        else:
            self.mod.os.environ["CLAUDE_PROJECT_DIR"] = str(project_dir)
        self.patch(mock.patch.object(self.mod.sys, "stdin", _Stdin(body)))
        self.patch(mock.patch.object(self.mod.sys, "argv", ["refresh-index.py"]))
        return self.mod.main()

    def run_event(self, event, project_dir=None):
        return self.run_hook(json.dumps(event), project_dir)

    def worker_roots(self):
        """The checkout each spawned worker was pointed at."""
        return [Path(arguments[0][-1]) for arguments, _ in self.spawned]

    def hold(self, root):
        """Hold the lock the way a worker does, for as long as the test runs."""
        handle = open(root / self.mod.LOCK, "a+b")
        self.addCleanup(handle.close)
        self.assertTrue(self.mod.grab(handle), "the lock was already held")
        return handle


class ChoosingTheCheckout(Base):
    def test_the_events_cwd_decides_the_checkout(self):
        """`/branch` moves the session into a worktree of its own, so the tree
        that changed is the event's and not the one `CLAUDE_PROJECT_DIR`
        still names."""
        edited = self.checkout("worktree")
        original = self.checkout("main")

        self.assertEqual(0, self.run_event({"cwd": str(edited)}, original))

        self.assertEqual([edited], self.worker_roots())

    def test_the_edited_file_decides_over_the_session_directory(self):
        """`guard-edit-target` admits an edit against the session's tree or
        the one it forked from, so an absolute edit into the original while
        the session sits in a worktree leaves the tree that changed stale."""
        edited = self.checkout("original")
        session = self.checkout("worktree")
        touched = edited / "src" / "Thing.cs"
        touched.parent.mkdir(parents=True)

        self.run_event({"cwd": str(session),
                        "tool_input": {"file_path": str(touched)}})

        self.assertEqual([edited.resolve()], self.worker_roots())

    def test_a_relative_edit_climbing_out_lands_in_the_other_checkout(self):
        """`guard-edit-target` admits a non-link `..` path, and the session's
        own checkout is among the lexical parents of one, so an unresolved
        walk finds the wrong `.git` first."""
        edited = self.checkout("original")
        session = self.checkout("worktree")
        (edited / "src").mkdir(parents=True)

        self.run_event({"cwd": str(session),
                        "tool_input": {"file_path": "../original/src/Thing.cs"}})

        self.assertEqual([edited.resolve()], self.worker_roots())

    def test_a_relative_edit_is_resolved_against_the_session_directory(self):
        session = self.checkout("worktree")
        (session / "src").mkdir(parents=True)

        self.run_event({"cwd": str(session),
                        "tool_input": {"file_path": "src/Thing.cs"}})

        self.assertEqual([session.resolve()], self.worker_roots())

    def test_a_checkout_path_outside_ascii_is_the_one_refreshed(self):
        """Decoded in the ANSI code page, `\u0141\u00f3d\u017a` becomes a
        directory that does not exist, and no checkout is found at all."""
        session = self.checkout("\u0141\u00f3d\u017a")

        self.run_hook(json.dumps({"cwd": str(session)}, ensure_ascii=False))

        self.assertEqual([session], self.worker_roots())

    def test_an_event_naming_no_file_falls_back_to_the_directory(self):
        session = self.checkout("worktree")

        self.run_event({"cwd": str(session), "tool_input": {}})

        self.assertEqual([session], self.worker_roots())

    def test_a_nested_cwd_walks_up_to_its_checkout(self):
        edited = self.checkout("worktree")
        deep = edited / "src" / "Services"
        deep.mkdir(parents=True)

        self.run_event({"cwd": str(deep)})

        self.assertEqual([edited], self.worker_roots())

    def test_the_project_dir_is_the_fallback(self):
        original = self.checkout("main")

        self.run_event({}, original)

        self.assertEqual([original], self.worker_roots())

    def test_an_unindexed_checkout_is_left_alone(self):
        """Unindexed is not stale. `CLAUDE_PROJECT_DIR` names an indexed
        checkout because every event Claude Code sends carries one: without it
        the assertion passes on a hook that falls through and refreshes the
        tree that did not change."""
        bare = self.checkout("fresh", indexed=False)
        indexed = self.checkout("main")

        self.assertEqual(0, self.run_event({"cwd": str(bare)}, indexed))

        self.assertEqual([], self.spawned)

    def test_a_cwd_outside_any_checkout_spawns_nothing(self):
        self.assertEqual(0, self.run_event({"cwd": str(self.tmp)}))

        self.assertEqual([], self.spawned)

    def test_a_body_that_is_not_an_event_is_silent(self):
        """From outside any checkout, because the working directory is the
        last fallback and this suite's own may be an indexed one."""
        self.patch(mock.patch.object(self.mod.os, "getcwd", lambda: str(self.tmp)))

        self.assertEqual(0, self.run_hook("<html>not json</html>"))

        self.assertEqual([], self.spawned)

    def test_with_nothing_named_the_working_directory_decides(self):
        """How `git-worktree-fork.sh` names the worktree it has just made: it
        runs the hook there with no event and no `CLAUDE_PROJECT_DIR`."""
        here = self.checkout("worktree")
        self.patch(mock.patch.object(self.mod.os, "getcwd", lambda: str(here)))

        self.assertEqual(0, self.run_hook(""))

        self.assertEqual([here], self.worker_roots())


class SeedingAWorktree(Base):
    """A linked worktree with no index takes its main checkout's, never a
    build. The checkouts are laid out as git lays them out: a `.git`
    directory in the main checkout, and a `gitdir:` file in the worktree whose
    git dir's `commondir` leads back to it."""

    def main(self, indexed=True):
        root = self.checkout("main", indexed=False)
        if indexed:
            (root / CACHE).mkdir(parents=True)
            self.database(root / CACHE / "index.sqlite", "main")
        return root

    def linked(self, main, name="probe"):
        git_dir = main / ".git" / "worktrees" / name
        git_dir.mkdir(parents=True)
        (git_dir / "commondir").write_text("../..\n", encoding="utf-8")
        root = main / ".claude" / "worktrees" / name
        root.mkdir(parents=True)
        (root / ".git").write_text(f"gitdir: {git_dir}\n", encoding="utf-8")
        return root

    @staticmethod
    def database(path, marker):
        with sqlite3.connect(str(path)) as connection:
            connection.execute("create table origin (name text)")
            connection.execute("insert into origin values (?)", (marker,))
        connection.close()

    @staticmethod
    def origin(path):
        connection = sqlite3.connect(str(path))
        try:
            return connection.execute("select name from origin").fetchone()[0]
        finally:
            connection.close()

    def test_a_worktree_with_no_index_is_seeded_and_then_updated(self):
        worktree = self.linked(self.main())

        self.run_event({"cwd": str(worktree)})
        self.assertEqual([worktree], self.worker_roots())
        self.mod.work(worktree)

        self.assertEqual("main", self.origin(worktree / CACHE / "index.sqlite"))
        self.assertEqual(1, len(self.ran), "the seed was never updated")
        self.assertEqual(str(worktree), self.ran[0][1]["cwd"])

    def test_only_the_index_crosses(self):
        """The main checkout's memory and configuration are its own state,
        and a seed in flight is renamed in rather than left beside it."""
        main = self.main()
        (main / CACHE / "memory.sqlite").write_text("", encoding="utf-8")
        (main / CACHE / "config.json").write_text("{}", encoding="utf-8")
        worktree = self.linked(main)
        self.run_event({"cwd": str(worktree)})

        self.mod.work(worktree)

        names = {path.name for path in (worktree / CACHE).iterdir()}
        self.assertEqual({"index.sqlite", "refresh.lock"}, names)

    def test_a_wal_left_by_an_earlier_seed_is_not_replayed(self):
        main = self.main()
        worktree = self.linked(main)
        (worktree / CACHE).mkdir(parents=True)
        leftover = worktree / CACHE / "index.sqlite-wal"
        leftover.write_bytes(b"not this seed's")

        self.mod.seed(worktree)

        self.assertFalse(leftover.exists())
        self.assertEqual("main", self.origin(worktree / CACHE / "index.sqlite"))

    def test_a_source_gone_before_the_copy_is_not_recreated(self):
        """It can vanish between the look and the copy, and opening it then
        must not leave an empty index in the main checkout."""
        main = self.main()
        worktree = self.linked(main)
        source = main / CACHE / "index.sqlite"
        (worktree / CACHE).mkdir(parents=True)
        self.patch(mock.patch.object(self.mod, "seed_source", lambda _root: source))
        source.unlink()

        self.mod.seed(worktree)

        self.assertFalse(source.exists())
        self.assertFalse((worktree / CACHE / "index.sqlite").exists())

    @unittest.skipUnless(sys.platform == "win32", "an extended-length path is a Windows form")
    def test_an_extended_length_source_is_seeded(self):
        """`resolve()` can return one, and a UNC path takes the same route:
        the URI's authority stays empty, or SQLite refuses to open it."""
        main = self.main()
        worktree = self.linked(main)
        (worktree / CACHE).mkdir(parents=True)
        source = Path("\\\\?\\" + str((main / CACHE / "index.sqlite").resolve()))
        self.patch(mock.patch.object(self.mod, "seed_source", lambda _root: source))

        self.mod.seed(worktree)

        self.assertEqual("main", self.origin(worktree / CACHE / "index.sqlite"))

    def test_a_main_checkout_with_no_index_seeds_nothing(self):
        """And builds nothing: `update` refuses a cache with no index, and a
        full build is the cost this exists to avoid."""
        worktree = self.linked(self.main(indexed=False))

        self.assertEqual(0, self.run_event({"cwd": str(worktree)}))

        self.assertEqual([], self.spawned)
        self.assertFalse((worktree / CACHE).exists())

    def test_a_worktree_index_is_left_as_it_is(self):
        worktree = self.linked(self.main())
        (worktree / CACHE).mkdir(parents=True)
        self.database(worktree / CACHE / "index.sqlite", "worktree")
        self.run_event({"cwd": str(worktree)})

        self.mod.work(worktree)

        self.assertEqual("worktree", self.origin(worktree / CACHE / "index.sqlite"))
        self.assertEqual(1, len(self.ran))

    def test_the_main_checkout_is_never_seeded(self):
        """Its `.git` is a directory, so it is nobody's worktree."""
        main = self.main(indexed=False)

        self.assertIsNone(self.mod.main_checkout(main))
        self.assertEqual(0, self.run_event({"cwd": str(main)}))
        self.assertEqual([], self.spawned)

    def test_a_gitdir_with_no_commondir_is_not_a_worktree(self):
        """A submodule's `.git` is a `gitdir:` file too, with no main
        checkout behind it."""
        main = self.main()
        worktree = self.linked(main)
        (main / ".git" / "worktrees" / "probe" / "commondir").unlink()

        self.assertIsNone(self.mod.main_checkout(worktree))
        self.assertEqual(0, self.run_event({"cwd": str(worktree)}))
        self.assertEqual([], self.spawned)

    def test_a_bare_repository_has_no_index_to_give(self):
        main = self.main()
        worktree = self.linked(main)
        bare = self.tmp / "bare.git"
        (main / ".git").rename(bare)
        (worktree / ".git").write_text(
            f"gitdir: {bare / 'worktrees' / 'probe'}\n", encoding="utf-8")

        self.assertIsNone(self.mod.main_checkout(worktree))

    def test_a_link_at_the_main_index_is_not_followed(self):
        main = self.main(indexed=False)
        (main / CACHE).mkdir(parents=True)
        elsewhere = self.tmp / "elsewhere.sqlite"
        self.database(elsewhere, "elsewhere")
        try:
            os.symlink(elsewhere, main / CACHE / "index.sqlite")
        except (OSError, NotImplementedError) as refused:
            self.skipTest(f"no symbolic link here: {refused}")
        worktree = self.linked(main)

        self.assertIsNone(self.mod.seed_source(worktree))
        self.assertEqual(0, self.run_event({"cwd": str(worktree)}))
        self.assertEqual([], self.spawned)


class OneRefreshAtATime(Base):
    """`codebase-index update` does not serialise, so this has to.

    Overlapping runs against one checkout leave a single winner and
    `database is locked` for the rest, and the loser may be the run carrying
    the newest edit — a stale index, said nothing about, because the streams
    are discarded.
    """

    def test_the_worker_refreshes_once_for_one_request(self):
        root = self.checkout("main")
        self.run_event({"cwd": str(root)})

        self.assertEqual(0, self.mod.work(root))

        self.assertEqual(1, len(self.ran))

    def test_the_worker_runs_again_for_a_request_made_while_it_worked(self):
        """The marker is what makes an edit arriving mid-refresh reach the
        worker already running rather than starting a second."""
        root = self.checkout("main")
        self.run_event({"cwd": str(root)})

        def refresh_and_edit(*_a, **_k):
            self.ran.append(("refresh", {}))
            if len(self.ran) == 1:
                self.mod.request(root)
            return _Completed(0)

        self.patch(mock.patch.object(
            self.mod.subprocess, "run", side_effect=refresh_and_edit))

        self.mod.work(root)

        self.assertEqual(2, len(self.ran))

    def test_a_request_made_as_the_lock_is_released_is_not_lost(self):
        """The window: the worker has already looked and found nothing, and
        the edit lands before the descriptor closes. Its hook sees `busy` as
        true and leaves a marker rather than a worker, so the one finishing
        has to look once more after letting go."""
        root = self.checkout("main")
        looks = []
        looking = self.mod.take_request

        def look_then_edit(target):
            answer = looking(target)
            looks.append(answer)
            if len(looks) == 1 and not answer:
                self.mod.request(target)
            return answer

        self.patch(mock.patch.object(
            self.mod, "take_request", side_effect=look_then_edit))

        self.mod.work(root)

        self.assertEqual(1, len(self.ran))
        self.assertFalse((root / self.mod.PENDING).exists())

    def test_a_failed_update_keeps_its_request(self):
        """A request marked served by an update that failed is an edit that
        never reaches the index: the streams are discarded, so nothing else
        would ever say so."""
        root = self.checkout("main")
        self.run_status = 1
        self.mod.request(root)
        self.patch(mock.patch.object(self.mod.time, "sleep", lambda _pause: None))

        self.assertEqual(0, self.mod.work(root))

        self.assertEqual(self.mod.REFRESH_ATTEMPTS, len(self.ran))
        self.assertTrue((root / self.mod.PENDING).exists())

    def test_a_retry_that_works_serves_the_request(self):
        """Contention is transient, which is the whole reason to try again."""
        root = self.checkout("main")
        self.mod.request(root)
        self.patch(mock.patch.object(self.mod.time, "sleep", lambda _pause: None))

        def fail_once(*arguments, **keywords):
            self.ran.append((arguments, keywords))
            return _Completed(1 if len(self.ran) == 1 else 0)

        self.patch(mock.patch.object(
            self.mod.subprocess, "run", side_effect=fail_once))

        self.mod.work(root)

        self.assertEqual(2, len(self.ran))
        self.assertFalse((root / self.mod.PENDING).exists())

    def test_the_attempts_are_spaced_and_the_last_is_not_followed(self):
        """An update that answered a contended index answers the same one if
        it is started again at once."""
        root = self.checkout("main")
        self.run_status = 1
        self.mod.request(root)
        pauses = []
        self.patch(mock.patch.object(self.mod.time, "sleep", pauses.append))

        self.mod.work(root)

        self.assertEqual(
            [self.mod.RETRY_PAUSE] * (self.mod.REFRESH_ATTEMPTS - 1), pauses)

    def test_a_worker_that_cannot_take_the_lock_does_nothing(self):
        """Its marker belongs to whoever holds the lock, and taking it would
        be the second update against one index that the lock exists to stop."""
        root = self.checkout("main")
        self.mod.request(root)
        self.hold(root)

        self.assertEqual(0, self.mod.work(root))

        self.assertEqual([], self.ran)
        self.assertTrue((root / self.mod.PENDING).exists())

    def test_a_held_lock_is_reported_busy_and_starts_no_worker(self):
        root = self.checkout("main")
        self.hold(root)

        self.assertTrue(self.mod.busy(root))

        self.assertEqual(0, self.run_event({"cwd": str(root)}))
        self.assertEqual([], self.spawned)
        self.assertTrue((root / self.mod.PENDING).exists())

    def test_an_unheld_lock_is_not_busy(self):
        root = self.checkout("main")

        self.assertFalse(self.mod.busy(root))

    def test_a_lock_holder_is_the_only_worker_that_refreshes(self):
        """Processes rather than calls, because a lock refuses a claimant in
        another process and nothing else. The second request is made while
        the first refresh is still running: that is the only moment at which
        two workers could both have something to do, and a case that never
        reaches it cannot tell a lock from no lock at all."""
        root = self.checkout("main")
        ledger = self.tmp / "ledger.txt"
        ledger.write_text("", encoding="utf-8")
        release = self.tmp / "release"
        child = self.tmp / "child.py"
        child.write_text(
            CHILD.format(hook=HOOK, root=root, ledger=ledger, release=release,
                         holder=HOLDER),
            encoding="utf-8")

        def recorded():
            return [line
                    for line in ledger.read_text(encoding="utf-8").splitlines()
                    if line]

        self.mod.request(root)
        holder = self.start_process([sys.executable, str(child), HOLDER])
        deadline = time.time() + 60
        while not recorded():
            self.assertLess(time.time(), deadline, "the holder never refreshed")
            time.sleep(0.02)

        self.mod.request(root)
        latecomers = [
            self.start_process([sys.executable, str(child), f"w{index}"])
            for index in range(1, 4)
        ]
        for worker in latecomers:
            worker.wait(timeout=60)
        while_held = recorded()

        release.write_text("", encoding="utf-8")
        holder.wait(timeout=60)
        lines = recorded()

        self.assertEqual([HOLDER + " in"], while_held, while_held)
        depth = 0
        for line in lines:
            depth += 1 if line.endswith(" in") else -1
            self.assertLessEqual(depth, 1, lines)
        self.assertEqual(2, sum(1 for line in lines if line.endswith(" in")), lines)
        self.assertFalse((root / self.mod.PENDING).exists(), lines)


HOLDER = "w0"

# The worker each process in the case above runs. Only the holder waits, and
# for two reasons: it has to still be refreshing when the others arrive, and
# a claimant that got through has to be able to say so without the case
# discovering it through a timeout.
CHILD = """
import importlib.util, pathlib, sys, time

spec = importlib.util.spec_from_file_location("refresh_index", r"{hook}")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

root = pathlib.Path(r"{root}")
ledger = pathlib.Path(r"{ledger}")
release = pathlib.Path(r"{release}")
me = sys.argv[1]


def note(what):
    with open(ledger, "a", encoding="utf-8") as out:
        print(me, what, file=out)


def held_refresh(_root):
    note("in")
    if me == "{holder}":
        deadline = time.time() + 30
        while not release.exists() and time.time() < deadline:
            time.sleep(0.02)
    note("out")
    return True


mod.refresh = held_refresh
sys.exit(mod.work(root))
"""


class Refreshing(Base):
    def test_the_call_is_the_cli_with_auto_update_off(self):
        """An unpinned newer package rewrites the tracked skill when it runs,
        and this calls the CLI rather than the wrapper that would stop it."""
        root = self.checkout("main")

        self.mod.refresh(root)

        arguments, keywords = self.ran[0]
        self.assertEqual(["codebase-index", "update"], arguments[0])
        self.assertEqual("1", keywords["env"]["CBX_NO_SKILL_AUTO_UPDATE"])
        self.assertEqual(1, len(self.ran), "the fallback ran as well")

    def test_a_failing_update_is_not_a_refresh(self):
        """The status is the only thing left that says the index moved, so
        it is the thing the caller is given."""
        self.run_status = 1

        self.assertFalse(self.mod.refresh(self.checkout("main")))

    def test_a_working_update_is_a_refresh(self):
        """The subject, because a function that always answered false would
        satisfy the case above."""
        self.assertTrue(self.mod.refresh(self.checkout("main")))

    def test_no_stream_is_kept(self):
        """A hook's output is not a report anybody reads."""
        root = self.checkout("main")

        self.mod.refresh(root)

        keywords = self.ran[0][1]
        for stream in ("stdin", "stdout", "stderr"):
            self.assertEqual(self.mod.subprocess.DEVNULL, keywords[stream])

    def test_a_missing_console_script_falls_back_to_the_module(self):
        """`py -3.12 -m pip` is a supported install and need not put the
        console script on PATH, which is why both `cbx` wrappers fall back
        the same way."""
        attempts = []

        def absent_script(command, **keywords):
            attempts.append(command)
            if command[0] == "codebase-index":
                raise OSError("no such executable")
            self.ran.append(((command,), keywords))
            return _Completed(0)

        self.patch(mock.patch.object(
            self.mod.subprocess, "run", side_effect=absent_script))

        self.mod.refresh(self.checkout("main"))

        self.assertEqual(2, len(attempts), attempts)
        self.assertEqual(
            [self.mod.sys.executable, "-P", "-m", "codebase_index", "update"],
            attempts[1])

    def test_neither_form_running_is_silent(self):
        """An index that cannot refresh is not a reason to fail the edit."""
        self.patch(mock.patch.object(
            self.mod.subprocess, "run", side_effect=OSError("nothing runnable")))

        self.assertFalse(self.mod.refresh(self.checkout("main")))
        self.assertEqual([], self.ran)


class Registration(Base):
    def test_settings_calls_it_after_every_shell_call(self):
        """A commit, a pull, a formatter or an edit script moves the tree
        through either shell tool with no edit tool behind it."""
        matched = {
            alternative.strip()
            for entry in entries_running_the_hook(SETTINGS).get("PostToolUse", [])
            for alternative in entry.get("matcher", "").split("|")
        }

        for tool in ("Bash", "PowerShell"):
            self.assertIn(tool, matched)

    def test_settings_calls_it_on_every_edit(self):
        """Every verdict above is silent if settings never runs the file.

        The entries are narrowed to the ones that run this hook before their
        matchers are read. Asking for a command and for a matcher separately
        is satisfied by a second entry that has one and not the other."""
        mine = entries_running_the_hook(SETTINGS).get("PostToolUse", [])

        # Split before comparing: `"Edit" in "NotebookEdit|MultiEdit"` is
        # true, so a matcher that had lost plain Edit would satisfy a
        # substring test while the primary edit surface went unwatched.
        matched = {
            alternative.strip()
            for entry in mine
            for alternative in entry.get("matcher", "").split("|")
        }

        self.assertTrue(mine, f"nothing in {SETTINGS} runs the hook")
        for tool in ("Edit", "Write", "MultiEdit", "NotebookEdit"):
            self.assertIn(tool, matched, mine)

    def test_settings_refreshes_at_session_start(self):
        """The moves no edit makes: a merge, a switch or a pull."""
        self.assertIn("SessionStart", entries_running_the_hook(SETTINGS))


class RegisteredCommand(unittest.TestCase):
    def test_a_missing_hook_file_does_not_exit_2(self):
        """Python exits 2 on a file it cannot open, a worktree emptied under a
        running session, and under `PostToolUse` that is an error after every
        call; through `runpy` the same failure exits 1."""
        with tempfile.TemporaryDirectory() as gone:
            done = subprocess.run(
                [sys.executable, *shlex.split(COMMAND)[2:]], input=b"{}", capture_output=True,
                timeout=30, check=False, env=dict(os.environ, CLAUDE_PROJECT_DIR=gone))

        self.assertNotEqual(2, done.returncode, done.stderr)
        self.assertIn(b"FileNotFoundError", done.stderr)


class DocumentedConfiguration(unittest.TestCase):
    """The example carries the installed block, which only a test can keep true.

    `docs/harness-boundaries.md` says the two are the same block, and Claude
    Code never reads anything under `examples/` — so a divergence costs
    nothing at the moment it happens and everything to whoever copies it.
    """

    def test_the_example_is_the_installed_block(self):
        self.assertEqual(
            entries_running_the_hook(SETTINGS), entries_running_the_hook(EXAMPLE))

    def test_the_example_runs_this_hook_at_all(self):
        """The subject, because two empty lists are equal."""
        self.assertTrue(entries_running_the_hook(EXAMPLE))


class RestoresWhatItPatched(unittest.TestCase):
    """The canary, and it sorts after the suites above on purpose.

    A leaked patch is invisible to the file that leaks it and fatal to every
    file loaded after it, so the suite that would notice has to be one that
    runs later.
    """

    def test_the_interpreters_modules_are_as_they_were(self):
        import os as real_os
        import subprocess as real_subprocess
        import sys as real_sys

        self.assertFalse(isinstance(real_subprocess.Popen, mock.MagicMock))
        self.assertFalse(isinstance(real_subprocess.run, mock.MagicMock))
        self.assertNotIsInstance(real_os.environ, dict)
        self.assertTrue(hasattr(real_sys.stdin, "fileno"))


if __name__ == "__main__":
    unittest.main()
