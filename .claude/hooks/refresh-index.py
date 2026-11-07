#!/usr/bin/env python3
"""Refresh the code index of the checkout an edit or a shell call changed. One
refresh owns an index at a time, and it returns 0 whatever happens: an index
that cannot refresh is no reason to fail the call that provoked it."""

from __future__ import annotations

import hashlib
import json
import os
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.parse
from pathlib import Path

# Where the CLI keeps a checkout's index. A checkout without one is not
# stale, it is unindexed, and a full build is minutes inside an agent's turn,
# so the hook never builds one: a linked worktree is seeded from its main
# checkout's instead, and anything else without an index is left alone.
CACHE = Path(".claude") / "cache" / "codebase-index"
INDEX = CACHE / "index.sqlite"

# `codebase-index update` does not serialise: overlapping runs against one
# checkout leave a single winner and `database is locked` for the rest, and
# the loser may be the run carrying the newest edit. Discarded streams make
# that silent, so this lock is what keeps the index honest.

# The operating system holds it, on an open descriptor, and releases it when
# the worker exits however it exits, so the lock itself needs no staleness
# rule and no takeover. The pid recorded in the file is data, and has one: a
# record older than REFRESH_TIMEOUT plus REAP_TIMEOUT names nothing, so a
# reused pid holds workers off that long at most, and an orphan no longer.

# The marker beside the lock is how an edit arriving mid-run reaches the
# worker running. Both live in the git directory, one pair per checkout: no
# branch tracks anything there, as it can under `CACHE`.
STATE = "index-refresh"

# Windows finds a bare program name in the working directory before PATH, and
# the CLI runs in the checkout. Upper case, so it replaces the variable
# Claude Code passes down rather than sitting beside it.
NO_CWD_SEARCH = "NODEFAULTCURRENTDIRECTORYINEXEPATH"

# The files SQLite opens by name beside a database.
SIDECARS = ("-wal", "-shm", "-journal")

# An update that cannot finish is not allowed to hold the lock for ever, and
# one that will not end when killed is waited on this long and then left.
REFRESH_TIMEOUT = 600
REAP_TIMEOUT = 30

# A failed update is retried, because the failure worth retrying is the one
# this lock cannot prevent: the CLI errors rather than waits when another
# update holds the index, and the skill's documented `cbx update` runs
# outside the lock entirely. The bound is what keeps a detached worker off
# a broken install, and giving up is not final — the request goes back.
REFRESH_ATTEMPTS = 3
RETRY_PAUSE = 5

# A marker the worker cannot remove is tried again this often, this far apart,
# with the lock let go between tries, and then left for the next edit.
MARKER_RETRIES = 5
MARKER_WAIT = 0.2

if sys.platform == "win32":
    import msvcrt

    def grab(handle) -> bool:
        """Take the lock on `handle`, or answer that somebody else has it."""
        try:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            return False
        return True
else:
    import fcntl

    def grab(handle) -> bool:
        """Take the lock on `handle`, or answer that somebody else has it."""
        try:
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            return False
        return True


def home() -> Path:
    """The checkout this hook file sits in, three directories above it."""
    return Path(__file__).resolve().parents[2]


def common_dir(root: Path) -> Path | None:
    """The git directory `root`'s repository keeps, read from the files git
    writes: `.git` itself in a main checkout, and in a linked worktree the
    `commondir` of the directory its `gitdir:` pointer names."""
    marker = root / ".git"
    try:
        if marker.is_dir():
            return marker
        pointer = marker.read_text(encoding="utf-8").strip()
        if not pointer.startswith("gitdir:"):
            return None
        admin = root / pointer[len("gitdir:"):].strip()
        try:
            named = (admin / "commondir").read_text(encoding="utf-8").strip()
        except FileNotFoundError:
            return admin
        return admin / named
    except (OSError, UnicodeDecodeError, ValueError):
        return None


def linked(path: Path) -> bool:
    # `isjunction` is Python 3.12's, and an older `python3` may run the hook.
    isjunction = getattr(os.path, "isjunction", None)
    return path.is_symlink() or (isjunction is not None and isjunction(path))


def state_dir() -> Path | None:
    """Where this hook keeps its lock and markers, or None where the git
    directory's own name, or the state directory's, is a link. Components
    above them are not looked at: from a linked worktree the git directory is
    reached through `commondir`, which lstat never reports as a link."""
    common = common_dir(home())
    if common is None:
        return None
    directory = common / STATE
    if linked(common) or linked(directory):
        return None
    return directory


