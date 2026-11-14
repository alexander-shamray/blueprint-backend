"""The run half of /dispatch's helpers: dispatch_run.py, reached through dispatch_board.py's command line."""

import contextlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

import dispatch_board as db
import dispatch_run as dr
from test_dispatch_board import FIXTURE, OWNED, NO_BRANCH, REMAINING_ROWS, line, stub, tasks_of

# The run half: a run directory under a scratch root the test changes into, as the dispatcher runs from the
# main checkout.

RUN = "20261101-220000"
NOW = datetime(2026, 11, 1, 22, 0, tzinfo=timezone.utc)
OID = "89994113e5f0c1d2a3b4c5d6e7f8091a2b3c4d5e"
OTHER_OID = "d84f1368f0000000000000000000000000000000"
HEAD = "5ffc18315aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
BRANCH = "chore/dispatch-s1"
WORKTREE = ".claude/worktrees/dispatch-s1"
S1_ROWS = [row for row in REMAINING_ROWS.splitlines() if "| S1 |" in row]
MERGED_S1 = {"group": "S1", "issues": [1682, 1683], "outcome": "merged", "pr": 1690,
             "title": "fix(harness): narrow the RabbitMQ read grants", "merge_oid": OID,
             "rounds": [{"kind": "full", "raised": 5, "fixed": 5, "refused": 0},
                        {"kind": "recheck", "raised": 2, "fixed": 1, "refused": 1}],
             "review_end": "clean", "filed": [1701], "reason": None,
             "workspace": {"worktree": WORKTREE, "removed": False},
             "decisions": ["kept the narrower grant, the issue's recommended option"]}
HANDED_S1 = {**MERGED_S1, "outcome": "handed-back", "pr": None, "title": None, "merge_oid": None,
             "review_end": None,
             "reason": "needs a provider key only the owner has"}
USAGE = {"input_tokens": 20, "cache_creation_input_tokens": 5265, "cache_read_input_tokens": 754597,
         "output_tokens": 2814, "output_tokens_details": {"thinking_tokens": 199}}
# Counts of run 20261104-230438's sync-1781, whose `usage` held its last turn alone: 71399 of these 1662280 tokens.
SONNET = {"inputTokens": 44, "outputTokens": 4906, "cacheReadInputTokens": 1332430, "cacheCreationInputTokens": 58215,
          "webSearchRequests": 0, "costUSD": 0.415, "contextWindow": 1000000}
OPUS = {"inputTokens": 14, "outputTokens": 3868, "cacheReadInputTokens": 212506, "cacheCreationInputTokens": 50297,
        "webSearchRequests": 0, "costUSD": 0.371, "contextWindow": 1000000}
MODEL_USAGE = {"claude-sonnet-5-5": SONNET, "claude-opus-5-5": OPUS}
SESSION_USAGE = {"input_tokens": 58, "cache_creation_input_tokens": 108512, "cache_read_input_tokens": 1544936,
                 "output_tokens": 8774}
# The spike's envelope keys (docs/superpowers/plans/2026-11-01-dispatch-spike.md); its values trimmed.
SUCCESS = {"type": "result", "subtype": "success", "is_error": False, "api_error_status": None,
           "stop_reason": "end_turn", "terminal_reason": "completed", "num_turns": 9,
           "session_id": "43b7cd26-0682-471a-a3e5-98c841a5c448", "total_cost_usd": 1.886, "usage": USAGE,
           "modelUsage": MODEL_USAGE, "permission_denials": [],
           "result": "PR #1690 is merged; the rate limit docs were not touched."}
# No limit was observed in the spike, so these are built from the envelope's structured fields and the SDK's
# rate_limit error kind (ruling R9); they await the first real limit. The result names a later reset, read only
# where no field names one.
LIMIT_429 = {**SUCCESS, "subtype": "error_during_execution", "is_error": True, "api_error_status": 429,
             "terminal_reason": "error", "resetsAt": "2026-11-02T01:30:00Z",
             "result": "Claude usage limit reached. Your limit resets at 2026-11-02T03:00:00Z."}
LIMIT_KIND = {**SUCCESS, "subtype": "error_during_execution", "is_error": True, "api_error_status": None,
              "error": {"type": "rate_limit"}, "result": "API Error"}
# sync-497's envelope from run 20261114-081919, trimmed of fields nothing reads: the API refused the account
# before any turn ran, so every group launched that night would meet the same refusal.
NO_CREDIT = {"type": "result", "subtype": "success", "is_error": True, "api_error_status": 400,
             "api_error_type": "invalid_request_error", "api_error_origin": "first_party", "stop_reason": "stop_sequence",
             "terminal_reason": "api_error", "num_turns": 1, "duration_api_ms": 0, "total_cost_usd": 0,
             "session_id": "f57e1234-5bc1-4c50-bfac-e5c527967b57", "modelUsage": {}, "permission_denials": [],
             "usage": {"input_tokens": 0, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0,
                       "output_tokens": 0},
             "result": "Credit balance is too low"}
FAKE_CLAUDE = """import json, os, sys
here = os.path.dirname(os.path.abspath(__file__))
seen = {"argv": sys.argv[1:], "cwd": os.getcwd(), "stdin": sys.stdin.buffer.read().decode("utf-8"),
        "temp": {name: os.environ.get(name) for name in ("TMP", "TEMP", "TMPDIR")},
        "dispatched": os.environ.get("HARNESS_DISPATCHED"), "api_key": os.environ.get("ANTHROPIC_API_KEY")}
with open(os.path.join(here, "seen.json"), "w", encoding="utf-8") as handle:
    json.dump(seen, handle)
with open(os.path.join(here, "reply.bin"), "rb") as handle:
    sys.stdout.buffer.write(handle.read())
with open(os.path.join(here, "code.txt"), encoding="utf-8") as handle:
    sys.exit(int(handle.read()))
"""


def state_dir(run=RUN):
    return dr.run_dir(".", run)


def record(run, gid, pid=None, **fields):
    """A group as `tasks move` and `launch` leave it, with `fields` over it; a pid goes where `run` puts it."""
    directory = state_dir(run)
    if not (directory / "state.json").is_file():
        dr.save_state(directory, {"run": run, "pairs": 2, "lang": "en", "groups": {}})
    rows = S1_ROWS if gid == "S1" else [line(gid=gid, number=1700 + int(gid[1:]))]
    parsed = [db._row("Remaining", row) for row in rows]
    slug = gid.lower()
    entry = {"keys": [r.key for r in parsed], "rows": rows, "issues": [r.number for r in parsed], "model": "Opus",
             "branch": f"chore/dispatch-{slug}", "worktree": f".claude/worktrees/dispatch-{slug}", "crashes": 0,
             "launched": NOW.isoformat()}
    entry.update(fields)
    os.makedirs(entry["worktree"], exist_ok=True)
    state = dr.load_state(directory)
    state["groups"][gid] = entry
    dr.save_state(directory, state)
    if pid is not None:
        (directory / f"{gid}.pid").write_text(f"{pid}\n", encoding="utf-8")


def group_state(run, gid):
    return dr.load_state(state_dir(run))["groups"][gid]


def make_worktree(path):
    os.makedirs(path, exist_ok=True)


def run_cli(argv, **sources):
    out = io.StringIO()
    sources.setdefault("owned_source", OWNED)
    sources.setdefault("red_source", list)
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
        db.main(argv, **sources)
    return out.getvalue()


def task_text(gid="S1", text="Narrow the grants"):
    (state_dir() / f"{gid}.task.txt").write_text(f"{text}\n", encoding="utf-8")


def run_fake_claude(stdout=b"{}", code=0, description="Narrow the RabbitMQ read grants"):
    """The session record `run` leaves after a fake `claude` that replies `stdout` and exits `code`."""
    fake = Path("fake")
    fake.mkdir(exist_ok=True)
    (fake / "claude.py").write_text(FAKE_CLAUDE, encoding="utf-8")
    (fake / "reply.bin").write_bytes(stdout)
    (fake / "code.txt").write_text(str(code), encoding="utf-8")
    if not (state_dir() / "state.json").is_file() or "S1" not in dr.load_state(state_dir())["groups"]:
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
    with mock.patch.object(dr, "CLAUDE_COMMAND", [sys.executable, str((fake / "claude.py").resolve())]), \
            contextlib.redirect_stdout(io.StringIO()):
        task_text("S1", description)
        assert db.main(["run", RUN, "S1"]) == 0
    return json.loads((state_dir() / "S1.session.json").read_text(encoding="utf-8"))


def finished_pid():
    child = subprocess.Popen([sys.executable, "-c", "pass"])
    child.wait()
    return child.pid


ABSENT = object()


def end(**fields):
    base = {"exit_code": 0, "is_error": False, "rate_limited": False, "resets_at": None, "usage": {}, "denials": []}
    return dr.SessionEnd(**{**base, **fields})


