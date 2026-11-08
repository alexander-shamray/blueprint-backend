"""What `.claude/hooks/guard-helper-argv.py` refuses, and what it must not; registration is a case of its own."""

import json
import re
import subprocess
import sys
import unittest
from pathlib import Path

from review_helpers import assert_launched_on_the_floor

SCRIPTS = Path(__file__).resolve().parent
HOOK = SCRIPTS.parent / "hooks" / "guard-helper-argv.py"
SETTINGS = SCRIPTS.parent / "settings.json"
COMMANDS = SCRIPTS.parent / "commands"
BOARD = "py -3.12 .claude/scripts/dispatch_board.py"


class GuardHelperArgv(unittest.TestCase):
    def judge(self, command, tool="Bash"):
        event = {"hook_event_name": "PreToolUse", "tool_name": tool, "tool_input": {"command": command}}
        done = subprocess.run([sys.executable, str(HOOK)], input=json.dumps(event).encode("utf-8"),
                              capture_output=True, check=True)
        out = done.stdout.decode("utf-8").strip()
        return json.loads(out)["hookSpecificOutput"]["permissionDecisionReason"] if out else None

    def test_a_substitution_on_a_helper_line_is_refused(self):
        for command in (
            f'{BOARD} run 20261101-220000 S1 --description "`id`"',
            f'{BOARD} run 20261101-220000 S1 --description "fix $(id) now"',
            f"{BOARD} tasks place --tasks l --run r 1 S1 7 --task \"`printf x \\\n--json`\"",
            'py -3.12 .claude/scripts/issue_decisions.py record 1 --run "$(id)"',
            'py -3.12 .claude/scripts/sync_pr.py verify 1 --head `id`',
            'gh pr create --base main --title "fix: read `guard.py`" --body-file b.md',
            'gh pr create --base main --title t --body "$(cat b.md)"',
            f'cd /tmp && {BOARD} wake r --tasks "$(id)"',
            f'{BOARD} wake r --tasks l > "$(id)"',
            f'oid=$(git rev-parse HEAD)\n{BOARD} verify r S1 --head "$oid"',
            f'{BOARD} wake r <<EOF\n$(id)\nEOF',
        ):
            with self.subTest(command=command):
                self.assertIn("substitution", self.judge(command) or "")

    def test_a_wrapped_helper_line_is_refused(self):
        for command in (
            f'PYTHONUTF8=1 {BOARD} x "$(id)"',
            f'A=1 B=2 {BOARD} x "$(id)"',
            f'env PYTHONUTF8=1 {BOARD} x "$(id)"',
            f'env -u HOME {BOARD} x "$(id)"',
            f'timeout 60 {BOARD} x "$(id)"',
            f'timeout -k 5 60 {BOARD} x "$(id)"',
            f'nohup {BOARD} x "$(id)"',
            'py -3.12 -X utf8 .claude/scripts/dispatch_board.py x "$(id)"',
            'python -W ignore .claude/scripts/sync_pr.py x `id`',
            'gh --repo o/r pr create --title "$(id)"',
            'gh -R o/r pr create --title "$(id)"',
            'GH_TOKEN=x gh --repo=o/r pr create --title `id`',
            'if py -3.12 .claude/scripts/sync_pr.py verify 1 --head "$(id)"; then echo ok; fi',
            'for n in 1; do py -3.12 .claude/scripts/sync_pr.py verify 1 --head "$(id)"; done',
            '! py -3.12 .claude/scripts/sync_pr.py verify 1 --head "$(id)"',
        ):
            with self.subTest(command=command):
                self.assertIn("substitution", self.judge(command) or "")

    def test_a_wrapped_line_that_runs_no_helper_is_left_alone(self):
        for command in (
            'PYTHONUTF8=1 echo "$(id)"',
            'timeout 60 py -3.12 -c "print(1)" "$(id)"',
            'py -3.12 -X utf8 -m unittest "$(ls x)"',
            'gh --repo o/r pr view "$(id)"',
            'env -u HOME gh issue list --search "$(id)"',
        ):
            with self.subTest(command=command):
                self.assertIsNone(self.judge(command))

    def test_the_same_text_kept_from_the_shell_passes(self):
        for command in (
            f"{BOARD} run 20261101-220000 S1",
            f"{BOARD} result 20261101-220000 S1 --refused 'it says `id` and $(id)'",
            "gh pr create --base main --title 'fix: read `guard.py`' --body-file b.md",
            "gh pr create --base main --title 'it'\\''s `quoted`' --body-file b.md",
        ):
            with self.subTest(command=command):
                self.assertIsNone(self.judge(command))

    def test_a_single_quote_that_lets_the_text_out_is_refused(self):
        self.assertIn("substitution", self.judge("gh pr create --title 'it's $(id)' --body-file b.md") or "")

    def test_a_line_that_runs_no_helper_is_left_alone(self):
        for command in (
            'echo "$(id)"',
            'git log --format=%s "$(git merge-base HEAD main)"',
            f'echo "{BOARD} `id`"',
            'grep -n "gh pr create" "$(ls b.md)"',
            'py -3.12 -c "print(1)" "$(id)"',
            'py -3.12 -m unittest discover -s .claude/scripts -p "$(ls x)"',
            f"cat <<'EOF'\n{BOARD} run \"$(id)\"\nEOF",
            f'# {BOARD} "$(id)"\necho ok',
        ):
            with self.subTest(command=command):
                self.assertIsNone(self.judge(command))

    def test_only_a_bash_event_is_judged(self):
        self.assertIsNone(self.judge(f'{BOARD} "$(id)"', tool="Read"))

    def test_an_event_nothing_can_read_is_named_rather_than_crashing(self):
        done = subprocess.run([sys.executable, str(HOOK)], input=b"not json", capture_output=True, check=True)
        self.assertEqual(b"", done.stdout)
        done = subprocess.run([sys.executable, str(HOOK)], input=b"\xff", capture_output=True, check=True)
        self.assertIn("UTF-8", done.stdout.decode("utf-8"))

    def test_the_hook_is_registered_for_bash(self):
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
        commands = [hook.get("command", "") for entry in settings["hooks"]["PreToolUse"]
                    if entry.get("matcher") == "Bash" for hook in entry.get("hooks", [])]
        mine = [command for command in commands if HOOK.name in command]
        self.assertEqual(1, len(mine), commands)
        assert_launched_on_the_floor(self, mine[0], HOOK.name)