def identity(path: Path) -> str:
    """A name for the entry `path` opens that its spellings share: its device
    and inode, because `normcase` folds case on Windows alone."""
    try:
        info = os.stat(path)
        return f"{info.st_dev}:{info.st_ino}"
    except OSError:
        return os.path.normcase(os.path.realpath(path))


def state_path(root: Path, suffix: str) -> Path | None:
    directory = state_dir()
    if directory is None:
        return None
    key = hashlib.sha256(identity(root).encode("utf-8", "replace")).hexdigest()
    return directory / f"{key[:32]}.{suffix}"


def lock_path(root: Path) -> Path | None:
    return state_path(root, "lock")


def pending_path(root: Path) -> Path | None:
    return state_path(root, "pending")


def open_lock(root: Path):
    """The lock file's handle, made where it is missing, or None."""
    path = lock_path(root)
    if path is None:
        return None
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        return open(path, "a+b")
    except OSError:
        return None


def checkout_root(start: Path) -> Path | None:
    """The checkout `start` sits in, by the `.git` at or above it."""
    try:
        candidates = (start, *start.parents)
    except OSError:
        return None
    for candidate in candidates:
        if (candidate / ".git").exists():
            return candidate
    return None


def named_file(event: dict) -> str | None:
    """The path the tool touched as the event spells it, or None when the
    event names no file."""
    given = event.get("tool_input")
    if not isinstance(given, dict):
        return None
    for key in ("file_path", "notebook_path"):
        value = given.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return None


def edited(event: dict) -> Path | None:
    """The file the tool touched, resolved against the session's directory,
    or None when it names none or one that cannot be placed. A relative path
    is relative to `cwd`; an absolute one may name another checkout:
    `guard-edit-target` admits the session's tree or its fork source."""
    value = named_file(event)
    if value is None:
        return None
    path = Path(value)
    base = event.get("cwd")
    if not path.is_absolute():
        if not base:
            return None
        path = Path(str(base)) / path
    # Resolved before anything walks it. `../../../src/Thing.cs` from a
    # worktree under `.claude/worktrees/` lands in the main checkout,
    # and the worktree is among that path's lexical parents — so an
    # unresolved walk finds the worktree's `.git` and refreshes the
    # tree the edit did not touch.
    try:
        return path.resolve()
    except (OSError, ValueError):
        return None


def main_checkout(root: Path) -> Path | None:
    """The main checkout `root` is a linked worktree of, or None, read from
    the files git writes because this runs on every call: a `gitdir:` file
    whose `commondir` names a `.git` directory. A submodule has no
    `commondir`, and a bare repository's is not named `.git`."""
    try:
        pointer = (root / ".git").read_text(encoding="utf-8").strip()
        if not pointer.startswith("gitdir:"):
            return None
        git_dir = root / pointer[len("gitdir:"):].strip()
        common = (git_dir / (git_dir / "commondir").read_text(encoding="utf-8").strip()).resolve()
        if common.name != ".git" or common.parent == root.resolve():
            return None
    except (OSError, UnicodeDecodeError, ValueError):
        return None
    return common.parent


def same_repository(root: Path) -> bool:
    """Whether `root` keeps the git directory this hook's own checkout does."""
    ours, theirs = common_dir(home()), common_dir(root)
    if ours is None or theirs is None:
        return False
    try:
        return os.path.samefile(ours, theirs)
    except (OSError, ValueError):
        return False


def written(root: Path) -> bool:
    """Whether `root` holds an index a build has written. A file shorter than
    SQLite's 100-byte header is one a first build created and never wrote,
    which `update` cannot open: it is no index at all."""
    try:
        return (root / INDEX).stat().st_size >= 100
    except OSError:
        return False


def seed_source(root: Path) -> Path | None:
    """The index a linked worktree without one is seeded from, or None: only
    a regular file at the main checkout's own index path, so a link planted
    there cannot point the copy at anything else."""
    if (root / INDEX).exists():
        return None
    main = main_checkout(root)
    if main is None:
        return None
    source = main / INDEX
    if source.is_symlink() or not source.is_file():
        return None
    return source


def refreshable(root: Path) -> bool:
    """Whether a worker for `root` would have anything to do: an index, or
    one to seed. A cache directory alone is neither, and `update` there is a
    build."""
    return written(root) or seed_source(root) is not None


