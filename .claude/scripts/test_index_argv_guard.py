"""What `.claude/hooks/guard-index-argv.py` refuses, and what it must not.

Prefix grants auto-approve `search:*`, so a second command or a `$(…)` after
that prefix would run without a prompt. The hook is the argv-level guard.
Registration is a separate case: every verdict here is silent if settings
never calls the file.
"""

import importlib.util
import json
import subprocess
import sys
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
HOOK = SCRIPTS.parent / "hooks" / "guard-index-argv.py"
SETTINGS = SCRIPTS.parent / "settings.json"


def _load():
    spec = importlib.util.spec_from_file_location("guard_index_argv", HOOK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class GuardIndexArgv(unittest.TestCase):
    def setUp(self):
        self.mod = _load()

    def judge(self, command):
        event = {
            "hook_event_name": "PreToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": command},
        }
        result = subprocess.run(
            [sys.executable, str(HOOK)],
            input=json.dumps(event), capture_output=True, text=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        if not result.stdout.strip():
            return None
        payload = json.loads(result.stdout)["hookSpecificOutput"]
        self.assertEqual("deny", payload["permissionDecision"])
        return payload["permissionDecisionReason"]

    def test_honest_search_is_admitted(self):
        self.assertIsNone(self.judge(
            'bash .claude/skills/codebase-index/scripts/cbx search "X" --json'))

    def test_a_substitution_is_refused(self):
        reason = self.judge('codebase-index search "$(rm -rf /)" --json')
        self.assertIsNotNone(reason)
        self.assertIn("substitution", reason)

    def test_a_second_command_is_refused(self):
        reason = self.judge("codebase-index search x; rm -rf /")
        self.assertIsNotNone(reason)
        self.assertIn("share the line", reason)

    def test_graph_is_refused(self):
        reason = self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx graph X --output x.html")
        self.assertIsNotNone(reason)
        self.assertIn("graph", reason)

    def test_unrelated_bash_is_admitted(self):
        self.assertIsNone(self.judge("git status -sb"))

    def test_the_hook_is_registered_for_bash(self):
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
        entries = settings.get("hooks", {}).get("PreToolUse", [])
        commands = [
            h.get("command", "")
            for entry in entries if entry.get("matcher") == "Bash"
            for h in entry.get("hooks", [])
        ]
        self.assertTrue(
            any(HOOK.name in c for c in commands),
            f"{HOOK.name} is not among the registered Bash hooks: {commands}")


if __name__ == "__main__":
    unittest.main()
