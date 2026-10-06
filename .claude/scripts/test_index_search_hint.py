"""What `.claude/hooks/index-search-hint.py` refuses before a tool call, and
when. It fails open and lets most calls through, so its exit status proves
nothing; the cases run it as `settings.json` does, a process over stdin,
beside a transcript and a temporary directory written for the case, and read
what it printed."""

import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
CLAUDE = SCRIPTS.parent
HINT = CLAUDE / "hooks" / "index-search-hint.py"
SETTINGS = CLAUDE / "settings.json"
SKILL = CLAUDE / "skills" / "codebase-index" / "SKILL.md"
EVENT = "PreToolUse"
MATCHER = "Grep|Bash|PowerShell"
COMMAND = ("py -3.12 -P -c \"import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR']"
           " + '/.claude/hooks/index-search-hint.py', run_name='__main__')\"")

# Each searches the tree for a named symbol, so a session's first is refused.
SEARCHES = (
    'grep -rn "ClaimAsync(" src',
    "grep -R HealthProbe .",
    "grep --recursive -n GiveUpAge src tests",
    "git grep -n 'Order.Place'",
    "git -C .claude grep OutboxRelay",
    "rg TimeSpan src",
    'grep -rE "Claim|Release" src',
    'rg "(ClaimAsync|ReleaseAsync)" src',
    'grep -rn "\\(ClaimAsync\\|ReleaseAsync\\)" src',
    'grep -rn "Undeclared\\|Reached" src',
    "LC_ALL=C grep -r Money .",
    "cd src && grep -rn PlaceOrderHandler .",
    "/usr/bin/grep -rn Saga src",
    "rg -g '!*.md' OutboxRelay src",
    "git grep -n OutboxRelay -- ':!*.md'",
    "git grep -n OutboxRelay -- ':^docs'",
    "git grep -n OutboxRelay -- ':(exclude)*.md'",
    "cd src && git grep -n OutboxRelay -- ':!*.md'",
    "git -C src grep -n OutboxRelay -- ':(exclude)*.md'",
    "grep -r -e 'Retry(' src",
    "(cd docs && grep -rn Saga .); grep -rn Retry src",
    "grep -rn Retry \\\n  src",
    "git status --short\ngrep -rn Retry src",
    "grep -rn MAX_RETRIES src",
)

# Each searches the tree for text, which grep lists whole and the index does not.
TEXT = (
    "rg 'class \\w+Options'",
    "grep -rn health/ready src 2>/dev/null | head -5",
    'grep -rn "connection refused" src',
    "grep -rn timeout src",
    "grep -rn TODO src",
    'grep -rn "appsettings.json" src',
    "rg 'Claim.*Async' src",
)

