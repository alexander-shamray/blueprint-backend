"""What `.claude/hooks/index-query-hint.py` says, and to which prompts. It
fails open and is silent on most, so its exit status proves nothing; the
cases run it as `settings.json` does, a process over stdin, and read what it
printed."""

import json
import re
import shlex
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CLAUDE = SCRIPTS.parent
HINT = CLAUDE / "hooks" / "index-query-hint.py"
SETTINGS = CLAUDE / "settings.json"
SKILL = CLAUDE / "skills" / "codebase-index" / "SKILL.md"
EVENT = "UserPromptSubmit"
COMMAND = ("py -3.12 -P -c \"import runpy; runpy.run_path("
           "r'${CLAUDE_PROJECT_DIR}/.claude/hooks/index-query-hint.py', run_name='__main__')\"")

# One prompt per alternative of each pattern, and the subcommand it routes to.
CASES = {
    "what breaks if OrderPlaced changes?": "impact",
    "What depends on Money?": "impact",
    "what is the impact of renaming the outbox table": "impact",
    "who calls PlaceOrderHandler": "refs",
    "what calls the publisher?": "refs",
    "What uses IClock in Ordering?": "refs",
    "how does the saga work?": "explain",
    "How does Order.Place work": "explain",
    "how does the outbox relay keep working": "explain",
    "how does the inbox filter works": "explain",
    "find the class that parses money": "symbol",
    "find method names for refunds": "symbol",
    "find handlers for OrderPaid": "symbol",
    "find the classes under Catalog": "symbol",
    "where is the idempotency store?": "search",
    "Where are the migrations kept": "search",
    "where does Shipping publish ShipmentDispatched": "search",
}

# Each looks like a question above and is not one.
NOT_QUESTIONS = (
    "somewhere is a bug in the saga",
    "elsewhere is fine",
    "the impact offset is wrong",
    "nobody calls back",
    "how does it. Work on the saga next",
    "finder classes need a rename",
    "rewhere is not a word",
    "fix the build",
    "",
)


def run(payload: bytes):
    return subprocess.run(
        [sys.executable, str(HINT)], input=payload, capture_output=True,
        timeout=30, check=False)


def ask(prompt: str):
    return run(json.dumps({"hook_event_name": EVENT, "prompt": prompt}).encode())


def context(done) -> str:
    """The `additionalContext` a run returned, or '' when it said nothing."""
    if not done.stdout.strip():
        return ""
    answer = json.loads(done.stdout)
    output = answer["hookSpecificOutput"]
    assert output["hookEventName"] == EVENT, answer
    return output["additionalContext"]


def emitted(text: str) -> str:
    """The command inside the hint's backticks."""
    found = re.search(r"`([^`]+)`", text)
    assert found, text
    return found.group(1)


class TheWiring(unittest.TestCase):
    def test_it_runs_on_every_prompt_and_under_no_other_event(self):
        hooks = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"]
        running = [
            event for event, entries in hooks.items()
            for entry in entries
            for hook in entry.get("hooks", [])
            if hook.get("command") == COMMAND
        ]

        self.assertEqual([EVENT], running)

    def test_the_registered_command_cannot_erase_a_prompt(self):
        """Python exits 2 on a file it cannot open, a worktree emptied under a
        running session, and exit 2 here erases every prompt; through `runpy`
        the same failure exits 1, which only reports."""
        with tempfile.TemporaryDirectory() as gone:
            for root, hinted in ((CLAUDE.parent, True), (Path(gone), False)):
                argv = shlex.split(COMMAND.replace("${CLAUDE_PROJECT_DIR}", root.as_posix()))
                done = subprocess.run(
                    [sys.executable, *argv[2:]], capture_output=True, timeout=30, check=False,
                    input=json.dumps({"hook_event_name": EVENT, "prompt": "where is X"}).encode())
                with self.subTest(root=str(root)):
                    self.assertNotEqual(2, done.returncode, done.stderr)
                    self.assertEqual(hinted, bool(context(done)), done.stderr)

    def test_its_entry_has_no_matcher(self):
        """A prompt event takes none; one written would be ignored or read
        as a filter nobody meant."""
        hooks = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"]
        for entry in hooks[EVENT]:
            self.assertNotIn("matcher", entry)


