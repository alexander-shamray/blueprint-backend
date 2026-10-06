"""What `.claude/hooks/index-mcp-root.py` refuses before a code-index MCP call.
It fails open and lets most calls through, so its exit status proves nothing;
the cases run it as `settings.json` does, a process over stdin, and read what
it printed."""

import importlib.util
import json
import os
import re
import shlex
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CLAUDE = SCRIPTS.parent
HOOK = CLAUDE / "hooks" / "index-mcp-root.py"
SETTINGS = CLAUDE / "settings.json"
CBX = CLAUDE / "skills" / "codebase-index" / "scripts" / "cbx"
EVENT = "PreToolUse"
MATCHER = "mcp__codebase-index__.*"
COMMAND = ("py -3.12 -P -c \"import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR']"
           " + '/.claude/hooks/index-mcp-root.py', run_name='__main__')\"")


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(event: dict, project: Path | str | None, where: Path | None = None):
    environment = dict(os.environ, PYTHONIOENCODING="cp1252:surrogateescape")
    environment.pop("CLAUDE_PROJECT_DIR", None)
    if project is not None:
        environment["CLAUDE_PROJECT_DIR"] = str(project)
    return subprocess.run(
        [sys.executable, str(HOOK)], input=json.dumps(event).encode(), capture_output=True,
        timeout=30, check=False, env=environment, cwd=where)


def reason(done) -> str:
    """Why a run refused the call, or '' when it let it through."""
    assert done.returncode == 0, done.stderr
    if not done.stdout.strip():
        return ""
    output = json.loads(done.stdout)["hookSpecificOutput"]
    assert output["hookEventName"] == EVENT, output
    assert output["permissionDecision"] == "deny", output
    return output["permissionDecisionReason"]


def commands(text: str) -> list[str]:
    return re.findall(r"`(bash \.claude/skills/codebase-index/scripts/cbx [^`]*)`", text)