def admitted(root: Path) -> bool:
    """Whether the hook may refresh `root` at all: a checkout of its own
    repository, with state to keep and something to refresh."""
    return state_dir() is not None and same_repository(root) and refreshable(root)


def target(event: dict) -> Path | None:
    """The indexed or seedable checkout holding the file that changed, else
    the one `cwd` names, then `CLAUDE_PROJECT_DIR`, then the working
    directory `git-worktree-fork.sh` runs it in, each in turn and never as a
    second chance after an unindexed tree. An event naming a file that cannot
    be placed answers with none of them."""
    if named_file(event) is not None:
        named = edited(event)
        if named is None:
            return None
    else:
        named = (event.get("cwd") or os.environ.get("CLAUDE_PROJECT_DIR")
                 or os.getcwd())
    root = checkout_root(Path(str(named)))
    if root is None or not admitted(root):
        return None
    if not written(root):
        try:
            (root / CACHE).mkdir(parents=True, exist_ok=True)
        except OSError:
            return None
    return root


def seed(root: Path) -> None:
    """Give a linked worktree with no index its main checkout's, through
    SQLite's backup because a copied file can tear mid-`update`. Renamed in so
    `update` never opens a partial file, after any WAL an earlier seed left;
    only `index.sqlite` crosses, as the rest of that cache is its own state."""
    source = seed_source(root)
    if source is None:
        return
    index = root / INDEX
    partial = index.with_name(f"{index.name}.seed.{os.getpid()}")
    copying = False
    try:
        # A killed worker's partial is named for it, and the lock this runs
        # under is the only reason no other seed is writing one.
        for name in os.listdir(index.parent):
            if name.startswith(f"{index.name}.seed."):
                try:
                    (index.parent / name).unlink()
                except OSError:
                    pass
        if any(os.path.lexists(str(partial) + suffix) for suffix in ("", *SIDECARS)):
            return
        copying = True
        for leftover in ("-wal", "-shm"):
            index.with_name(index.name + leftover).unlink(missing_ok=True)
        # The authority stays empty: `as_uri()` puts a UNC server or a `?` there,
        # and SQLite refuses both. `mode=rw` refuses a source gone since
        # `seed_source` looked, where a plain connect would create one empty.
        posix = source.as_posix()
        reading = sqlite3.connect(
            "file://" + ("" if posix.startswith("/") else "/") + urllib.parse.quote(posix) + "?mode=rw",
            uri=True)
        try:
            writing = sqlite3.connect(str(partial))
            try:
                reading.backup(writing)
            finally:
                writing.close()
        finally:
            reading.close()
        # The header says WAL, or the copy is one the readers refuse.
        with open(partial, "rb") as copy:
            if copy.read(100)[18:20] != bytes((2, 2)):
                return
        os.replace(partial, index)
    except (OSError, sqlite3.Error):
        pass
    finally:
        # Only what this process made: a refusal above found files it did not.
        for suffix in ("", *SIDECARS) if copying else ():
            try:
                Path(str(partial) + suffix).unlink(missing_ok=True)
            except OSError:
                pass


def request(root: Path) -> None:
    """Record that an edit wants a refresh, whoever ends up running it."""
    path = pending_path(root)
    if path is None:
        return
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("", encoding="utf-8")
    except OSError:
        pass


def take_request(root: Path) -> bool | None:
    """Consume an outstanding request, if there is one. None is a marker that
    is there and will not go, which is not the same as one that is absent."""
    path = pending_path(root)
    if path is None:
        return False
    try:
        path.unlink()
        return True
    except FileNotFoundError:
        return False
    except OSError:
        return None


def busy(root: Path) -> bool:
    """Whether a worker already holds this checkout's lock.

    Advisory: a worker may start between this answer and the spawn it saves.
    That costs one process that takes no lock, never two refreshes.
    """
    handle = open_lock(root)
    if handle is None:
        return False
    with handle:
        return not grab(handle)


def inside(path: Path, tree: Path) -> bool:
    return path == tree or tree in path.parents