class TheRunbooksKeepIssueTextFromTheShell(unittest.TestCase):
    """The guard's subject: each line a runbook tells a session to type with a prefix grant on it."""

    def test_no_helper_line_puts_a_placeholder_in_double_quotes(self):
        for name in ("dispatch.md", "pr.md"):
            text = (COMMANDS / name).read_text(encoding="utf-8")
            # pr.md's create line is the helper's since the raw `gh pr create` grant was withdrawn.
            lines = re.findall(r"^\s*(?:py -3\.12 \.claude/scripts/\S+|gh pr create|bash \.claude/scripts/gh-pr-create\.sh)"
                               r".*$", text, re.MULTILINE)
            self.assertTrue(lines, name)
            for found in lines:
                with self.subTest(line=found):
                    self.assertNotRegex(found, r'"[^"]*<')

    def test_the_task_text_is_written_before_anything_a_resume_would_read_it_after(self):
        text = (COMMANDS / "dispatch.md").read_text(encoding="utf-8")
        launch = text[text.index("**Launch a group**"):text.index("**On an executor's exit**")]
        self.assertLess(launch.index("<g>.task.txt"), launch.index("tasks move"))

    def test_the_inline_refused_flag_is_single_quoted(self):
        text = " ".join((COMMANDS / "dispatch.md").read_text(encoding="utf-8").split())
        self.assertIn("--refused '<", text)
        self.assertNotIn("--description", text)
        self.assertNotRegex(text, r"--(task|labels|depends|model|conflicts)\b")


if __name__ == "__main__":
    unittest.main()
