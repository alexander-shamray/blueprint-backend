"""What `.claude/hooks/refresh-index.py` refreshes, and what it leaves alone.

The hook suppresses every failure on purpose, so its exit status says
nothing: a wrong checkout, an absent CLI and a hook nothing calls all look
like success. The invariant held here is that the tree refreshed is the one
the edit landed in, exactly one refresh owns it, or none of them run.
"""

import importlib.util
import json
import shutil
import tempfile
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
# file's name and neither of them runs it -- and the hook swallows every
# failure, so a registration broken that way is green everywhere else.
COMMAND = 'py -3.12 "${CLAUDE_PROJECT_DIR}/.claude/hooks/refresh-index.py"'


def _load():
    spec = importlib.util.spec_from_file_location("refresh_index", HOOK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def entries_running_the_hook(settings: Path) -> list:
    """The PostToolUse entries of `settings` that run this hook, and no others."""
    document = json.loads(settings.read_text(encoding="utf-8"))
    return [
        entry
        for entry in document.get("hooks", {}).get("PostToolUse", [])
        if any(hook.get("type") == "command" and hook.get("command") == COMMAND
               for hook in entry.get("hooks", []))
    ]


class _Stdin:
    """`sys.stdin` for one call, with only the method the hook uses."""

    def __init__(self, body):
        self.body = body

    def read(self):
        return self.body


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
        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen",
            side_effect=lambda *a, **k: self.spawned.append((a, k))))
        self.patch(mock.patch.object(
            self.mod.subprocess, "run",
            side_effect=lambda *a, **k: self.ran.append((a, k))))

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
        """The checkout each spawned worker was pointed at.

        Second from the end, because the stamp it owns follows it."""
        return [Path(arguments[0][-2]) for arguments, _ in self.spawned]


class ChoosingTheCheckout(Base):
    def test_the_events_cwd_decides_the_checkout(self):
        """`/branch` moves the session into a sibling worktree, so the tree
        that changed is the event's and not the one `CLAUDE_PROJECT_DIR`
        still names."""
        edited = self.checkout("worktree")
        original = self.checkout("main")

        self.assertEqual(0, self.run_event({"cwd": str(edited)}, original))

        self.assertEqual([edited], self.worker_roots())

    def test_the_edited_file_decides_over_the_session_directory(self):
        """`guard-edit-target` admits an edit against the session's tree or
        the one it forked from, so an absolute edit into the original while
        the session sits in a sibling leaves the tree that changed stale."""
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
        """Unindexed is not stale. Building an index per throwaway worktree
        is a cost this hook was never asked for.

        `CLAUDE_PROJECT_DIR` names an indexed checkout here because the real
        hook always receives one: without it the assertion passes on a hook
        that falls through and refreshes the tree that did not change."""
        bare = self.checkout("fresh", indexed=False)
        indexed = self.checkout("main")

        self.assertEqual(0, self.run_event({"cwd": str(bare)}, indexed))

        self.assertEqual([], self.spawned)

    def test_a_cwd_outside_any_checkout_spawns_nothing(self):
        self.assertEqual(0, self.run_event({"cwd": str(self.tmp)}))

        self.assertEqual([], self.spawned)

    def test_a_body_that_is_not_an_event_is_silent(self):
        self.assertEqual(0, self.run_hook("<html>not json</html>"))

        self.assertEqual([], self.spawned)