class InRoot(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        here = os.getcwd()
        os.chdir(self.tmp.name)
        self.addCleanup(os.chdir, here)
        self.root = Path(self.tmp.name)
        self.clock = NOW
        patcher = mock.patch.object(dr, "now", lambda: self.clock)
        patcher.start()
        self.addCleanup(patcher.stop)
        # Every launch makes an executor temp directory; a suite's goes in its own tree, never the system's.
        (self.root / "system-temp").mkdir()
        temp = mock.patch.object(dr, "TEMP_ROOT", str(self.root / "system-temp"))
        temp.start()
        self.addCleanup(temp.stop)


class PathWhichTests(InRoot):
    def test_a_claude_planted_in_the_cwd_is_not_found_and_one_on_path_is(self):
        (self.root / "claude.cmd").write_text("@echo planted\r\n", encoding="ascii")
        (self.root / "claude").write_text("#!/bin/sh\n", encoding="ascii")
        with tempfile.TemporaryDirectory() as other:
            env = {"PATH": os.pathsep.join(["", ".", other])}
            with mock.patch.dict(os.environ, env):
                self.assertIsNone(db.path_which("claude"))
                real = Path(other) / ("claude.cmd" if os.name == "nt" else "claude")
                real.write_text("@echo real\r\n" if os.name == "nt" else "#!/bin/sh\n", encoding="ascii")
                real.chmod(0o755)
                self.assertEqual(os.path.normcase(real), os.path.normcase(db.path_which("claude")))

    def test_a_git_planted_in_the_cwd_is_not_used_by_git_or_git_bash(self):
        for name in ("git.cmd", "git.exe", "git", "bash.exe", "bash"):
            (self.root / name).write_text("@echo planted\r\n", encoding="ascii")
            (self.root / name).chmod(0o755)
        with mock.patch.dict(os.environ, {"PATH": os.pathsep.join(["", "."])}):
            with self.assertRaises(db.Refusal) as caught:
                db.git(str(self.root), "status")
            self.assertEqual("no git on PATH", str(caught.exception))
            with self.assertRaisesRegex(db.Refusal, "^no Git bash found beside the git on PATH$"):
                db.git_bash()

    def test_a_path_entry_that_is_the_cwd_is_skipped(self):
        name = "claude.cmd" if os.name == "nt" else "claude"
        (self.root / name).write_text("x", encoding="ascii")
        (self.root / name).chmod(0o755)
        with mock.patch.dict(os.environ, {"PATH": str(self.root)}):
            self.assertIsNone(db.path_which("claude"))


class RunDirectoryTests(InRoot):
    def test_pairs_outside_one_to_four_is_refused(self):
        for pairs in (0, 5, -1):
            with self.assertRaisesRegex(db.Refusal, f"--pairs {pairs} is outside 1 to 4"):
                dr.new_run(".", pairs, "en", NOW)
        self.assertIn("refused: --pairs 5", run_cli(["start", "--pairs", "5", "--tasks", "todo.md"]))
        self.assertFalse((self.root / "artifacts").exists())
        self.assertEqual(RUN, dr.new_run(".", 4, "en", NOW))

    def test_pairs_that_is_a_bool_is_refused(self):
        for pairs in (True, False):
            with self.assertRaisesRegex(db.Refusal, f"--pairs {pairs} is not a whole number"):
                dr.new_run(".", pairs, "en", NOW)
        self.assertFalse((self.root / "artifacts").exists())

    def test_lang_defaults_to_en(self):
        self.assertEqual(f"{RUN}\n", run_cli(["start", "--pairs", "2", "--tasks", "todo.md"]))
        state = dr.load_state(state_dir())
        self.assertEqual(("en", 2, {}), (state["lang"], state["pairs"], state["groups"]))
        self.clock = NOW + timedelta(hours=1)
        (state_dir() / "report.md").write_text("done", encoding="utf-8")
        run_cli(["start", "--pairs", "1", "--lang", "ru", "--tasks", "todo.md"])
        self.assertEqual("ru", dr.load_state(state_dir("20261101-230000"))["lang"])
        self.assertIn("refused: --lang", run_cli(["start", "--pairs", "1", "--lang", "../x", "--tasks", "t"]))

    def test_a_second_start_with_a_run_open_names_it_and_exits_3(self):
        run_cli(["start", "--pairs", "2", "--tasks", "todo.md"])
        self.clock = NOW + timedelta(minutes=5)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(3, db.main(["start", "--pairs", "3", "--tasks", "todo.md"]))
        self.assertEqual(f"{RUN}\n", out.getvalue())
        self.assertEqual(RUN, dr.open_run(".", "todo.md")[0])
        (state_dir() / "report.md").write_text("done", encoding="utf-8")
        self.assertIsNone(dr.open_run(".", "todo.md")[0])
        os.makedirs("artifacts/dispatch/spike-notes")
        self.assertIsNone(dr.open_run(".", "todo.md")[0])

    def start(self, tasks="todo.md"):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = db.main(["start", "--pairs", "2", "--tasks", tasks])
        return code, out.getvalue(), err.getvalue()

    def test_a_reported_run_stays_open_while_in_progress_holds_its_rows(self):
        # Only this run's `done` and `back` touch its rows, so a run its report left holding some is resumed.
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        self.assertEqual(0, self.start()[0])
        self.assertEqual("", run_cli(["tasks", "move", "--tasks", "todo.md", "--run", RUN, "S1",
                                      "--branch", BRANCH, "--worktree", WORKTREE]))
        Path("moved.md").write_bytes(Path("todo.md").read_bytes())
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        (state_dir() / "report.md").write_text("the first night", encoding="utf-8")
        make_worktree(WORKTREE)
        self.clock = NOW + timedelta(hours=10)
        fresh = "20261102-080000"
        cases = {"interrupted in its window": ("window", "moved.md", RUN),
                 "moved, never launched": (None, "moved.md", RUN),
                 "a merge not confirmed in main": ("verified", "moved.md", RUN),
                 "a hand-back not sent back": ("handed-back", "moved.md", RUN),
                 "done": ("done", "moved.md", None), "sent back": ("returned", "moved.md", None),
                 "a window group whose rows the owner removed": ("window", "todo.md", None),
                 "a verified group whose rows the owner removed": ("verified", "todo.md", None)}
        for name, (status, listed, reopened) in cases.items():
            with self.subTest(group=name):
                state = dr.load_state(state_dir())
                state["groups"]["S1"].pop("status", None)
                state["groups"]["S1"].update({"status": status, "worktree": WORKTREE} if status else {})
                dr.save_state(state_dir(), state)
                self.assertEqual((reopened, None), dr.open_run(".", listed))
                code, out, _ = self.start(listed)
                self.assertEqual((3, f"{RUN}\n") if reopened else (0, f"{fresh}\n"), (code, out))
                shutil.rmtree(state_dir(fresh), ignore_errors=True)
                report = run_cli(["report", RUN, "--tasks", listed, "--today", "2026-11-02"],
                                 body_source=lambda n: "", open_source=lambda: {1440}, branch_source=NO_BRANCH)
                self.assertEqual(bool(reopened), f"it resumes run {RUN}" in report)
        # A list that cannot be read keeps the run open, and says why, rather than leave its rows to no run.
        code, out, err = self.start("missing.md")
        self.assertEqual((3, f"{RUN}\n"), (code, out))
        self.assertIn("kept open: the task list missing.md could not be read", err)

    def test_the_resume_line_follows_the_listed_rows_not_the_statuses(self):
        # A verified or window group whose rows the owner removed by hand holds nothing, whatever its status.
        dr.new_run(".", 2, "en", NOW)
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        self.assertEqual("", run_cli(["tasks", "move", "--tasks", "todo.md", "--run", RUN, "S1",
                                      "--branch", BRANCH, "--worktree", WORKTREE]))
        make_worktree(WORKTREE)
        for status in ("window", "verified", "handed-back"):
            for listed, resumed in (("todo.md", True), ("cleared.md", False)):
                with self.subTest(status=status, rows="listed" if resumed else "removed"):
                    state = dr.load_state(state_dir())
                    state["groups"]["S1"].update(status=status, worktree=WORKTREE)
                    dr.save_state(state_dir(), state)
                    with open("cleared.md", "w", encoding="utf-8", newline="") as handle:
                        handle.write(FIXTURE)
                    report = run_cli(["report", RUN, "--tasks", listed, "--today", "2026-11-02"],
                                     body_source=lambda n: "", open_source=lambda: {1440}, branch_source=NO_BRANCH)
                    self.assertEqual(resumed, f"it resumes run {RUN}" in report, report)

    def test_a_group_whose_worktree_is_gone_holds_the_run_while_its_rows_are_listed(self):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        self.start()
        self.assertEqual("", run_cli(["tasks", "move", "--tasks", "todo.md", "--run", RUN, "S1",
                                      "--branch", BRANCH, "--worktree", WORKTREE]))
        state = dr.load_state(state_dir())
        state["groups"]["S1"].update(status="running", worktree=WORKTREE, launched=NOW.isoformat())
        dr.save_state(state_dir(), state)
        (state_dir() / "report.md").write_text("the first night", encoding="utf-8")
        self.assertFalse(Path(WORKTREE).exists())
        self.assertIn(f"refused S1: its worktree {WORKTREE} is gone", run_cli(["resume", RUN]))
        self.assertEqual((RUN, None), dr.open_run(".", "todo.md"))
        lines = run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-02"], body_source=lambda n: "",
                        open_source=lambda: {1440}, branch_source=NO_BRANCH).splitlines()
        needs = lines[lines.index("## Needs a human") + 1:lines.index("## Interrupted")]
        self.assertIn(f"S1 running: its worktree {WORKTREE} is gone; its In progress rows keep run {RUN} open until "
                      "they are sent back or removed", needs)
        interrupted = lines[lines.index("## Interrupted") + 1:lines.index("## Filed overnight")]
        self.assertEqual([f"resume: run /dispatch again in the main checkout; it resumes run {RUN}"], interrupted)
        # Once the owner clears the rows the ghost holds nothing, and the run closes.
        with open("cleared.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        self.assertEqual((None, None), dr.open_run(".", "cleared.md"))
        lines = run_cli(["report", RUN, "--tasks", "cleared.md", "--today", "2026-11-02"], body_source=lambda n: "",
                        open_source=lambda: {1440}, branch_source=NO_BRANCH).splitlines()
        self.assertIn(f"S1 running: its worktree {WORKTREE} is gone; its rows are no longer In progress", lines)
        self.assertNotIn(f"it resumes run {RUN}", "\n".join(lines))

    def test_a_run_name_that_is_not_a_timestamp_is_refused(self):
        for bad in ("../x", "20261101", "20261101-22000", "20261101-220000/..", ""):
            with self.subTest(bad=bad), self.assertRaisesRegex(db.Refusal, "is not a yyyymmdd-hhmmss timestamp"):
                dr.run_dir(".", bad)
        self.assertEqual(Path("artifacts/dispatch") / RUN, dr.run_dir("", RUN))
        self.assertIn("refused:", run_cli(["resume", "../x"]))

    def test_git_bash_is_never_system32(self):
        found = db.git_bash()
        self.assertNotIn("system32", found.casefold())
        if os.name == "nt":
            self.assertTrue(found.casefold().endswith("bin\\bash.exe"), found)
        install = self.root / "Git"
        for part in ("cmd/git.exe", "bin/bash.exe"):
            (install / part).parent.mkdir(parents=True, exist_ok=True)
            (install / part).write_text("", encoding="utf-8")
        self.assertEqual(str((install / "bin" / "bash.exe").resolve()),
                         db._bash_beside(str(install / "cmd" / "git.exe")))
        self.assertIsNone(db._bash_beside(str(self.root / "elsewhere" / "git.exe")))
        for relay in (r"C:\Windows\System32\bash.exe", "c:/windows/SYSTEM32/bin/bash.exe", None, ""):
            with self.subTest(relay=relay), self.assertRaises(db.Refusal):
                db._no_relay(relay)


class LaunchTests(InRoot):
    def s1(self):
        return next(g for g in db.groups(db.parse_tasks(FIXTURE)) if g.id == "S1")

    def test_the_prompt_is_ship_with_the_tag_and_the_token(self):
        self.assertEqual("/ship #S1-1682+1683 Narrow the RabbitMQ read grants --dispatched artifacts/dispatch/"
                         "20261101-220000", dr.prompt(self.s1(), "Narrow the RabbitMQ read grants", RUN))
        s3 = next(g for g in db.groups(db.parse_tasks(FIXTURE)) if g.id == "S3")
        self.assertTrue(dr.prompt(s3, "Bind the switch keys", RUN).startswith("/ship #S3-1681 Bind"))
        with self.assertRaises(db.Refusal):
            dr.prompt(self.s1(), "fine", "../x")

    def test_a_description_carrying_a_token_is_refused(self):
        for bad in ("Narrow --dispatched artifacts/x", "Narrow --SLEEP", "two\nlines", "two\rlines",
                    "two\u2028lines", "   "):
            with self.subTest(bad=bad), self.assertRaises(db.Refusal):
                dr.prompt(self.s1(), bad, RUN)

    def test_a_model_outside_opus_and_sonnet_is_refused(self):
        self.assertEqual(("opus", "sonnet"), (dr.model_flag("Opus"), dr.model_flag("Sonnet")))
        for bad in ("—", "Haiku", "opus", ""):
            with self.subTest(bad=bad), self.assertRaisesRegex(db.Refusal, "is not one of Opus, Sonnet"):
                dr.model_flag(bad)
        self.write_tasks(FIXTURE.replace("| security, code | none | Opus |", "| security, code | none | — |"))
        self.move("S1")
        porcelain = f"worktree {self.root / WORKTREE}\nHEAD {HEAD}\nbranch refs/heads/{BRANCH}\n\n"
        with mock.patch.object(dr, "git", lambda root, *args: porcelain):
            out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE])
        self.assertIn("refused: model '—' is not one of Opus, Sonnet", out)

    def write_tasks(self, text):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(text)

    def move(self, gid, branch=BRANCH, worktree=WORKTREE):
        dr.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        self.assertEqual("", run_cli(["tasks", "move", "--tasks", "todo.md", "--run", RUN, gid,
                                      "--branch", branch, "--worktree", worktree]))
        make_worktree(worktree)

    def test_launch_records_the_group_after_its_move_on_a_worktree_git_shows_on_its_branch(self):
        self.write_tasks(FIXTURE)
        self.move("S1")
        listed = (f"worktree {self.root.as_posix()}\nHEAD {HEAD}\nbranch refs/heads/main\n\n"
                  f"worktree {(self.root / WORKTREE).as_posix()}\nHEAD {HEAD}\nbranch refs/heads/{BRANCH}\n\n")
        calls = []
        earlier = state_dir() / "S1.refused.json"
        earlier.write_text('{"refused": "stop file"}\n', encoding="utf-8")
        with mock.patch.object(dr, "git", lambda root, *args: calls.append(args) or listed):
            out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE])
        self.assertEqual("", out)
        self.assertEqual([("worktree", "list", "--porcelain")], calls)
        self.assertFalse(earlier.exists(), "a relaunch kept the refusal of the run before it")
        entry = group_state(RUN, "S1")
        self.assertEqual(("running", "Opus", [1682, 1683], BRANCH, WORKTREE, 0),
                         (entry["status"], entry["model"], entry["issues"], entry["branch"], entry["worktree"],
                          entry["crashes"]))
        self.assertEqual(S1_ROWS, entry["rows"])
        for wrong in (listed.replace(f"refs/heads/{BRANCH}", "refs/heads/other"), listed.split("\n\n")[0] + "\n\n"):
            with self.subTest(wrong=wrong), mock.patch.object(dr, "git", lambda root, *args: wrong):
                out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH,
                               "--worktree", WORKTREE])
                self.assertIn(f"refused: {WORKTREE} is not a worktree on {BRANCH}", out)
        with mock.patch.object(dr, "git", lambda root, *args: listed):
            out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", "fix/other",
                           "--worktree", WORKTREE])
        self.assertIn("refused: S1's In progress rows name `chore/dispatch-s1`, not fix/other", out)
        with mock.patch.object(dr, "git", lambda root, *args: listed):
            out = run_cli(["launch", RUN, "S3", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE])
        self.assertIn("refused: S3 is not in this run", out)

    def test_a_launch_on_a_red_main_is_refused_and_changes_nothing(self):
        # A resumed night's `moved, never launched` group was named by a `wake` that read a green main.
        self.write_tasks(FIXTURE)
        self.move("S1")
        listed = f"worktree {(self.root / WORKTREE).as_posix()}\nHEAD {HEAD}\nbranch refs/heads/{BRANCH}\n\n"
        earlier = state_dir() / "S1.refused.json"
        earlier.write_text('{"refused": "stop file"}\n', encoding="utf-8")
        argv = ["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE]
        with mock.patch.object(dr, "git", lambda root, *args: listed):
            out = run_cli(argv, red_source=lambda: ["main is red: #1895 u1", "main is red: #1896 u2"])
        self.assertIn("refused: S1 is not launched: main is red: #1895 u1; main is red: #1896 u2", out)
        self.assertIsNone(group_state(RUN, "S1").get("status"), "a refused launch recorded the group as running")
        self.assertTrue(earlier.exists(), "a refused launch removed the earlier launch's files")
        with mock.patch.object(dr, "git", lambda root, *args: listed):
            self.assertEqual("", run_cli(argv))
        self.assertEqual("running", group_state(RUN, "S1")["status"], "a green main launches the same group")

    def test_a_launch_after_a_hand_back_removes_the_earlier_launchs_files_or_is_refused(self):
        # tasks move reset the group, so nothing names the earlier launch's record as read: one a later run met beside
        # its own marker would be refused as unread, and the group would hold its slot all night.
        self.write_tasks(FIXTURE)
        self.move("S1")
        listed = f"worktree {(self.root / WORKTREE).as_posix()}\nHEAD {HEAD}\nbranch refs/heads/{BRANCH}\n\n"
        argv = ["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE]
        earlier = (NOW - timedelta(hours=1)).isoformat()
        names = ("S1.json", "S1.session.json", "S1.pid")
        (state_dir() / "S1.json").write_text(json.dumps(HANDED_S1), encoding="utf-8")
        (state_dir() / "S1.session.json").write_text(json.dumps(
            {"exit": 0, "started": earlier, "ended": earlier, "stdout": "{}", "stderr": ""}), encoding="utf-8")
        (state_dir() / "S1.pid").write_text(f"{finished_pid()}\n", encoding="utf-8")
        unlink = Path.unlink

        def refuse_record(path, missing_ok=False):
            if path.name == "S1.session.json":
                raise PermissionError(13, "Permission denied")
            unlink(path, missing_ok=missing_ok)
        with mock.patch.object(dr, "git", lambda root, *args: listed), mock.patch.object(Path, "unlink", refuse_record):
            out = run_cli(argv)
        self.assertIn("refused: could not remove the stale", out)
        self.assertIn("S1.session.json: [Errno 13] Permission denied", out)
        self.assertNotIn("status", group_state(RUN, "S1"))
        with mock.patch.object(dr, "git", lambda root, *args: listed):
            self.assertEqual("", run_cli(argv))
        for name in names:
            self.assertFalse((state_dir() / name).exists(), f"the launch kept the earlier launch's {name}")
        self.assertEqual("running", group_state(RUN, "S1")["status"])
        # The night's run now cannot remove its own stale files, so its marker stands alone and frees the slot.
        stale = "could not remove the stale S1.json: [Errno 13] Permission denied"

        def refuse_stale(*paths):
            raise db.Refusal(stale)
        with mock.patch.object(dr, "_remove_stale", refuse_stale):
            self.assertIn(f"refused: {stale}", run_cli(["run", RUN, "S1"]))
        self.assertEqual(f"S1: crashed-before-launch - run refused before launch: {stale}",
                         run_cli(["result", RUN, "S1"]).splitlines()[0])

    def test_a_second_launch_of_a_running_group_is_refused_and_keeps_its_executors_files(self):
        # Removed, the live pid and the unread record would leave resume to relaunch over the executor.
        self.write_tasks(FIXTURE)
        self.move("S1")
        listed = f"worktree {(self.root / WORKTREE).as_posix()}\nHEAD {HEAD}\nbranch refs/heads/{BRANCH}\n\n"
        argv = ["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE]
        with mock.patch.object(dr, "git", lambda root, *args: listed):
            self.assertEqual("", run_cli(argv))
            (state_dir() / "S1.pid").write_text(f"{os.getpid()}\n", encoding="utf-8")
            (state_dir() / "S1.session.json").write_text("{}\n", encoding="utf-8")
            out = run_cli(argv)
        self.assertIn("refused: S1 is running, not moved and unlaunched", out)
        for name in ("S1.pid", "S1.session.json"):
            self.assertTrue((state_dir() / name).is_file(), f"a second launch removed the executor's {name}")
        self.assertEqual("running", group_state(RUN, "S1")["status"])

    def test_run_passes_the_prompt_on_stdin_and_no_skip_permissions_flag(self):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        stale = Path(WORKTREE) / "artifacts" / "dispatch" / RUN / "S1.json"
        stale.parent.mkdir(parents=True)
        stale.write_text(json.dumps(MERGED_S1), encoding="utf-8")
        # `absorb` takes what stands in the run directory as this launch's, so an earlier launch's has to go here.
        earlier = state_dir() / "S1.json"
        earlier.write_text(json.dumps(MERGED_S1), encoding="utf-8")
        raw = run_fake_claude(stdout=json.dumps(SUCCESS).encode("utf-8"))
        self.assertFalse(earlier.exists())
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertEqual(["-p", "--output-format", "json", "--model", "opus"], seen["argv"])
        for flag in ("--dangerously-skip-permissions", "--permission-mode"):
            self.assertNotIn(flag, " ".join(seen["argv"]))
        self.assertEqual(os.path.normcase(os.path.realpath(WORKTREE)), os.path.normcase(os.path.realpath(seen["cwd"])))
        self.assertEqual("/ship #S1-1682+1683 Narrow the RabbitMQ read grants --dispatched "
                         "artifacts/dispatch/20261101-220000", seen["stdin"])
        self.assertFalse(stale.exists())
        self.assertEqual(0, raw["exit"])
        self.assertEqual(SUCCESS, json.loads(raw["stdout"]))
        self.assertTrue(int((state_dir() / "S1.pid").read_text(encoding="utf-8")) > 0)

    def test_a_relaunch_is_told_what_the_earlier_session_left(self):
        # Night 20261109-205859: three executors ended on /commit's or /pr's report, leaving a branch and a PR.
        record(RUN, "S1", status="relaunch", branch=BRANCH, worktree=WORKTREE, crashes=1)
        reads = []

        def fake_git(root, *args):
            reads.append(args)
            return {"rev-list": "3\n", "status": " M payload/x.py\n"}.get(args[0], "")

        rows = [{"number": 356, "state": "OPEN"}, {"number": 300, "state": "CLOSED"}]
        with mock.patch.object(dr, "git", fake_git), mock.patch.object(dr, "helper", lambda root, *a: json.dumps(rows)):
            run_fake_claude(stdout=json.dumps(SUCCESS).encode("utf-8"), description="Narrow")
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertEqual("/ship #S1-1682+1683 Narrow (relaunched: the previous session ended with no result the "
                         f"dispatcher accepted, and left {BRANCH} 3 commits ahead of origin/main, uncommitted work in its tree, PR #356 open; "
                         "resume from there, and end the turn only on the result) --dispatched "
                         f"artifacts/dispatch/{RUN}", seen["stdin"])
        self.assertIn(("rev-list", "--count", "origin/main..HEAD"), reads)
        # status.showUntrackedFiles=no would otherwise read untracked work as nothing left.
        self.assertIn(("status", "--porcelain", "--untracked-files=all"), reads)

    def test_a_first_launch_reads_nothing_it_left(self):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        read = mock.Mock(side_effect=AssertionError("a first launch has no earlier session to read"))
        with mock.patch.object(dr, "helper", read):
            run_fake_claude(stdout=json.dumps(SUCCESS).encode("utf-8"), description="Narrow")
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertEqual(f"/ship #S1-1682+1683 Narrow --dispatched artifacts/dispatch/{RUN}", seen["stdin"])
        read.assert_not_called()

    def test_what_a_relaunch_is_told_drops_each_read_that_fails(self):
        make_worktree(WORKTREE)
        entry = {"status": "window", "branch": BRANCH, "worktree": WORKTREE}

        def refuse(*args):
            raise dr.Refusal("not a git repository")

        opened = json.dumps([{"number": 356, "state": "OPEN"}])
        with mock.patch.object(dr, "git", refuse), mock.patch.object(dr, "helper", lambda root, *a: opened):
            self.assertEqual(" (relaunched: the previous session ended with no result the dispatcher accepted, and "
                             "left PR #356 open; resume from there, and end the turn only on the result)",
                             dr._left_behind(".", entry))
        with mock.patch.object(dr, "git", lambda root, *a: "1\n" if a[0] == "rev-list" else ""), \
                mock.patch.object(dr, "helper", refuse):
            self.assertIn(f"left {BRANCH} 1 commit ahead of origin/main;", dr._left_behind(".", entry))
        # Nothing left, or out of shape, is no clause: the relaunch reads its own state as any /ship does.
        for answer in ("0\n", "not a count"):
            with self.subTest(answer=answer), \
                    mock.patch.object(dr, "git", lambda root, *a: answer if a[0] == "rev-list" else ""), \
                    mock.patch.object(dr, "helper", lambda root, *a: '{"not": "rows"}'):
                self.assertEqual("", dr._left_behind(".", entry))

    def test_a_sync_relaunch_is_told_nothing(self):
        # A sync is held to its proved head and never committed to, so there is nothing of its own to resume.
        make_worktree(WORKTREE)
        entry = {"kind": "sync", "status": "relaunch", "branch": "chore/harness-sync-1234567", "worktree": WORKTREE}
        with mock.patch.object(dr, "git", mock.Mock(side_effect=AssertionError("read"))):
            self.assertEqual("", dr._left_behind(".", entry))

    def test_run_gives_each_executor_its_own_temp_directory_outside_every_checkout(self):
        # Two executors sharing the system temp directory ran each other's scratch scripts.
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        parent = {name: "shared-parent-temp" for name in ("TMP", "TEMP", "TMPDIR")}
        with mock.patch.dict(os.environ, parent):
            run_fake_claude(stdout=json.dumps(SUCCESS).encode("utf-8"))
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        own = Path(seen["temp"]["TMP"])
        self.assertEqual({name: str(own) for name in parent}, seen["temp"])
        self.assertEqual((self.root / "system-temp").resolve(), own.parent)
        self.assertTrue(own.name.startswith(f"dispatch-{self.root.resolve().name}-{RUN}-S1-"), own.name)
        # Each launch makes a new one, so no path is fixed in advance for someone else to plant.
        first, second = dr.executor_temp(".", RUN, "S1"), dr.executor_temp(".", RUN, "S1")
        self.addCleanup(shutil.rmtree, first, True)
        self.addCleanup(shutil.rmtree, second, True)
        self.assertNotEqual(first, second)

    def test_run_marks_the_executor_so_the_task_list_helper_refuses_its_writes(self):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        run_fake_claude(stdout=json.dumps(SUCCESS).encode("utf-8"))
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertEqual("1", seen["dispatched"])

    def test_run_keeps_the_dispatchers_api_key_from_the_executor(self):
        # Inherited, the key outranked the owner's subscription login, and a drained API balance crashed every launch.
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        with mock.patch.dict(os.environ, {"ANTHROPIC_API_KEY": "sk-ant-dispatcher"}):
            run_fake_claude(stdout=json.dumps(SUCCESS).encode("utf-8"))
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertIsNone(seen["api_key"])

    def test_run_removes_the_executors_temp_directory_once_it_exits(self):
        # Each launch's mkdtemp directory outlived its executor and piled up under the system temp root.
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        run_fake_claude(stdout=json.dumps(SUCCESS).encode("utf-8"))
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertFalse(Path(seen["temp"]["TMP"]).exists())
        self.assertEqual([], list((self.root / "system-temp").iterdir()))

    def test_run_removes_the_temp_directory_of_an_executor_that_fails_or_cannot_start(self):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        run_fake_claude(stdout=b"", code=1)
        self.assertEqual([], list((self.root / "system-temp").iterdir()))
        task_text()
        with mock.patch.object(dr, "CLAUDE_COMMAND", [str(self.root / "no-such-claude")]):
            self.assertIn("refused: could not start claude", run_cli(["run", RUN, "S1"]))
        self.assertEqual([], list((self.root / "system-temp").iterdir()))

    def test_run_removes_the_temp_directory_when_the_pid_file_cannot_be_written(self):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        task_text()

        def refuse(path, text):
            raise dr.Refusal("pid file refused")

        with mock.patch.object(dr, "CLAUDE_COMMAND", [sys.executable, "-c", "pass"]), \
                mock.patch.object(dr, "_write", refuse):
            self.assertIn("refused: pid file refused", run_cli(["run", RUN, "S1"]))
        self.assertEqual([], list((self.root / "system-temp").iterdir()))

    def test_the_executor_temp_root_is_the_systems_and_outside_the_checkout_and_its_worktrees(self):
        # refresh-index.py's worker runs from it, and guard-git-argv.py's scratch checkouts are judged under it.
        with mock.patch.object(dr, "TEMP_ROOT", None):
            made = dr.executor_temp(".", RUN, "S1")
        self.addCleanup(os.rmdir, made)
        got = os.path.normcase(str(made))
        system = os.path.normcase(os.path.realpath(tempfile.gettempdir()))
        self.assertTrue(got.startswith(system + os.sep), got)
        checkout = os.path.normcase(str(self.root.resolve()))
        self.assertFalse(got.startswith(checkout + os.sep), got)

    def test_run_refuses_an_executor_whose_temp_directory_cannot_be_made(self):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        blocker = Path("not-a-directory")
        blocker.write_text("a file where the temp root goes", encoding="utf-8")
        task_text()
        with mock.patch.object(dr, "TEMP_ROOT", str(blocker.resolve())), \
                mock.patch.object(dr, "CLAUDE_COMMAND", [sys.executable, "-c", "print('{}')"]):
            self.assertIn("refused: could not create the executor's temp directory", run_cli(["run", RUN, "S1"]))
        self.assertFalse((state_dir() / "S1.pid").exists())
        self.assertTrue((state_dir() / "S1.refused.json").is_file())

    def test_run_refuses_a_group_with_no_claude_or_already_handed_back(self):
        record(RUN, "S1", status="running")
        task_text()
        with mock.patch.object(dr, "CLAUDE_COMMAND", None), mock.patch.object(dr, "path_which", lambda _: None):
            self.assertIn("refused: no claude on PATH", run_cli(["run", RUN, "S1"]))
        record(RUN, "S1", status="handed-back")
        self.assertIn("refused: S1 is handed-back, not waiting for an executor",
                      run_cli(["run", RUN, "S1"]))


class ResultTests(unittest.TestCase):
    def check(self, data):
        return dr.validate_result(data, "S1", [1682, 1683])

    def test_a_merged_result_needs_a_pr_and_a_whole_oid(self):
        got = self.check(MERGED_S1)
        self.assertIsInstance(got, dr.Result)
        self.assertEqual((1690, OID, (1701,)), (got.pr, got.merge_oid, got.filed))
        for change in ({"pr": None}, {"pr": 0}, {"pr": True}, {"pr": "1690"}, {"merge_oid": None},
                       {"merge_oid": OID[:9]}, {"merge_oid": OID.upper()}, {"merge_oid": OID + "0"},
                       {"review_end": None}):
            with self.subTest(change=change):
                self.assertIsInstance(self.check({**MERGED_S1, **change}), dr.Invalid)

    def test_a_title_is_null_or_text(self):
        # A relaunch that step 0 ended on a merged PR did not open it, so it may not know the title.
        self.assertEqual(MERGED_S1["title"], self.check(MERGED_S1).title)
        self.assertIsInstance(self.check({**MERGED_S1, "title": None}), dr.Result)
        for bad in ("", "   ", 7, ["a title"]):
            with self.subTest(title=bad):
                self.assertEqual("title is neither null nor text", self.check({**MERGED_S1, "title": bad}).why)

    def test_a_relaunch_onto_a_merged_branch_has_no_review_to_end(self):
        # /ship's step 0 ends a relaunch whose PR already merged before any round runs.
        relaunch = {**MERGED_S1, "rounds": [], "review_end": None}
        self.assertIsInstance(self.check(relaunch), dr.Result)
        self.assertIsInstance(self.check({**relaunch, "rounds": MERGED_S1["rounds"]}), dr.Invalid,
                              "a review that ran has an end")

    def test_a_hand_back_or_stop_needs_a_reason(self):
        self.assertIsInstance(self.check(HANDED_S1), dr.Result)
        self.assertIsInstance(self.check({**HANDED_S1, "outcome": "stopped", "review_end": "ceiling"}), dr.Result)
        for outcome in ("handed-back", "stopped"):
            for reason in (None, "", "   ", 7):
                with self.subTest(outcome=outcome, reason=reason):
                    self.assertIsInstance(self.check({**HANDED_S1, "outcome": outcome, "reason": reason}),
                                          dr.Invalid)

    def test_each_round_is_full_or_recheck_with_three_counts(self):
        good = {"kind": "recheck", "raised": 0, "fixed": 0, "refused": 0}
        self.assertIsInstance(self.check({**MERGED_S1, "rounds": [good]}), dr.Result)
        self.assertIsInstance(self.check({**HANDED_S1, "rounds": []}), dr.Result)
        for bad in ({**good, "kind": "partial"}, {k: v for k, v in good.items() if k != "refused"},
                    {**good, "raised": -1}, {**good, "fixed": True}, {**good, "fixed": 1.0}, {**good, "extra": 1},
                    "full"):
            with self.subTest(bad=bad):
                self.assertIsInstance(self.check({**MERGED_S1, "rounds": [bad]}), dr.Invalid)
        for field, value in (("rounds", {}), ("filed", [0]), ("filed", ["1701"]), ("decisions", [3]),
                             ("workspace", {"worktree": WORKTREE}), ("workspace", {"worktree": 1, "removed": False})):
            with self.subTest(field=field, value=value):
                self.assertIsInstance(self.check({**MERGED_S1, field: value}), dr.Invalid)

    def test_an_outcome_or_review_end_outside_the_spec_is_invalid(self):
        for change in ({"outcome": "landed"}, {"outcome": None}, {"review_end": "tired"},
                       {"review_end": "Clean"}):
            with self.subTest(change=change):
                self.assertIsInstance(self.check({**MERGED_S1, **change}), dr.Invalid)
        for review_end in dr.REVIEW_ENDS:
            self.assertIsInstance(self.check({**MERGED_S1, "review_end": review_end}), dr.Result)

    def test_a_missing_or_unparsable_file_is_no_result(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "S1.json"
            self.assertIsNone(dr.load_result(path, "S1", [1682, 1683]))
            for text in (b"{not json", b"\xff\xfe", b"[]", b"null"):
                path.write_bytes(text)
                got = dr.load_result(path, "S1", [1682, 1683])
                self.assertIsInstance(got, dr.Invalid, text)
                self.assertEqual("relaunch", dr.classify(end(exit_code=0), got, 0, NOW))
            path.write_text(json.dumps(MERGED_S1), encoding="utf-8")
            self.assertIsInstance(dr.load_result(path, "S1", [1682, 1683]), dr.Result)
        self.assertEqual("relaunch", dr.classify(end(exit_code=1, is_error=True), None, 0, NOW))

    def test_a_result_for_another_group_is_no_result(self):
        for change in ({"group": "S2"}, {"issues": [1682]},
                       {"verdict": "merged"}):
            with self.subTest(change=change):
                got = dr.validate_result({**MERGED_S1, **change}, "S1",
                                         [1682, 1683])
                self.assertIsInstance(got, dr.Invalid)
        for change in ({"issues": [1683, 1682]},):
            self.assertIsInstance(self.check({**MERGED_S1, **change}), dr.Result)
        for change in ({"issues": [1682, 1682]}, {"issues": [1682, 1683, 1684]}, {"issues": "1682+1683"}):
            self.assertIsInstance(self.check({**MERGED_S1, **change}), dr.Invalid)
        missing = {k: v for k, v in MERGED_S1.items() if k != "decisions"}
        self.assertIsInstance(self.check(missing), dr.Invalid)
        self.assertIsInstance(self.check([MERGED_S1]), dr.Invalid)


class ClassifyTests(InRoot):
    def result(self, data=MERGED_S1):
        return dr.validate_result(data, "S1", [1682, 1683])

    def test_merged_is_claimed_and_awaits_verification(self):
        self.assertEqual("merged-claimed", dr.classify(end(), self.result(), 0, NOW))
        # A valid result wins over the exit: the session may die after writing it.
        self.assertEqual("merged-claimed", dr.classify(end(exit_code=1, is_error=True), self.result(), 1, NOW))

    def test_handed_back_and_stopped_are_never_relaunched(self):
        self.assertEqual("handed-back", dr.classify(end(), self.result(HANDED_S1), 0, NOW))
        stopped = self.result({**HANDED_S1, "outcome": "stopped"})
        self.assertEqual("stopped", dr.classify(end(exit_code=1, is_error=True), stopped, 0, NOW))
        limited = end(is_error=True, rate_limited=True, resets_at=NOW + timedelta(hours=1))
        self.assertEqual("stopped", dr.classify(limited, stopped, 0, NOW))

    def test_a_limit_resetting_within_five_hours_is_the_window(self):
        for later in (timedelta(hours=1), timedelta(hours=5, minutes=10), timedelta(minutes=-30)):
            with self.subTest(later=later):
                limited = end(exit_code=1, is_error=True, rate_limited=True, resets_at=NOW + later)
                self.assertEqual("window", dr.classify(limited, None, 1, NOW))
        got = dr.session_end({"exit": 1, "stdout": json.dumps(LIMIT_429), "stderr": "",
                              "ended": NOW.isoformat()})
        self.assertEqual((True, True, datetime(2026, 11, 2, 1, 30, tzinfo=timezone.utc)),
                         (got.is_error, got.rate_limited, got.resets_at))
        self.assertEqual("window", dr.classify(got, None, 0, NOW))

    def test_a_limit_resetting_later_is_the_week(self):
        for later in (timedelta(hours=5, minutes=11), timedelta(days=3)):
            limited = end(exit_code=1, is_error=True, rate_limited=True, resets_at=NOW + later)
            self.assertEqual("week", dr.classify(limited, None, 0, NOW))
        text = "API Error: rate_limit_error; weekly limit reached; resets 2026-11-05 09:00:00+00:00"
        got = dr.session_end({"exit": 1, "stdout": "", "stderr": text, "ended": NOW.isoformat()})
        self.assertEqual(datetime(2026, 11, 5, 9, 0, tzinfo=timezone.utc), got.resets_at)
        self.assertEqual("week", dr.classify(got, None, 0, NOW))

    def test_an_account_the_api_refused_before_any_work_ends_the_night_and_is_no_crash(self):
        # Run 20261114-081919 read this as `relaunch`, then `crash-hand-back`, and sent sync-497 back for it.
        for status in (400, 401):
            with self.subTest(status=status):
                got = dr.session_end({"exit": 1, "stdout": json.dumps({**NO_CREDIT, "api_error_status": status}),
                                      "stderr": "", "ended": NOW.isoformat()})
                self.assertEqual(f"{status} invalid_request_error: Credit balance is too low", got.account_refused)
                for crashes in (0, 1):
                    self.assertEqual("account", dr.classify(got, None, crashes, NOW))
        self.assertEqual("the API refused the account: 400 invalid_request_error: Credit balance is too low",
                         dr._reason("account", dr.session_end({"exit": 1, "stdout": json.dumps(NO_CREDIT)}), None))

    def test_an_api_error_that_did_work_or_is_not_the_accounts_is_still_a_crash(self):
        for changed in ({"num_turns": 2}, {"num_turns": True}, {"num_turns": None}, {"api_error_status": 500},
                        {"api_error_status": "400"}, {"terminal_reason": "error"}, {"api_error_status": 429}):
            with self.subTest(changed=changed):
                got = dr.session_end({"exit": 1, "stdout": json.dumps({**NO_CREDIT, **changed}), "stderr": ""})
                self.assertIsNone(got.account_refused)
                self.assertIn(dr.classify(got, None, 0, NOW), ("relaunch", "window", "week"))
        # A session that says it did not fail and exited 0 is no refusal, whatever else its envelope carries.
        got = dr.session_end({"exit": 0, "stdout": json.dumps({**NO_CREDIT, "is_error": False}), "stderr": ""})
        self.assertIsNone(got.account_refused)
        # A result the session wrote is its outcome, whatever the envelope says.
        got = dr.session_end({"exit": 1, "stdout": json.dumps(NO_CREDIT), "stderr": ""})
        self.assertEqual("handed-back", dr.classify(got, self.result(HANDED_S1), 0, NOW))

    def test_a_crash_relaunches_once_and_a_second_hands_back(self):
        crashed = dr.session_end({"exit": 3221225786, "stdout": "", "stderr": "Error: socket hang up"})
        self.assertEqual((True, False), (crashed.is_error, crashed.rate_limited))
        self.assertEqual("relaunch", dr.classify(crashed, None, 0, NOW))
        self.assertEqual("crash-hand-back", dr.classify(crashed, None, 1, NOW))
        # A clean exit with no result is still a crash: the result is the only success.
        self.assertEqual("relaunch", dr.classify(dr.session_end({"exit": 0, "stdout": json.dumps(SUCCESS)}),
                                                 None, 0, NOW))

    def test_an_invalid_result_counts_as_a_crash(self):
        bad = self.result({**MERGED_S1, "group": "S2"})
        self.assertEqual("relaunch", dr.classify(end(), bad, 0, NOW))
        self.assertEqual("crash-hand-back", dr.classify(end(), bad, 1, NOW))

    def test_a_denial_with_no_result_is_a_hand_back(self):
        # 2026-11-01-dispatch-spike.md, Prompts: a call the auto classifier refuses is a hand-back, never a
        # widened grant at night.
        denied = {**SUCCESS, "is_error": True, "subtype": "error_during_execution",
                  "permission_denials": [{"tool_name": "Bash", "tool_use_id": "t1", "tool_input": {}}]}
        got = dr.session_end({"exit": 1, "stdout": json.dumps(denied), "stderr": ""})
        self.assertEqual(["Bash"], got.denials)
        self.assertEqual("handed-back", dr.classify(got, None, 0, NOW))

    def test_session_output_is_read_as_utf8_and_a_limit_it_names_is_a_crash(self):
        raw = run_fake_claude(stdout="Usage limit reached — "
                              "лимит".encode("utf-8"),
                              code=1)
        end = dr.session_end(raw)
        self.assertIn("—", raw["stdout"])
        # Nothing in stdout with no envelope says who wrote it, so it names no limit.
        self.assertFalse(end.rate_limited)
        self.assertIsNone(end.resets_at)
        self.assertEqual("relaunch", dr.classify(end, None, 0, NOW))
        self.assertEqual("crash-hand-back", dr.classify(end, None, 1, NOW))

    def spent(self, **fields):
        envelope = {key: value for key, value in {**SUCCESS, **fields}.items() if value is not ABSENT}
        return dr.session_end({"exit": 0, "stdout": json.dumps(envelope), "stderr": ""})

    def test_a_sessions_tokens_are_modelUsage_summed_over_its_models_not_its_last_turn(self):
        ok = self.spent()
        self.assertEqual(1662280, sum(ok.usage.values()))
        self.assertEqual("modelUsage", ok.usage_source)
        self.assertEqual(1.886, ok.cost_usd)
        self.assertEqual({"input_tokens": 14, "cache_creation_input_tokens": 50297,
                          "cache_read_input_tokens": 212506, "output_tokens": 3868},
                         ok.usage_models["claude-opus-5-5"])
        self.assertEqual(1395595, sum(ok.usage_models["claude-sonnet-5-5"].values()))

    def test_usage_is_read_only_for_an_envelope_with_no_model_counts(self):
        for name, listed in (("absent", ABSENT), ("not a dict", [1]), ("empty", {}), ("no entry a dict", {"m": 3}),
                             ("no count in an entry", {"m": {"costUSD": 1.5, "inputTokens": "9"}})):
            with self.subTest(name):
                ok = self.spent(modelUsage=listed)
                self.assertEqual({k: USAGE[k] for k in dr.TOKEN_KEYS}, ok.usage)
                self.assertEqual("usage", ok.usage_source)
                self.assertEqual({}, ok.usage_models)
        bare = self.spent(modelUsage=ABSENT, usage=ABSENT)
        self.assertEqual(({}, None), (bare.usage, bare.usage_source))

    def test_a_usage_of_zeros_is_no_source(self):
        zero = {key: 0 for key in dr.TOKEN_KEYS}
        ok = self.spent(modelUsage={}, usage=zero)
        self.assertEqual((zero, None), (ok.usage, ok.usage_source))

    def test_a_model_keeps_the_counts_it_has_and_a_malformed_one_is_not_summed(self):
        ok = self.spent(modelUsage={"a": {"inputTokens": 5, "outputTokens": True, "cacheReadInputTokens": -1},
                                    "b": {"inputTokens": 7, "outputTokens": 2}, "c": "not counts"})
        self.assertEqual({"input_tokens": 12, "output_tokens": 2}, ok.usage)
        self.assertEqual({"a": {"input_tokens": 5}, "b": {"input_tokens": 7, "output_tokens": 2}}, ok.usage_models)

    def test_only_a_plain_finite_dollar_figure_is_a_cost(self):
        for value in (ABSENT, None, "1.5", True, -0.5, 10 ** 400):
            with self.subTest(value=value):
                self.assertIsNone(self.spent(total_cost_usd=value).cost_usd)
        for text in ("NaN", "Infinity"):
            with self.subTest(text=text):
                raw = json.dumps(SUCCESS).replace('"total_cost_usd": 1.886', f'"total_cost_usd": {text}')
                self.assertIn(text, raw)
                self.assertIsNone(dr.session_end({"exit": 0, "stdout": raw, "stderr": ""}).cost_usd)
        self.assertEqual(0.0, self.spent(total_cost_usd=0).cost_usd)

    def test_the_envelope_is_read_by_its_structured_fields_first(self):
        ok = dr.session_end({"exit": 0, "stdout": json.dumps(SUCCESS), "stderr": ""})
        self.assertEqual((False, False, None), (ok.is_error, ok.rate_limited, ok.resets_at))
        self.assertEqual(SESSION_USAGE, ok.usage)
        self.assertEqual(SUCCESS["session_id"], ok.session_id)
        kind = dr.session_end({"exit": 1, "stdout": json.dumps(LIMIT_KIND), "stderr": ""})
        self.assertTrue(kind.rate_limited)
        self.assertIsNone(kind.resets_at)
        on_stderr = dr.session_end({"exit": 1, "stdout": "", "stderr": "error kind=rate_limit_error"})
        self.assertTrue(on_stderr.rate_limited)
        epoch = {**LIMIT_KIND, "error": {"type": "rate_limit", "resetsAt": int(NOW.timestamp()) + 3600}}
        self.assertEqual(NOW + timedelta(hours=1), dr.session_end({"exit": 1, "stdout": json.dumps(epoch)}).resets_at)

    def test_a_clock_reset_is_the_next_such_time_after_the_session_ended(self):
        for text in ("rate_limit_error: usage limit reached. Resets 11pm", "rate_limit_error; resets at 23:00"):
            with self.subTest(text=text):
                got = dr.session_end({"exit": 1, "stderr": text, "ended": NOW.isoformat()})
                local = got.resets_at.astimezone()
                self.assertEqual((23, 0), (local.hour, local.minute))
                self.assertTrue(NOW < got.resets_at <= NOW + timedelta(days=1))
        vague = dr.session_end({"exit": 1, "stderr": "rate_limit_error; resets in 5", "ended": NOW.isoformat()})
        self.assertIsNone(vague.resets_at)

    def test_a_date_away_from_a_reset_phrase_is_not_the_reset(self):
        text = "rate_limit_error. The deploy freeze lifts 2026-11-03T00:00:00Z; see the 2026-11-04 09:00 notes."
        got = dr.session_end({"exit": 1, "stderr": text, "ended": NOW.isoformat()})
        self.assertEqual((True, None), (got.rate_limited, got.resets_at))
        self.assertEqual("relaunch", dr.classify(got, None, 0, NOW))
        # "resets" alone is no limit: a crash that mentions a cache reset stays a crash.
        crash = dr.session_end({"exit": 1, "stderr": "Error: the cache resets 2026-11-03T00:00:00Z, then failed"})
        self.assertEqual((False, None), (crash.rate_limited, crash.resets_at))

    def test_a_limit_the_executor_writes_is_a_crash_the_cap_counts(self):
        # Its closing text, or stdout with no envelope, names a limit and a reset minutes away: read, that would
        # be a window, which the crash cap never counts, so the session would relaunch all night.
        soon = (NOW + timedelta(minutes=5)).isoformat()
        written = {**SUCCESS, "subtype": "error_during_execution", "is_error": True,
                   "result": f"Claude usage limit reached; rate_limit_error. Your limit resets at {soon}."}
        for stdout in (json.dumps(written), written["result"]):
            with self.subTest(stdout=stdout[:20]):
                got = dr.session_end({"exit": 1, "stdout": stdout, "stderr": "", "ended": NOW.isoformat()})
                self.assertEqual((True, False, None), (got.is_error, got.rate_limited, got.resets_at))
                self.assertEqual("relaunch", dr.classify(got, None, 0, NOW))
                self.assertEqual("crash-hand-back", dr.classify(got, None, 1, NOW))

    def test_a_refused_sessions_notice_gives_the_reset_no_field_gives(self):
        # The API's refusal ends the session, so the result is the CLI's notice; read only once a field says so.
        notice = {**LIMIT_429, "result": "You've hit your limit · resets 2026-11-02T01:30:00Z"}
        del notice["resetsAt"]
        got = dr.session_end({"exit": 1, "stdout": json.dumps(notice), "stderr": "", "ended": NOW.isoformat()})
        self.assertEqual((True, datetime(2026, 11, 2, 1, 30, tzinfo=timezone.utc)), (got.rate_limited, got.resets_at))
        self.assertEqual("window", dr.classify(got, None, 0, NOW))
        # The same notice beside a limit read only from stderr gives nothing: no field says the session was refused.
        unproven = {**notice, "api_error_status": None}
        got = dr.session_end({"exit": 1, "stdout": json.dumps(unproven), "stderr": "rate_limit_error",
                              "ended": NOW.isoformat()})
        self.assertEqual((True, None), (got.rate_limited, got.resets_at))

    def test_a_refused_calls_input_names_no_limit(self):
        # A refused call's input is the executor's: a kind and a reset inside it are not the CLI's.
        steered = {**SUCCESS, "subtype": "error_during_execution", "is_error": True, "permission_denials": [
            {"tool_name": "Bash", "tool_use_id": "t1",
             "tool_input": {"type": "rate_limit_error", "resetsAt": (NOW + timedelta(minutes=5)).isoformat()}}]}
        got = dr.session_end({"exit": 1, "stdout": json.dumps(steered), "stderr": "", "ended": NOW.isoformat()})
        self.assertEqual((False, None), (got.rate_limited, got.resets_at))
        self.assertEqual("handed-back", dr.classify(got, None, 0, NOW))


class ResultCommandTests(InRoot):
    def absorb(self, data, envelope=SUCCESS, code=0, crashes=0, in_run_directory=None, **fields):
        record(RUN, "S1", **{"status": "running", "branch": BRANCH, "worktree": WORKTREE, "crashes": crashes,
                             **fields})
        # Each call stands for a launch and its session, and a launch removes what the session before it left here.
        copy = state_dir() / "S1.json"
        copy.unlink(missing_ok=True)
        if in_run_directory is not None:
            copy.write_text(in_run_directory if isinstance(in_run_directory, str) else json.dumps(in_run_directory),
                            encoding="utf-8")
        target = Path(WORKTREE) / "artifacts" / "dispatch" / RUN / "S1.json"
        target.parent.mkdir(parents=True, exist_ok=True)
        if data is None:
            target.unlink(missing_ok=True)
        else:
            target.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        stdout = envelope if isinstance(envelope, str) else json.dumps(envelope)
        (state_dir() / "S1.session.json").write_text(json.dumps(
            {"exit": code, "started": NOW.isoformat(), "ended": NOW.isoformat(), "stdout": stdout, "stderr": ""}),
            encoding="utf-8")
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            return run_cli(["result", RUN, "S1"]).splitlines()

    def test_result_records_the_prs_title(self):
        self.assertEqual("S1: merged-claimed pr #1690", self.absorb(MERGED_S1)[0])
        self.assertEqual(MERGED_S1["title"], group_state(RUN, "S1")["title"])

    def test_a_result_written_to_the_run_directory_is_read_rather_than_relaunched(self):
        # H22 of run 20261108-071135 wrote to the main checkout's run directory; a hand-back there read as a crash,
        # and its reason was lost with the file.
        for outcome in ("handed-back", "stopped"):
            with self.subTest(outcome=outcome):
                result = {**HANDED_S1, "outcome": outcome, "reason": "needs a key"}
                self.assertEqual(f"S1: {outcome} - needs a key", self.absorb(None, in_run_directory=result)[0])
                self.assertEqual("needs a key", group_state(RUN, "S1")["reason"])

    def test_a_merge_written_to_the_run_directory_is_a_claim(self):
        self.assertEqual("S1: merged-claimed pr #1690", self.absorb(None, in_run_directory=MERGED_S1)[0])

    def test_a_bad_result_in_the_run_directory_is_still_a_crash(self):
        self.assertIn("S1: relaunch - invalid result: ", self.absorb(None, in_run_directory="not json")[0])

    def test_the_worktrees_result_wins_over_one_in_the_run_directory(self):
        other = {**HANDED_S1, "reason": "the run directory's"}
        self.assertEqual("S1: merged-claimed pr #1690", self.absorb(MERGED_S1, in_run_directory=other)[0])
        self.assertEqual(MERGED_S1, json.loads((state_dir() / "S1.json").read_text(encoding="utf-8")))

    def test_usage_sums_every_session_the_group_ran(self):
        earlier = {"input_tokens": 100, "cache_read_input_tokens": 1000, "output_tokens": 50}
        self.absorb(MERGED_S1, status="relaunch", absorbed="2026-11-01T20:00:00+00:00", usage=earlier)
        spent = group_state(RUN, "S1")["usage"]
        self.assertEqual({key: earlier.get(key, 0) + SESSION_USAGE[key] for key in dr.TOKEN_KEYS}, spent)

    def test_a_relaunched_groups_models_and_cost_sum_and_its_sources_are_both_named(self):
        earlier = {"input_tokens": 100}
        models = {"claude-opus-5-5": {"input_tokens": 1000, "output_tokens": 2}, "claude-gone": {"output_tokens": 9}}
        self.absorb(MERGED_S1, status="relaunch", absorbed="2026-11-01T20:00:00+00:00", usage=earlier,
                    usage_sources=["usage"], usage_models=models, cost_usd=0.25)
        entry = group_state(RUN, "S1")
        self.assertEqual(["usage", "modelUsage"], entry["usage_sources"])
        self.assertEqual(1014, entry["usage_models"]["claude-opus-5-5"]["input_tokens"])
        self.assertEqual(3870, entry["usage_models"]["claude-opus-5-5"]["output_tokens"])
        self.assertEqual({"output_tokens": 9}, entry["usage_models"]["claude-gone"])
        self.assertAlmostEqual(2.136, entry["cost_usd"])

    def test_counts_recorded_before_the_sources_were_are_the_last_turn_reader(self):
        self.absorb(MERGED_S1, status="relaunch", absorbed="2026-11-01T20:00:00+00:00", usage={"input_tokens": 100})
        self.assertEqual(["usage", "modelUsage"], group_state(RUN, "S1")["usage_sources"])

    def test_a_session_that_spent_nothing_leaves_no_source_on_the_next_sessions_row(self):
        self.absorb(MERGED_S1, status="relaunch", absorbed="2026-11-01T20:00:00+00:00", usage={"input_tokens": 0},
                    usage_sources=[])
        self.assertEqual(["modelUsage"], group_state(RUN, "S1")["usage_sources"])

    def test_a_session_with_no_envelope_changes_no_usage_or_source(self):
        self.absorb(MERGED_S1, envelope="not an envelope", status="relaunch",
                    absorbed="2026-11-01T20:00:00+00:00", usage={"input_tokens": 100}, usage_sources=["modelUsage"],
                    usage_models={"m": {"input_tokens": 100}}, cost_usd=0.5)
        entry = group_state(RUN, "S1")
        self.assertEqual(({"input_tokens": 100}, ["modelUsage"]), (entry["usage"], entry["usage_sources"]))
        self.assertEqual(({"m": {"input_tokens": 100}}, 0.5), (entry["usage_models"], entry["cost_usd"]))
        # Its spend is unknown, so the group's row must not read as the whole cost.
        self.assertEqual(1, entry["usage_uncounted"])
        self.assertEqual(["S1: 100 tokens (input 100, cache write 0, cache read 0, output 0), $0.50, from modelUsage, "
                          "1 session left no usage, not counted"], dr._usage_lines("S1", entry))
        self.absorb(MERGED_S1, envelope="not an envelope", status="relaunch", absorbed="2026-11-01T20:00:00+00:00",
                    usage_uncounted=1)
        entry = group_state(RUN, "S1")
        self.assertEqual(2, entry["usage_uncounted"])
        self.assertEqual(["S1: 2 sessions left no usage, not counted"],
                         dr._usage_lines("S1", entry))
        self.assertEqual(["S1: $0.25, 1 session left no usage, not counted"],
                         dr._usage_lines("S1", {"usage": {}, "cost_usd": 0.25, "usage_uncounted": 1}))
        self.absorb(MERGED_S1)
        self.assertEqual(0, group_state(RUN, "S1")["usage_uncounted"])

    def test_result_copies_validates_classifies_records_and_names_the_transcript(self):
        self.assertEqual("transcript: not found", self.absorb(MERGED_S1)[1])
        slug = dr.project_slug(os.path.abspath(WORKTREE))
        transcript = self.root / "projects" / slug / (SUCCESS["session_id"] + ".jsonl")
        transcript.parent.mkdir(parents=True)
        transcript.write_text("{}\n", encoding="utf-8")
        lines = self.absorb(MERGED_S1)
        self.assertEqual("S1: merged-claimed pr #1690", lines[0])
        self.assertEqual(f"transcript: {transcript}", lines[1])
        self.assertEqual(MERGED_S1, json.loads((state_dir() / "S1.json").read_text(encoding="utf-8")))
        entry = group_state(RUN, "S1")
        self.assertEqual(("merged-claimed", 1690, OID, [1701]), (entry["status"], entry["pr"], entry["merge_oid"],
                                                                  entry["filed"]))
        self.assertEqual(NOW.isoformat(), entry["absorbed"])

    def test_result_lines_carry_the_reason_and_the_denials_on_one_line(self):
        hand = {**HANDED_S1, "reason": "needs a key —\nnot here"}
        denied = {**SUCCESS, "permission_denials": [{"tool_name": "Bash"}, {"tool_name": "Edit"}]}
        self.assertEqual("S1: handed-back - needs a key — not here [denials: 2]", self.absorb(hand, denied)[0])
        self.assertEqual("S1: relaunch - no result; exit 1", self.absorb(None, "boom", code=1)[0])
        self.assertEqual(1, group_state(RUN, "S1")["crashes"])
        self.assertEqual("S1: crash-hand-back - a second crash: no result; exit 1",
                         self.absorb(None, "boom", code=1, crashes=1)[0])
        self.assertIn("S1: window - resets 2026-11-02T01:30:00+00:00", self.absorb(None, LIMIT_429, code=1)[0])
        self.assertEqual("2026-11-02T01:30:00+00:00", group_state(RUN, "S1")["resets_at"])
        lines = self.absorb({**MERGED_S1, "group": "S2"})
        self.assertTrue(lines[0].startswith("S1: relaunch - invalid result: group 'S2' is not S1"), lines[0])

    def test_result_before_the_session_ended_is_refused(self):
        record(RUN, "S1", status="running")
        self.assertIn("refused: S1 has no session record; its executor has not exited",
                      run_cli(["result", RUN, "S1"]))

    def test_a_run_whose_marker_was_not_written_goes_back_on_its_refusal_line(self):
        # The run directory refused the marker as well as the launch, so run's exit line is all `result` can read.
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        (state_dir() / "STOP").write_text("", encoding="utf-8")
        write = dr._write

        def refuse_marker(path, text):
            if Path(path).name == "S1.refused.json":
                raise db.Refusal(f"could not write {path}: [Errno 13] Permission denied")
            write(path, text)
        err = io.StringIO()
        with mock.patch.object(dr, "_write", refuse_marker), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
            self.assertEqual(2, db.main(["run", RUN, "S1"]))
        line = err.getvalue().strip()
        self.assertTrue(line.startswith("refused: stop file") and "S1.refused.json" in line, line)
        self.assertFalse((state_dir() / "S1.refused.json").exists())
        self.assertIn("refused: S1 has no session record; its executor has not exited",
                      run_cli(["result", RUN, "S1"]))
        self.assertEqual("running", group_state(RUN, "S1")["status"])
        lines = run_cli(["result", RUN, "S1", "--refused", line]).splitlines()
        self.assertTrue(lines[0].startswith("S1: crashed-before-launch - run refused before launch, its marker "
                                            "unwritten: refused: stop file"), lines)
        self.assertEqual("crashed-before-launch", group_state(RUN, "S1")["status"])
        self.assertEqual(["back S1: crashed-before-launch"], run_cli(["resume", RUN]).splitlines())

    def test_a_refusal_line_after_an_executor_started_waits_for_it_or_hands_it_back(self):
        # result reads a pid file as an executor that ran, whichever run left it: it may have merged, and only a
        # human can say.
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        pid = state_dir() / "S1.pid"
        line = "refused: could not write S1.session.json"
        pid.write_text(f"{os.getpid()}\n", encoding="utf-8")
        self.assertIn("refused: S1 has no session record and its executor is still running",
                      run_cli(["result", RUN, "S1", "--refused", line]))
        self.assertEqual("running", group_state(RUN, "S1")["status"])
        pid.write_text(f"{finished_pid()}\n", encoding="utf-8")
        self.assertEqual(f"S1: crash-hand-back - its executor ran and left no session record: {line}",
                         run_cli(["result", RUN, "S1", "--refused", line]).splitlines()[0])
        self.assertEqual("crash-hand-back", group_state(RUN, "S1")["status"])

    def test_a_refused_relaunch_that_left_the_read_record_goes_back_on_its_refusal_line(self):
        # The refusal came before _launch removed the earlier session's record and pid, both already read.
        earlier = (NOW - timedelta(hours=1)).isoformat()
        record(RUN, "S1", status="relaunch", branch=BRANCH, worktree=WORKTREE, crashes=1, absorbed=earlier)
        (state_dir() / "S1.session.json").write_text(json.dumps(
            {"exit": 1, "started": earlier, "ended": earlier, "stdout": "boom", "stderr": ""}), encoding="utf-8")
        (state_dir() / "S1.pid").write_text(f"{finished_pid()}\n", encoding="utf-8")
        self.assertIn("refused: S1's session record was already read", run_cli(["result", RUN, "S1"]))
        self.assertEqual("relaunch", group_state(RUN, "S1")["status"])
        line = "refused: could not remove the stale S1.json: [Errno 13] Permission denied"
        self.assertEqual(f"S1: crash-hand-back - its executor ran and left no session record: {line}",
                         run_cli(["result", RUN, "S1", "--refused", line]).splitlines()[0])
        self.assertEqual("crash-hand-back", group_state(RUN, "S1")["status"])

    def test_a_refused_relaunchs_marker_beside_the_read_record_sends_the_group_back(self):
        # Removing the earlier session's files failed, so run wrote its marker beside the record already read.
        earlier = (NOW - timedelta(hours=1)).isoformat()
        record(RUN, "S1", status="relaunch", branch=BRANCH, worktree=WORKTREE, crashes=1, absorbed=earlier)
        (state_dir() / "S1.session.json").write_text(json.dumps(
            {"exit": 1, "started": earlier, "ended": earlier, "stdout": "boom", "stderr": ""}), encoding="utf-8")
        (state_dir() / "S1.refused.json").write_text(json.dumps(
            {"refused": "could not remove the stale S1.json", "at": NOW.isoformat()}), encoding="utf-8")
        self.assertEqual(["result S1: its run launched nothing and was not read"],
                         run_cli(["resume", RUN]).splitlines())
        self.assertEqual("S1: crashed-before-launch - run refused before launch: could not remove the stale S1.json",
                         run_cli(["result", RUN, "S1"]).splitlines()[0])
        self.assertEqual("crashed-before-launch", group_state(RUN, "S1")["status"])

    def test_a_refusal_line_beside_a_marker_or_a_session_record_is_not_read(self):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        (state_dir() / "S1.refused.json").write_text(
            json.dumps({"refused": "stop file", "at": NOW.isoformat()}), encoding="utf-8")
        self.assertIn("S1: crashed-before-launch - run refused before launch: stop file",
                      run_cli(["result", RUN, "S1", "--refused", "refused: something else"]))
        self.assertEqual("S1: merged-claimed pr #1690",
                         self.absorb_refused(MERGED_S1, "refused: something else")[0])

    def absorb_refused(self, data, line):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        (state_dir() / "S1.refused.json").unlink(missing_ok=True)
        target = Path(WORKTREE) / "artifacts" / "dispatch" / RUN / "S1.json"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(data), encoding="utf-8")
        (state_dir() / "S1.session.json").write_text(json.dumps(
            {"exit": 0, "started": NOW.isoformat(), "ended": NOW.isoformat(), "stdout": json.dumps(SUCCESS),
             "stderr": ""}), encoding="utf-8")
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            return run_cli(["result", RUN, "S1", "--refused", line]).splitlines()

    def test_a_merge_with_no_round_is_clean_only_on_a_relaunch_this_launch_read_a_session_of(self):
        # Ruling R15's shape is a relaunch onto a PR an earlier session merged; from a first session nobody
        # reviewed it, so it still verifies and lands but is flagged for the report's Needs a human.
        bare = {**MERGED_S1, "rounds": [], "review_end": None}
        earlier = (NOW - timedelta(hours=1)).isoformat()
        cases = {
            "a first session": (bare, {}, True),
            "a relaunch after a crash": (bare, {"status": "relaunch", "crashes": 1, "absorbed": earlier}, False),
            "a relaunch after the window": (bare, {"status": "window", "absorbed": earlier}, False),
            "a relaunch status with no earlier session read": (bare, {"status": "relaunch", "crashes": 1}, True),
            "a launch run again over a read session": (bare, {"absorbed": earlier}, True),
            "a first session with no rounds but a review end": ({**bare, "review_end": "clean"}, {}, True),
            "a reviewed merge from a first session": (MERGED_S1, {}, False),
        }
        for name, (data, fields, unreviewed) in cases.items():
            with self.subTest(case=name):
                line = self.absorb(data, **fields)[0]
                entry = group_state(RUN, "S1")
                self.assertEqual("merged-claimed", entry["status"])
                self.assertEqual(unreviewed, entry["unreviewed"])
                self.assertEqual("S1: merged-claimed pr #1690" + (" - merged with no review round" if unreviewed
                                                                  else ""), line)

    def test_a_merge_with_no_round_is_reviewed_when_the_transcript_shows_a_reviewer_spawned(self):
        # A session that re-entered /ship after a compaction reports no rounds, though its transcript holds one.
        bare = {**MERGED_S1, "rounds": [], "review_end": None}
        transcript = (self.root / "projects" / dr.project_slug(os.path.abspath(WORKTREE))
                      / (SUCCESS["session_id"] + ".jsonl"))
        transcript.parent.mkdir(parents=True)

        def spawn(kind, error=None):
            use = {"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "t1", "name": "Agent",
                                                                 "input": {"subagent_type": kind}}]}}
            back = {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "t1",
                                                             **({} if error is None else {"is_error": error})}]}}
            return "\n".join(json.dumps(r) for r in (use, back)) + "\n"

        cases = {"a reviewer spawned": (spawn("branch-reviewer"), False),
                 "a reviewer spawned and answered": (spawn("branch-reviewer", False), False),
                 "a reviewer spawn refused": (spawn("branch-reviewer", True), True),
                 "another agent spawned": (spawn("general-purpose"), True),
                 "a transcript that is not JSON": ("not json\n", True)}
        for name, (text, unreviewed) in cases.items():
            with self.subTest(case=name):
                transcript.write_text(text, encoding="utf-8")
                self.absorb(bare)
                self.assertEqual(unreviewed, group_state(RUN, "S1")["unreviewed"])
                self.assertEqual(None if unreviewed else "transcript", group_state(RUN, "S1").get("review_seen"))
        # With no session id the transcript is only the newest in the folder, which may be another session's.
        transcript.write_text(spawn("branch-reviewer"), encoding="utf-8")
        self.absorb(bare, {k: v for k, v in SUCCESS.items() if k != "session_id"})
        self.assertTrue(group_state(RUN, "S1")["unreviewed"])

    def test_a_refusal_older_than_the_launch_is_not_this_launchs(self):
        # Only a `run` of this launch leaves a marker that sends the group back unread.
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        marker = state_dir() / "S1.refused.json"
        for name, body in (("older than the launch", {"refused": "stop file",
                                                      "at": (NOW - timedelta(seconds=1)).isoformat()}),
                           ("undated", {"refused": "stop file"})):
            with self.subTest(marker=name):
                marker.write_text(json.dumps(body), encoding="utf-8")
                self.assertNotIn("launched nothing", run_cli(["resume", RUN]))
                self.assertIn("refused: S1 has no session record; its executor has not exited",
                              run_cli(["result", RUN, "S1"]))
                self.assertEqual("running", group_state(RUN, "S1")["status"])
        marker.write_text(json.dumps({"refused": "stop file", "at": NOW.isoformat()}), encoding="utf-8")
        self.assertIn("S1: crashed-before-launch - run refused before launch: stop file",
                      run_cli(["result", RUN, "S1"]))

    def test_a_refusal_beside_a_session_record_is_refused_rather_than_either_picked(self):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        (state_dir() / "S1.refused.json").write_text(
            json.dumps({"refused": "stop file", "at": NOW.isoformat()}), encoding="utf-8")
        (state_dir() / "S1.session.json").write_text(json.dumps(
            {"exit": 0, "started": NOW.isoformat(), "ended": NOW.isoformat(), "stdout": json.dumps(SUCCESS),
             "stderr": ""}), encoding="utf-8")
        before = (state_dir() / "state.json").read_bytes()
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            self.assertEqual(2, db.main(["result", RUN, "S1"]))
        self.assertEqual("refused: S1 has both S1.refused.json and a session record, so which ended its launch is "
                         "unknown\n", out.getvalue())
        self.assertEqual(before, (state_dir() / "state.json").read_bytes())
        self.assertIn("result S1: its executor ended and its result was not read", run_cli(["resume", RUN]))