def console_script(root: Path) -> str | None:
    """`codebase-index` found on PATH alone, as a full path, or None.

    Absolute PATH entries, nothing in a checkout, and on Windows only `.exe` or
    `.com`: `docs/harness-boundaries.md`, *Index refresh and worktree seeding*.
    """
    fenced = [tree.resolve() for tree in (root, main_checkout(root)) if tree is not None]
    try:
        here = Path.cwd().resolve()
    except OSError:
        here = None
    if sys.platform == "win32":
        names = ["codebase-index.exe", "codebase-index.com"]
    else:
        names = ["codebase-index"]
    for entry in os.environ.get("PATH", "").split(os.pathsep):
        directory = Path(entry.strip('"'))
        if not entry.strip('"') or not directory.is_absolute():
            continue
        try:
            listed = directory.resolve()
        except OSError:
            continue
        if listed == here or any(inside(listed, tree) for tree in fenced):
            continue
        for name in names:
            candidate = directory / name
            try:
                if not (candidate.is_file() and os.access(candidate, os.X_OK)):
                    continue
                real = candidate.resolve()
            except OSError:
                continue
            if real.parent != here and not any(inside(real, tree) for tree in fenced):
                return str(candidate)
    return None


def note(handle, text: str) -> None:
    """Make `text` the whole of the lock file, through the handle that holds
    the lock, so the record lives and dies with the lock."""
    if handle is None:
        return
    try:
        handle.seek(0)
        handle.truncate()
        handle.write(text.encode("ascii"))
        handle.flush()
    except OSError:
        pass


def alive(pid: int) -> bool:
    """Whether a process with this id is running, and for any doubt, yes: one
    skipped refresh costs less than two indexers on one cache."""
    if pid <= 0:
        return False
    if sys.platform == "win32":
        try:
            import ctypes
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
            kernel32.OpenProcess.restype = ctypes.c_void_p
            kernel32.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
            kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
            # PROCESS_QUERY_LIMITED_INFORMATION; access denied is a process
            # that exists, and invalid parameter is one that does not.
            process = kernel32.OpenProcess(0x1000, False, pid)
            if not process:
                return ctypes.get_last_error() != 87
            try:
                code = ctypes.c_ulong()
                if not kernel32.GetExitCodeProcess(process, ctypes.byref(code)):
                    return True
                return code.value == 259
            finally:
                kernel32.CloseHandle(process)
        except (ImportError, OSError, AttributeError, ValueError):
            return True
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except OSError:
        return True
    return True


def running_indexer(handle) -> int | None:
    """The pid the lock file records, when that process is still running: the
    lock ends with its worker, and the indexer it started does not."""
    if handle is None:
        return None
    try:
        handle.seek(0)
        recorded = handle.read().decode("ascii", "replace").strip()
    except OSError:
        return None
    # A record that is no pid, or one no system call takes, names nothing.
    if not recorded.isdigit() or not 0 < int(recorded) < 2**31 or int(recorded) == os.getpid():
        return None
    try:
        age = time.time() - os.fstat(handle.fileno()).st_mtime
    except OSError:
        age = 0
    if age > REFRESH_TIMEOUT + REAP_TIMEOUT:
        return None
    return int(recorded) if alive(int(recorded)) else None


def refresh(root: Path, handle=None) -> bool:
    """One update, waited on and answered, because the streams are gone.

    The script by its full path, then the module through this interpreter:
    `docs/harness-boundaries.md`, *Index refresh and worktree seeding*. The
    indexer's pid goes in the lock's `handle` and is cleared once it is gone."""
    environment = dict(os.environ, CBX_NO_SKILL_AUTO_UPDATE="1", PYTHONSAFEPATH="1",
                       **{NO_CWD_SEARCH: "1"})
    script = console_script(root)
    for command in (
        *([[script, "update"]] if script else []),
        [sys.executable, "-P", "-m", "codebase_index", "update"],
    ):
        note(handle, str(os.getpid()))
        try:
            child = subprocess.Popen(
                command, cwd=str(root), env=environment,
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL)
        except OSError:
            continue
        note(handle, str(child.pid))
        try:
            finished = child.wait(timeout=REFRESH_TIMEOUT) == 0
        except subprocess.TimeoutExpired:
            finished = False
            try:
                child.kill()
            except OSError:
                pass
        # Reaped before the record is cleared. A child that outlives the wait
        # stays on it, written again so its age runs from now, and the next
        # worker stands off while that lasts.
        try:
            child.wait(timeout=REAP_TIMEOUT)
        except (OSError, subprocess.SubprocessError):
            note(handle, str(child.pid))
            return finished
        note(handle, "")
        return finished
    note(handle, "")
    return False


