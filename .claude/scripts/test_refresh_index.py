"""What `.claude/hooks/refresh-index.py` refreshes, and what it leaves alone.

The hook suppresses every failure, so its exit status says nothing. The tree
refreshed is the one the edit landed in, exactly one refresh owns it, or none.
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
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
NO_CWD_SEARCH = "NoDefaultCurrentDirectoryInExePath"
HOOK = SCRIPTS.parent / "hooks" / "refresh-index.py"
SETTINGS = SCRIPTS.parent / "settings.json"
EXAMPLE = (SCRIPTS.parent / "skills" / "codebase-index" / "examples" / "hooks"
           / "settings.json")
CACHE = Path(".claude") / "cache" / "codebase-index"
INDEX = CACHE / "index.sqlite"

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


class _Child:
    """A process `Popen` started, finished at once with a status."""

    def __init__(self, returncode, pid=4242):
        self.returncode = returncode
        self.pid = pid

    def wait(self, timeout=None):
        return self.returncode

    def kill(self):
        pass


class _Stdin:
    """`sys.stdin` for one call as Windows hands it to `py -3.12`: the bytes
    as sent, and text decoded in the ANSI code page."""

    def __init__(self, body):
        self.buffer = io.BytesIO(body.encode("utf-8"))

    def read(self):
        return self.buffer.getvalue().decode("cp1252", "surrogateescape")


class Base(unittest.TestCase):
    """Every patch below is undone by addCleanup, and that is load-bearing.

    `subprocess`, `os` and `sys` inside the hook are the interpreter's own, so
    an attribute left set reaches every other suite in the same discover run.
    """

    def setUp(self):
        self.mod = _load()
        self.spawned = []
        self.ran = []
        self.owner = None
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        # The checkout the hook file sits in: the first one a case makes.
        self.patch(mock.patch.object(
            self.mod, "home", side_effect=lambda: self.owner or self.tmp / "no-owner"))
        # Kept before the patch below, because the hook's `subprocess` is the
        # interpreter's own: a test that needs to start a real process has to
        # hold the real function rather than the stand-in.
        self.start_process = subprocess.Popen
        self.run_status = 0
        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=self.record_start))

    def record_start(self, *arguments, **keywords):
        """A worker as the hook starts it, or an update as the worker does:
        recorded, and the update answered."""
        if "--worker" in arguments[0]:
            self.spawned.append((arguments, keywords))
            return _Child(0)
        self.ran.append((arguments, keywords))
        return _Child(self.run_status)

    def patch(self, patcher):
        patcher.start()
        self.addCleanup(patcher.stop)
        return patcher

    def checkout(self, name, indexed=True):
        """A checkout of the case's repository, with or without an index. The
        first is the main checkout the hook file sits in, and each later one
        a linked worktree of it."""
        if self.owner is None:
            root = self.tmp / name
            (root / ".git").mkdir(parents=True)
            self.owner = root
        else:
            root = self.linked(self.owner, name, at=self.tmp / name)
        if indexed:
            self.index(root)
        return root

    def linked(self, main, name="probe", at=None):
        git_dir = main / ".git" / "worktrees" / name
        git_dir.mkdir(parents=True)
        (git_dir / "commondir").write_text("../..\n", encoding="utf-8")
        root = at or main / ".claude" / "worktrees" / name
        root.mkdir(parents=True)
        (root / ".git").write_text(f"gitdir: {git_dir}\n", encoding="utf-8")
        return root

    @staticmethod
    def database(path, marker):
        """A small database in the WAL mode the package leaves its index in."""
        connection = sqlite3.connect(str(path))
        try:
            connection.execute("pragma journal_mode=wal")
            connection.execute("create table origin (name text)")
            connection.execute("insert into origin values (?)", (marker,))
            connection.commit()
        finally:
            connection.close()

    def index(self, root, marker="main"):
        """A written index at the root's cache path."""
        (root / CACHE).mkdir(parents=True, exist_ok=True)
        self.database(root / INDEX, marker)
        return root / INDEX

    def apart(self, name):
        """An indexed checkout of a repository of its own."""
        root = self.tmp / name
        (root / ".git").mkdir(parents=True)
        self.index(root)
        return root

    def link_dir(self, link, destination):
        """A directory link, by whichever primitive this platform grants."""
        try:
            os.symlink(destination, link, target_is_directory=True)
        except (OSError, NotImplementedError) as refused:
            if sys.platform != "win32":
                self.skipTest(f"no symbolic link here: {refused}")
            # The real `Popen`, which `setUp` replaces.
            junction = self.start_process(
                ["cmd", "/c", "mklink", "/J", str(link), str(destination)],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if junction.wait(timeout=30) != 0:
                self.skipTest("no directory link here")

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
        path = self.mod.lock_path(root)
        path.parent.mkdir(parents=True, exist_ok=True)
        handle = open(path, "a+b")
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

    def test_the_worker_holds_no_directory_of_the_checkout(self):
        root = self.checkout("worktree")
        self.run_event({"cwd": str(root)})

        self.assertEqual([root], self.worker_roots())
        working = Path(self.spawned[0][1]["cwd"]).resolve()
        # Not `root.parent` either: a worker's directory is where Windows looks
        # first for a program named bare, and that is a checkout's parent, or
        # for a worktree a directory of the main checkout.
        self.assertEqual(Path(tempfile.gettempdir()).resolve(), working)
        self.assertNotEqual(root.resolve().parent, working)

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

    def test_another_repository_is_refused(self):
        """The hook belongs to one repository; a checkout of another that
        holds a cache of its own is no concern of its."""
        ours = self.checkout("main")
        theirs = self.apart("other")

        self.assertEqual(0, self.run_event({"cwd": str(theirs)}, ours))

        self.assertEqual([], self.spawned)
        self.assertFalse(self.mod.pending_path(theirs).exists())

    def test_an_event_naming_a_file_is_never_answered_with_cwd(self):
        """A file that cannot be placed refreshes nothing, never the tree
        the session stands in, which the change did not touch."""
        session = self.checkout("main")
        self.addCleanup(os.chdir, os.getcwd())
        os.chdir(session)
        unplaced = {
            "a relative path with no directory to be relative to":
                {"tool_input": {"file_path": "src/Thing.cs"}},
            "a notebook path likewise":
                {"tool_input": {"notebook_path": "n.ipynb"}},
        }
        for name, event in unplaced.items():
            with self.subTest(name):
                self.assertEqual(0, self.run_event(event, session))
                self.assertEqual([], self.spawned)

        real_resolve = Path.resolve

        def refuse(path, *arguments, **keywords):
            if path.name == "Thing.cs":
                raise OSError("a path the system cannot resolve")
            return real_resolve(path, *arguments, **keywords)

        self.patch(mock.patch.object(Path, "resolve", autospec=True, side_effect=refuse))
        event = {"cwd": str(session), "tool_input": {"file_path": str(session / "Thing.cs")}}
        self.assertEqual(0, self.run_event(event, session))
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
        with open(worktree / INDEX, "rb") as seeded:
            self.assertEqual(bytes((2, 2)), seeded.read(100)[18:20])
        self.assertEqual(1, len(self.ran), "the seed was never updated")
        self.assertEqual(str(worktree), self.ran[0][1]["cwd"])

    def test_a_copy_without_a_wal_header_is_never_renamed_in(self):
        """Such a copy is one the package's own readers refuse: it must not
        reach the index path, nor leave the partial it was made in."""
        main = self.main()
        connection = sqlite3.connect(str(main / INDEX), isolation_level=None)
        try:
            connection.execute("pragma journal_mode=delete")
        finally:
            connection.close()
        worktree = self.linked(main)
        (worktree / CACHE).mkdir(parents=True)

        self.mod.seed(worktree)

        self.assertFalse((worktree / INDEX).exists())
        self.assertEqual([], [path.name for path in (worktree / CACHE).iterdir()])

    def test_a_killed_workers_partial_is_removed_by_the_next_seed(self):
        """The partial is named for the worker that made it, so one a killed
        worker left is never met by a later pid, and sits beside the index
        until something removes it."""
        main = self.main()
        worktree = self.linked(main)
        (worktree / CACHE).mkdir(parents=True)
        stale = worktree / CACHE / "index.sqlite.seed.99999999"
        stale.write_bytes(b"half a copy")
        journal = stale.with_name(stale.name + "-journal")
        journal.write_bytes(b"")

        self.mod.seed(worktree)

        self.assertFalse(stale.exists())
        self.assertFalse(journal.exists())
        self.assertEqual("main", self.origin(worktree / INDEX))

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
        self.assertEqual({"index.sqlite"}, names)

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

    def test_nothing_is_seeded_or_built_without_a_main_index(self):
        """A cache directory is not an index: `update` on one is a build,
        which is the cost this exists to avoid and the one the agent's turn
        cannot afford."""
        main = self.main(indexed=False)
        (main / CACHE).mkdir(parents=True)
        worktree = self.linked(main)
        (worktree / CACHE).mkdir(parents=True)

        self.assertEqual(0, self.run_event({"cwd": str(worktree)}))
        self.assertEqual(0, self.run_event({"cwd": str(main)}))
        self.mod.request(worktree)
        self.mod.work(worktree)

        self.assertEqual([], self.spawned)
        self.assertEqual([], self.ran)
        self.assertFalse((worktree / INDEX).exists())
        self.assertFalse(self.mod.refreshable(worktree))

    def test_only_a_written_index_counts_as_one(self):
        """A first build creates the file before it writes SQLite's header,
        and `update` cannot open what that leaves."""
        root = self.checkout("main", indexed=False)
        self.assertFalse(self.mod.written(root))
        (root / CACHE).mkdir(parents=True)
        (root / INDEX).write_bytes(b"")
        self.assertFalse(self.mod.written(root))
        (root / INDEX).write_bytes(b"\0" * 100)
        self.assertTrue(self.mod.written(root))

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

    Overlapping runs leave one winner and `database is locked` for the rest,
    and the loser may carry the newest edit: a stale index, said nothing about.
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
            return _Child(0)

        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=refresh_and_edit))

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
        self.assertFalse(self.mod.pending_path(root).exists())

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
        self.assertTrue(self.mod.pending_path(root).exists())

    def test_a_retry_that_works_serves_the_request(self):
        """Contention is transient, which is the whole reason to try again."""
        root = self.checkout("main")
        self.mod.request(root)
        self.patch(mock.patch.object(self.mod.time, "sleep", lambda _pause: None))

        def fail_once(*arguments, **keywords):
            self.ran.append((arguments, keywords))
            return _Child(1 if len(self.ran) == 1 else 0)

        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=fail_once))

        self.mod.work(root)

        self.assertEqual(2, len(self.ran))
        self.assertFalse(self.mod.pending_path(root).exists())

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

    def work_apart(self, root, bound=15):
        """`work` on a thread, so a worker that spins fails here and does not
        hold the run."""
        worker = threading.Thread(target=self.mod.work, args=(root,), daemon=True)
        worker.start()
        worker.join(bound)
        return worker

    def test_a_marker_that_never_goes_costs_a_bounded_number_of_tries(self):
        """A marker that is there and cannot be taken is not an absent one:
        read as absent, the worker looked, found the file, took the lock
        and looked again, for ever."""
        root = self.checkout("main")
        self.mod.request(root)
        with mock.patch.object(self.mod, "take_request", return_value=None) as take, \
                mock.patch.object(self.mod.time, "sleep") as sleep:
            worker = self.work_apart(root)

        self.assertFalse(worker.is_alive(), "the worker is spinning on the marker")
        self.assertEqual([], self.ran)
        self.assertEqual(self.mod.MARKER_RETRIES + 1, take.call_count)
        self.assertEqual(self.mod.MARKER_RETRIES, sleep.call_count)
        self.assertFalse(self.mod.busy(root), "the lock was kept")

    def test_a_directory_where_the_marker_goes_costs_the_same(self):
        root = self.checkout("main")
        self.mod.pending_path(root).mkdir(parents=True)
        self.patch(mock.patch.object(self.mod.time, "sleep", lambda _pause: None))

        worker = self.work_apart(root)

        self.assertFalse(worker.is_alive(), "the worker is spinning on the marker")
        self.assertEqual([], self.ran)

    @unittest.skipUnless(sys.platform == "win32", "only Windows refuses to remove an open file")
    def test_a_marker_the_hook_holds_open_is_served_when_it_is_closed(self):
        """The hook has the marker open while it makes it, and Windows
        refuses the worker's removal until it lets go."""
        root = self.checkout("main")
        self.mod.request(root)
        handle = open(self.mod.pending_path(root), "a", encoding="ascii")
        self.addCleanup(handle.close)
        closer = threading.Timer(0.3, handle.close)
        self.addCleanup(closer.cancel)
        closer.start()

        self.mod.work(root)

        self.assertEqual(1, len(self.ran))
        self.assertFalse(self.mod.pending_path(root).exists())

    def test_a_worker_that_cannot_take_the_lock_does_nothing(self):
        """Its marker belongs to whoever holds the lock, and taking it would
        be the second update against one index that the lock exists to stop."""
        root = self.checkout("main")
        self.mod.request(root)
        self.hold(root)

        self.assertEqual(0, self.mod.work(root))

        self.assertEqual([], self.ran)
        self.assertTrue(self.mod.pending_path(root).exists())

    def test_a_held_lock_is_reported_busy_and_starts_no_worker(self):
        root = self.checkout("main")
        self.hold(root)

        self.assertTrue(self.mod.busy(root))

        self.assertEqual(0, self.run_event({"cwd": str(root)}))
        self.assertEqual([], self.spawned)
        self.assertTrue(self.mod.pending_path(root).exists())

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
                         holder=HOLDER, owner=self.owner),
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
        self.assertFalse(self.mod.pending_path(root).exists(), lines)