class MergeCheckTests(InRoot):
    def fake_pr(self, state="MERGED", merge_oid=OID, branch=BRANCH, number=1690):
        commit = {"oid": merge_oid} if merge_oid else None
        view = {"state": state, "mergeable": "UNKNOWN", "mergeStateStatus": "UNKNOWN", "headRefOid": HEAD,
                "mergeCommit": commit}
        listed = [{"number": number, "state": state, "url": "https://example.invalid/pr", "headRefOid": HEAD,
                   "baseRefName": "main"}]
        stub(self.root, "pr-state.sh", f'[ "$1" = 1690 ] || exit 9\necho \'{json.dumps(view)}\'\n')
        stub(self.root, "pr-for-branch.sh",
             f'if [ "$1" = "{branch}" ]; then echo \'{json.dumps(listed)}\'; else echo "[]"; fi\n')
        record(RUN, "S1", status="merged-claimed", pr=1690, merge_oid=OID, branch=BRANCH, worktree=WORKTREE)

    def verify(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = db.main(["verify", RUN, "S1"])
        return code, out.getvalue()

    def test_a_merge_github_confirms_is_verified(self):
        self.fake_pr()
        self.assertEqual((0, f"verified {OID}\n"), self.verify())
        self.assertEqual("verified", group_state(RUN, "S1")["status"])

    def test_a_merge_claim_github_does_not_show_is_a_stop(self):
        for state, oid, head in (("OPEN", None, BRANCH),
                                 ("MERGED", OTHER_OID, BRANCH),
                                 ("MERGED", OID, "other/branch")):
            with self.subTest(state=state, oid=oid, head=head), contextlib.redirect_stdout(io.StringIO()):
                self.fake_pr(state=state, merge_oid=oid, branch=head)
                self.assertEqual(3, db.main(["verify", RUN, "S1"]))
                self.assertEqual("stopped", group_state(RUN, "S1")["status"])

    def test_another_pr_number_or_an_unknown_head_is_a_stop(self):
        self.fake_pr(number=1691)
        code, out = self.verify()
        self.assertEqual(3, code)
        self.assertEqual(f"not verified: #1690 is not a pull request from {BRANCH}\n", out)
        self.assertIn("merge not verified", group_state(RUN, "S1")["reason"])
        # Two absent heads are not the same head.
        self.fake_pr()
        listed = [{"number": 1690, "state": "MERGED", "baseRefName": "main"}]
        stub(self.root, "pr-for-branch.sh", f"echo '{json.dumps(listed)}'\n")
        stub(self.root, "pr-state.sh", f"echo '{json.dumps({'state': 'MERGED', 'mergeCommit': {'oid': OID}})}'\n")
        self.assertEqual((3, f"not verified: #1690's head is not {BRANCH}'s\n"), self.verify())

    def test_a_github_read_that_fails_is_refused_and_the_claim_kept(self):
        for script, body, why in (("pr-state.sh", "echo 'gh: HTTP 502' >&2\nexit 1\n",
                                   "GitHub could not be read: pr-state.sh 1690 exited 1: gh: HTTP 502"),
                                  ("pr-for-branch.sh", "echo '<html>'\n", "GitHub's answer was not JSON"),
                                  ("pr-state.sh", "echo '[]'\n", "GitHub's answer has another shape")):
            with self.subTest(script=script, why=why):
                self.fake_pr()
                stub(self.root, script, body)
                err = io.StringIO()
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
                    self.assertEqual(2, db.main(["verify", RUN, "S1"]))
                self.assertIn(f"refused: {why}", err.getvalue())
                self.assertEqual("merged-claimed", group_state(RUN, "S1")["status"])
                self.assertIn("verify S1", run_cli(["resume", RUN]))

    def test_verify_refuses_a_group_that_claimed_no_merge(self):
        record(RUN, "S1", status="handed-back")
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            self.assertEqual(2, db.main(["verify", RUN, "S1"]))
        self.assertIn("refused: S1 is handed-back, not a claimed merge", err.getvalue())


class BerserkTests(InRoot):
    """The flag's run half: where it is stored, what it reaches, and the rows it must not reach."""

    BODIES = {1700: "docs only"}

    def start(self, *extra):
        return run_cli(["start", "--pairs", "2", "--tasks", "todo.md", *extra])

    def wake(self, text, **sources):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(text)
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            return run_cli(["wake", RUN, "--tasks", "todo.md", "--today", "2026-11-01"],
                           body_source=lambda n: self.BODIES.get(n, ""), open_source=set,
                           branch_source=NO_BRANCH, **sources).splitlines()

    def state(self, **fields):
        dr.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "berserk": True, "groups": {}, **fields})

    def s1(self):
        return next(g for g in db.groups(db.parse_tasks(FIXTURE)) if g.id == "S1")

    def test_start_stores_the_flag_and_it_is_off_by_default(self):
        self.start("--berserk")
        self.assertIs(True, dr.load_state(state_dir())["berserk"])
        (state_dir() / "report.md").write_text("done", encoding="utf-8")
        self.clock = NOW + timedelta(hours=1)
        name = self.start().strip()
        self.assertIs(False, dr.load_state(state_dir(name))["berserk"])

    def test_a_resume_keeps_the_flag_from_state(self):
        self.start("--berserk")
        err = io.StringIO()
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(3, db.main(["start", "--pairs", "2", "--tasks", "todo.md"]))
        self.assertIs(True, dr.load_state(state_dir())["berserk"])
        self.assertIn("kept with --berserk", err.getvalue())

    def test_a_resume_refuses_the_flag_to_a_run_that_started_without_it(self):
        self.start()
        err = io.StringIO()
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(3, db.main(["start", "--pairs", "2", "--tasks", "todo.md", "--berserk"]))
        self.assertIs(False, dr.load_state(state_dir())["berserk"])
        self.assertIn("kept without --berserk", err.getvalue())

    def test_an_unreadable_open_run_still_exits_3(self):
        self.start()
        (state_dir() / "state.json").write_text("not json", encoding="utf-8")
        with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(3, db.main(["start", "--pairs", "2", "--tasks", "todo.md", "--berserk"]))

    def test_the_prompt_carries_berserk_after_dispatched(self):
        self.assertEqual("/ship #S1-1682+1683 Narrow --dispatched artifacts/dispatch/20261101-220000 --berserk",
                         dr.prompt(self.s1(), "Narrow", RUN, True))
        self.assertNotIn("--berserk", dr.prompt(self.s1(), "Narrow", RUN))

    def test_a_description_cannot_carry_berserk(self):
        for bad in ("Narrow --berserk", "Narrow --BERSERK now"):
            for flag in (False, True):
                with self.subTest(bad=bad, flag=flag), self.assertRaisesRegex(db.Refusal, "--berserk"):
                    dr.prompt(self.s1(), bad, RUN, flag)

    def test_the_run_passes_the_flag_from_state_and_a_sync_never_carries_it(self):
        self.state()
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        run_fake_claude(json.dumps(SUCCESS).encode("utf-8"), description="Narrow")
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertEqual(f"/ship #S1-1682+1683 Narrow --dispatched artifacts/dispatch/{RUN} --berserk", seen["stdin"])
        entry = {"kind": "sync", "pr": 106}
        self.assertEqual(f"/ship sync #106 --dispatched artifacts/dispatch/{RUN}",
                         dr._prompt_for(state_dir(), "sync-106", entry, RUN, True))

    def test_wake_releases_a_row_only_in_a_run_that_holds_the_flag(self):
        text = tasks_of(line(depends="OWNER"))
        dr.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        self.assertEqual("end: drained", self.wake(text)[-1])
        self.state()
        lines = self.wake(text)
        self.assertEqual(["released Z1: waits on a human", "launch: Z1"], lines[-2:])
        self.assertEqual({"Z1": "waits on a human"}, dr.load_state(state_dir())["released"])

    def test_a_launch_a_red_main_held_released_nothing(self):
        # The report names what the flag released, so a launch that did not happen must not record one.
        self.state()
        lines = self.wake(tasks_of(line(depends="OWNER")), red_source=lambda: ["main is red: #9 u"])
        self.assertEqual(["main is red: #9 u", "launch: none"], lines[-2:])
        self.assertFalse(any(text.startswith("released ") for text in lines), lines)
        self.assertNotIn("released", dr.load_state(state_dir()))

    def test_a_wave_launches_its_ready_groups_before_the_released(self):
        self.state(pairs=1)
        text = tasks_of(line(number=1, gid="A1", depends="OWNER", task="Do `a.cs`"),
                        line(number=2, gid="A2", task="Do `b.cs`"))
        self.assertEqual("launch: A2", self.wake(text)[-1])
        self.assertNotIn("released", dr.load_state(state_dir()), "a group not chosen is not recorded")

    def test_a_group_sent_back_is_never_relaunched_by_its_owner_mark(self):
        # `tasks back` marks a row so nothing relaunches it unseen; the flag must not turn that mark into a launch.
        self.state()
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        run_cli(["tasks", "move", "--tasks", "todo.md", "--run", RUN, "S3", "--branch", BRANCH, "--worktree", WORKTREE])
        run_cli(["tasks", "back", "--tasks", "todo.md", "--run", RUN, "S3"])
        with open("todo.md", encoding="utf-8", newline="") as handle:
            text = handle.read()
        self.assertIn(f"| OWNER; {db.SENT_BACK} |", text)
        self.assertNotIn("S3", self.wake(text)[-1])

    def test_a_later_flagged_run_does_not_lift_a_row_an_earlier_one_tried(self):
        self.state()
        self.assertEqual("end: drained", self.wake(tasks_of(line(depends=f"OWNER; {db.SENT_BACK}")))[-1])
        self.assertEqual("launch: Z1", self.wake(tasks_of(line(depends="OWNER")))[-1],
                         "a plain night's hand-back reads OWNER alone, which is the row the flag is for")

    def test_a_row_the_night_placed_keeps_its_cautious_owner(self):
        self.state(placed=[1700])
        self.assertEqual("end: drained", self.wake(tasks_of(line(depends="OWNER")))[-1])

    def report(self):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(tasks_of(line(number=9, gid="Z9", labels="paused")))
        return run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-01"],
                       body_source=lambda n: "", open_source=set).splitlines()

    def section(self, lines, name):
        start = lines.index(name) + 1
        rest = [i for i, text in enumerate(lines[start:], start) if text.startswith("## ")]
        return lines[start:rest[0] if rest else len(lines)]

    def test_the_report_lists_a_decision_made_for_the_owner(self):
        self.state(released={"S1": "waits on a human"})
        record(RUN, "S1", status="done", closed_from="verified", pr=1690, merge_oid=OID, title="fix: narrow",
               decisions=["owner's decision: took the narrow grant, rejected the wide one"])
        lines = self.report()
        heads = [text for text in lines if text.startswith("## ")]
        self.assertEqual(["## Merged", "## Decided without the owner", "## Needs a human"], heads[:3])
        decided = self.section(lines, "## Decided without the owner")
        self.assertEqual(1, len(decided), decided)
        for fact in ("S1", "#1682", "#1683", "#1690", "took the narrow grant, rejected the wide one",
                     "bash .claude/scripts/git-revert-pr.sh 1690"):
            self.assertIn(fact, decided[0])

    def test_a_ready_group_that_took_an_owners_decision_is_listed_too(self):
        self.state()
        record(RUN, "S1", status="done", closed_from="verified", pr=1690, merge_oid=OID,
               decisions=["owner's decision: took A, rejected B"])
        record(RUN, "M1", status="done", closed_from="verified", pr=1691, merge_oid=OID, decisions=["took the lint fix"])
        decided = self.section(self.report(), "## Decided without the owner")
        self.assertEqual(1, len(decided), decided)
        self.assertIn("#1690", decided[0])

    def test_a_run_without_the_flag_prints_the_seven_sections(self):
        dr.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        self.assertEqual([f"## {name}" for name in dr.REPORT_SECTIONS],
                         [text for text in self.report() if text.startswith("## ")])

    def test_needs_a_human_gives_a_released_groups_reason_as_its_point_of_failure(self):
        self.state(released={"S1": "edits .claude/"})
        record(RUN, "S1", status="returned", closed_from="handed-back", reason="tried the hook edit; a deny stopped it")
        record(RUN, "M1", status="returned", closed_from="handed-back", reason="needs a key")
        needs = self.section(self.report(), "## Needs a human")
        self.assertTrue(needs[0].startswith("S1 handed-back, point of failure on #1682, #1683: tried the hook edit; "
                                            "a deny stopped it"), needs)
        self.assertTrue(needs[1].startswith("M1 handed-back: needs a key"), needs)

    def test_every_way_a_released_group_can_end_unmerged_reaches_the_same_line(self):
        # The comment is step 3's, read off this line, so a crash, a stop and a failed verify are not three paths.
        self.state(released={"S1": "waits on a human", "M1": "edits .claude/", "M2": "edits .claude/"})
        record(RUN, "S1", status="crash-hand-back", reason="crashed twice")
        record(RUN, "M1", status="returned", closed_from="stopped", reason="the PR was closed")
        record(RUN, "M2", status="returned", reason=None)
        needs = self.section(self.report(), "## Needs a human")
        self.assertEqual(3, sum(", point of failure on #" in text for text in needs), needs)


