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
from test_dispatch_board import FIXTURE, NO_BRANCH, REMAINING_ROWS, line, stub, tasks_of

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
# The spike's envelope keys (docs/superpowers/plans/2026-11-01-dispatch-spike.md); its values trimmed.
SUCCESS = {"type": "result", "subtype": "success", "is_error": False, "api_error_status": None,
           "stop_reason": "end_turn", "terminal_reason": "completed", "num_turns": 9,
           "session_id": "43b7cd26-0682-471a-a3e5-98c841a5c448", "total_cost_usd": 1.886, "usage": USAGE,
           "permission_denials": [], "result": "PR #1690 is merged; the rate limit docs were not touched."}
# No limit was observed in the spike, so these are built from the envelope's structured fields and the SDK's
# rate_limit error kind (ruling R9); they await the first real limit. The result names a later reset, read only
# where no field names one.
LIMIT_429 = {**SUCCESS, "subtype": "error_during_execution", "is_error": True, "api_error_status": 429,
             "terminal_reason": "error", "resetsAt": "2026-11-02T01:30:00Z",
             "result": "Claude usage limit reached. Your limit resets at 2026-11-02T03:00:00Z."}
LIMIT_KIND = {**SUCCESS, "subtype": "error_during_execution", "is_error": True, "api_error_status": None,
              "error": {"type": "rate_limit"}, "result": "API Error"}
FAKE_CLAUDE = """import json, os, sys
here = os.path.dirname(os.path.abspath(__file__))
seen = {"argv": sys.argv[1:], "cwd": os.getcwd(), "stdin": sys.stdin.buffer.read().decode("utf-8")}
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
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
        db.main(argv, **sources)
    return out.getvalue()


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
        assert db.main(["run", RUN, "S1", "--description", description]) == 0
    return json.loads((state_dir() / "S1.session.json").read_text(encoding="utf-8"))


def finished_pid():
    child = subprocess.Popen([sys.executable, "-c", "pass"])
    child.wait()
    return child.pid


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
            self.assertIn(f"refused: {stale}", run_cli(["run", RUN, "S1", "--description", "Narrow the grants"]))
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
        raw = run_fake_claude(stdout=json.dumps(SUCCESS).encode("utf-8"))
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

    def test_run_refuses_a_group_with_no_claude_or_already_handed_back(self):
        record(RUN, "S1", status="running")
        with mock.patch.object(dr, "CLAUDE_COMMAND", None), mock.patch.object(dr, "path_which", lambda _: None):
            self.assertIn("refused: no claude on PATH", run_cli(["run", RUN, "S1", "--description", "x"]))
        record(RUN, "S1", status="handed-back")
        self.assertIn("refused: S1 is handed-back, not waiting for an executor",
                      run_cli(["run", RUN, "S1", "--description", "x"]))


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

    def test_the_envelope_is_read_by_its_structured_fields_first(self):
        ok = dr.session_end({"exit": 0, "stdout": json.dumps(SUCCESS), "stderr": ""})
        self.assertEqual((False, False, None), (ok.is_error, ok.rate_limited, ok.resets_at))
        self.assertEqual({k: USAGE[k] for k in dr.TOKEN_KEYS}, ok.usage)
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
    def absorb(self, data, envelope=SUCCESS, code=0, crashes=0, **fields):
        record(RUN, "S1", **{"status": "running", "branch": BRANCH, "worktree": WORKTREE, "crashes": crashes,
                             **fields})
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

    def test_usage_sums_every_session_the_group_ran(self):
        earlier = {"input_tokens": 100, "cache_read_input_tokens": 1000, "output_tokens": 50}
        self.absorb(MERGED_S1, status="relaunch", absorbed="2026-11-01T20:00:00+00:00", usage=earlier)
        spent = group_state(RUN, "S1")["usage"]
        self.assertEqual({key: earlier.get(key, 0) + USAGE[key] for key in dr.TOKEN_KEYS}, spent)

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
            self.assertEqual(2, db.main(["run", RUN, "S1", "--description", "Narrow the grants"]))
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


class WakeTests(InRoot):
    BODIES = {1682: "touches definitions.json", 1683: "deploy/rabbitmq/definitions.json",
              1681: "src/gateway/program.cs", 1685: "a.cs", 1686: "b.cs", 1440: "docs/x.md"}

    def wake(self, text=FIXTURE, pairs=2):
        if not (state_dir() / "state.json").is_file():
            dr.save_state(state_dir(), {"run": RUN, "pairs": pairs, "lang": "en", "groups": {}})
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(text)
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            return run_cli(["wake", RUN, "--tasks", "todo.md", "--today", "2026-11-01"],
                           body_source=lambda n: self.BODIES.get(n, ""), open_source=lambda: {1440},
                           branch_source=NO_BRANCH).splitlines()

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
        self.assertEqual({"end: drained", "end: held", "end: week", "end: stop-file"}, endings)
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
        with mock.patch.object(dr, "CLAUDE_COMMAND", [sys.executable, "-c", "print('{}')"]):
            self.assertEqual("", run_cli(["run", RUN, "S9", "--description", "Resume after the week"]))
        self.assertTrue((state_dir() / "S9.session.json").is_file())
        self.assertIn("S9: relaunch - no result; exit 0", run_cli(["result", RUN, "S9"]))

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
        with mock.patch.object(db.subprocess, "run", side_effect=subprocess.TimeoutExpired(["gh"], 120)):
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
            self.assertEqual(2, db.main(["run", RUN, "S1", "--description", "Narrow the grants"]))
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
            self.assertEqual(2, db.main(["run", RUN, "S1", "--description", "Narrow the grants"]))
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
                with mock.patch.object(dr, "CLAUDE_COMMAND", claude), patch:
                    self.assertIn(f"refused: {why}", run_cli(["run", RUN, "S1", "--description", "x"]))
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
               review_end="clean", filed=[1701], usage={key: USAGE[key] for key in dr.TOKEN_KEYS})
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
        self.assertIn("S1: 762696 tokens (input 20, cache write 5265, cache read 754597, output 2814)",
                      self.section(lines, "## Usage"))

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


if __name__ == "__main__":
    unittest.main()
