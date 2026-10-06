"""What `.claude/hooks/index-search-hint.py` says after a tool call, and when.
It fails open and is silent on most calls, so its exit status proves nothing;
the cases run it as `settings.json` does, a process over stdin, beside a
transcript written for the case, and read what it printed."""

import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CLAUDE = SCRIPTS.parent
HINT = CLAUDE / "hooks" / "index-search-hint.py"
SETTINGS = CLAUDE / "settings.json"
SKILL = CLAUDE / "skills" / "codebase-index" / "SKILL.md"
EVENT = "PostToolUse"
MATCHER = "Grep|Bash"
COMMAND = ("py -3.12 -P -c \"import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR']"
           " + '/.claude/hooks/index-search-hint.py', run_name='__main__')\"")
EVERY = 5

# Each is a search over the tree, and the tool the hint sends it to.
SEARCHES = {
    'grep -rn "ClaimAsync(" src': "find_refs",
    "grep -R HealthProbe .": "find_refs",
    "grep --recursive -n GiveUpAge src tests": "find_refs",
    "git grep -n 'Order.Place'": "find_refs",
    "git -C ../other grep OutboxRelay": "find_refs",
    "rg TimeSpan src": "find_refs",
    "rg 'class \\w+Options'": "search_code",
    'grep -rE "Claim|Release" src': "search_code",
    "grep -rn health/ready src 2>/dev/null | head -5": "search_code",
    "LC_ALL=C grep -r Money .": "find_refs",
    "cd src && grep -rn PlaceOrderHandler .": "find_refs",
    "/usr/bin/grep -rn Saga src": "find_refs",
    "grep -r -e 'Retry(' src": "find_refs",
}

# Each reads one file, a pipe or prose, which the index does not replace.
NOT_SEARCHES = (
    "grep -n Money src/Money.cs",
    "git log --oneline | grep fix",
    "cat build.log | rg error",
    "grep -rn Saga docs/backend-architecture",
    "grep -rn Saga docs 2>/dev/null",
    "rg -g '*.md' outbox",
    "rg --type md outbox",
    "grep -r --include=*.md outbox .",
    "echo grep -r Foo .",
    "git status --short",
    "dotnet build Platform.slnx",
)


def grep_call(pattern="ClaimAsync\\(", **extra):
    return "Grep", {"pattern": pattern, **extra}


def bash_call(command):
    return "Bash", {"command": command}


def tool_use(call_id, name, given):
    return {"type": "assistant", "message": {"role": "assistant", "content": [
        {"type": "tool_use", "id": call_id, "name": name, "input": given}]}}


class Session:
    """A transcript of earlier tool calls, and the hook run after one more."""

    def __init__(self, scratch: Path, earlier=(), lines=()):
        self.path = scratch / "transcript.jsonl"
        rows = [tool_use(f"toolu_{index}", name, given) for index, (name, given) in enumerate(earlier)]
        with self.path.open("w", encoding="utf-8") as handle:
            for row in [*lines, *rows]:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")

    def after(self, name, given, cwd=None, recorded=False, call_id="toolu_now"):
        if recorded:
            with self.path.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps(tool_use(call_id, name, given)) + "\n")
        event = {"hook_event_name": EVENT, "session_id": "s", "transcript_path": str(self.path),
                 "cwd": str(cwd or CLAUDE.parent), "tool_name": name, "tool_input": given,
                 "tool_use_id": call_id, "tool_response": {}}
        return run(json.dumps(event, ensure_ascii=False).encode("utf-8"))


def run(payload: bytes):
    """The hook as a process whose text stdin is what a Windows pipe gets."""
    return subprocess.run(
        [sys.executable, str(HINT)], input=payload, capture_output=True,
        timeout=30, check=False, env=dict(os.environ, PYTHONIOENCODING="cp1252:surrogateescape"))


def context(done) -> str:
    """The `additionalContext` a run returned, or '' when it said nothing."""
    assert done.returncode == 0, done.stderr
    if not done.stdout.strip():
        return ""
    answer = json.loads(done.stdout)
    output = answer["hookSpecificOutput"]
    assert output["hookEventName"] == EVENT, answer
    return output["additionalContext"]


def tools(text: str) -> list[str]:
    return re.findall(r"`mcp__codebase-index__(\w+)`", text)


class Scratch(unittest.TestCase):
    def setUp(self):
        self._scratch = tempfile.TemporaryDirectory()
        self.scratch = Path(self._scratch.name)

    def tearDown(self):
        self._scratch.cleanup()

    def hinted(self, name, given, earlier=(), **options) -> str:
        return context(Session(self.scratch, earlier).after(name, given, **options))