class WakeTests(InRoot):
    BODIES = {1682: "touches definitions.json", 1683: "deploy/rabbitmq/definitions.json",
              1681: "src/gateway/program.cs", 1685: "a.cs", 1686: "b.cs", 1440: "docs/x.md"}

    def wake(self, text=FIXTURE, pairs=2, **sources):
        if not (state_dir() / "state.json").is_file():
            dr.save_state(state_dir(), {"run": RUN, "pairs": pairs, "lang": "en", "groups": {}})
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(text)
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            return run_cli(["wake", RUN, "--tasks", "todo.md", "--today", "2026-11-01"],
                           body_source=lambda n: self.BODIES.get(n, ""), open_source=lambda: {1440},
                           branch_source=NO_BRANCH, **sources).splitlines()

    def test_an_open_main_is_red_issue_holds_what_would_have_launched(self):
        # blueprint-backend's run 20261109-093606: `launch: J2` beside the issue that had just stopped J1 at step 6.
        self.assertEqual("launch: S1 S3", self.wake()[-1])
        red = ["main is red: #1895 u1", "main is red: #1896 u2"]
        lines = self.wake(red_source=lambda: red)
        self.assertEqual([*red, "launch: none"], lines[-3:])
        self.assertEqual(1, sum(text.startswith("launch:") for text in lines), lines)
        self.assertEqual("launch: S1 S3", self.wake()[-1], "a later wake reads it again, so a green main launches")

    def test_a_red_main_holds_beside_a_running_group_and_ends_no_night(self):
        record(RUN, "S9", status="running")
        lines = self.wake(red_source=lambda: ["main is red: #1 u"])
        self.assertIn("running: S9", lines)
        self.assertEqual(["main is red: #1 u", "launch: none"], lines[-2:])

    def test_a_read_that_cannot_say_main_is_green_launches_nothing(self):
        def failed():
            raise db.Refusal("main-red-open.sh exited 2: cannot list the open issues")
        printed = "\n".join(self.wake(red_source=failed))
        self.assertIn("refused: main-red-open.sh exited 2", printed)
        self.assertNotIn("launch:", printed)

    def test_a_night_with_nothing_to_launch_does_not_read_it(self):
        def failed():
            raise AssertionError("no launch is at stake, so a failed read must not cost this wake its ending")
        self.assertEqual("end: drained", self.wake(tasks_of(line(depends="OWNER")), red_source=failed)[-1])
        record(RUN, "S9", status="running")
        self.assertEqual("launch: none", self.wake(tasks_of(line(depends="OWNER")), red_source=failed)[-1])

    def test_the_command_line_reads_the_helper_from_the_root_it_was_given(self):
        with mock.patch.object(db, "main_red", return_value=["main is red: #1 u"]) as read:
            lines = self.wake(red_source=None)
        read.assert_called_once_with(".")
        self.assertEqual(["main is red: #1 u", "launch: none"], lines[-2:])

    def test_the_runbook_says_what_a_red_main_asks_of_the_dispatcher(self):
        runbook = " ".join((Path(dr.__file__).parent.parent / "commands" / "dispatch.md").read_text(
            encoding="utf-8").split())
        loop = runbook[runbook.index("2. **The night loop.**"):runbook.index("**Launch a group**")]
        self.assertIn("`main is red: #<n> <url>`", loop)
        self.assertIn("no rerun, no close and no revert from this session: each is the owner's", loop)
        self.assertIn("each `main is red` line a `wake` held on, under *Needs a human*", runbook)

    def test_a_stop_file_ends_the_night_on_the_next_wake(self):
        self.assertEqual("launch: S1 S3", self.wake()[-1])
        (state_dir() / "STOP").write_text("anything at all", encoding="utf-8")
        lines = self.wake()
        self.assertEqual("stop file: yes", lines[0])
        self.assertEqual("end: stop-file", lines[-1])

    def test_wake_with_nothing_ready_and_nothing_running_is_drained(self):
        lines = self.wake(tasks_of(line(depends="OWNER"), line(number=1701, gid="Z2", labels="question")))
        self.assertEqual(["stop file: no", "running: none", "free slots: 2", "weekly: not read, no source is set",
                          "end: drained"], lines)
        record(RUN, "S9", status="running")
        lines = self.wake(tasks_of(line(depends="OWNER")))
        self.assertEqual("launch: none", lines[-1])

    def test_a_ready_group_only_another_sessions_row_holds_is_not_drained(self):
        # FIXTURE's In progress row is another session's and names the dispatch plan, so a group on it is held.
        held = line(task="Fix `docs/superpowers/plans/2026-11-01-dispatch.md`")
        self.assertEqual(["stop file: no", "running: none", "free slots: 2", "weekly: not read, no source is set",
                          "end: held"],
                         self.wake(tasks_of(held)))
        self.assertEqual("launch: Z1", self.wake(tasks_of(line()))[-1])
        self.assertEqual("end: drained", self.wake(tasks_of(line(depends="OWNER")))[-1])

    def test_the_runbook_names_every_ending_wake_prints(self):
        # Read from the source, so an ending added later fails here until the night loop says what it means.
        endings = set(re.findall(r'"(end: [a-z-]+)"', Path(dr.__file__).read_text(encoding="utf-8")))
        self.assertEqual({"end: drained", "end: held", "end: week", "end: account", "end: stop-file"}, endings)
        runbook = " ".join((Path(dr.__file__).parent.parent / "commands" / "dispatch.md").read_text(
            encoding="utf-8").split())
        loop = runbook[runbook.index("2. **The night loop.**"):runbook.index("**Launch a group**")]
        for ending in sorted(endings):
            with self.subTest(ending=ending):
                self.assertIn(f"`{ending}`", loop)

    def test_wake_offers_no_more_than_pairs_minus_running(self):
        record(RUN, "S9", status="window", pid=os.getpid())
        lines = self.wake()
        self.assertIn("running: S9", lines)
        self.assertIn("free slots: 1", lines)
        self.assertEqual("launch: S1", lines[-1])
        record(RUN, "S8", status="relaunch")
        self.assertEqual(["free slots: 0", "weekly: not read, no source is set", "launch: none"], self.wake()[-3:])

    def test_a_week_limit_ends_the_night_until_it_resets(self):
        record(RUN, "S9", status="week")
        self.assertEqual("end: week", self.wake()[-1])
        record(RUN, "S9", status="week", resets_at=(NOW + timedelta(days=2)).isoformat())
        self.assertEqual("end: week", self.wake()[-1])
        self.assertIn(f"wait S9: the week resets {(NOW + timedelta(days=2)).isoformat()}", run_cli(["resume", RUN]))
        self.assertNotIn("relaunch S9", run_cli(["resume", RUN]))
        record(RUN, "S9", status="week", resets_at="whenever")
        self.assertIn("wait S9: the week resets unknown", run_cli(["resume", RUN]))
        # A run resumed after the reset relaunches the group, which holds its slot meanwhile.
        record(RUN, "S9", status="week", resets_at=(NOW - timedelta(minutes=1)).isoformat())
        self.assertEqual(["running: S9", "free slots: 1", "weekly: not read, no source is set", "launch: S1"],
                         [self.wake()[i] for i in (1, 3, 4, 5)])
        self.assertIn("relaunch S9", run_cli(["resume", RUN]))
        task_text("S9")
        with mock.patch.object(dr, "CLAUDE_COMMAND", [sys.executable, "-c", "print('{}')"]):
            self.assertEqual("", run_cli(["run", RUN, "S9"]))
        self.assertTrue((state_dir() / "S9.session.json").is_file())
        self.assertIn("S9: relaunch - no result; exit 0", run_cli(["result", RUN, "S9"]))

    def test_an_account_the_api_refused_ends_the_night_until_the_group_is_relaunched(self):
        # Each group launched after the refusal burned a relaunch on it; the night ends on the first instead.
        record(RUN, "S9", status="running")
        (state_dir() / "S9.session.json").write_text(json.dumps(
            {"exit": 1, "started": NOW.isoformat(), "ended": NOW.isoformat(), "stdout": json.dumps(NO_CREDIT),
             "stderr": ""}), encoding="utf-8")
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            self.assertIn("S9: account - the API refused the account: 400 invalid_request_error: Credit balance is "
                          "too low", run_cli(["result", RUN, "S9"]))
        self.assertEqual(0, group_state(RUN, "S9")["crashes"], "a refused account is no crash of the group's")
        lines = self.wake()
        self.assertIn("running: S9", lines)
        self.assertEqual("end: account", lines[-1])
        self.assertEqual("end: account", self.wake()[-1], "a later wake of the night still ends it")
        report = run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-01"],
                         body_source=lambda n: "", open_source=lambda: {1440}, branch_source=NO_BRANCH)
        self.assertIn("S9 account: the API refused the account: 400 invalid_request_error: Credit balance is too "
                      f"low; fix the account, then run /dispatch again, which resumes run {RUN} and relaunches it",
                      report)
        # A resumed run lifts the hold before its relaunch starts, so a wake racing that launch launches again.
        self.assertIn("relaunch S9", run_cli(["resume", RUN]))
        self.assertTrue((state_dir() / "S9.session.json").is_file())
        self.assertEqual(["running: S9", "launch: S1"], [self.wake()[i] for i in (1, -1)])
        self.assertIn("relaunch S9", run_cli(["resume", RUN]))

    def test_a_merge_with_no_round_after_a_refused_account_is_a_first_sessions(self):
        # The refused session ran no turn past the first, so it cannot have reviewed what the relaunch merged.
        merged = dr.validate_result({**MERGED_S1, "rounds": []}, "S1", [1682, 1683])
        entry = {"absorbed": NOW.isoformat(), "status": "account", "crashes": 0}
        self.assertTrue(dr._unreviewed(entry, merged, 0))
        self.assertFalse(dr._unreviewed({**entry, "status": "week"}, merged, 0))
        self.assertTrue(dr._unreviewed({**entry, "status": "running"}, merged, 0), "as resume leaves it")

    def test_a_refused_sync_is_taken_again_by_the_next_night(self):
        # A sync holds no row, so nothing keeps its run open for `resume`: its report line says what comes next.
        record(RUN, "S7", status="account", kind="sync", keys=[], reason="the API refused the account: 400")
        self.assertEqual("end: account", self.wake()[-1])
        report = run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-01"],
                         body_source=lambda n: "", open_source=lambda: {1440}, branch_source=NO_BRANCH)
        self.assertIn("S7 account: the API refused the account: 400; fix the account, then run /dispatch "
                      "again, which takes the sync again", report)
        self.assertNotIn("resume: run /dispatch again", report)
        # Another group's rows keep the run open, so `resume` relaunches the sync instead, and the line says so.
        with mock.patch.object(dr, "_holds_rows", return_value=(True, None)):
            report = run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-01"],
                             body_source=lambda n: "", open_source=lambda: {1440}, branch_source=NO_BRANCH)
        self.assertIn(f"S7 account: the API refused the account: 400; fix the account, then run /dispatch again, "
                      f"which resumes run {RUN} and relaunches it", report)

    def test_a_refused_relaunch_is_lifted_back_to_a_relaunch(self):
        # A group relaunched from a window or a crash, then refused: its earlier session may have left work and a
        # review, so the lifted hold keeps it a relaunch, which reads what that session left and may merge unrounded.
        for launched_as, lifted in (("running", "running"), ("relaunch", "relaunch"), ("window", "relaunch")):
            with self.subTest(launched_as=launched_as):
                record(RUN, "S9", status=launched_as, absorbed=None)
                (state_dir() / "S9.session.json").write_text(json.dumps(
                    {"exit": 1, "started": NOW.isoformat(), "ended": NOW.isoformat(),
                     "stdout": json.dumps(NO_CREDIT), "stderr": ""}), encoding="utf-8")
                with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
                    self.assertIn("S9: account", run_cli(["result", RUN, "S9"]))
                self.assertIn("relaunch S9", run_cli(["resume", RUN]))
                self.assertEqual(lifted, group_state(RUN, "S9")["status"])

    def test_an_unreadable_week_reset_holds_under_a_moving_clock(self):
        ticks = iter(NOW + timedelta(seconds=n) for n in range(100))
        record(RUN, "S9", status="week", resets_at="whenever")
        with mock.patch.object(dr, "now", lambda: next(ticks)):
            self.assertEqual("end: week", self.wake()[-1])
            self.assertIn("wait S9: the week resets unknown", run_cli(["resume", RUN]))

    def test_a_malformed_state_is_refused_not_a_traceback(self):
        dr.save_state(state_dir(), {"run": RUN, "pairs": "two", "lang": "en", "groups": {}})
        self.assertIn("refused: ", self.wake()[-1])
        record(RUN, "S1", status="running", crashes="many")
        (state_dir() / "S1.session.json").write_text(json.dumps({"exit": 1, "started": "x"}), encoding="utf-8")
        self.assertIn("refused: S1's crashes in state.json is missing or malformed", run_cli(["result", RUN, "S1"]))
        state = dr.load_state(state_dir())
        del state["groups"]["S1"]["keys"]
        state["groups"]["S2"] = "not an object"
        dr.save_state(state_dir(), state)
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE])
        self.assertIn("refused: S1's keys in state.json is missing or malformed", out)
        self.assertIn("refused: S2's record in state.json is not an object", run_cli(["resume", RUN]))

    def test_a_helper_that_hangs_is_a_refusal(self):
        with mock.patch.object(db, "run_bounded", side_effect=subprocess.TimeoutExpired(["gh"], 120)):
            with self.assertRaisesRegex(db.Refusal, "took over 120 s"):
                db.git(".", "worktree", "list")
        self.assertEqual(120, db.CHILD_TIMEOUT)

    def test_wake_names_each_running_executors_transcript(self):
        record(RUN, "S9", status="running", pid=os.getpid())
        folder = self.root / "projects" / dr.project_slug(os.path.abspath(".claude/worktrees/dispatch-s9"))
        folder.mkdir(parents=True)
        (folder / "abc.jsonl").write_text("{}\n", encoding="utf-8")
        with mock.patch.object(dr, "_created", lambda path: 9e12):
            lines = self.wake()
        self.assertIn(f"transcript S9: {folder / 'abc.jsonl'}", lines)

    def test_wake_follows_a_relaunched_executor_not_the_session_it_read(self):
        folder = self.root / "projects" / dr.project_slug(os.path.abspath(".claude/worktrees/dispatch-s9"))
        folder.mkdir(parents=True)
        dead, live = folder / "dead.jsonl", folder / "live.jsonl"
        for status in ("relaunch", "window", "week"):
            with self.subTest(status=status):
                record(RUN, "S9", status=status, pid=os.getpid(), transcript=str(dead))
                pid_time = os.stat(state_dir() / "S9.pid").st_mtime
                for path in (dead, live):
                    path.write_text("{}\n", encoding="utf-8")
                # The read session's transcript predates the pid file `run` wrote for the relaunch; the live one not.
                created = {str(dead): pid_time - 60, str(live): pid_time + 1}
                with mock.patch.object(dr, "_created", lambda path: created[str(path)]):
                    self.assertIn(f"transcript S9: {live}", self.wake())

    def test_the_window_wait_is_capped_and_a_past_reset_returns_at_once(self):
        slept = []
        with mock.patch.object(dr, "SLEEP", slept.append):
            for later, expected in ((timedelta(hours=10), 18600), (timedelta(hours=1), 3660),
                                    (timedelta(minutes=-5), 0), (timedelta(0), 0)):
                with self.subTest(later=later):
                    slept.clear()
                    record(RUN, "S1", status="window", resets_at=(NOW + later).isoformat())
                    self.assertIn("waited", run_cli(["wait", RUN, "S1"]))
                    self.assertEqual(expected, sum(slept))
                    self.assertTrue(all(step <= 60 for step in slept))
        self.assertEqual(18600, dr.window_wait(NOW + timedelta(days=2), NOW))

    def test_a_moved_groups_conflicts_cell_still_holds_what_it_names(self):
        # The In progress row a move leaves has no conflicts cell; the claim comes from the rows the run recorded.
        a1 = "| 1 | A1 | 1711 | Fix `src/a/x.cs` | code | none | Opus | conflicts with B1: shared `src/shared/y.cs` |"
        b1 = "| 1 | B1 | 1712 | Fix `src/b/z.cs` | code | none | Opus | none |"
        c1 = "| 1 | C1 | 1713 | Fix `src/c/w.cs` | code | none | Opus | none |"
        self.assertEqual("launch: A1 C1", self.wake(tasks_of(a1, b1, c1), pairs=3)[-1])
        self.assertEqual("", run_cli(["tasks", "move", "--tasks", "todo.md", "--run", RUN, "A1"]))
        state = dr.load_state(state_dir())
        state["groups"]["A1"].update(status="running", worktree=".claude/worktrees/dispatch-a1")
        dr.save_state(state_dir(), state)
        make_worktree(".claude/worktrees/dispatch-a1")
        moved = Path("todo.md").read_text(encoding="utf-8")
        self.assertEqual("launch: C1", self.wake(moved)[-1])
        # Once the run's rows have left In progress, the claim goes with them.
        state["groups"]["A1"].update(status="done")
        dr.save_state(state_dir(), state)
        self.assertEqual("launch: B1 C1", self.wake(tasks_of(b1, c1))[-1])

    def test_a_stop_file_ends_a_wait_whose_reset_has_already_passed(self):
        record(RUN, "S1", status="window", resets_at=(NOW - timedelta(minutes=5)).isoformat())
        (state_dir() / "STOP").write_text("", encoding="utf-8")
        with mock.patch.object(dr, "SLEEP", lambda _: self.fail("slept with nothing to wait for")):
            self.assertEqual("stop file: yes\n", run_cli(["wait", RUN, "S1"]))

    def test_a_relaunch_after_the_stop_file_starts_nothing_and_its_group_goes_back(self):
        # The crashed session the relaunch follows was read, and its record and copied result are still on disk.
        earlier = (NOW - timedelta(hours=1)).isoformat()
        record(RUN, "S1", status="relaunch", branch=BRANCH, worktree=WORKTREE, crashes=1, absorbed=earlier)
        (state_dir() / "S1.session.json").write_text(json.dumps(
            {"exit": 1, "started": earlier, "ended": earlier, "stdout": "boom", "stderr": ""}), encoding="utf-8")
        (state_dir() / "S1.json").write_text("{}", encoding="utf-8")
        (state_dir() / "STOP").write_text("", encoding="utf-8")
        launched = []

        def popen(*args, **kwargs):
            launched.append(args)
            raise OSError("an executor was started")
        with mock.patch.object(dr.subprocess, "Popen", popen), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(2, db.main(["run", RUN, "S1"]))
        self.assertEqual([], launched)
        self.assertIn("refused: stop file", err.getvalue())
        self.assertFalse((state_dir() / "S1.session.json").exists())
        self.assertFalse((state_dir() / "S1.json").exists())
        self.assertTrue((state_dir() / "S1.refused.json").is_file())
        self.assertEqual(["result S1: its run launched nothing and was not read"],
                         run_cli(["resume", RUN]).splitlines())
        lines = run_cli(["result", RUN, "S1"]).splitlines()
        self.assertTrue(lines[0].startswith("S1: crashed-before-launch - run refused before launch: stop file"),
                        lines)
        self.assertEqual("crashed-before-launch", group_state(RUN, "S1")["status"])
        self.assertEqual(["back S1: crashed-before-launch"], run_cli(["resume", RUN]).splitlines())

    def test_a_relaunch_removes_the_earlier_sessions_pid_before_anything_can_refuse(self):
        # `result --refused` reads a pid file as an executor this run started, so an earlier one must not survive.
        record(RUN, "S1", status="relaunch", branch=BRANCH, worktree=WORKTREE, crashes=1,
               absorbed=(NOW - timedelta(hours=1)).isoformat())
        (state_dir() / "S1.pid").write_text(f"{finished_pid()}\n", encoding="utf-8")
        (state_dir() / "STOP").write_text("", encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, db.main(["run", RUN, "S1"]))
        self.assertFalse((state_dir() / "S1.pid").exists())

    def test_a_run_that_launches_nothing_frees_its_slot_and_its_group_goes_back(self):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        dr.save_state(state_dir(), {"run": RUN, "pairs": 1, "lang": "en", "groups": {}})
        self.assertEqual("", run_cli(["tasks", "move", "--tasks", "todo.md", "--run", RUN, "S1",
                                      "--branch", BRANCH, "--worktree", WORKTREE]))
        state = dr.load_state(state_dir())
        state["groups"]["S1"].update(status="running", model="Opus", issues=[1682, 1683], branch=BRANCH,
                                     worktree=WORKTREE, crashes=0, launched=NOW.isoformat())
        dr.save_state(state_dir(), state)
        make_worktree(WORKTREE)
        real_unlink = Path.unlink

        def locked(path, missing_ok=False):
            if path.name == "S1.session.json":
                raise PermissionError("held open")
            real_unlink(path, missing_ok=missing_ok)
        cases = {"no claude on PATH": (None, mock.patch.object(dr, "path_which", lambda _: None),
                                       "no claude on PATH"),
                 "a stale file it cannot remove": ([sys.executable, "-c", "pass"],
                                                   mock.patch.object(Path, "unlink", locked),
                                                   "could not remove the stale")}
        for name, (claude, patch, why) in cases.items():
            with self.subTest(case=name):
                state = dr.load_state(state_dir())
                state["groups"]["S1"]["status"] = "running"
                dr.save_state(state_dir(), state)
                task_text()
                with mock.patch.object(dr, "CLAUDE_COMMAND", claude), patch:
                    self.assertIn(f"refused: {why}", run_cli(["run", RUN, "S1"]))
                self.assertIn("free slots: 0", self.wake(Path("todo.md").read_text(encoding="utf-8"), pairs=1))
                self.assertIn(f"S1: crashed-before-launch - run refused before launch: {why}",
                              run_cli(["result", RUN, "S1"]))
                self.assertIn("free slots: 1", self.wake(Path("todo.md").read_text(encoding="utf-8"), pairs=1))
        self.assertEqual("", run_cli(["tasks", "back", "--tasks", "todo.md", "--run", RUN, "S1"]))
        entry = group_state(RUN, "S1")
        self.assertEqual(("returned", "crashed-before-launch"), (entry["status"], entry["closed_from"]))

    def test_a_stop_file_cuts_the_window_wait_short(self):
        slept = []

        def sleep(seconds):
            slept.append(seconds)
            (state_dir() / "STOP").write_text("", encoding="utf-8")
        record(RUN, "S1", status="window", resets_at=(NOW + timedelta(hours=1)).isoformat())
        with mock.patch.object(dr, "SLEEP", sleep):
            self.assertIn("stop file: yes", run_cli(["wait", RUN, "S1"]))
        self.assertEqual([60], slept)
        record(RUN, "S1", status="running")
        self.assertIn("refused: S1 is running, not waiting on a window", run_cli(["wait", RUN, "S1"]))

    def test_alive_is_true_for_this_process_and_false_for_a_finished_child(self):
        self.assertTrue(dr.alive(os.getpid()))
        self.assertFalse(dr.alive(finished_pid()))
        for bad in (0, -1, None, "12"):
            self.assertFalse(dr.alive(bad))
        if os.name == "nt":
            # A process this user may not query is alive; any other refusal to open it is a dead pid.
            kernel = mock.Mock()
            kernel.OpenProcess.return_value = None
            import ctypes
            for error, expected in ((dr.ERROR_ACCESS_DENIED, True), (87, False)):
                with mock.patch.object(ctypes, "WinDLL", return_value=kernel), \
                        mock.patch.object(ctypes, "get_last_error", return_value=error):
                    self.assertEqual(expected, dr.alive(4242))
        else:
            # The kernel's answer, not a Linux path, since macOS has no `/proc`: another user's process is alive,
            # and only a pid with no process behind it is dead. The pid is a finished one, so `/proc` would say dead.
            gone = finished_pid()
            for error, expected in ((PermissionError, True), (ProcessLookupError, False)):
                with mock.patch.object(dr.os, "kill", side_effect=error) as kill:
                    self.assertEqual(expected, dr.alive(gone))
                kill.assert_called_once_with(gone, 0)

    def test_a_pid_past_the_c_int_range_is_dead_rather_than_a_traceback(self):
        # A hand-edited pid file can hold one: os.kill raises OverflowError on both, and a DWORD would truncate 2**64.
        for pid in (2 ** 31, 2 ** 64):
            self.assertFalse(dr.alive(pid))

    def test_resume_adopts_only_its_own_dead_groups(self):
        FINISHED_PID = finished_pid()
        record(RUN, "S1", status="running", pid=FINISHED_PID)
        record(RUN, "S2", status="running", pid=os.getpid())   # still alive
        make_worktree(".claude/worktrees/other")               # not in state
        out = run_cli(["resume", RUN])
        self.assertIn("relaunch S1", out)
        self.assertIn("refused S2: its executor is still running", out)
        self.assertNotIn("other", out)

    def test_resume_names_what_each_unfinished_group_needs_next(self):
        dead = finished_pid()
        record(RUN, "S1", status="running", pid=dead)
        (state_dir() / "S1.session.json").write_text(json.dumps({"exit": 0, "started": "later"}), encoding="utf-8")
        record(RUN, "S2", status="merged-claimed")
        record(RUN, "S3", status="window", pid=dead, resets_at=NOW.isoformat())
        record(RUN, "S4", status="stopped")
        record(RUN, "S5", status="done")
        record(RUN, "S6", status="relaunch", pid=dead, worktree=".claude/worktrees/gone")
        os.rmdir(".claude/worktrees/gone")
        record(RUN, "S7")
        lines = run_cli(["resume", RUN]).splitlines()
        self.assertEqual(["result S1: its executor ended and its result was not read", "verify S2",
                          f"wait S3: the window resets {NOW.isoformat()}, then relaunch",
                          "back S4: stopped", "refused S6: its worktree .claude/worktrees/gone is gone",
                          "launch S7: moved, never launched"], lines)