class TheWorkerJudgesItsRoot(Base):
    """The worker is started with a path as an argument, and runs for as long
    as edits keep arriving, so what was true of the root when it was
    chosen is asked again at the worker's entry and before every update."""

    def worker(self, *arguments):
        self.patch(mock.patch.object(self.mod.sys, "argv", ["refresh-index.py", *arguments]))
        with mock.patch.object(self.mod, "work", return_value=0) as work:
            self.assertEqual(0, self.mod.main())
        return work

    def test_the_worker_entry_point_judges_its_root_again(self):
        ours = self.checkout("main")
        theirs = self.apart("other")
        bare = self.checkout("bare", indexed=False)
        (self.tmp / "loose").mkdir()

        self.worker("--worker", str(ours)).assert_called_once_with(ours)
        self.worker("--worker", str(bare)).assert_called_once_with(bare)
        # With the main checkout's index gone, the worktree has none to seed.
        (ours / INDEX).unlink()
        for name, arguments in {
            "another repository": ("--worker", str(theirs)),
            "a checkout with no index and none to seed": ("--worker", str(bare)),
            "a directory that is no checkout": ("--worker", str(self.tmp / "loose")),
            "an extra argument": ("--worker", str(ours), "extra"),
            "no root": ("--worker",),
        }.items():
            with self.subTest(name):
                self.worker(*arguments).assert_not_called()

    def test_a_root_that_stops_qualifying_mid_drain_is_not_updated_again(self):
        """Each request is a full update, and a worker can serve many: a
        checkout of another repository or a removed index landing after the
        first is caught before the second."""
        def lose_the_index(root):
            (root / INDEX).unlink()

        def leave_the_repository(root):
            (root / ".git").write_text(
                f"gitdir: {self.apart('elsewhere') / '.git'}\n", encoding="utf-8")

        main = self.checkout("main")
        for slug, change in {"index": lose_the_index,
                             "repository": leave_the_repository}.items():
            with self.subTest(slug):
                self.ran.clear()
                root = self.linked(main, name=slug)
                self.index(root)

                def update_and_change(*arguments, **keywords):
                    self.ran.append((arguments, keywords))
                    change(root)
                    self.mod.request(root)
                    return _Child(0)

                self.patch(mock.patch.object(
                    self.mod.subprocess, "Popen", side_effect=update_and_change))
                self.mod.request(root)
                self.mod.work(root)

                self.assertEqual(1, len(self.ran))

    def test_an_index_lost_while_a_retry_waits_is_not_built(self):
        """The pause before a retry is long enough for the index to go, and an
        update then would build one the checkout never asked for."""
        root = self.checkout("main")

        def fail_and_lose_the_index(*arguments, **keywords):
            self.ran.append((arguments, keywords))
            (root / INDEX).unlink(missing_ok=True)
            return _Child(1)

        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=fail_and_lose_the_index))
        self.patch(mock.patch.object(self.mod.time, "sleep", lambda _pause: None))
        self.mod.request(root)

        self.mod.work(root)

        self.assertEqual(1, len(self.ran))
        self.assertFalse(self.mod.pending_path(root).exists())