class TheWiring(unittest.TestCase):
    def test_it_runs_after_grep_and_bash_and_under_no_other_event(self):
        hooks = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"]
        running = [
            (event, entry.get("matcher")) for event, entries in hooks.items()
            for entry in entries
            for hook in entry.get("hooks", [])
            if hook.get("command") == COMMAND
        ]

        self.assertEqual([(EVENT, MATCHER)], running)

    def test_the_registered_command_survives_a_missing_file(self):
        """Python exits 2 on a file it cannot open, a worktree emptied under a
        running session; through `runpy` the same failure exits 1, and a
        project path holding a quote still runs."""
        argv = [sys.executable, *shlex.split(COMMAND)[2:]]
        with tempfile.TemporaryDirectory() as scratch:
            transcript = Path(scratch) / "t.jsonl"
            transcript.write_text("", encoding="utf-8")
            event = json.dumps({"hook_event_name": EVENT, "transcript_path": str(transcript),
                                "tool_name": "Grep", "tool_input": {"pattern": "Money"},
                                "tool_use_id": "x"}).encode()
            quoted = Path(scratch) / "O'Brien"
            (quoted / ".claude" / "hooks").mkdir(parents=True)
            shutil.copyfile(HINT, quoted / ".claude" / "hooks" / HINT.name)
            for root, hinted in ((CLAUDE.parent, True), (quoted, True), (Path(scratch), False)):
                done = subprocess.run(
                    argv, input=event, capture_output=True, timeout=30, check=False,
                    env=dict(os.environ, CLAUDE_PROJECT_DIR=str(root)))
                with self.subTest(root=str(root)):
                    self.assertNotEqual(2, done.returncode, done.stderr)
                    self.assertEqual(hinted, bool(done.stdout.strip()), done.stderr)


class WhatCountsAsASearch(Scratch):
    def test_each_tree_search_is_hinted_toward_its_tool(self):
        for command, expected in SEARCHES.items():
            with self.subTest(command=command):
                self.assertEqual(expected, tools(self.hinted(*bash_call(command)))[0])

    def test_a_search_of_one_file_a_pipe_or_prose_is_silent(self):
        for command in NOT_SEARCHES:
            with self.subTest(command=command):
                self.assertEqual("", self.hinted(*bash_call(command)))

    def test_the_grep_tool_is_a_search_unless_it_reads_prose(self):
        self.assertTrue(self.hinted(*grep_call()))
        self.assertTrue(self.hinted(*grep_call(path="src/Services")))
        for extra in ({"path": "docs/backend-architecture"}, {"glob": "*.md"},
                      {"type": "md"}, {"path": "C:\\repo\\docs"}):
            with self.subTest(extra=extra):
                self.assertEqual("", self.hinted(*grep_call(**extra)))

    def test_an_identifier_names_refs_and_symbol_and_anything_else_search(self):
        self.assertEqual(["find_refs", "find_symbol"], tools(self.hinted(*grep_call("\\bOrder.Place\\b"))))
        self.assertEqual(["search_code"], tools(self.hinted(*grep_call("retry.*budget"))))

    def test_the_hint_puts_the_subject_into_the_cli_form(self):
        self.assertIn('cbx refs "ClaimAsync" --json', self.hinted(*grep_call("ClaimAsync\\(")))
        self.assertIn('cbx search "retry budget" --limit 3', self.hinted(*grep_call("retry.*budget")))

    def test_other_tools_are_silent(self):
        for name, given in (("Read", {"file_path": "src/x.cs"}), ("Glob", {"pattern": "**/*.cs"}),
                            ("Edit", {"file_path": "x", "old_string": "a", "new_string": "b"})):
            with self.subTest(name=name):
                self.assertEqual("", self.hinted(name, given))


