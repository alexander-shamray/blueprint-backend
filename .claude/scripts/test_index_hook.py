"""index-hook.sh: the root check and the index refresh start on whatever Python the host has, and never exit 2.

A raw `py -3.12` exits 127 where the Windows launcher is absent, so on macOS and Linux neither hook ran. These cases
run each registered command's arguments through the launcher with stand-in interpreters on a PATH built here.
"""

import json
import os
import shlex
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from review_helpers import BASH, SCRIPTS, SETTINGS, setUpModule  # noqa: F401 - unittest calls setUpModule

WRAPPER = SCRIPTS.parent / "hooks" / "index-hook.sh"
LAUNCHER = 'bash "${CLAUDE_PROJECT_DIR}/.claude/hooks/index-hook.sh"'
HOOKS = ("index-mcp-root.py", "refresh-index.py")
# Each hook under the events it has to run under, and the matcher it runs with there.
WIRED = {
    ("PreToolUse", "index-mcp-root.py"): "mcp__codebase-index__.*",
    ("PostToolUse", "refresh-index.py"): "Edit|Write|NotebookEdit|MultiEdit|Bash|PowerShell",
    ("SessionStart", "refresh-index.py"): None,
}


def registered():
    """(event, matcher, command) for every hook command settings.json holds."""
    events = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"]
    return [(event, entry.get("matcher"), hook.get("command", ""))
            for event, entries in events.items() for entry in entries for hook in entry.get("hooks", [])]


class TheWiring(unittest.TestCase):
    def test_every_command_naming_an_index_hook_goes_through_the_launcher(self):
        # The subject: every command these cases have to judge, found by the hook it names, not by the launcher.
        found = [(event, command) for event, _, command in registered() if any(h in command for h in HOOKS)]
        self.assertEqual(len(WIRED), len(found), found)
        for event, command in found:
            with self.subTest(event=event, command=command):
                self.assertTrue(command.startswith(LAUNCHER + " "), command)
                self.assertNotIn("py -3.12", command)

    def test_each_hook_runs_under_its_events_and_names_the_event_it_runs_under(self):
        wired = {}
        for event, matcher, command in registered():
            if command.startswith(LAUNCHER + " "):
                named, hook = shlex.split(command)[2:]
                self.assertEqual(event, named, command)
                wired[(event, hook)] = matcher
        self.assertEqual(WIRED, wired)


class TheLauncher(unittest.TestCase):
    def path_with(self, **programs):
        """A PATH holding the shell's own tools and stand-ins named by `programs`, each a bash body, and no other
        interpreter the launcher looks for."""
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d, ignore_errors=True)
        for name, body in programs.items():
            program = Path(d) / name
            program.write_text("#!/usr/bin/env bash\n" + body, encoding="utf-8")
            program.chmod(0o755)
        # macOS splits these between /bin and /usr/bin, so each one `cat`'s directory lacks adds its own.
        tools = os.path.dirname(shutil.which("cat"))
        for tool in ("dirname", "mktemp", "rm"):
            if shutil.which(tool, path=tools) is None:
                tools += os.pathsep + os.path.dirname(shutil.which(tool))
        if any(shutil.which(name, path=tools) for name in ("py", "python3", "python")):
            # Linux and macOS keep a real `python3` beside `cat`, which the launcher would reach before a stand-in
            # `python`, so the tools go in through links of their own; Windows' Git tools hold none.
            linked = tempfile.mkdtemp()
            self.addCleanup(shutil.rmtree, linked, ignore_errors=True)
            for tool in ("bash", "cat", "dirname", "mktemp", "rm", "touch"):
                os.symlink(shutil.which(tool), Path(linked) / tool)
            tools = linked
        for interpreter in ("py", "python3", "python"):
            self.assertIsNone(shutil.which(interpreter, path=tools), f"the tools directories hold a `{interpreter}`")
        return d + os.pathsep + tools, Path(d)

    def launch(self, path, event, hook, stdin='{"cwd":"x"}\n'):
        env = dict(os.environ, PATH=path, CLAUDE_PROJECT_DIR=str(SCRIPTS.parent.parent))
        return subprocess.run([BASH, str(WRAPPER), event, hook], input=stdin, capture_output=True, text=True,
                              encoding="utf-8", env=env, timeout=60)

    def test_each_registered_command_runs_where_there_is_no_windows_launcher(self):
        # macOS and Linux: no `py`, a `python3`. The stand-in returns its input, so the payload crossing both ways
        # says the command started an interpreter and handed it the event.
        for (event, hook) in WIRED:
            for interpreter in ("python3", "python"):
                with self.subTest(event=event, hook=hook, interpreter=interpreter):
                    path, d = self.path_with(**{interpreter: 'echo "$@" > "$(dirname "$0")/argv"; cat\n'})
                    r = self.launch(path, event, hook)
                    self.assertEqual((0, '{"cwd":"x"}\n'), (r.returncode, r.stdout), r.stderr)
                    argv = (d / "argv").read_text(encoding="utf-8")
                    self.assertTrue(argv.startswith("-P -c "), argv)

    def test_py_is_taken_before_python3(self):
        # Windows' `python3` is the Store alias: on PATH and not Python.
        path, _ = self.path_with(py='[ "$1" = -3.12 ] && echo from-py\n', python3="echo from-python3\n")
        r = self.launch(path, "SessionStart", "refresh-index.py")
        self.assertEqual((0, "from-py\n"), (r.returncode, r.stdout), r.stderr)

    def test_a_hook_that_fails_leaves_a_line_naming_it_and_still_exits_zero(self):
        # Exit 2 under PreToolUse refuses every index call; an interpreter too old for `-P` exits 2 on it.
        for (event, hook) in WIRED:
            with self.subTest(event=event, hook=hook):
                path, _ = self.path_with(python3="echo half; exit 2\n")
                r = self.launch(path, event, hook)
                self.assertEqual(0, r.returncode, r.stderr)
                self.assertNotIn("half", r.stdout)
                out = json.loads(r.stdout)["hookSpecificOutput"]
                self.assertEqual(event, out["hookEventName"])
                self.assertNotIn("permissionDecision", out)
                self.assertIn(f"{hook} did not run (exit 2 under python3)", out["additionalContext"])

    def test_the_real_hook_runs_through_it(self):
        # The positive control on the shipped files and this host's own interpreters: the root check, handed an
        # event naming no tool, lets it through and says nothing, so any output here is the failure line. Not the
        # refresh, which would refresh the checkout this suite runs in.
        r = subprocess.run([BASH, str(WRAPPER), "PreToolUse", "index-mcp-root.py"], input="{}", capture_output=True,
                           text=True, encoding="utf-8", timeout=60,
                           env=dict(os.environ, CLAUDE_PROJECT_DIR=str(SCRIPTS.parent.parent)))
        self.assertEqual((0, ""), (r.returncode, r.stdout), r.stderr)

    def test_a_hook_outside_the_set_starts_nothing(self):
        path, d = self.path_with(python3='touch "$(dirname "$0")/ran"\n')
        r = self.launch(path, "PreToolUse", "guard-git-argv.py")
        self.assertEqual((0, ""), (r.returncode, r.stdout), r.stderr)
        self.assertIn("not a hook this launcher runs", r.stderr)
        self.assertFalse((d / "ran").exists())


if __name__ == "__main__":
    unittest.main()