class WeeklyTests(InRoot):
    # No cheap source exists today (2026-11-01-dispatch-spike.md, Weekly figure), so the with-source cases run on an
    # injected one; they document the contract for the day a source is found.
    RESET = "2026-11-06T09:00:00+00:00"

    def start(self):
        if not (state_dir() / "state.json").is_file():
            dr.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})

    def usage(self, source=None):
        self.start()
        return run_cli(["usage", RUN], weekly_source=source)

    def wake(self, source=None):
        self.start()
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            return run_cli(["wake", RUN, "--tasks", "todo.md", "--today", "2026-11-01"], weekly_source=source,
                           body_source=lambda n: "", open_source=lambda: {1440}, branch_source=NO_BRANCH).splitlines()

    def test_no_source_is_not_read_and_never_ends_the_night(self):
        self.assertIsNone(dr.WEEKLY_SOURCE)
        self.assertIsNone(dr.read_weekly())
        self.assertFalse(dr.week_ends_night(None))
        self.assertEqual("weekly: not read, no source is set\n", self.usage())
        self.assertEqual(["weekly: not read, no source is set", "launch: S1 S3"], self.wake()[-2:])
        self.assertNotIn("start", dr.load_state(state_dir())["usage"])

    def test_a_real_reading_after_unread_wakes_is_the_start(self):
        self.wake()
        self.wake()
        self.assertEqual(f"weekly: 75% left, resets {self.RESET}\n",
                         self.usage(lambda: {"percentUsed": 25, "resetsAt": self.RESET}))
        recorded = dr.load_state(state_dir())["usage"]
        self.assertEqual((75.0, False), (recorded["start"]["percent_left"], "end" in recorded))

    def test_every_wake_prints_one_weekly_line(self):
        for source, expected in ((None, "weekly: not read, no source is set"),
                                 (lambda: {"percentUsed": 40, "resetsAt": self.RESET},
                                  f"weekly: 60% left, resets {self.RESET}")):
            lines = self.wake(source)
            self.assertEqual([expected], [text for text in lines if text.startswith("weekly:")])

    def test_a_failed_reading_is_recorded_and_is_not_the_limit(self):
        def broken():
            raise OSError("no token file")
        self.assertEqual("weekly: reading failed: no token file\n", self.usage(broken))
        failed = dr.load_state(state_dir())["usage"]["failed"]
        self.assertEqual((1, "no token file"), (failed["count"], failed["why"]))
        with self.assertRaises(dr.ReadingFailed):
            dr.read_weekly(broken)
        self.assertNotEqual("end: week", self.wake(broken)[-1])

    def test_failures_keep_a_count_and_the_last_reason_not_a_list(self):
        for n in range(5):
            self.clock = NOW + timedelta(minutes=n)
            self.usage(lambda n=n: (_ for _ in ()).throw(OSError(f"broken {n}")))
        usage = dr.load_state(state_dir())["usage"]
        self.assertEqual({"count": 5, "at": (NOW + timedelta(minutes=4)).isoformat(), "why": "broken 4"},
                         usage["failed"])
        self.assertNotIn("failures", usage)

    def test_the_next_wake_reads_again_after_a_failure(self):
        calls = []

        def flaky():
            calls.append(1)
            if len(calls) == 1:
                raise ValueError("timed out")
            return {"percentUsed": 10, "resetsAt": self.RESET}
        first = self.wake(flaky)
        self.assertIn("weekly: reading failed: timed out", first)
        second = self.wake(flaky)
        self.assertEqual(2, len(calls))
        self.assertNotIn("weekly: reading failed: timed out", second)
        self.assertEqual(90.0, dr.load_state(state_dir())["usage"]["start"]["percent_left"])
        self.assertEqual(1, dr.load_state(state_dir())["usage"]["failed"]["count"])

    def test_one_percent_left_ends_the_night_and_two_does_not(self):
        stamp = datetime(2026, 11, 6, tzinfo=timezone.utc)
        self.assertTrue(dr.week_ends_night(dr.WeeklyReading(1.0, stamp)))
        self.assertTrue(dr.week_ends_night(dr.WeeklyReading(0.0, stamp)))
        self.assertFalse(dr.week_ends_night(dr.WeeklyReading(2.0, stamp)))
        self.assertEqual("end: week", self.wake(lambda: {"percentUsed": 99, "resetsAt": self.RESET})[-1])
        self.assertNotEqual("end: week", self.wake(lambda: {"percentUsed": 98, "resetsAt": self.RESET})[-1])

    def test_the_reading_maps_percent_used_to_percent_left(self):
        reading = dr.read_weekly(lambda: {"percentUsed": 37.5, "resetsAt": self.RESET})
        self.assertEqual(62.5, reading.percent_left)
        self.assertEqual(datetime.fromisoformat(self.RESET), reading.resets_at)
        for bad in ({}, {"percentUsed": "most", "resetsAt": self.RESET}, {"percentUsed": 5, "resetsAt": "soon"},
                    {"percentUsed": 140, "resetsAt": self.RESET}, [1]):
            with self.assertRaises(dr.ReadingFailed, msg=bad):
                dr.read_weekly(lambda bad=bad: bad)

    def test_usage_records_start_once_and_end_on_every_later_reading(self):
        self.assertEqual(f"weekly: 60% left, resets {self.RESET}\n",
                         self.usage(lambda: {"percentUsed": 40, "resetsAt": self.RESET}))
        self.clock = NOW + timedelta(hours=3)
        self.usage(lambda: {"percentUsed": 55, "resetsAt": self.RESET})
        self.clock = NOW + timedelta(hours=6)
        self.usage(lambda: {"percentUsed": 70, "resetsAt": self.RESET})
        recorded = dr.load_state(state_dir())["usage"]
        self.assertEqual((60.0, 30.0), (recorded["start"]["percent_left"], recorded["end"]["percent_left"]))
        self.assertEqual(NOW.isoformat(), recorded["start"]["at"])