class Checkouts(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.main = Path(scratch.name) / "main"
        (self.main / ".git").mkdir(parents=True)
        self.tree = self.main / ".claude" / "worktrees" / "wt"
        (self.tree / "src").mkdir(parents=True)
        (self.tree / ".git").write_text("gitdir: ../../../.git/worktrees/wt\n", encoding="utf-8")

    def ask(self, tool, given=None, cwd=None, project="main"):
        event = {"hook_event_name": EVENT, "tool_name": "mcp__codebase-index__" + tool,
                 "tool_input": {} if given is None else given, "cwd": str(cwd or self.tree / "src")}
        return reason(run(event, self.main if project == "main" else project))


class TheWiring(unittest.TestCase):
    def test_it_runs_before_the_index_tools_and_under_no_other_event(self):
        hooks = json.loads(SETTINGS.read_text(encoding="utf-8"))["hooks"]
        running = [
            (event, entry.get("matcher")) for event, entries in hooks.items()
            for entry in entries
            for hook in entry.get("hooks", [])
            if hook.get("command") == COMMAND
        ]

        self.assertEqual([(EVENT, MATCHER)], running)

    def test_the_registered_command_survives_a_missing_file(self):
        """Through `runpy` a file it cannot open exits 1, where a file argument
        exits 2, which under this event would refuse every index call."""
        argv = [sys.executable, *shlex.split(COMMAND)[2:]]
        with tempfile.TemporaryDirectory() as scratch:
            done = subprocess.run(argv, input=b"{}", capture_output=True, timeout=30, check=False,
                                  env=dict(os.environ, CLAUDE_PROJECT_DIR=scratch))
            self.assertNotEqual(2, done.returncode, done.stderr)

    def test_the_matcher_reaches_every_tool_it_knows(self):
        hook = load("index_mcp_root", HOOK)

        for tool in hook.COMMANDS:
            with self.subTest(tool=tool):
                self.assertTrue(re.fullmatch(MATCHER, hook.MCP + tool))


class WhenItRefuses(Checkouts):
    def test_a_session_moved_into_a_worktree_is_sent_to_the_cli(self):
        text = self.ask("find_refs", {"symbol": "RefusedAsync"})

        self.assertIn(f"reads {self.main}", text)
        self.assertIn(f"not {self.tree}", text)
        self.assertIn("codebase-index skill", text)
        self.assertEqual(["bash .claude/skills/codebase-index/scripts/cbx refs RefusedAsync --json"],
                         commands(text))

    def test_a_session_started_in_the_worktree_is_let_through(self):
        self.assertEqual("", self.ask("find_refs", {"symbol": "X"}, project=self.tree))

    def test_a_session_in_the_checkout_it_started_in_is_let_through(self):
        self.assertEqual("", self.ask("search_code", {"query": "x"}, cwd=self.main / ".claude"))

    def test_the_health_check_is_let_through_to_show_the_root(self):
        self.assertEqual("", self.ask("healthcheck"))

    def test_another_server_is_not_judged(self):
        event = {"tool_name": "mcp__other__find_refs", "tool_input": {}, "cwd": str(self.tree)}
        self.assertEqual("", reason(run(event, self.main)))

    def test_a_session_with_no_project_directory_is_let_through(self):
        self.assertEqual("", self.ask("find_refs", {"symbol": "X"}, project=None))

    def test_an_empty_project_directory_is_not_the_hooks_own(self):
        event = {"tool_name": "mcp__codebase-index__find_refs", "tool_input": {"symbol": "X"},
                 "cwd": str(self.tree)}
        self.assertEqual("", reason(run(event, "", where=self.main)))

    def test_a_directory_in_no_checkout_is_let_through(self):
        outside = self.main.parent / "loose"
        outside.mkdir()
        self.assertEqual("", self.ask("find_refs", {"symbol": "X"}, cwd=outside))


class WhatItNames(Checkouts):
    CASES = (
        ("search_code", {"query": "outbox relay", "session": "r1"},
         "cbx search 'outbox relay' --session r1 --json"),
        ("explain_code", {"query": "saga"}, "cbx explain saga --json"),
        ("find_symbol", {"name": "Order"}, "cbx symbol Order --json"),
        ("find_refs", {"symbol": "Order", "kind": "callers"}, "cbx refs Order --kind callers --json"),
        ("impact_of", {"target": "Order"}, "cbx impact Order --json"),
        ("impact_of_diff", {}, "cbx diff-impact --json"),
        ("path_between", {"source": "A", "target": "B"}, "cbx path A B --json"),
        ("describe_symbol", {"symbol": "Order"}, "cbx describe Order --json"),
        ("verify_evidence", {"session": "r1"}, "cbx verify --session r1 --json"),
        ("architecture_overview", {}, "cbx architecture --json"),
        ("index_stats", {}, "cbx stats --json"),
    )

    def test_each_tool_is_asked_again_as_its_command(self):
        for tool, given, expected in self.CASES:
            with self.subTest(tool=tool):
                named = commands(self.ask(tool, given))
                self.assertEqual(["bash .claude/skills/codebase-index/scripts/" + expected], named)

    def test_every_command_it_names_passes_the_index_guard(self):
        guard = load("guard_index_argv", CLAUDE / "hooks" / "guard-index-argv.py")
        hostile = ("$(rm -rf .)", "a; b", "`x`", "it's")
        for tool, given, _ in self.CASES:
            for value in hostile:
                carried = {key: value for key in given} or given
                for command in commands(self.ask(tool, carried)):
                    with self.subTest(tool=tool, value=value):
                        self.assertIsNone(guard.offence(command, guard._git_guard()))

    def test_an_argument_it_cannot_carry_is_left_to_the_agent(self):
        for value in ("A\nB", "A`B"):
            with self.subTest(value=value):
                text = self.ask("find_refs", {"symbol": value})

                self.assertEqual(["bash .claude/skills/codebase-index/scripts/cbx refs"], commands(text))
                self.assertIn("with the call's arguments", text)

    def test_a_call_holding_an_argument_the_line_drops_gets_the_bare_form(self):
        """A line without the option would ask another question and be named
        as the same one."""
        for tool, given, sub in (
                ("impact_of", {"target": "Order", "direction": "down", "depth": 3}, "impact"),
                ("search_code", {"query": "relay", "limit": 5}, "search"),
                ("impact_of_diff", {"base": "origin/main"}, "diff-impact"),
                ("verify_evidence", {"refs": ["src/a.cs:1-4"]}, "verify"),
                ("find_refs", {"symbol": "Order", "kind": "writers"}, "refs"),
                ("find_symbol", {"name": "Order", "kind": "class"}, "symbol"),
                ("explain_code", {"query": "saga", "session": "a\nb"}, "explain")):
            with self.subTest(tool=tool):
                text = self.ask(tool, given)

                self.assertEqual([f"bash .claude/skills/codebase-index/scripts/cbx {sub}"], commands(text))
                self.assertIn("with the call's arguments", text)

    def test_every_subcommand_is_one_the_wrapper_admits(self):
        admitted = re.search(r'^ALLOWED="([^"]*)"', CBX.read_text(encoding="utf-8"), re.M).group(1).split()
        for sub, _, _ in load("index_mcp_root", HOOK).COMMANDS.values():
            with self.subTest(sub=sub):
                self.assertIn(sub, admitted)

    def test_the_query_hint_maps_its_routes_the_same_way(self):
        hint = load("index_query_hint", CLAUDE / "hooks" / "index-query-hint.py")
        mine = load("index_mcp_root", HOOK).COMMANDS
        for sub, tool in hint.TOOLS.items():
            with self.subTest(tool=tool):
                self.assertEqual(sub, mine[tool][0])


class WhatItCannotRead(unittest.TestCase):
    def test_input_it_cannot_read_goes_through(self):
        with tempfile.TemporaryDirectory() as scratch:
            for payload in (b"", b"<html>", b"\xff\xfe\x00", b"[1, 2]",
                            b'{"tool_name": "mcp__codebase-index__find_refs", "tool_input": "X", "cwd": 3}'):
                with self.subTest(payload=payload):
                    done = subprocess.run(
                        [sys.executable, str(HOOK)], input=payload, capture_output=True, timeout=30,
                        check=False, env=dict(os.environ, CLAUDE_PROJECT_DIR=scratch))
                    self.assertEqual(0, done.returncode, done.stderr)
                    self.assertEqual(b"", done.stdout.strip())


if __name__ == "__main__":
    unittest.main()
