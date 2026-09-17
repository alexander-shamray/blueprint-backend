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

    def test_a_continuation_cannot_smuggle_a_substitution_past_the_scan(self):
        # Bash removes `\<newline>` inside double quotes too, so
        # `cbx search "$\<newline>(…)"` is a live `$(`. Scanning the raw
        # string never sees it.
        reason = self.judge(
            'bash .claude/skills/codebase-index/scripts/cbx search '
            '"$\\\n(whoami)"')
        self.assertIsNotNone(reason)
        self.assertIn("substitution", reason)
        reason = self.judge(
            'codebase-index search "`printf x \\\n--json`"')
        self.assertIsNotNone(reason)
        self.assertIn("substitution", reason)
        self.assertIsNone(self.judge(
            'codebase-index search "hello\\\nworld" --json'))

    def test_a_second_command_is_refused(self):
        reason = self.judge("codebase-index search x; rm -rf /")
        self.assertIsNotNone(reason)
        self.assertIn("share the line", reason)

    def test_a_write_redirection_is_refused(self):
        for command in (
            "bash .claude/skills/codebase-index/scripts/cbx search x "
            "> src/Foo.cs",
            "bash .claude/skills/codebase-index/scripts/cbx search x "
            "> /tmp/result.json",
            "bash .claude/skills/codebase-index/scripts/cbx search x "
            "2> /tmp/err",
            "bash .claude/skills/codebase-index/scripts/cbx search x "
            ">> /tmp/out",
            "bash .claude/skills/codebase-index/scripts/cbx search x "
            "&> /tmp/both",
        ):
            with self.subTest(command=command):
                reason = self.judge(command)
                self.assertIsNotNone(reason)
                self.assertIn("write redirection", reason)

    def test_a_descriptor_duplication_is_admitted(self):
        self.assertIsNone(self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx search x 2>&1"))
        self.assertIsNone(self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx search x >&2"))
        self.assertIsNone(self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx search x >&-"))

    def test_a_digit_prefixed_write_target_is_refused(self):
        # `>&` duplicates a descriptor only when the whole target is digits
        # or `-`. Bash treats `>&2file` as `>2file 2>&1`.
        reason = self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx search x >&2file")
        self.assertIsNotNone(reason)
        self.assertIn("write redirection", reason)

    def test_a_quoted_punctuation_query_is_not_a_second_command(self):
        # posix shlex drops quotes, so a query that is only `;` looks like a
        # run boundary if the split happens after tokenising. Bash does not
        # treat a quoted `;` as a separator.
        self.assertIsNone(self.judge(
            'bash .claude/skills/codebase-index/scripts/cbx search ";" '
            '--json'))
        self.assertIsNone(self.judge(
            'bash .claude/skills/codebase-index/scripts/cbx search "|" '
            '--json'))
        reason = self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx search x; "
            "echo pwned")
        self.assertIsNotNone(reason)
        self.assertIn("share the line", reason)

    def test_an_input_redirection_is_not_a_second_command(self):
        self.assertIsNone(self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx search x "
            "< /tmp/in"))

    def test_a_redirection_does_not_hide_a_second_command(self):
        reason = self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx search x "
            "> /tmp/out; rm -rf /")
        self.assertIsNotNone(reason)
        self.assertIn("share the line", reason)

    def test_graph_is_refused(self):
        reason = self.judge(
            "bash .claude/skills/codebase-index/scripts/cbx graph X --output x.html")
        self.assertIsNotNone(reason)
        self.assertIn("graph", reason)

    def test_unrelated_bash_is_admitted(self):
        self.assertIsNone(self.judge("git status -sb"))

    def test_rg_with_the_cli_name_in_the_query_is_admitted(self):
        self.assertIsNone(self.judge("rg codebase-index src"))

    def test_echo_of_the_cli_name_is_admitted(self):
        self.assertIsNone(self.judge("echo codebase-index"))

    def test_substitution_in_an_unrelated_command_is_admitted(self):
        self.assertIsNone(self.judge('rg codebase-index "$(printf x)"'))

    def test_python_module_graph_is_refused(self):
        reason = self.judge(
            "python -m codebase_index graph X --output x.html")
        self.assertIsNotNone(reason)
        self.assertIn("graph", reason)

    def test_echo_dash_m_codebase_index_is_admitted(self):
        self.assertIsNone(self.judge("echo -m codebase_index graph"))

    def test_python_script_args_are_not_a_module_invocation(self):
        self.assertIsNone(self.judge(
            "python script.py -m codebase_index graph"))

    def test_python_interpreter_flags_before_the_module_still_count(self):
        reason = self.judge("python -u -W ignore -m codebase_index graph X")
        self.assertIsNotNone(reason)
        self.assertIn("graph", reason)

    def test_py_launcher_module_graph_is_refused(self):
        reason = self.judge("py -3.12 -m codebase_index graph X")
        self.assertIsNotNone(reason)
        self.assertIn("graph", reason)

    def test_a_list_event_fails_open(self):
        result = subprocess.run(
            [sys.executable, str(HOOK)],
            input="[]", capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout.strip())

    def test_a_non_object_tool_input_fails_open(self):
        event = {
            "hook_event_name": "PreToolUse",
            "tool_name": "Bash",
            "tool_input": ["command"],
        }
        result = subprocess.run(
            [sys.executable, str(HOOK)],
            input=json.dumps(event), capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout.strip())

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