class ReportTests(InRoot):
    # The morning report's facts, in English, for the dispatcher to write up in --lang (spec section 10).
    SECTIONS = ["## Merged", "## Needs a human", "## Interrupted", "## Filed overnight", "## Left in the backlog",
                "## Usage", "## Syncs"]

    def report(self, text=FIXTURE):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(text)
        return run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-01"], body_source=lambda n: "",
                       open_source=lambda: {1440}, branch_source=NO_BRANCH).splitlines()

    def section(self, lines, heading):
        start = lines.index(heading) + 1
        rest = [i for i, text in enumerate(lines[start:], start) if text.startswith("## ")]
        return lines[start:rest[0] if rest else len(lines)]

    def test_the_report_has_the_seven_sections_in_order(self):
        record(RUN, "S1", status="done", closed_from="verified", pr=1690, merge_oid=OID, rounds=MERGED_S1["rounds"],
               review_end="clean", filed=[1701], usage=SESSION_USAGE, usage_sources=["modelUsage"], cost_usd=0.787,
               usage_models={"claude-sonnet-5-5": {"input_tokens": 44, "output_tokens": 4906},
                             "claude-opus-5-5": {"input_tokens": 14, "output_tokens": 3868}})
        record(RUN, "M2", status="running")
        lines = self.report()
        self.assertEqual(self.SECTIONS, [text for text in lines if text.startswith("## ")])
        merged = self.section(lines, "## Merged")
        self.assertEqual(1, len(merged), merged)
        for fact in ("S1", "#1690", OID, "#1682", "#1683", "full raised 5 fixed 5 refused 0",
                     "recheck raised 2 fixed 1 refused 1", "review clean", "filed #1701"):
            self.assertIn(fact, merged[0])
        self.assertEqual(["none"], self.section(lines, "## Needs a human"))
        # FIXTURE's In progress holds none of M2's rows, so nothing keeps the run open for a resume line.
        self.assertEqual(["M2 running"], self.section(lines, "## Interrupted"))
        self.assertEqual(["#1701"], self.section(lines, "## Filed overnight"))
        self.assertIn("J1 wave 2: waits until 2026-11-09", self.section(lines, "## Left in the backlog"))
        usage = self.section(lines, "## Usage")
        self.assertIn("S1: 1662280 tokens (input 58, cache write 108512, cache read 1544936, output 8774), $0.79, "
                      "from modelUsage", usage)
        self.assertIn("S1 claude-sonnet-5-5: 4950 tokens (input 44, cache write 0, cache read 0, output 4906)", usage)
        self.assertIn("S1 claude-opus-5-5: 3882 tokens (input 14, cache write 0, cache read 0, output 3868)", usage)

    def test_each_usage_row_says_which_field_it_came_from(self):
        full = "from modelUsage"
        last = "from usage, the last turn alone"
        for sources, text in ((["modelUsage"], full), (["usage"], last), (None, last), ("garbled", last),
                              ([[], {}], last), ([["modelUsage"], "modelUsage"], full),
                              (["usage", "modelUsage"], f"{full}, and {last}")):
            with self.subTest(sources=sources):
                record(RUN, "S1", status="running", usage={"input_tokens": 20}, usage_sources=sources)
                row = self.section(self.report(), "## Usage")[0]
                self.assertEqual(f"S1: 20 tokens (input 20, cache write 0, cache read 0, output 0), {text}", row)

    def test_one_model_gets_no_line_of_its_own_and_a_garbled_cost_is_not_printed(self):
        record(RUN, "S1", status="running", usage={"input_tokens": 20}, usage_sources=["modelUsage"], cost_usd="x",
               usage_models={"claude-opus-5-5": {"input_tokens": 20}})
        self.assertEqual(["S1: 20 tokens (input 20, cache write 0, cache read 0, output 0), from modelUsage"],
                         self.section(self.report(), "## Usage")[:-1])

    def test_what_the_interview_files_or_closes_is_filed_overnight(self):
        # A close comment is a comment on an issue, so it counts as the spec's filed or commented on.
        record(RUN, "S1", status="done", closed_from="verified", pr=1690, merge_oid=OID,
               rounds=MERGED_S1["rounds"], review_end="clean", filed=[1701])
        for issue in ("1705", "1706", "1705"):
            self.assertEqual("", run_cli(["filed", RUN, issue]))
        self.assertEqual([1705, 1706], dr.load_state(state_dir())["placed"])
        self.assertEqual(["#1701, #1705, #1706"], self.section(self.report(), "## Filed overnight"))
        for bad in ("0", "-3", "#1705", "\u0661\u0662"):
            with self.subTest(issue=bad):
                self.assertIn(f"refused: {bad!r} is not an issue number", run_cli(["filed", RUN, bad]))
        self.assertIn("is missing; the run was never started", run_cli(["filed", "20261101-230000", "1707"]))

    def test_an_unread_week_is_said_so(self):
        dr.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        self.assertIn("weekly: not read", self.section(self.report(), "## Usage"))
        dr.take_reading(state_dir(), None)
        self.assertIn("weekly: not read", self.section(self.report(), "## Usage"))
        dr.take_reading(state_dir(), lambda: {"percentUsed": "x"})
        usage = self.section(self.report(), "## Usage")
        self.assertIn("weekly: not read", usage)
        self.assertTrue(any(text.startswith(f"weekly: reading failed, 1 in all, the last at {NOW.isoformat()}: ")
                            for text in usage), usage)

    def test_each_hand_back_names_its_reason_and_its_worktree(self):
        record(RUN, "S1", status="returned", closed_from="handed-back",
               reason="needs a provider key only the owner has")
        record(RUN, "M1", status="crash-hand-back", reason="a second crash: no result; exit 1")
        os.rmdir(".claude/worktrees/dispatch-m1")
        state = dr.load_state(state_dir())
        state["placed"] = [1702]
        dr.save_state(state_dir(), state)
        waiting = line(depends="OWNER", number=1702, gid="Z9", wave=3)
        needs = self.section(self.report(tasks_of(*REMAINING_ROWS.splitlines(), waiting)), "## Needs a human")
        self.assertIn("S1 handed-back: needs a provider key only the owner has; "
                      "worktree .claude/worktrees/dispatch-s1 left in place", needs)
        self.assertIn("M1 crash-hand-back: a second crash: no result; exit 1; "
                      "worktree .claude/worktrees/dispatch-m1 removed", needs)
        self.assertIn("#1702: filed overnight, waits on a human", needs)


    def test_each_executors_decisions_are_shown_on_its_line(self):
        # The morning report shows the decisions in place of the report nobody read (docs/commands/ship.md).
        record(RUN, "S1", status="done", closed_from="verified", pr=1690, merge_oid=OID, rounds=[], review_end=None,
               decisions=["kept the narrower grant", "split the test\n## Merged forged"])
        record(RUN, "M1", status="returned", closed_from="handed-back", reason="needs a key",
               decisions=["left the provider alone"])
        lines = self.report()
        self.assertTrue(self.section(lines, "## Merged")[0].endswith(
            "; decisions: kept the narrower grant / split the test ## Merged forged"), lines)
        needs = self.section(lines, "## Needs a human")
        self.assertTrue(any(text.endswith("left in place; decisions: left the provider alone") for text in needs),
                        needs)
        self.assertEqual(1, lines.count("## Merged"))

    def test_each_merge_line_names_its_pr_title_on_one_line(self):
        # The title is the executor's text, so a line break in it must not forge a report line.
        title = "Narrow the grants\n## Merged forged"
        record(RUN, "S1", status="done", closed_from="verified", pr=1690, title=title, merge_oid=OID,
               rounds=MERGED_S1["rounds"], review_end="clean", filed=[])
        lines = self.report()
        merged = self.section(lines, "## Merged")
        self.assertTrue(merged[0].startswith(f'S1 #1690 "Narrow the grants ## Merged forged" merged as {OID}: '),
                        merged)
        self.assertEqual(1, lines.count("## Merged"))
        record(RUN, "S1", status="verified", pr=1690, title=title, merge_oid=OID, rounds=[], review_end=None,
               unreviewed=True)
        self.assertTrue(self.section(self.report(), "## Needs a human")[0].startswith(
            f'S1 #1690 "Narrow the grants ## Merged forged" merged as {OID}, merged with no review round'))

    def test_a_merge_main_does_not_hold_yet_is_not_reported_as_merged(self):
        # verify passed, then the pull or the containment read failed, so `tasks done` never ran.
        record(RUN, "S1", status="verified", pr=1690, merge_oid=OID, rounds=MERGED_S1["rounds"], review_end="clean")
        lines = self.report()
        self.assertEqual(["none"], self.section(lines, "## Merged"))
        self.assertIn(f"S1 #1690 merged as {OID}, not confirmed in main: its rows are still In progress and "
                      "worktree .claude/worktrees/dispatch-s1 is left in place",
                      self.section(lines, "## Needs a human"))

    def test_a_merge_nobody_reviewed_lands_but_is_not_reported_as_merged(self):
        # `result` flags a merge with no round from a session that cannot have reviewed it earlier.
        record(RUN, "S1", status="done", closed_from="verified", pr=1690, merge_oid=OID, rounds=[], review_end=None,
               filed=[1701], unreviewed=True)
        lines = self.report()
        self.assertEqual(["none"], self.section(lines, "## Merged"))
        self.assertIn(f"S1 #1690 merged with no review round, as {OID}: #1682, #1683; filed #1701",
                      self.section(lines, "## Needs a human"))
        record(RUN, "S1", status="verified", pr=1690, merge_oid=OID, rounds=[], review_end=None, unreviewed=True)
        self.assertTrue(self.section(self.report(), "## Needs a human")[0].startswith(
            f"S1 #1690 merged as {OID}, merged with no review round, not confirmed in main"))

    def test_a_merge_its_transcript_cleared_says_where_its_round_was_seen(self):
        # It reported no round, so "review not run" would contradict the evidence that put it under Merged.
        record(RUN, "S1", status="done", closed_from="verified", pr=1690, merge_oid=OID, rounds=[], review_end=None,
               filed=[1701], unreviewed=False, review_seen="transcript")
        self.assertEqual([f"S1 #1690 merged as {OID}: #1682, #1683; no rounds reported; a branch-reviewer round in "
                          "its transcript; filed #1701"], self.section(self.report(), "## Merged"))