class StateOutOfReach(Base):
    """The lock and the marker are the hook's own writes, so they live where
    no branch can aim them: a tracked link under the checkout's cache is
    followed by `open`, and the write lands wherever it points."""

    def aimed(self):
        elsewhere = self.tmp / "aimed-at"
        elsewhere.mkdir()
        return elsewhere

    def test_a_linked_cache_receives_none_of_the_hooks_state(self):
        root = self.checkout("main", indexed=False)
        elsewhere = self.aimed()
        (elsewhere / "codebase-index").mkdir()
        (root / ".claude").mkdir()
        self.link_dir(root / ".claude" / "cache", elsewhere)

        self.run_event({"cwd": str(root)})
        self.mod.request(root)
        self.mod.busy(root)

        self.assertEqual([], os.listdir(elsewhere / "codebase-index"))

    def test_the_state_is_in_the_git_directory_under_a_name_for_the_root(self):
        main = self.checkout("main")
        worktree = self.linked(main)

        for root in (main, worktree):
            self.mod.request(root)
        pending = [self.mod.pending_path(root) for root in (main, worktree)]

        for path in pending:
            self.assertEqual(main / ".git" / "index-refresh", path.parent)
            self.assertTrue(path.is_file(), path)
        self.assertNotEqual(*pending)
        self.assertEqual(
            self.mod.pending_path(worktree),
            self.mod.pending_path(worktree.parent / ".." / worktree.parent.name / worktree.name))

    def test_a_python_without_isjunction_still_judges_a_link(self):
        """`os.path.isjunction` is Python 3.12's, and `git-worktree-fork.sh`
        runs the hook with whatever `python3` it finds."""
        root = self.checkout("main")
        self.patch(mock.patch.object(self.mod.os.path, "isjunction", create=True))
        del self.mod.os.path.isjunction

        self.assertFalse(self.mod.linked(root))
        self.assertIsNotNone(self.mod.state_dir())

    def test_a_link_at_the_state_directory_refreshes_nothing(self):
        root = self.checkout("main")
        elsewhere = self.aimed()
        self.link_dir(root / ".git" / "index-refresh", elsewhere)

        self.assertIsNone(self.mod.lock_path(root))
        self.assertEqual(0, self.run_event({"cwd": str(root)}))
        self.mod.request(root)

        self.assertEqual([], self.spawned)
        self.assertEqual([], os.listdir(elsewhere))

    def test_no_git_directory_is_no_state(self):
        root = self.tmp / "loose"
        root.mkdir()
        self.owner = root

        self.assertIsNone(self.mod.lock_path(root))
        self.assertFalse(self.mod.busy(root))


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
mod.home = lambda: pathlib.Path(r"{owner}")