def refreshed(root: Path, handle=None) -> bool | None:
    """One request's update, tried again while it keeps failing; None when
    the root stopped qualifying. The pause is what makes a retry worth
    anything, and the root is judged again after it: an index can go."""
    for attempt in range(REFRESH_ATTEMPTS):
        if not (same_repository(root) and written(root)):
            return None
        # An indexer that outlived its kill is on the record, and an attempt
        # would overwrite that and start a second beside it.
        if running_indexer(handle) is not None:
            return False
        if refresh(root, handle):
            return True
        if attempt + 1 < REFRESH_ATTEMPTS:
            time.sleep(RETRY_PAUSE)
    return False


def drain(root: Path, handle=None) -> bool | None:
    """Every outstanding request, refreshed; false when one was given up on,
    None when a marker would not go. A failed request is put back so the next
    worker carries its edit, and returning keeps that from spinning against a
    broken install. A root that no longer qualifies is left without an update
    and without that."""
    while True:
        # Before every marker: an indexer that outlived its kill leaves its
        # pid on the record, and this worker must not start another beside it.
        if running_indexer(handle) is not None:
            return False
        taken = take_request(root)
        if taken is None:
            return None
        if not taken:
            return True
        done = refreshed(root, handle)
        if done is None:
            return False
        if not done:
            request(root)
            return False


def work(root: Path) -> int:
    """Refresh until no request is outstanding, holding the lock throughout.

    Held for the whole drain, so an edit arriving mid-run reaches this worker
    through the marker; a worker that cannot take it leaves the marker alone.
    """
    stuck = 0
    while True:
        handle = open_lock(root)
        if handle is None:
            return 0
        with handle:
            if not grab(handle):
                return 0
            if running_indexer(handle) is not None:
                return 0
            seed(root)
            drained = drain(root, handle)
            if drained is False:
                return 0
        if drained is None:
            stuck += 1
            if stuck > MARKER_RETRIES:
                return 0
            time.sleep(MARKER_WAIT)
            continue
        stuck = 0
        # The lock is gone by here, and that is the point. A request made
        # between the last look above and this release found `busy` true and
        # left a marker rather than a worker, so it is taken now. Another
        # worker may have taken the lock in between, and then the grab above
        # fails and this returns: the marker is theirs.
        path = pending_path(root)
        if path is None or not path.exists():
            return 0


def detached() -> dict:
    """`Popen`'s keywords for a worker that outlives the hook's session."""
    if sys.platform == "win32":
        # Never DETACHED_PROCESS: a worker started with it exits 0 without its
        # child ever running, and says nothing. CREATE_NO_WINDOW gives it a
        # hidden console of its own, and the group keeps the hook's Ctrl+C off it.
        return {"creationflags": subprocess.CREATE_NO_WINDOW | subprocess.CREATE_NEW_PROCESS_GROUP}
    return {"start_new_session": True}


def main() -> int:
    if sys.argv[1:2] == ["--worker"]:
        # Judged as an event's root is: the argument is only what the hook
        # that started this worker chose, some time ago.
        if len(sys.argv) == 3 and admitted(Path(sys.argv[2])):
            return work(Path(sys.argv[2]))
        return 0

    # Bytes, decoded here: Python reads a Windows pipe in the ANSI code page,
    # which turns a non-ASCII checkout path into one that does not exist.
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
    except (json.JSONDecodeError, OSError, UnicodeDecodeError):
        event = {}

    root = target(event if isinstance(event, dict) else {})
    if root is None:
        return 0

    # The marker first, so a worker that is already draining finds this edit
    # whether or not the spawn below happens.
    request(root)
    if busy(root):
        return 0

    # The worker stands in the system temp directory, outside every checkout:
    # Windows will not delete a live process's working directory, and looks
    # there first for a program named bare. The CLI still runs in the checkout
    # (`docs/harness-boundaries.md`, *Index refresh and worktree seeding*).
    root = root.absolute()
    try:
        subprocess.Popen(
            [sys.executable, "-P", str(Path(__file__).resolve()), "--worker", str(root)],
            cwd=tempfile.gettempdir(),
            env=dict(os.environ, **{NO_CWD_SEARCH: "1"}),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            **detached(),
        )
    except OSError:
        # Nothing to undo: no lock was taken here, and the marker stays for
        # the next edit or the worker already running.
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