class OneRefreshAtATime(Base):
    """`codebase-index update` does not serialise, so this has to.

    Overlapping runs against one checkout leave a single winner and
    `database is locked` for the rest, and the loser may be the run carrying
    the newest edit — a stale index, said nothing about, because the streams
    are discarded.
    """

    def test_a_second_edit_starts_no_second_worker(self):
        root = self.checkout("main")

        self.run_event({"cwd": str(root)})
        self.run_event({"cwd": str(root)})

        self.assertEqual([root], self.worker_roots())

    def test_a_second_edit_leaves_its_request_behind(self):
        """The marker is what makes the worker already running pick the edit
        up, rather than the edit being dropped with the lock held."""
        root = self.checkout("main")

        self.run_event({"cwd": str(root)})
        self.run_event({"cwd": str(root)})

        self.assertTrue((root / self.mod.PENDING).exists())

    def claimed(self, root):
        """The stamp the hook's own claim wrote, as a worker would receive it."""
        return self.mod.holder(root)

    def test_the_worker_refreshes_once_for_one_request(self):
        root = self.checkout("main")
        self.run_event({"cwd": str(root)})

        self.assertEqual(0, self.mod.work(root, self.claimed(root)))

        self.assertEqual(1, len(self.ran))
        self.assertFalse((root / self.mod.LOCK).exists())

    def test_the_worker_runs_again_for_a_request_made_while_it_worked(self):
        """The window the marker exists for: an edit arriving mid-refresh."""
        root = self.checkout("main")
        self.run_event({"cwd": str(root)})

        def refresh_and_edit(*_a, **_k):
            self.ran.append(("refresh", {}))
            if len(self.ran) == 1:
                self.mod.request(root)

        self.patch(mock.patch.object(
            self.mod.subprocess, "run", side_effect=refresh_and_edit))

        self.mod.work(root, self.claimed(root))

        self.assertEqual(2, len(self.ran))

    def test_a_request_made_as_the_lock_is_dropped_is_not_lost(self):
        """The narrow window the re-claim exists for: `take_request` has
        already said no, and the edit lands before the lock is gone. Its
        hook finds the lock still held and leaves a marker rather than a
        worker, so the one finishing has to look once more."""
        root = self.checkout("main")
        self.run_event({"cwd": str(root)})
        stamp = self.claimed(root)
        letting_go = self.mod.release
        once = []

        def edit_as_it_lets_go(target, owned):
            if not once:
                once.append(True)
                self.mod.request(target)
            letting_go(target, owned)

        self.patch(mock.patch.object(
            self.mod, "release", side_effect=edit_as_it_lets_go))

        self.mod.work(root, stamp)

        self.assertEqual(2, len(self.ran))

    def test_a_worker_that_never_released_is_not_waited_on_for_ever(self):
        root = self.checkout("main")
        (root / self.mod.LOCK).write_text("", encoding="utf-8")
        import os
        stale = self.mod.time.time() - self.mod.STALE_SECONDS - 60
        os.utime(root / self.mod.LOCK, (stale, stale))

        self.run_event({"cwd": str(root)})

        self.assertEqual([root], self.worker_roots())

    def test_a_live_worker_keeps_its_lock_from_going_stale(self):
        """The takeover was unsound: the mtime was written once, at claim,
        while a worker may run several updates. One still refreshing after
        the stale window had its lock unlinked from under it."""
        root = self.checkout("main")
        self.run_event({"cwd": str(root)})
        stamp = self.claimed(root)
        import os
        stale = self.mod.time.time() - self.mod.STALE_SECONDS - 60
        os.utime(root / self.mod.LOCK, (stale, stale))

        self.assertTrue(self.mod.beat(root, stamp))

        self.assertIsNone(self.mod.claim(root), "a beaten lock was taken as stale")

    def test_a_worker_whose_lock_was_taken_stops(self):
        """And does not release, because the lock is the successor's now."""
        root = self.checkout("main")
        self.run_event({"cwd": str(root)})
        stamp = self.claimed(root)
        (root / self.mod.LOCK).write_text("somebody-else", encoding="utf-8")
        self.mod.request(root)

        self.assertEqual(0, self.mod.work(root, stamp))

        self.assertEqual([], self.ran)
        self.assertEqual("somebody-else", self.mod.holder(root))

    def test_release_lets_go_of_nothing_it_does_not_own(self):
        root = self.checkout("main")
        self.run_event({"cwd": str(root)})
        (root / self.mod.LOCK).write_text("somebody-else", encoding="utf-8")

        self.mod.release(root, "ours")

        self.assertEqual("somebody-else", self.mod.holder(root))

    def test_an_update_cannot_outlive_the_stale_window(self):
        """What makes a stale lock safe to take rather than a guess: the
        worker beats before every update, and no update may run longer."""
        self.assertLess(self.mod.REFRESH_TIMEOUT, self.mod.STALE_SECONDS)

    def test_a_worker_that_will_not_start_does_not_keep_the_lock(self):
        """Otherwise one failed spawn blocks every later refresh until the
        lock goes stale."""
        root = self.checkout("main")
        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=OSError("no interpreter")))

        self.assertEqual(0, self.run_event({"cwd": str(root)}))

        self.assertFalse((root / self.mod.LOCK).exists())


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

        self.mod.refresh(self.checkout("main"))

        self.assertEqual([], self.ran)


class Registration(Base):
    def test_settings_calls_it_on_every_edit(self):
        """Every verdict above is silent if settings never runs the file.

        The entries are narrowed to the ones that run this hook before their
        matchers are read. Asking for a command and for a matcher separately
        is satisfied by a second entry that has one and not the other."""
        mine = entries_running_the_hook(SETTINGS)

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