class WhenItSpeaks(Scratch):
    def test_a_session_that_made_a_lookup_hears_nothing(self):
        for earlier in ((("mcp__codebase-index__find_refs", {"symbol": "X"}),),
                        (("mcp__codebase-index__search_code", {"query": "x"}),),
                        (bash_call('bash .claude/skills/codebase-index/scripts/cbx refs "X" --json'),),
                        (bash_call("codebase-index search outbox --json"),),
                        (("mcp__codebase-index__verify_evidence", {}),),
                        (bash_call("bash .claude/skills/codebase-index/scripts/cbx verify --session t --json"),)):
            with self.subTest(earlier=earlier):
                self.assertEqual("", self.hinted(*grep_call(), earlier=earlier))

    def test_reading_the_index_without_asking_it_is_not_a_lookup(self):
        for earlier in ((("mcp__codebase-index__healthcheck", {}),),
                        (("mcp__codebase-index__index_stats", {}),),
                        (bash_call("bash .claude/skills/codebase-index/scripts/cbx stats --json"),)):
            with self.subTest(earlier=earlier):
                self.assertTrue(self.hinted(*grep_call(), earlier=earlier))

    def test_it_speaks_on_the_first_search_and_every_fifth_after(self):
        for count in range(0, 2 * EVERY + 2):
            earlier = [grep_call()] * count + [bash_call("git status")] * 3
            with self.subTest(earlier_searches=count):
                self.assertEqual(count % EVERY == 0, bool(self.hinted(*grep_call(), earlier=earlier)))

    def test_the_call_it_follows_counts_once_whether_or_not_it_is_recorded(self):
        for recorded in (False, True):
            with self.subTest(recorded=recorded):
                self.assertTrue(self.hinted(*grep_call(), recorded=recorded))

    def test_text_that_mentions_a_tool_call_is_not_one(self):
        mention = {"type": "user", "message": {"role": "user", "content": [
            {"type": "text", "text": '{"type": "tool_use", "name": "mcp__codebase-index__find_refs"}'}]}}
        session = Session(self.scratch, lines=[mention])

        self.assertTrue(context(session.after(*grep_call())))

    def test_a_transcript_line_it_cannot_parse_is_skipped(self):
        """A live transcript's last line can be half written; read as a
        failure, it would silence the hint for the rest of the session."""
        session = Session(self.scratch)
        with session.path.open("a", encoding="utf-8") as handle:
            handle.write('{"tool_use": broken\n')

        self.assertTrue(context(session.after(*grep_call())))


class TheWorktreeClause(Scratch):
    def test_a_linked_worktree_is_told_to_use_the_cli(self):
        main = self.scratch / "main"
        (main / ".git" / "worktrees" / "wt").mkdir(parents=True)
        (main / ".git" / "worktrees" / "wt" / "commondir").write_text("../..", encoding="utf-8")
        tree = main / ".claude" / "worktrees" / "wt"
        (tree / "src").mkdir(parents=True)
        (tree / ".git").write_text(f"gitdir: {main / '.git' / 'worktrees' / 'wt'}\n", encoding="utf-8")

        self.assertIn("linked worktree", self.hinted(*grep_call(), cwd=tree / "src"))
        self.assertNotIn("linked worktree", self.hinted(*grep_call(), cwd=main))

    def test_a_submodule_is_not_a_worktree(self):
        module = self.scratch / "module"
        (self.scratch / ".git" / "modules" / "module").mkdir(parents=True)
        module.mkdir()
        (module / ".git").write_text("gitdir: ../.git/modules/module\n", encoding="utf-8")

        self.assertNotIn("linked worktree", self.hinted(*grep_call(), cwd=module))


class WhatItCannotRead(Scratch):
    def test_input_it_cannot_read_is_silent_and_never_blocks(self):
        for payload in (b"", b"<html>", b"\xff\xfe\x00", b"[1, 2]",
                        b'{"tool_name": "Grep", "tool_input": {"pattern": "X"}}',
                        json.dumps({"tool_name": "Grep", "tool_input": {"pattern": "X"},
                                    "transcript_path": str(self.scratch / "absent.jsonl")}).encode(),
                        b'{"tool_name": "Grep", "tool_input": "X", "transcript_path": "t"}'):
            with self.subTest(payload=payload):
                done = run(payload)
                self.assertEqual(0, done.returncode, done.stderr)
                self.assertEqual(b"", done.stdout.strip())

    def test_a_pattern_outside_ascii_is_decoded_as_utf_8(self):
        text = self.hinted(*grep_call("\u0141\u00f3d\u017aRelay"))

        self.assertIn('cbx refs "\u0141\u00f3d\u017aRelay"', text)


class TheSkillAgrees(Scratch):
    """A hint naming a command the skill does not approve sends the session to
    a permission prompt instead of the index."""

    def test_every_cli_command_it_emits_is_one_the_skill_approves(self):
        front = SKILL.read_text(encoding="utf-8").split("---")[1]
        for pattern in ("ClaimAsync\\(", "retry.*budget"):
            command = re.search(r"`(bash [^`]+)`", self.hinted(*grep_call(pattern))).group(1)
            prefix = " ".join(command.split()[:3])
            with self.subTest(command=command):
                self.assertIn(f"Bash({prefix}:*)", front)

    def test_search_carries_the_limit_the_skill_sets(self):
        self.assertIn("**`search` takes `--limit 3`**", SKILL.read_text(encoding="utf-8"))
        self.assertIn("--limit 3", self.hinted(*grep_call("retry.*budget")))
        self.assertIn("`limit: 3`", self.hinted(*grep_call("retry.*budget")))


if __name__ == "__main__":
    unittest.main()