root = pathlib.Path(r"{root}")
ledger = pathlib.Path(r"{ledger}")
release = pathlib.Path(r"{release}")
me = sys.argv[1]


def note(what):
    with open(ledger, "a", encoding="utf-8") as out:
        print(me, what, file=out)


def held_refresh(_root, _handle=None):
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


class TheIndexerAKilledWorkerLeftBehind(Base):
    """The lock is the worker's, and ends with it, but the indexer it started
    is a process of its own: a worker killed mid-run leaves one still writing
    the cache, and the next edit would start a second beside it. So the lock
    file records the indexer's pid, and a worker that takes the lock and finds
    that process running leaves the tree to it."""

    def child(self):
        """A process that runs until it is told not to, stopped by cleanup."""
        child = self.start_process(
            [sys.executable, "-c", "import sys; sys.stdin.read()"], stdin=subprocess.PIPE)

        def stop():
            # Kill before waiting: a failing assertion must not leave the
            # suite waiting on a child nothing has told to stop.
            child.kill()
            child.wait(timeout=30)
            child.stdin.close()

        self.addCleanup(stop)
        return child, stop

    def record(self, root, text):
        """What a worker killed mid-update left in the lock file."""
        path = self.mod.lock_path(root)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="ascii")

    def test_an_indexer_left_running_by_a_killed_worker_keeps_the_tree(self):
        root = self.checkout("main")
        child, stop = self.child()
        self.record(root, str(child.pid))
        self.mod.request(root)

        with mock.patch.object(self.mod, "refresh") as refresh:
            self.mod.work(root)
        refresh.assert_not_called()
        self.assertTrue(self.mod.pending_path(root).exists())

        stop()
        with mock.patch.object(self.mod, "refresh") as refresh:
            self.mod.work(root)
        refresh.assert_called_once()

    def test_a_record_older_than_an_update_can_run_names_a_reused_pid(self):
        """A record older than `REFRESH_TIMEOUT` plus `REAP_TIMEOUT` names
        nothing, or standing off for a pid some other process now holds
        would last as long as that process."""
        root = self.checkout("main")
        child, _stop = self.child()
        self.record(root, str(child.pid))
        self.mod.request(root)
        path = self.mod.lock_path(root)
        for age, called in ((self.mod.REFRESH_TIMEOUT, 0), (self.mod.REFRESH_TIMEOUT + self.mod.REAP_TIMEOUT + 60, 1)):
            with self.subTest(age=age):
                then = time.time() - age
                os.utime(path, (then, then))
                with mock.patch.object(self.mod, "refresh") as refresh:
                    self.mod.work(root)
                self.assertEqual(called, refresh.call_count)
                self.mod.request(root)

    def test_a_record_naming_nothing_alive_keeps_nothing(self):
        root = self.checkout("main")
        gone = self.start_process([sys.executable, "-c", "pass"])
        gone.wait(timeout=60)
        for text in ("", "not a pid", "0", "99999999999999999999", str(gone.pid)):
            with self.subTest(record=text):
                self.record(root, text)
                self.mod.request(root)
                with mock.patch.object(self.mod, "refresh") as refresh:
                    self.mod.work(root)
                refresh.assert_called_once()

    def test_a_running_process_is_alive_and_a_finished_one_is_not(self):
        child, stop = self.child()
        self.assertTrue(self.mod.alive(child.pid))
        self.assertTrue(self.mod.alive(os.getpid()))
        stop()
        self.assertFalse(self.mod.alive(child.pid))
        self.assertFalse(self.mod.alive(0))

    def test_a_note_written_under_a_real_lock_is_read_back_by_the_next_worker(self):
        """Every other case mocks `note` or writes the file itself, and `note`
        swallows what fails: this one writes through a real handle under a real
        lock, lets go, and reads the record as the next worker does."""
        root = self.checkout("main")
        child, _stop = self.child()
        first = self.hold(root)
        self.mod.note(first, str(child.pid))
        first.close()

        second = self.hold(root)
        self.assertEqual(child.pid, self.mod.running_indexer(second))
        self.mod.note(second, "")
        self.assertIsNone(self.mod.running_indexer(second))
        second.seek(0)
        self.assertEqual(b"", second.read())

    def test_the_worker_claims_the_record_before_the_child_exists(self):
        """The order of the notes: the worker's own pid, then the indexer's
        once it exists, then an empty record once the indexer is gone."""
        root = self.checkout("main")
        notes = []
        child = mock.Mock(pid=4242)
        child.wait.return_value = 0
        handle = self.hold(root)
        self.patch(mock.patch.object(
            self.mod, "note", side_effect=lambda _handle, text: notes.append(text)))
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: None))
        self.patch(mock.patch.object(self.mod.subprocess, "Popen", return_value=child))

        self.assertTrue(self.mod.refresh(root, handle))

        self.assertEqual([str(os.getpid()), "4242", ""], notes)

    def test_a_timed_out_child_is_killed_and_reaped_before_the_record_is_cleared(self):
        """`kill` only asks: a record cleared while the process is still
        exiting lets the next update start beside it."""
        root = self.checkout("main")
        order = []
        child = mock.Mock(pid=4242)
        waits = []

        def wait(timeout=None):
            waits.append(timeout)
            if len(waits) == 1:
                raise subprocess.TimeoutExpired("codebase-index", timeout)
            order.append("reaped")
            return 0

        child.wait.side_effect = wait
        child.kill.side_effect = lambda: order.append("killed")
        handle = self.hold(root)
        self.patch(mock.patch.object(
            self.mod, "note", side_effect=lambda _handle, text: order.append(f"note {text!r}")))
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: None))
        self.patch(mock.patch.object(self.mod.subprocess, "Popen", return_value=child))

        self.assertFalse(self.mod.refresh(root, handle))

        self.assertEqual(["killed", "reaped"], order[order.index("killed"):][:2])
        self.assertEqual("note ''", order[-1])

    def test_a_child_that_outlives_the_kill_stays_on_the_record(self):
        root = self.checkout("main")
        notes = []
        child = mock.Mock(pid=4242)
        child.wait.side_effect = subprocess.TimeoutExpired("codebase-index", 1)
        handle = self.hold(root)
        self.patch(mock.patch.object(
            self.mod, "note", side_effect=lambda _handle, text: notes.append(text)))
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: None))
        self.patch(mock.patch.object(self.mod.subprocess, "Popen", return_value=child))

        self.assertFalse(self.mod.refresh(root, handle))

        child.kill.assert_called_once()
        self.assertEqual([str(os.getpid()), "4242", "4242"], notes)

    def test_a_survivor_is_stood_off_from_when_it_was_found_alive(self):
        """The record is written when the indexer starts, and the worker only
        learns it survived after both waits, so its age then already exceeds
        the bound: unless it is written again, the retry reads it as stale."""
        root = self.checkout("main")
        survivor, _stop = self.child()
        started = mock.Mock(pid=survivor.pid)
        path = self.mod.lock_path(root)
        waits = []

        def wait(timeout=None):
            waits.append(timeout)
            # The time those waits took, as the file's age.
            then = time.time() - sum(waits) - 1
            os.utime(path, (then, then))
            raise subprocess.TimeoutExpired("codebase-index", timeout)

        started.wait.side_effect = wait
        start = mock.Mock(return_value=started)
        handle = self.hold(root)
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: None))
        self.patch(mock.patch.object(self.mod.time, "sleep", lambda _pause: None))
        self.patch(mock.patch.object(self.mod.subprocess, "Popen", start))

        self.assertFalse(self.mod.refreshed(root, handle))

        self.assertEqual([self.mod.REFRESH_TIMEOUT, self.mod.REAP_TIMEOUT], waits)
        self.assertEqual(1, start.call_count)

    def test_a_retry_does_not_start_an_indexer_beside_one_that_survived(self):
        """The survivor's pid is the record, and a retry that overwrote it
        with the worker's own would start a second indexer beside it."""
        root = self.checkout("main")
        survivor, _stop = self.child()
        started = mock.Mock(pid=survivor.pid)
        started.wait.side_effect = subprocess.TimeoutExpired("codebase-index", 1)
        start = mock.Mock(return_value=started)
        handle = self.hold(root)
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: None))
        self.patch(mock.patch.object(self.mod.time, "sleep", lambda _pause: None))
        self.patch(mock.patch.object(self.mod.subprocess, "Popen", start))

        self.assertFalse(self.mod.refreshed(root, handle))

        self.assertEqual(1, start.call_count)
        handle.seek(0)
        self.assertEqual(str(survivor.pid).encode(), handle.read())