# Each reads one file, a pipe or prose, which the index does not replace.
NOT_SEARCHES = (
    "grep -n Money src/Money.cs",
    "git log --oneline | grep Fix",
    "cat build.log | rg Error",
    "grep -rn Saga docs/backend-architecture",
    "grep -rn Saga docs 2>/dev/null",
    "grep -rn Saga \\\n  docs",
    "rg -g '*.md' Outbox",
    "rg --type md Outbox",
    "rg --type=md Outbox",
    "rg -tmd Outbox",
    "rg -g*.md Outbox",
    "rg --iglob '*.MD' Outbox",
    "rg --iglob=*.md Outbox",
    "grep -r --include=*.md Outbox .",
    "grep -r --include '*.md' Outbox .",
    "rg --files src/Services",
    "rg ClaimAsync .claude/settings.json",
    "grep -rn ClaimAsync .claude/settings.json .claude/hooks/index-search-hint.py",
    "echo grep -r Foo .",
    "git commit -F - <<'EOF'\ngrep -rn Foo src\nEOF",
    "cat <<-EOF > notes.txt\n\tgrep -rn Foo src\n\tEOF",
    'gh pr create --body "Steps:\ngrep -rn Foo src\n"',
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
    """A transcript of earlier tool calls, a temporary directory, and the
    hook run before one more call."""

    def __init__(self, scratch: Path, earlier=(), lines=(), session=None):
        self.path = scratch / "transcript.jsonl"
        self.temp = scratch / "temp"
        self.temp.mkdir(exist_ok=True)
        self.id = session or uuid.uuid4().hex
        rows = [tool_use(f"toolu_{index}", name, given) for index, (name, given) in enumerate(earlier)]
        with self.path.open("w", encoding="utf-8") as handle:
            for row in [*lines, *rows]:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")

    def event(self, name, given, cwd=None, **extra):
        return json.dumps({"hook_event_name": EVENT, "session_id": self.id, "transcript_path": str(self.path),
                           "cwd": str(cwd or CLAUDE.parent), "tool_name": name, "tool_input": given,
                           "tool_use_id": "toolu_now", **extra}, ensure_ascii=False).encode("utf-8")

    def before(self, name, given, cwd=None, **extra):
        return run(self.event(name, given, cwd, **extra), self.temp)


def environment(temp: Path) -> dict:
    """What the hook sees: a text stdin a Windows pipe gets, and `temp` as
    the temporary directory, so each case keeps its own record of asks."""
    return dict(os.environ, PYTHONIOENCODING="cp1252:surrogateescape",
                TMP=str(temp), TEMP=str(temp), TMPDIR=str(temp))


def run(payload: bytes, temp: Path):
    return subprocess.run(
        [sys.executable, str(HINT)], input=payload, capture_output=True,
        timeout=30, check=False, env=environment(temp))


def reason(done) -> str:
    """Why a run refused the call, or '' when it let it through."""
    assert done.returncode == 0, done.stderr
    if not done.stdout.strip():
        return ""
    answer = json.loads(done.stdout)
    output = answer["hookSpecificOutput"]
    assert output["hookEventName"] == EVENT, answer
    assert output["permissionDecision"] == "deny", answer
    return output["permissionDecisionReason"]


def tools(text: str) -> list[str]:
    return re.findall(r"`mcp__codebase-index__(\w+)`", text)


class Scratch(unittest.TestCase):
    def setUp(self):
        self._scratch = tempfile.TemporaryDirectory()
        self.scratch = Path(self._scratch.name)

    def tearDown(self):
        self._scratch.cleanup()

    def refused(self, name, given, earlier=(), **options) -> str:
        return reason(Session(self.scratch, earlier).before(name, given, **options))

    def worktree(self) -> tuple[Path, Path]:
        main = self.scratch / "main"
        (main / ".git" / "worktrees" / "wt").mkdir(parents=True)
        (main / ".git" / "worktrees" / "wt" / "commondir").write_text("../..", encoding="utf-8")
        tree = main / ".claude" / "worktrees" / "wt"
        (tree / "src").mkdir(parents=True)
        (tree / ".git").write_text(f"gitdir: {main / '.git' / 'worktrees' / 'wt'}\n", encoding="utf-8")
        return main, tree


class TheWiring(unittest.TestCase):
    def test_it_runs_before_grep_and_both_shells_and_under_no_other_event(self):
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
            quoted = Path(scratch) / "O'Brien"
            (quoted / ".claude" / "hooks").mkdir(parents=True)
            shutil.copyfile(HINT, quoted / ".claude" / "hooks" / HINT.name)
            for root, refused in ((CLAUDE.parent, True), (quoted, True), (Path(scratch), False)):
                event = json.dumps({"hook_event_name": EVENT, "session_id": uuid.uuid4().hex,
                                    "transcript_path": str(transcript), "tool_name": "Grep",
                                    "tool_input": {"pattern": "Money"}, "tool_use_id": "x"}).encode()
                done = subprocess.run(
                    argv, input=event, capture_output=True, timeout=30, check=False,
                    env=dict(environment(Path(scratch)), CLAUDE_PROJECT_DIR=str(root)))
                with self.subTest(root=str(root)):
                    self.assertNotEqual(2, done.returncode, done.stderr)
                    self.assertEqual(refused, bool(done.stdout.strip()), done.stderr)


class WhatCountsAsASearch(Scratch):
    def test_each_tree_search_for_a_symbol_is_refused(self):
        """Which route the reason names depends on the checkout the suite
        runs in, which `OneRoute` fixes; here only the refusal is asked."""
        for command in SEARCHES:
            with self.subTest(command=command):
                self.assertIn("Before searching for `", self.refused(*bash_call(command)))

    def test_a_search_for_text_goes_through(self):
        for command in TEXT:
            with self.subTest(command=command):
                self.assertEqual("", self.refused(*bash_call(command)))

    def test_a_search_of_one_file_a_pipe_or_prose_goes_through(self):
        for command in NOT_SEARCHES:
            with self.subTest(command=command):
                self.assertEqual("", self.refused(*bash_call(command)))

    def test_the_grep_tool_is_a_search_unless_it_reads_prose_or_text(self):
        self.assertTrue(self.refused(*grep_call()))
        self.assertTrue(self.refused(*grep_call(path="src/Services")))
        self.assertTrue(self.refused(*grep_call(glob="!*.md")))
        for extra in ({"path": "docs/backend-architecture"}, {"glob": "*.md"},
                      {"type": "md"}, {"path": "C:\\repo\\docs"}, {"pattern": "retry.*budget"}):
            with self.subTest(extra=extra):
                self.assertEqual("", self.refused(*grep_call(**extra)))

    def test_the_grep_tool_reads_prose_where_its_path_lands_in_the_checkout(self):
        """A checkout cloned under a directory named `docs` is still code, and
        a Grep from inside `docs/` reads prose, as `grep -r` from there does."""
        repo = self.scratch / "Docs" / "repo"
        (repo / ".git").mkdir(parents=True)
        (repo / "src").mkdir()
        (repo / "docs").mkdir()
        for given, cwd, refused in ((grep_call(path=str(repo / "src")), repo, True),
                                    (grep_call(), repo, True),
                                    (grep_call(), repo / "docs", False),
                                    (bash_call("grep -rn ClaimAsync ."), repo / "docs", False)):
            with self.subTest(given=given, cwd=cwd):
                self.assertEqual(refused, bool(self.refused(*given, cwd=cwd)))

    def test_a_powershell_search_is_a_search(self):
        self.assertTrue(self.refused("PowerShell", {"command": "rg OutboxRelay src"}))
        self.assertEqual("", self.refused("PowerShell", {"command": "rg OutboxRelay docs"}))

    def test_the_grep_tool_aimed_at_one_file_is_not_a_search(self):
        self.assertEqual("", self.refused(*grep_call(path=".claude/settings.json")))
        self.assertEqual("", self.refused(*grep_call(path=str(SETTINGS))))
        self.assertTrue(self.refused(*grep_call(path=".claude")))

    def test_a_search_outside_the_checkout_is_not_a_search_of_its_index(self):
        repo, other = self.scratch / "repo", self.scratch / "other"
        (repo / ".git").mkdir(parents=True)
        (repo / "src").mkdir()
        other.mkdir()
        for name, given, refused in (
                (*grep_call(path=str(other)), False),
                (*grep_call(path="../other"), False),
                (*grep_call(path="src"), True),
                (*grep_call(), True),
                (*bash_call("git -C ../other grep OutboxRelay"), False),
                (*bash_call("grep -rn OutboxRelay ../other"), False),
                (*bash_call("git -C src grep OutboxRelay"), True),
                (*bash_call("grep -rn OutboxRelay src"), True)):
            with self.subTest(name=name, given=given):
                self.assertEqual(refused, bool(self.refused(name, given, cwd=repo)))

    def test_a_cd_before_the_search_moves_where_its_paths_are_read(self):
        repo = self.scratch / "repo"
        (repo / ".git").mkdir(parents=True)
        (repo / "src").mkdir()
        (repo / "src" / "Money.cs").write_text("class Money {}", encoding="utf-8")
        (repo / "docs").mkdir()
        (self.scratch / "other").mkdir()
        for command, refused in (("cd ../other && grep -rn OutboxRelay .", False),
                                 ("cd src && grep -rn Amount Money.cs", False),
                                 ("cd docs && grep -rn Saga .", False),
                                 ("(cd docs && grep -rn Saga .); grep -rn Saga src", True),
                                 ("cd src; rg Amount", True),
                                 ("cd src && git -C .. grep Amount", True),
                                 ("cd src && grep -rn Amount .", True)):
            with self.subTest(command=command):
                self.assertEqual(refused, bool(self.refused(*bash_call(command), cwd=repo)))

    def test_each_named_symbol_is_asked_about_up_to_three(self):
        text = self.refused(*bash_call('grep -rnE "MapPost|MapPut|MapDelete|MapGet" src'))
        for name in ("MapPost", "MapPut", "MapDelete"):
            self.assertIn(f"`{name}`", text)
        self.assertNotIn("MapGet", text)
        self.assertIn("`Order.Place`", self.refused(*grep_call("\\bOrder.Place\\b")))

    def test_other_tools_go_through(self):
        for name, given in (("Read", {"file_path": "src/x.cs"}), ("Glob", {"pattern": "**/*.cs"}),
                            ("Edit", {"file_path": "x", "old_string": "a", "new_string": "b"})):
            with self.subTest(name=name):
                self.assertEqual("", self.refused(name, given))


class WhenItAsks(Scratch):
    def test_a_session_that_made_a_lookup_is_not_refused(self):
        for earlier in ((("mcp__codebase-index__find_refs", {"symbol": "X"}),),
                        (("mcp__codebase-index__search_code", {"query": "x"}),),
                        (bash_call('bash .claude/skills/codebase-index/scripts/cbx refs "X" --json'),),
                        (bash_call("codebase-index search outbox --json"),),
                        (("mcp__codebase-index__verify_evidence", {}),),
                        (bash_call("bash .claude/skills/codebase-index/scripts/cbx verify --session t --json"),),
                        (bash_call("pwsh -NoProfile -ExecutionPolicy Bypass -File "
                                   ".claude/skills/codebase-index/scripts/cbx.ps1 search outbox --json"),),
                        (bash_call("py -3.12 -P -m codebase_index refs X --json"),),
                        (bash_call("git status\nbash .claude/skills/codebase-index/scripts/cbx refs X --json"),),
                        (bash_call("CBX_NO_SKILL_AUTO_UPDATE=1 bash "
                                   ".claude/skills/codebase-index/scripts/cbx refs X --json"),),
                        (("PowerShell", {"command": "& C:\\repo\\.claude\\skills\\codebase-index\\scripts\\cbx.ps1"
                                                    " refs X --json"}),)):
            with self.subTest(earlier=earlier):
                self.assertEqual("", self.refused(*grep_call(), earlier=earlier))

    def test_a_command_that_only_names_the_cli_is_not_a_lookup(self):
        for command in ('git log -p | grep "cbx refs"',
                        'git commit -m "say cbx search first"',
                        "echo codebase-index search",
                        'py -3.12 -c "import codebase_index" search',
                        "git commit -F - <<'EOF'\nbash .claude/skills/codebase-index/scripts/cbx refs X --json\nEOF",
                        'gh pr create --body "Run:\nbash .claude/skills/codebase-index/scripts/cbx refs X\n"'):
            with self.subTest(command=command):
                self.assertTrue(self.refused(*grep_call(), earlier=[bash_call(command)]))

    def test_reading_the_index_without_asking_it_is_not_a_lookup(self):
        for earlier in ((("mcp__codebase-index__healthcheck", {}),),
                        (("mcp__codebase-index__index_stats", {}),),
                        (bash_call("bash .claude/skills/codebase-index/scripts/cbx stats --json"),)):
            with self.subTest(earlier=earlier):
                self.assertTrue(self.refused(*grep_call(), earlier=earlier))

    def test_it_refuses_once_per_session_and_the_rerun_goes_through(self):
        session = Session(self.scratch)
        self.assertTrue(reason(session.before(*grep_call())))
        self.assertEqual("", reason(session.before(*grep_call())))
        self.assertEqual("", reason(session.before(*bash_call("grep -rn OutboxRelay src"))))
        self.assertTrue(reason(Session(self.scratch).before(*grep_call())))

    def test_a_parallel_batch_is_refused_once(self):
        session = Session(self.scratch)
        event = session.event(*grep_call())
        running = [subprocess.Popen([sys.executable, str(HINT)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                    stderr=subprocess.PIPE, env=environment(session.temp)) for _ in range(4)]
        outputs = [process.communicate(event, timeout=30)[0] for process in running]

        self.assertEqual(1, sum(bool(output.strip()) for output in outputs), outputs)

    def test_a_call_that_is_not_refused_does_not_spend_the_ask(self):
        session = Session(self.scratch)
        for given in (bash_call("grep -rn timeout src"), bash_call("grep -n Money src/Money.cs"),
                      grep_call(path="docs")):
            self.assertEqual("", reason(session.before(*given)))
        self.assertEqual("", reason(session.before(*grep_call(), agent_id="a1", agent_type="Explore")))
        self.assertTrue(reason(session.before(*grep_call())))

    def test_a_lookup_does_not_spend_the_ask_either(self):
        lookup = (("mcp__codebase-index__find_refs", {"symbol": "X"}),)
        self.assertEqual("", reason(Session(self.scratch, lookup, session="same").before(*grep_call())))
        self.assertTrue(reason(Session(self.scratch, session="same").before(*grep_call())))

    def test_a_session_whose_ask_cannot_be_recorded_goes_through(self):
        """Refusing without a record would refuse the rerun as well."""
        for session in ("../escape", "a b", ""):
            with self.subTest(session=session):
                done = Session(self.scratch).before(*grep_call(), session_id=session)
                self.assertEqual("", reason(done))
        session = Session(self.scratch)
        (session.temp / "claude-index-ask").write_text("", encoding="utf-8")
        self.assertEqual("", reason(session.before(*grep_call())))

    def test_text_that_mentions_a_tool_call_is_not_one(self):
        mention = {"type": "user", "message": {"role": "user", "content": [
            {"type": "text", "text": '{"type": "tool_use", "name": "mcp__codebase-index__find_refs"}'}]}}
        session = Session(self.scratch, lines=[mention])

        self.assertTrue(reason(session.before(*grep_call())))

    def test_a_transcript_line_it_cannot_parse_is_skipped(self):
        """A live transcript's last line can be half written; read as a
        failure, it would let every search through for the session."""
        session = Session(self.scratch)
        with session.path.open("a", encoding="utf-8") as handle:
            handle.write('{"tool_use": broken\n')

        self.assertTrue(reason(session.before(*grep_call())))


class OneRoute(Scratch):
    def test_the_main_checkout_is_sent_to_the_mcp_tools_alone(self):
        main, _tree = self.worktree()
        text = self.refused(*grep_call(), cwd=main)

        self.assertEqual(["find_refs", "find_symbol"], tools(text))
        self.assertIn("select:mcp__codebase-index__find_refs,mcp__codebase-index__find_symbol", text)
        self.assertNotIn("cbx", text)

    def test_a_linked_worktree_is_sent_to_the_cli_alone(self):
        _main, tree = self.worktree()
        text = self.refused(*bash_call('grep -rnE "MapPost|MapPut" .'), cwd=tree / "src")

        self.assertIn('cbx refs "MapPost" --json', text)
        self.assertIn('cbx refs "MapPut" --json', text)
        self.assertIn('cbx symbol "MapPost" --json', text)
        self.assertIn("codebase-index skill", text)
        self.assertNotIn("mcp__", text)

    def test_a_submodule_is_not_a_worktree(self):
        module = self.scratch / "module"
        (self.scratch / ".git" / "modules" / "module").mkdir(parents=True)
        module.mkdir()
        (module / ".git").write_text("gitdir: ../.git/modules/module\n", encoding="utf-8")

        self.assertNotIn("cbx", self.refused(*grep_call(), cwd=module))


class WhatItCannotRead(Scratch):
    def test_input_it_cannot_read_goes_through(self):
        for payload in (b"", b"<html>", b"\xff\xfe\x00", b"[1, 2]",
                        b'{"tool_name": "Grep", "tool_input": {"pattern": "X"}, "session_id": "s"}',
                        json.dumps({"tool_name": "Grep", "tool_input": {"pattern": "X"}, "session_id": "s",
                                    "transcript_path": str(self.scratch / "absent.jsonl")}).encode(),
                        b'{"tool_name": "Grep", "tool_input": "X", "transcript_path": "t", "session_id": "s"}'):
            with self.subTest(payload=payload):
                done = run(payload, self.scratch)
                self.assertEqual(0, done.returncode, done.stderr)
                self.assertEqual(b"", done.stdout.strip())

    def test_a_pattern_outside_ascii_is_decoded_as_utf_8(self):
        _main, tree = self.worktree()
        text = self.refused(*grep_call("\u0141\u00f3d\u017aRelay"), cwd=tree)

        self.assertIn('cbx refs "\u0141\u00f3d\u017aRelay"', text)


class TheSkillAgrees(Scratch):
    """A refusal naming a command the skill does not approve sends the session
    to a permission prompt instead of the index."""

    def test_every_cli_command_it_emits_is_one_the_skill_approves(self):
        front = SKILL.read_text(encoding="utf-8").split("---")[1]
        _main, tree = self.worktree()
        commands = re.findall(r"`(bash [^`]+)`", self.refused(*grep_call(), cwd=tree))
        self.assertTrue(commands)
        for command in commands:
            prefix = " ".join(command.split()[:3])
            with self.subTest(command=command):
                self.assertIn(f"Bash({prefix}:*)", front)


if __name__ == "__main__":
    unittest.main()
