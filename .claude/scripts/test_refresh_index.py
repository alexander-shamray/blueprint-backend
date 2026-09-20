"""What `.claude/hooks/refresh-index.py` refreshes, and what it leaves alone.

The hook suppresses every failure on purpose, so its exit status says
nothing: a wrong checkout, an absent CLI and a hook nothing calls all look
like success. The invariant held here is that the tree refreshed is the one
the edit landed in, or none of them.
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
CACHE = Path(".claude") / "cache" / "codebase-index"


def _load():
    spec = importlib.util.spec_from_file_location("refresh_index", HOOK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class _Stdin:
    """`sys.stdin` for one call, with only the method the hook uses."""

    def __init__(self, body):
        self.body = body

    def read(self):
        return self.body


class RefreshIndex(unittest.TestCase):
    """Every patch below is undone by addCleanup, and that is load-bearing.

    `subprocess`, `os` and `sys` inside the hook are the interpreter's own
    modules rather than copies of them, so an attribute set on one and left
    there reaches every other suite in the same discover run.
    """

    def setUp(self):
        self.mod = _load()
        self.spawned = []
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen",
            side_effect=lambda *a, **k: self.spawned.append((a, k))))

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
        return self.mod.main()

    def run_event(self, event, project_dir=None):
        return self.run_hook(json.dumps(event), project_dir)

    def test_the_events_cwd_decides_the_checkout(self):
        """`/branch` moves the session into a sibling worktree, so the tree
        that changed is the event's and not the one `CLAUDE_PROJECT_DIR`
        still names. `guard-edit-target.anchors` owns the distinction."""
        edited = self.checkout("worktree")
        original = self.checkout("main")

        self.assertEqual(0, self.run_event({"cwd": str(edited)}, original))

        self.assertEqual(1, len(self.spawned))
        self.assertEqual(str(edited), self.spawned[0][1]["cwd"])

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

        self.assertEqual(str(edited), self.spawned[0][1]["cwd"])

    def test_a_relative_edit_climbing_out_lands_in_the_other_checkout(self):
        """`guard-edit-target` admits a non-link `..` path, and the session's
        own checkout is among the lexical parents of one, so an unresolved
        walk finds the wrong `.git` first."""
        edited = self.checkout("original")
        session = self.checkout("worktree")
        (edited / "src").mkdir(parents=True)

        self.run_event({"cwd": str(session),
                        "tool_input": {"file_path": "../original/src/Thing.cs"}})

        self.assertEqual(str(edited.resolve()), self.spawned[0][1]["cwd"])

    def test_a_relative_edit_is_resolved_against_the_session_directory(self):
        session = self.checkout("worktree")
        (session / "src").mkdir(parents=True)

        self.run_event({"cwd": str(session),
                        "tool_input": {"file_path": "src/Thing.cs"}})

        self.assertEqual(str(session), self.spawned[0][1]["cwd"])

    def test_an_event_naming_no_file_falls_back_to_the_directory(self):
        session = self.checkout("worktree")

        self.run_event({"cwd": str(session), "tool_input": {}})

        self.assertEqual(str(session), self.spawned[0][1]["cwd"])

    def test_a_nested_cwd_walks_up_to_its_checkout(self):
        edited = self.checkout("worktree")
        deep = edited / "src" / "Services"
        deep.mkdir(parents=True)

        self.run_event({"cwd": str(deep)})

        self.assertEqual(str(edited), self.spawned[0][1]["cwd"])

    def test_the_project_dir_is_the_fallback(self):
        original = self.checkout("main")

        self.run_event({}, original)

        self.assertEqual(str(original), self.spawned[0][1]["cwd"])

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

    def test_the_call_is_the_cli_with_auto_update_off(self):
        """An unpinned newer package rewrites the tracked skill when it runs,
        and this calls the CLI rather than the wrapper that would stop it."""
        self.run_event({"cwd": str(self.checkout("main"))})

        arguments, keywords = self.spawned[0]
        self.assertEqual(["codebase-index", "update"], arguments[0])
        self.assertEqual("1", keywords["env"]["CBX_NO_SKILL_AUTO_UPDATE"])
        self.assertEqual(1, len(self.spawned), "the fallback ran as well")

    def test_nothing_is_waited_on_and_no_stream_is_kept(self):
        """It runs on every edit, so it may not make one wait, and a hook's
        streams are not a report anybody reads."""
        self.run_event({"cwd": str(self.checkout("main"))})

        keywords = self.spawned[0][1]
        for stream in ("stdin", "stdout", "stderr"):
            self.assertEqual(self.mod.subprocess.DEVNULL, keywords[stream])

    def test_a_missing_console_script_falls_back_to_the_module(self):
        """`py -3.12 -m pip` is a supported install and need not put the
        console script on PATH, which is why both `cbx` wrappers fall back
        the same way. Swallowing the error instead leaves those sessions
        never refreshing, and saying nothing about it."""
        attempts = []

        def absent_script(command, **keywords):
            attempts.append(command)
            if command[0] == "codebase-index":
                raise OSError("no such executable")
            self.spawned.append(((command,), keywords))

        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=absent_script))

        self.assertEqual(0, self.run_event({"cwd": str(self.checkout("main"))}))

        self.assertEqual(2, len(attempts), attempts)
        self.assertEqual(
            [self.mod.sys.executable, "-P", "-m", "codebase_index", "update"],
            attempts[1])
        self.assertEqual(1, len(self.spawned))

    def test_neither_form_running_is_silent(self):
        """An index that cannot refresh is not a reason to fail the edit."""
        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=OSError("nothing runnable")))

        self.assertEqual(0, self.run_event({"cwd": str(self.checkout("main"))}))

        self.assertEqual([], self.spawned)

    def test_a_body_that_is_not_an_event_is_silent(self):
        self.assertEqual(0, self.run_hook("<html>not json</html>"))

        self.assertEqual([], self.spawned)

    def test_settings_calls_it_on_every_edit(self):
        """Every verdict above is silent if settings never runs the file.

        The entries are narrowed to the ones that run this hook before their
        matchers are read. Asking for a command and for a matcher separately
        is satisfied by a second entry that has one and not the other."""
        entries = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"].get(
            "PostToolUse", [])
        mine = [
            entry for entry in entries
            if any("refresh-index.py" in hook.get("command", "")
                   for hook in entry.get("hooks", []))
        ]

        # Split before comparing: `"Edit" in "NotebookEdit|MultiEdit"` is
        # true, so a matcher that had lost plain Edit would satisfy a
        # substring test while the primary edit surface went unwatched.
        matched = {
            alternative.strip()
            for entry in mine
            for alternative in entry.get("matcher", "").split("|")
        }

        self.assertTrue(mine, entries)
        for tool in ("Edit", "Write", "MultiEdit", "NotebookEdit"):
            self.assertIn(tool, matched, mine)


class RestoresWhatItPatched(unittest.TestCase):
    """The canary, and it sorts after the suite above on purpose.

    A leaked patch is invisible to the file that leaks it and fatal to every
    file loaded after it, so the suite that would notice has to be one that
    runs later.
    """

    def test_the_interpreters_modules_are_as_they_were(self):
        import os as real_os
        import subprocess as real_subprocess
        import sys as real_sys

        self.assertFalse(isinstance(real_subprocess.Popen, mock.MagicMock))
        self.assertNotIsInstance(real_os.environ, dict)
        self.assertTrue(hasattr(real_sys.stdin, "fileno"))


if __name__ == "__main__":
    unittest.main()