class Refreshing(Base):
    def test_the_call_is_the_cli_with_auto_update_off(self):
        """An unpinned newer package rewrites the tracked skill when it runs,
        and this calls the CLI rather than the wrapper that would stop it."""
        root = self.checkout("main")
        # A session sets it in every command's environment, which would let
        # this pass with the hook's own setting gone.
        self.patch(mock.patch.dict(self.mod.os.environ))
        self.mod.os.environ.pop("CBX_NO_SKILL_AUTO_UPDATE", None)
        script = str(self.tmp / "bin" / "codebase-index")
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: script))

        self.mod.refresh(root)

        arguments, keywords = self.ran[0]
        self.assertEqual([script, "update"], arguments[0])
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
        script = str(self.tmp / "bin" / "codebase-index")
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: script))

        def absent_script(command, **keywords):
            attempts.append(command)
            if command[0] == script:
                raise OSError("no such executable")
            self.ran.append(((command,), keywords))
            return _Child(0)

        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=absent_script))

        self.mod.refresh(self.checkout("main"))

        self.assertEqual(2, len(attempts), attempts)
        self.assertEqual(
            [self.mod.sys.executable, "-P", "-m", "codebase_index", "update"],
            attempts[1])

    def test_neither_form_running_is_silent(self):
        """An index that cannot refresh is not a reason to fail the edit."""
        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=OSError("nothing runnable")))

        self.assertFalse(self.mod.refresh(self.checkout("main")))
        self.assertEqual([], self.ran)

    def test_no_console_script_on_path_goes_straight_to_the_module(self):
        """A name the lookup did not find is never started bare, which is
        the start Windows would satisfy from the working directory."""
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: None))

        self.mod.refresh(self.checkout("main"))

        self.assertEqual(
            [[self.mod.sys.executable, "-P", "-m", "codebase_index", "update"]],
            [arguments[0] for arguments, _ in self.ran])


