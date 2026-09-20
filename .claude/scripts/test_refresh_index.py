"""What `.claude/hooks/refresh-index.py` refreshes, and what it leaves alone.

The hook suppresses every failure on purpose, so its exit status says
nothing: a wrong checkout, an absent CLI and a hook nothing calls all look
like success. The invariant held here is that the tree refreshed is the one
the edit landed in, exactly one refresh owns it, or none of them run.
"""

import importlib.util
import json
import shutil
import subprocess
import sys
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
# file's name and neither of them runs it — and the hook swallows every
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
        # Kept before the patch below, because the hook's `subprocess` is the
        # interpreter's own: a test that needs to start a real process has to
        # hold the real function rather than the stand-in.
        self.start_process = subprocess.Popen
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

        self.patch(mock.patch.object(
            self.mod.subprocess, "run", side_effect=refresh_and_edit))

        self.mod.work(root)

        self.assertEqual(2, len(self.ran))

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

    def test_real_workers_do_not_overlap(self):
        """Processes rather than calls, because sequential calls cannot show
        a lock failing: every earlier shape of this lock passed a sequential
        suite and lost a race between two claimants."""
        root = self.checkout("main")
        self.mod.request(root)
        ledger = self.tmp / "ledger.txt"
        ledger.write_text("", encoding="utf-8")
        child = self.tmp / "child.py"
        child.write_text(CHILD.format(hook=HOOK, root=root, ledger=ledger),
                         encoding="utf-8")

        workers = [
            self.start_process([sys.executable, str(child), f"w{index}"])
            for index in range(4)
        ]
        for worker in workers:
            worker.wait(timeout=120)

        lines = [line for line in ledger.read_text(encoding="utf-8").splitlines() if line]
        depth = 0
        for line in lines:
            depth += 1 if line.endswith(" in") else -1
            self.assertLessEqual(depth, 1, lines)
        self.assertEqual(2, sum(1 for line in lines if line.endswith(" in")), lines)
        self.assertFalse((root / self.mod.PENDING).exists(), lines)


# The worker each process in the case above runs: it refreshes slowly enough
# for an overlap to be visible, and asks for one more refresh the first time
# so the drain has something to pick up.
CHILD = '''
import importlib.util, pathlib, sys, time

spec = importlib.util.spec_from_file_location("refresh_index", r"{hook}")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

root = pathlib.Path(r"{root}")
ledger = pathlib.Path(r"{ledger}")
me = sys.argv[1]
once = []


def loud_refresh(_root):
    with open(ledger, "a", encoding="utf-8") as out:
        out.write(f"{{me}} in\\n")
    time.sleep(0.3)
    with open(ledger, "a", encoding="utf-8") as out:
        out.write(f"{{me}} out\\n")
    if not once:
        once.append(True)
        mod.request(root)


mod.refresh = loud_refresh
sys.exit(mod.work(root))
'''


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