class TranscriptTests(unittest.TestCase):
    def test_the_slug_turns_every_other_character_into_a_dash(self):
        self.assertEqual("C--dev-ashamray-blueprint-backend--claude-worktrees-dispatch",
                         dr.project_slug(r"C:\dev\ashamray\blueprint-backend\.claude\worktrees\dispatch"))

    def test_a_session_id_names_its_file_and_none_takes_the_newest_since_the_launch(self):
        with tempfile.TemporaryDirectory() as tmp:
            projects, worktree = Path(tmp) / "projects", os.path.join(tmp, "wt")
            folder = projects / dr.project_slug(os.path.abspath(worktree))
            with mock.patch.object(dr, "PROJECTS", projects):
                self.assertEqual(str(folder / "43b7cd26-0682.jsonl"),
                                 dr.transcript_path(worktree, "43b7cd26-0682", 0))
                self.assertIsNone(dr.transcript_path(worktree, "../../x", 0))
                self.assertIsNone(dr.transcript_path(worktree, None, 0))
                folder.mkdir(parents=True)
                created = {"old.jsonl": 100.0, "new.jsonl": 205.0, "newer.txt": 300.0}
                for name in created:
                    (folder / name).write_text("{}\n", encoding="utf-8")
                with mock.patch.object(dr, "_created", lambda path: created[Path(path).name]):
                    self.assertEqual(str(folder / "new.jsonl"), dr.transcript_path(worktree, None, 200.0))
                    self.assertIsNone(dr.transcript_path(worktree, None, 250.0))