class TheWorkerIsDetached(Base):
    """The worker has to outlive the hook, whose own session or console ends
    with the call, and has to run: the hook discards every stream and always
    exits 0, so a launch that starts a worker which does nothing is silent."""

    def started(self, platform=None):
        root = self.checkout("main")
        if platform is not None:
            self.patch(mock.patch.object(self.mod.sys, "platform", platform))
        self.run_event({"cwd": str(root)})
        self.assertEqual(1, len(self.spawned))
        return self.spawned[0][1]

    def test_a_posix_worker_has_a_session_of_its_own(self):
        keywords = self.started("linux")

        self.assertIs(True, keywords["start_new_session"])
        self.assertFalse(keywords.get("creationflags"))

    @unittest.skipUnless(sys.platform == "win32", "creation flags exist on Windows only")
    def test_the_flags_are_the_ones_measured_to_run_the_cli(self):
        """`DETACHED_PROCESS` was measured to start a worker that exits 0
        without its child ever running, with every case green on a runner
        that tolerates the flag: so the set is held here, not by a run."""
        flags = self.started()["creationflags"]

        self.assertFalse(flags & subprocess.DETACHED_PROCESS)
        self.assertTrue(flags & subprocess.CREATE_NO_WINDOW)
        self.assertTrue(flags & subprocess.CREATE_NEW_PROCESS_GROUP)
        self.assertNotIn("start_new_session", self.spawned[0][1])


# What `codebase-index update` is, for a case that runs the module route: it
# says what a bare name finds from the directory it was started in.
STUB_CLI = """
import pathlib, shutil
pathlib.Path("answer.txt").write_text(str(shutil.which("planted")), encoding="utf-8")
"""


