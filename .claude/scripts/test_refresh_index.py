"""What `.claude/hooks/refresh-index.py` refreshes, and what it leaves alone.

The hook suppresses every failure on purpose, so its exit status says
nothing: a wrong checkout, an absent CLI and a hook nothing calls all look
like success. Everything that could be wrong with it is asserted here,
registration included.
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
        """The finding this file exists for: `/branch` moves the session into
        a sibling worktree, so the tree that changed is the event's and not
        the one CLAUDE_PROJECT_DIR still names."""
        edited = self.checkout("worktree")
        original = self.checkout("main")

        self.assertEqual(0, self.run_event({"cwd": str(edited)}, original))

        self.assertEqual(1, len(self.spawned))
        self.assertEqual(str(edited), self.spawned[0][1]["cwd"])

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
        is a cost this hook was never asked for."""
        bare = self.checkout("fresh", indexed=False)

        self.assertEqual(0, self.run_event({"cwd": str(bare)}))

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

    def test_nothing_is_waited_on_and_no_stream_is_kept(self):
        """It runs on every edit, so it may not make one wait, and a hook's
        streams are not a report anybody reads."""
        self.run_event({"cwd": str(self.checkout("main"))})

        keywords = self.spawned[0][1]
        for stream in ("stdin", "stdout", "stderr"):
            self.assertEqual(self.mod.subprocess.DEVNULL, keywords[stream])

    def test_a_missing_executable_is_silent(self):
        """An index that cannot refresh is not a reason to fail the edit."""
        self.patch(mock.patch.object(
            self.mod.subprocess, "Popen", side_effect=OSError("no such executable")))

        self.assertEqual(0, self.run_event({"cwd": str(self.checkout("main"))}))

    def test_a_body_that_is_not_an_event_is_silent(self):
        self.assertEqual(0, self.run_hook("<html>not json</html>"))

        self.assertEqual([], self.spawned)

    def test_settings_calls_it_on_every_edit(self):
        """Every verdict above is silent if settings never runs the file."""
        entries = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"].get(
            "PostToolUse", [])
        commands = [
            hook["command"] for entry in entries for hook in entry.get("hooks", [])
        ]
        matchers = [entry.get("matcher", "") for entry in entries]

        self.assertTrue(any("refresh-index.py" in c for c in commands), commands)
        for tool in ("Edit", "Write", "MultiEdit", "NotebookEdit"):
            self.assertTrue(any(tool in m for m in matchers), matchers)


class RestoresWhatItPatched(unittest.TestCase):
    """The canary, and it sorts after the suite above on purpose.

    A leaked patch is invisible to the file that leaks it and fatal to every
    file loaded after it, which is how the first version of this suite passed
    alone and produced 76 errors under `discover`.
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