class TheHint(unittest.TestCase):
    def test_each_kind_of_question_names_its_own_subcommand(self):
        for prompt, subcommand in CASES.items():
            with self.subTest(prompt=prompt):
                done = ask(prompt)
                self.assertEqual(0, done.returncode, done.stderr)
                command = emitted(context(done))
                self.assertEqual(subcommand, command.split()[2], command)

    def test_every_route_is_reached_by_a_case(self):
        reached = {emitted(context(ask(prompt))) for prompt in CASES}

        self.assertEqual({row[2] for row in _module().ROUTES}, reached)

    def test_a_phrase_that_is_not_the_question_is_silent(self):
        for prompt in NOT_QUESTIONS:
            with self.subTest(prompt=prompt):
                done = ask(prompt)
                self.assertEqual(0, done.returncode, done.stderr)
                self.assertEqual(b"", done.stdout.strip())

    def test_ship_and_branch_are_read_for_the_task_they_carry(self):
        for prompt in ("/ship where is the outbox relay?",
                       "/branch how does checkout work"):
            with self.subTest(prompt=prompt):
                self.assertTrue(context(ask(prompt)))

    def test_every_other_slash_command_is_silent_whatever_follows_it(self):
        for prompt in ("/commit where is the outbox relay?",
                       "/shipping where is it", "/review-branch who calls X",
                       "  /pr how does it work"):
            with self.subTest(prompt=prompt):
                self.assertEqual(b"", ask(prompt).stdout.strip())

    def test_only_the_prompt_is_read_and_never_the_rest_of_the_payload(self):
        payload = {"hook_event_name": EVENT, "prompt": "fix the build",
                   "cwd": "C:/where is/who calls", "transcript_path": "where are"}

        self.assertEqual(b"", run(json.dumps(payload).encode()).stdout.strip())

    def test_a_prompt_spread_over_lines_still_reads(self):
        self.assertTrue(context(ask("where\nis the relay")))

    def test_a_long_prompt_is_read_as_far_as_the_cap(self):
        cap = _module().CAP
        self.assertTrue(context(ask("where is it " + "x" * (cap * 4))))
        self.assertEqual(b"", ask("x " * cap + "where is it").stdout.strip())

    def test_a_prompt_in_utf_8_is_read(self):
        """Raw UTF-8, as the payload arrives: `\u0141` carries a byte the Windows
        console code page has no character for, so decoding it there fails."""
        payload = json.dumps({"hook_event_name": EVENT,
                              "prompt": "where is the \u0141\u00f3d\u017a relay \u2014 the outbox?"},
                             ensure_ascii=False).encode("utf-8")

        self.assertTrue(context(run(payload)))

    def test_input_it_cannot_read_is_silent_and_never_blocks(self):
        for payload in (b"", b"<html>", b"\xff\xfe\x00", b'{"prompt": 7}',
                        b"[1, 2]", b'{"prompt": null}'):
            with self.subTest(payload=payload):
                done = run(payload)
                self.assertEqual(0, done.returncode, done.stderr)
                self.assertEqual(b"", done.stdout.strip())


class TheSkillAgrees(unittest.TestCase):
    """A hint naming a command the skill does not route, or does not approve,
    sends the session to a permission prompt instead of the index."""

    def setUp(self):
        self.skill = SKILL.read_text(encoding="utf-8")

    def test_every_command_it_emits_is_a_row_of_the_route_table(self):
        rows = set(re.findall(r"^\|[^|]+\| `([^`]+)` \|$", self.skill, re.M))
        for _kind, _pattern, command in _module().ROUTES:
            with self.subTest(command=command):
                self.assertIn(command.replace(" --limit 3", ""), rows)

    def test_search_carries_the_limit_the_skill_sets(self):
        self.assertIn("**`search` takes `--limit 3`**", self.skill)
        search = [row[2] for row in _module().ROUTES if " search " in row[2]]
        self.assertEqual(1, len(search))
        self.assertIn("--limit 3", search[0])

    def test_every_subcommand_it_names_is_one_the_skill_approves(self):
        front = self.skill.split("---")[1]
        for _kind, _pattern, command in _module().ROUTES:
            prefix = " ".join(command.split()[:3])
            with self.subTest(command=command):
                self.assertIn(f"Bash({prefix}:*)", front)


def _module():
    import importlib.util

    spec = importlib.util.spec_from_file_location("index_query_hint", HINT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


if __name__ == "__main__":
    unittest.main()