class NothingIsFoundInTheWorkingDirectory(Base):
    """Windows looks in the working directory for a program named bare before
    it looks on PATH, and the CLI runs in the checkout, where a branch can
    plant one. Everything the hook starts carries the variable that stops it,
    so the hook does not depend on its launcher having passed it down."""

    def test_every_start_carries_the_variable(self):
        root = self.checkout("main")
        self.patch(mock.patch.dict(self.mod.os.environ))
        self.mod.os.environ.pop(NO_CWD_SEARCH, None)
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: None))

        self.run_event({"cwd": str(root)})
        self.mod.refresh(root)

        self.assertEqual(1, len(self.spawned))
        for keywords in (self.spawned[0][1], self.ran[0][1]):
            # Windows folds the name's case, so the starts may not disagree.
            folded = {name.casefold(): value for name, value in keywords.get("env", {}).items()}
            self.assertEqual("1", folded.get(NO_CWD_SEARCH.casefold()), keywords)

    @unittest.skipUnless(sys.platform == "win32", "only Windows searches the working directory for a bare name")
    def test_a_program_planted_in_the_working_directory_is_never_run(self):
        root = self.checkout("main")
        (root / "planted.cmd").write_text("@echo planted\r\n", encoding="ascii")
        stub = self.tmp / "stub" / "codebase_index"
        stub.mkdir(parents=True)
        (stub / "__main__.py").write_text(STUB_CLI, encoding="utf-8")
        (stub / "__init__.py").write_text("", encoding="utf-8")
        self.patch(mock.patch.dict(self.mod.os.environ, {"PYTHONPATH": str(stub.parent)}))
        self.mod.os.environ.pop(NO_CWD_SEARCH, None)
        self.patch(mock.patch.object(self.mod, "console_script", lambda _root: None))

        self.patch(mock.patch.object(self.mod.subprocess, "Popen", self.start_process))
        answer = root / "answer.txt"

        # The control: without the variable the plant is what Windows finds.
        self.start_process([sys.executable, "-P", "-m", "codebase_index"], cwd=str(root),
                           env=dict(self.mod.os.environ)).wait(timeout=60)
        self.assertIn("planted", answer.read_text(encoding="utf-8"), "no plant to refuse")
        answer.unlink()

        self.assertTrue(self.mod.refresh(root))

        self.assertEqual("None", answer.read_text(encoding="utf-8"))


def plant(directory: Path) -> Path:
    """A `codebase-index` the platform would start, in `directory`."""
    directory.mkdir(parents=True, exist_ok=True)
    stub = directory / ("codebase-index.exe" if sys.platform == "win32" else "codebase-index")
    stub.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
    stub.chmod(0o755)
    return stub


class FindingTheCli(Base):
    """`codebase-index` is looked for on PATH alone, and never in a checkout.

    Windows searches the starting process's directory for a bare name first, so
    a committed stub could run; the full path the lookup answers searches none.
    """

    def search(self, *entries, cwd=None, root=None):
        root = root or self.checkout("main")
        self.patch(mock.patch.dict(self.mod.os.environ, {
            "PATH": os.pathsep.join(str(entry) for entry in entries),
            "PATHEXT": ".COM;.EXE;.BAT;.CMD"}))
        self.patch(mock.patch.object(self.mod.Path, "cwd", lambda: Path(cwd or root)))
        return self.mod.console_script(root)

    def test_a_stub_at_the_checkout_root_is_not_run(self):
        root = self.checkout("main")
        stub = plant(root)

        found = self.search(root, ".", "", cwd=root, root=root)
        self.mod.refresh(root)

        self.assertIsNone(found)
        started = [arguments[0][0] for arguments, _ in self.ran]
        self.assertNotIn(str(stub), started)
        self.assertTrue(all(Path(program).is_absolute() for program in started), started)

    def test_the_cli_on_path_is_started_by_its_full_path(self):
        installed = plant(self.tmp / "bin")
        root = self.checkout("main")
        plant(root)

        found = self.search(root, installed.parent, root=root)

        # As a Path, which compares as the platform's file names do.
        self.assertEqual(installed, Path(found))
        self.mod.refresh(root)
        self.assertEqual([found, "update"], self.ran[0][0][0])

    def test_a_directory_inside_the_checkout_is_skipped(self):
        root = self.checkout("main")
        plant(root / "tools")
        installed = plant(self.tmp / "bin")

        self.assertEqual(installed, Path(self.search(root / "tools", installed.parent, root=root)))

    def test_the_main_checkout_of_a_worktree_is_skipped(self):
        """A worktree outside its main checkout's tree still runs what that
        checkout's last merge put there, if the lookup let it."""
        main = self.checkout("main", indexed=False)
        git_dir = main / ".git" / "worktrees" / "probe"
        git_dir.mkdir(parents=True)
        (git_dir / "commondir").write_text("../..\n", encoding="utf-8")
        worktree = self.tmp / "probe"
        worktree.mkdir()
        (worktree / ".git").write_text(f"gitdir: {git_dir}\n", encoding="utf-8")
        plant(main)

        self.assertIsNone(self.search(main, cwd=self.tmp, root=worktree))

    def test_the_working_directory_and_relative_entries_are_skipped(self):
        root = self.checkout("main")
        elsewhere = self.tmp / "elsewhere"
        plant(elsewhere)
        plant(self.tmp / "rel")
        # Really there, so a relative entry the lookup kept would find "rel".
        self.addCleanup(os.chdir, os.getcwd())
        os.chdir(self.tmp)

        self.assertIsNone(self.search(elsewhere, "rel", ".", cwd=elsewhere, root=root))

    def test_a_link_into_the_checkout_is_skipped(self):
        root = self.checkout("main")
        stub = plant(root)
        outside = self.tmp / "bin"
        outside.mkdir()
        try:
            (outside / stub.name).symlink_to(stub)
        except (OSError, NotImplementedError) as error:
            self.skipTest(f"no symlinks here: {error}")

        # Run from outside the checkout, so that only the fence on the link's
        # target can refuse it: from the root, the working-directory check
        # would refuse it alone.
        self.assertIsNone(self.search(outside, cwd=self.tmp, root=root))

    @unittest.skipUnless(sys.platform == "win32", "PATHEXT is Windows' alone")
    def test_a_batch_shim_on_path_is_not_taken(self):
        """A `.bat` or `.cmd` runs through `cmd.exe` in the checkout, which
        looks there first for every command the script names."""
        root = self.checkout("main")
        shims = self.tmp / "bin"
        shims.mkdir()
        for name in ("codebase-index.bat", "codebase-index.cmd"):
            (shims / name).write_text("@exit /b 0\r\n", encoding="utf-8")

        self.assertIsNone(self.search(shims, root=root))


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
    Code never reads anything under `examples/`, so a divergence costs nothing
    at the moment it happens and everything to whoever copies it."""

    def test_the_example_is_the_installed_block(self):
        self.assertEqual(
            entries_running_the_hook(SETTINGS), entries_running_the_hook(EXAMPLE))

    def test_the_example_runs_this_hook_at_all(self):
        """The subject, because two empty lists are equal."""
        self.assertTrue(entries_running_the_hook(EXAMPLE))


# What `codebase-index update` is for the case below: it says that it ran, how,
# and where.
RECORDING_CLI = r"""
import os, pathlib, sys
pathlib.Path("cli-ran").write_text(
    "\n".join([*sys.argv[1:], os.getcwd()]), encoding="utf-8")