class StrangersIssues(InRoot):
    """`wake`, `launch` and `report` over a row the repository owner did not open, each through its command line."""

    def wake(self, *flags, owned=frozenset()):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(tasks_of(line(depends="OWNER" if flags else "none")))
        run_cli(["start", "--pairs", "2", "--tasks", "todo.md", *flags])
        name = dr.open_run(".", "todo.md")[0]
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            return run_cli(["wake", name, "--tasks", "todo.md"], body_source=lambda n: "", open_source=set,
                           branch_source=NO_BRANCH, owned_source=lambda: owned).splitlines()

    def test_wake_launches_none_of_them_and_the_owners_as_ever(self):
        self.assertEqual("end: drained", self.wake()[-1])
        shutil.rmtree("artifacts")
        self.assertEqual("launch: Z1", self.wake(owned=frozenset({1700}))[-1])

    def test_wake_under_berserk_launches_none_of_them(self):
        self.assertEqual("end: drained", self.wake("--berserk")[-1])
        shutil.rmtree("artifacts")
        self.assertEqual("launch: Z1", self.wake("--berserk", owned=frozenset({1700}))[-1])

    def test_a_failed_read_of_the_authors_launches_nothing(self):
        def failed():
            raise db.Refusal("gh-issue-owned.sh exited 3")
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(tasks_of(line()))
        run_cli(["start", "--pairs", "2", "--tasks", "todo.md"])
        name = dr.open_run(".", "todo.md")[0]
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            printed = run_cli(["wake", name, "--tasks", "todo.md"], body_source=lambda n: "", open_source=set,
                              branch_source=NO_BRANCH, owned_source=failed)
        self.assertIn("refused: gh-issue-owned.sh exited 3", printed)
        self.assertNotIn("launch:", printed)

    def test_launch_refuses_a_group_wake_never_named(self):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        dr.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        self.assertEqual("", run_cli(["tasks", "move", "--tasks", "todo.md", "--run", RUN, "S1",
                                      "--branch", BRANCH, "--worktree", WORKTREE]))
        make_worktree(WORKTREE)
        porcelain = f"worktree {self.root / WORKTREE}\nHEAD {HEAD}\nbranch refs/heads/{BRANCH}\n\n"
        argv = ["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE]
        with mock.patch.object(dr, "git", lambda root, *args: porcelain):
            refused = run_cli(argv, owned_source=lambda: frozenset({1682}))
            self.assertIn(f"refused: S1 is not launched: {db.NOT_OWNERS}: #1683", refused)
            self.assertNotIn("status", dr.load_state(state_dir())["groups"]["S1"])
            self.assertEqual("", run_cli(argv, owned_source=lambda: frozenset({1682, 1683})))

    def test_the_morning_report_names_what_it_left(self):
        dr.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(tasks_of(line()))
        lines = run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-01"], body_source=lambda n: "",
                        open_source=set, owned_source=frozenset).splitlines()
        self.assertIn(f"Z1 wave 1: {db.NOT_OWNERS}: #1700", lines)


class TaskTextIsAFile(InRoot):
    """The helper reads what a shell would otherwise have expanded on the command line."""

    def run_group(self):
        Path("fake").mkdir(exist_ok=True)
        (Path("fake") / "claude.py").write_text(FAKE_CLAUDE, encoding="utf-8")
        (Path("fake") / "reply.bin").write_bytes(b"{}")
        (Path("fake") / "code.txt").write_text("0", encoding="utf-8")
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        with mock.patch.object(dr, "CLAUDE_COMMAND", [sys.executable, str(Path("fake/claude.py").resolve())]):
            return run_cli(["run", RUN, "S1"])

    def test_a_backtick_in_the_text_arrives_as_text(self):
        make_worktree(WORKTREE)
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        task_text("S1", "Read `guard.py` and $(id)")
        self.assertEqual("", self.run_group())
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertIn("Read `guard.py` and $(id) --dispatched", seen["stdin"])

    def test_a_group_with_no_file_starts_nothing(self):
        make_worktree(WORKTREE)
        self.assertIn("refused: could not read S1's task text", self.run_group())
        self.assertFalse(Path("fake/seen.json").exists())

    def test_the_text_is_held_to_what_the_flag_was(self):
        make_worktree(WORKTREE)
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
        for text, why in (("two\nlines", "line break"), ("", "is empty"), ("go --berserk", "only the dispatcher")):
            with self.subTest(text=text):
                task_text("S1", text)
                self.assertIn(why, self.run_group())

    def test_the_flag_is_gone(self):
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            db.main(["run", RUN, "S1", "--description", "x"])


if __name__ == "__main__":
    unittest.main()