"""


def remove_tree(path: Path) -> None:
    """Remove a tree a process may still be leaving, for as long as it takes
    and then loudly, since Windows refuses a directory something stands in."""
    deadline = time.monotonic() + 60
    while True:
        try:
            return shutil.rmtree(path)
        except OSError:
            if time.monotonic() > deadline:
                raise
            time.sleep(0.2)


class TheDetachedWorkerReallyRuns(unittest.TestCase):
    """Every case above replaces `Popen`, so a worker that starts and does
    nothing leaves the index stale with the suite green. This one runs the
    registered command against a real event and waits for the detached worker
    to seed a linked worktree, run a stand-in CLI there and let go of the
    lock."""

    def setUp(self):
        self.base = Path(tempfile.mkdtemp())
        self.addCleanup(remove_tree, self.base)
        self.owner = self.base / "owner"
        (self.owner / ".git").mkdir(parents=True)
        hooks = self.owner / ".claude" / "hooks"
        hooks.mkdir(parents=True)
        shutil.copyfile(HOOK, hooks / HOOK.name)
        self.hook = _load()
        self.hook.home = lambda: self.owner
        self.worktree = self.owner / ".claude" / "worktrees" / "feature"
        admin = self.owner / ".git" / "worktrees" / "feature"
        admin.mkdir(parents=True)
        (admin / "commondir").write_text("../..\n", encoding="utf-8")
        self.worktree.mkdir(parents=True)
        (self.worktree / ".git").write_text(f"gitdir: {admin}\n", encoding="utf-8")
        index = self.owner / CACHE / "index.sqlite"
        index.parent.mkdir(parents=True)
        connection = sqlite3.connect(str(index))
        try:
            connection.execute("pragma journal_mode=wal")
            connection.execute("create table origin (name text)")
            connection.execute("insert into origin values ('main')")
            connection.commit()
        finally:
            connection.close()
        package = self.base / "stand-in" / "codebase_index"
        package.mkdir(parents=True)
        (package / "__init__.py").write_text("", encoding="utf-8")
        (package / "__main__.py").write_text(RECORDING_CLI, encoding="utf-8")

    def environment(self):
        installed = ("codebase-index.exe", "codebase-index.com") if sys.platform == "win32" else ("codebase-index",)
        path = [entry for entry in os.environ.get("PATH", "").split(os.pathsep)
                if not any((Path(entry) / name).is_file() for name in installed)]
        return dict(os.environ, CLAUDE_PROJECT_DIR=str(self.owner), PATH=os.pathsep.join(path),
                    PYTHONPATH=str(self.base / "stand-in"))

    def test_an_event_reaches_the_cli_through_the_detached_worker(self):
        event = json.dumps({"cwd": str(self.worktree), "hook_event_name": "PostToolUse"})
        done = subprocess.run(
            [sys.executable, *shlex.split(COMMAND)[2:]], input=event.encode("utf-8"),
            capture_output=True, timeout=60, check=False, env=self.environment())
        self.assertEqual(0, done.returncode, done.stderr)

        ran = self.worktree / "cli-ran"
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline and (not ran.exists() or self.hook.busy(self.worktree)):
            time.sleep(0.2)
        state = self.owner / ".git" / "index-refresh"
        self.assertTrue(
            ran.exists(),
            "the detached worker never ran the CLI within 60 s; the state "
            f"directory held {sorted(os.listdir(state)) if state.is_dir() else None!r}. A lock "
            "beside a marker and no record is a worker started and stopped "
            "without running it, which DETACHED_PROCESS did on Windows")
        arguments = ran.read_text(encoding="utf-8").split("\n")
        self.assertEqual("update", arguments[-2])
        self.assertTrue(os.path.samefile(self.worktree, arguments[-1]), arguments)
        connection = sqlite3.connect(str(self.worktree / CACHE / "index.sqlite"))
        try:
            self.assertEqual("main", connection.execute("select name from origin").fetchone()[0])
        finally:
            connection.close()
        self.assertFalse(self.hook.busy(self.worktree), "the worker finished without releasing")
        self.assertFalse(self.hook.pending_path(self.worktree).exists())


class WhatWasPatchedIsRestored(unittest.TestCase):
    """The canary, and its name sorts after every suite on purpose.

    A leaked patch is invisible to the file that leaks it and fatal to every
    file loaded after it, so the suite that notices has to run later.
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
