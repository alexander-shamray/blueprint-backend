"""/dispatch takes a sync pull request: first, alone, bound to the head it proved, and on no task-list row.

The bind is git's to answer, so the cases that reach it run a real repository with a real linked worktree; the
executor is a fake `claude`, as in `test_dispatch_run.py`.
"""

import contextlib
import io
import json
import os
import re
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

import dispatch_board as db
import dispatch_run as dr
from test_dispatch_board import FIXTURE, NO_BRANCH, line, stub, tasks_of
from test_dispatch_run import (FAKE_CLAUDE, NOW, OID, RUN, SUCCESS, InRoot, finished_pid, group_state, record,
                               run_cli, state_dir)

ROOT = Path(__file__).resolve().parents[2]
DISPATCH = ROOT / ".claude" / "commands" / "dispatch.md"
ARGUMENT = ROOT / "docs" / "commands" / "dispatch.md"
SHIP = ROOT / ".claude" / "commands" / "ship.md"

PR = 5
GID = f"sync-{PR}"
BRANCH = "chore/harness-sync-abcdef0"
WORKTREE = ".claude/worktrees/sync-abcdef0"
SYNC_MERGED = {"group": GID, "issues": [], "outcome": "merged", "pr": PR, "title": None, "merge_oid": OID,
               "rounds": [{"kind": "full", "raised": 1, "fixed": 0, "refused": 1}], "review_end": "clean",
               "filed": [], "reason": None, "workspace": {"worktree": WORKTREE, "removed": False},
               "decisions": ["kept the target's own note"]}
GONE = "its worktree is gone, so how its executor ended is unknown"
SYNC_CLOSED = {**SYNC_MERGED, "outcome": "stopped", "pr": None, "merge_oid": None, "rounds": [], "review_end": None,
               "reason": "closed unmerged", "decisions": []}


def flat(text):
    # Backticks and line breaks are layout, not wording: a rewrap must not fail a case.
    return " ".join(text.replace("`", "").split())


def git_in(cwd, *args):
    done = subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", *args], cwd=cwd, capture_output=True,
                          encoding="utf-8", check=True, timeout=60)
    return done.stdout.strip()


def sync_state(run=RUN, pairs=2):
    if not (state_dir(run) / "state.json").is_file():
        dr.save_state(state_dir(run), {"run": run, "pairs": pairs, "lang": "en", "groups": {}})


def record_sync(pr=PR, **fields):
    """A sync group as `sync-launch` leaves it, with `fields` over it."""
    sync_state()
    entry = {"kind": "sync", "keys": [], "issues": [], "pr": pr, "model": "Sonnet", "branch": BRANCH,
             "worktree": WORKTREE, "oid": OID, "launched": NOW.isoformat(), "crashes": 0, "status": "running"}
    entry.update(fields)
    os.makedirs(entry["worktree"], exist_ok=True)
    state = dr.load_state(state_dir())
    state["groups"][f"sync-{pr}"] = entry
    dr.save_state(state_dir(), state)


def resume_lines():
    with mock.patch.object(dr, "alive", lambda pid: False):
        return [text for text in run_cli(["resume", RUN]).splitlines() if text]


class SyncPrompt(unittest.TestCase):

    def test_sync_prompt_reads_back_as_ships_sync_argument(self):
        text = dr.sync_prompt(PR, RUN)
        self.assertEqual(f"/ship sync #5 --dispatched artifacts/dispatch/{RUN}", text)
        # The two tokens ship.md's own argument hint names, in its order, and what its Report reads the group from.
        hint = re.search(r'^argument-hint: "(.*)"$', SHIP.read_text(encoding="utf-8"), re.MULTILINE).group(1)
        self.assertIn("sync #<n>", hint)
        self.assertIn("--dispatched <run-dir>", hint)
        self.assertLess(hint.index("sync #<n>"), hint.index("--dispatched <run-dir>"))
        matched = re.fullmatch(r"/ship sync #(\d+) --dispatched (artifacts/dispatch/\d{8}-\d{6})", text)
        self.assertEqual((str(PR), f"artifacts/dispatch/{RUN}"), matched.groups())
        self.assertIn("or sync-<n> for sync #<n>", flat(SHIP.read_text(encoding="utf-8")))

    def test_sync_prompt_refuses_what_no_pull_request_or_run_is(self):
        with self.assertRaisesRegex(db.Refusal, "is not a pull request number"):
            dr.sync_prompt(0, RUN)
        with self.assertRaisesRegex(db.Refusal, "is not a pull request number"):
            dr.sync_prompt(True, RUN)
        with self.assertRaisesRegex(db.Refusal, "is not a yyyymmdd-hhmmss timestamp"):
            dr.sync_prompt(PR, "../x")


class GroupIds(InRoot):

    def test_only_a_syncs_group_is_new_and_no_task_list_row_may_hold_one(self):
        record_sync()
        self.assertIn("sync-5 is running", run_cli(["sync-close", RUN, GID]))
        for bad in ("sync-0", "sync-05", "Sync-5", "sync-", "sync-5-6", "sync-5/../x", "SYNC-5", "sync-٣", "sync-1٣"):
            with self.subTest(bad=bad):
                self.assertIn(f"refused: {bad!r} is not a group id", run_cli(["result", RUN, bad]))
        for action in (["move", "--branch", "b"], ["done"], ["back"]):
            with self.subTest(action=action[0]):
                argv = ["tasks", action[0], "--tasks", "todo.md", "--run", RUN, GID, *action[1:]]
                self.assertIn(f"refused: {GID!r} is not a group id", run_cli(argv))
        self.assertIn(f"refused: {GID!r} is not a group id",
                      run_cli(["tasks", "place", "--tasks", "todo.md", "--run", RUN, "1", GID, "1702"]))
        with self.assertRaisesRegex(db.Refusal, "has Group 'sync-5', not a group id or a dash"):
            db.parse_tasks(FIXTURE.replace("| — | owed (not filed) |", f"| {GID} | owed (not filed) |"))

    def test_a_conflicts_cell_names_no_sync(self):
        row = line(conflicts="sync-5 and Z2")
        self.assertEqual({"Z2"}, set(db.ROW_GROUP.findall(row.split("|")[8])))
        claim = db.group_claim(db.groups(db.parse_tasks(tasks_of(row)))[0], {})
        self.assertEqual({"Z2"}, set(claim.groups))


class Bound(InRoot):
    """A main checkout with a real worktree on a sync branch one commit ahead of main."""

    def setUp(self):
        super().setUp()
        git_in(".", "init", "-q", "-b", "main")
        # What the executor leaves under artifacts/ is ignored in a target, so it never reads as a dirty tree.
        Path(".gitignore").write_text("artifacts/\n", encoding="utf-8")
        git_in(".", "add", ".gitignore")
        git_in(".", "commit", "-q", "-m", "base")
        git_in(".", "worktree", "add", "-q", "-b", BRANCH, WORKTREE)
        self.tree = Path(WORKTREE)
        (self.tree / "g").write_text("sync\n", encoding="utf-8")
        git_in(self.tree, "add", "g")
        git_in(self.tree, "commit", "-q", "-m", "sync")
        self.oid = git_in(self.tree, "rev-parse", "HEAD")
        sync_state()

    def sync_launch(self, pr=PR, branch=BRANCH, worktree=WORKTREE, oid=None):
        return run_cli(["sync-launch", RUN, str(pr), "--branch", branch, "--worktree", worktree,
                        "--oid", oid or self.oid])

    def fake(self, stdout=None, code=0):
        fake = Path("fake")
        fake.mkdir(exist_ok=True)
        (fake / "claude.py").write_text(FAKE_CLAUDE, encoding="utf-8")
        (fake / "reply.bin").write_bytes(json.dumps(SUCCESS).encode("utf-8") if stdout is None else stdout)
        (fake / "code.txt").write_text(str(code), encoding="utf-8")
        return mock.patch.object(dr, "CLAUDE_COMMAND", [sys.executable, str((fake / "claude.py").resolve())])

    def run_group(self, *extra, gid=GID):
        with self.fake():
            return run_cli(["run", RUN, gid, *extra])

    def write_result(self, result, gid=GID):
        directory = Path(group_state(RUN, gid)["worktree"]) / "artifacts" / "dispatch" / RUN
        directory.mkdir(parents=True, exist_ok=True)
        (directory / f"{gid}.json").write_text(json.dumps(result), encoding="utf-8")


class SyncLaunch(Bound):

    def test_sync_launch_writes_no_task_list(self):
        self.assertEqual("", self.sync_launch())
        self.assertFalse(Path("todo.md").exists())
        entry = group_state(RUN, GID)
        self.assertEqual(("sync", [], [], PR, "Sonnet", BRANCH, WORKTREE, self.oid, "running", 0),
                         (entry["kind"], entry["issues"], entry["keys"], entry["pr"], entry["model"], entry["branch"],
                          entry["worktree"], entry["oid"], entry["status"], entry["crashes"]))
        # `tasks done` and `back`, and wake's running groups, read `rows`: a sync holding none reaches none of them.
        self.assertNotIn("rows", entry)
        self.assertEqual("sonnet", dr.model_flag(entry["model"]))
        out = io.StringIO()
        with contextlib.redirect_stderr(out), self.assertRaises(SystemExit):
            db.main(["sync-launch", RUN, str(PR), "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE,
                     "--oid", self.oid])
        self.assertIn("unrecognized arguments: --tasks", out.getvalue())

    def refused(self, why, **given):
        before = (state_dir() / "state.json").read_bytes()
        self.assertIn(f"refused: {why}", self.sync_launch(**given))
        self.assertEqual(before, (state_dir() / "state.json").read_bytes())

    def test_a_head_other_than_the_proved_one_launches_nothing(self):
        other = git_in(".", "rev-parse", "main")
        self.refused(f"{WORKTREE} is at {self.oid}, not the proved {other}", oid=other)

    def test_a_tree_that_is_not_clean_launches_nothing(self):
        (self.tree / "g").write_text("changed\n", encoding="utf-8")
        self.refused(f"{WORKTREE} is not clean: M g")
        git_in(self.tree, "checkout", "--", "g")
        (self.tree / "planted.py").write_text("pass\n", encoding="utf-8")
        self.refused(f"{WORKTREE} is not clean: ?? planted.py")

    def test_an_untracked_file_is_not_clean_whatever_status_shows(self):
        # status.showUntrackedFiles=no, from any config level, would read the planted module as a clean tree.
        git_in(self.tree, "config", "status.showUntrackedFiles", "no")
        (self.tree / "deep").mkdir()
        (self.tree / "deep" / "planted.py").write_text("pass\n", encoding="utf-8")
        self.assertEqual("", git_in(self.tree, "status", "--porcelain"))
        self.refused(f"{WORKTREE} is not clean: ?? deep/planted.py")

    def test_a_tree_that_is_not_a_worktree_on_the_head_launches_nothing(self):
        self.refused(f"{WORKTREE} is not a worktree on chore/harness-sync-1234567",
                     branch="chore/harness-sync-1234567")
        os.makedirs(".claude/worktrees/other")
        self.refused(f".claude/worktrees/other is not a worktree on {BRANCH}", worktree=".claude/worktrees/other")

    def test_what_is_not_a_head_or_a_whole_oid_is_refused(self):
        for bad in (self.oid[:7], self.oid.upper(), self.oid + "0", "HEAD"):
            with self.subTest(oid=bad):
                self.refused(f"{bad!r} is not a whole oid", oid=bad)
        self.refused("'fix/other' is not a sync head", branch="fix/other")
        self.refused("0 is not a pull request number", pr=0)

    def test_a_sync_is_launched_alone(self):
        record(RUN, "S1", status="running")
        self.refused("S1 is running; a sync is launched alone")
        record(RUN, "S1", status="window")
        self.refused("S1 is window; a sync is launched alone")
        record(RUN, "S1", status="done")
        self.assertEqual("", self.sync_launch())

    def test_a_sync_is_not_launched_beside_a_claimed_merge(self):
        record_sync(pr=6, status="merged-claimed", branch="chore/harness-sync-1234567")
        self.refused("sync-6's merge is claimed and not verified; a sync is launched alone")

    def test_the_stop_file_launches_no_sync(self):
        (state_dir() / "STOP").write_text("", encoding="utf-8")
        self.refused("stop file: the owner ended the night, so nothing more launches")

    def test_an_earlier_runs_live_executor_holds_its_sync(self):
        # Its empty keys let that run's report close it, and the head is still open, so a later night would take it.
        earlier = state_dir("20261031-220000")
        earlier.mkdir(parents=True)
        pid = earlier / f"{GID}.pid"
        pid.write_text(f"{os.getpid()}\n", encoding="utf-8")
        self.refused(f"{GID}'s executor from run 20261031-220000 is still running")
        pid.write_text(f"{finished_pid()}\n", encoding="utf-8")
        self.assertEqual("", self.sync_launch())

    def test_another_syncs_live_executor_does_not_hold_this_one(self):
        earlier = state_dir("20261031-220000")
        earlier.mkdir(parents=True)
        (earlier / "sync-6.pid").write_text(f"{os.getpid()}\n", encoding="utf-8")
        self.assertEqual("", self.sync_launch())

    def test_a_sync_this_run_holds_is_never_launched_again(self):
        record_sync(status="closed", closed_from="handed-back", worktree=WORKTREE)
        self.refused(f"{GID} is already in this run; a new take is a new group")


class SyncReady(Bound):
    """What refuses a launch whatever the tree holds, asked before the worktree exists, so a wait leaves none."""

    def ready(self, pr=PR, **sources):
        return run_cli(["sync-ready", RUN, str(pr)], **sources)

    def test_nothing_in_the_way_says_nothing_and_records_nothing(self):
        before = dr.load_state(state_dir())
        self.assertEqual("", self.ready())
        self.assertEqual(before, dr.load_state(state_dir()))

    def test_it_refuses_what_sync_launch_refuses_before_the_tree(self):
        record(RUN, "S1", status="running")
        self.assertIn("refused: S1 is running; a sync is launched alone", self.ready())
        self.assertIn("refused: S1 is running; a sync is launched alone", self.sync_launch())
        record(RUN, "S1", status="done")
        record_sync(pr=6, status="merged-claimed", branch="chore/harness-sync-1234567")
        self.assertIn("refused: sync-6's merge is claimed and not verified", self.ready())
        record_sync(pr=6, status="closed")
        self.assertIn("refused: sync-6 is already in this run", self.ready(6))
        (state_dir() / "STOP").write_text("", encoding="utf-8")
        self.assertIn("refused: stop file", self.ready())

    def test_a_red_main_holds_a_sync_before_it_has_a_tree_and_at_its_launch(self):
        # A sync's executor stops at step 6 on that issue as any other does, only after spending its round.
        before = dr.load_state(state_dir())

        def red():
            return ["main is red: #1 u1", "main is red: #2 u2"]
        expected = "refused: main is red: #1 u1; main is red: #2 u2; a sync waits for a green main"
        self.assertIn(expected, self.ready(red_source=red))
        launch = ["sync-launch", RUN, str(PR), "--branch", BRANCH, "--worktree", WORKTREE, "--oid", self.oid]
        self.assertIn(expected, run_cli(launch, red_source=red))
        self.assertEqual(before, dr.load_state(state_dir()))
        self.assertEqual("", self.ready())

    def test_a_read_that_cannot_say_main_is_green_holds_a_sync(self):
        def failed():
            raise db.Refusal("main-red-open.sh exited 2: cannot list the open issues")
        self.assertIn("refused: main-red-open.sh exited 2", self.ready(red_source=failed))

    def test_it_reads_no_tree(self):
        # The worktree does not exist yet when it runs, so a gone head or a dirty tree is sync-launch's alone.
        (self.tree / "dirty").write_text("x\n", encoding="utf-8")
        self.assertEqual("", self.ready())
        self.assertIn("is not clean", self.sync_launch())


class SyncDrop(Bound):
    """Every sync head offered to its drop helper, origin's and then the local ones, and nothing else offered."""

    def setUp(self):
        super().setUp()
        for name in ("chore/harness-sync-1234567", "chore/harness-sync-workflow", "feat/other"):
            git_in(".", "branch", name)
        stub(".", "git-branch-drop.sh",
             'if [ "$1" = chore/harness-sync-1234567 ]; then echo "dropped $1, superseded"; exit 0; fi\n'
             'echo "$1 is in use, and kept" >&2; exit 4\n')
        stub(".", "git-sync-head-drop.sh",
             'if [ "$1" = chore/harness-sync-7654321 ]; then echo "deleted $1 from origin"; exit 0; fi\n'
             'echo "$1 is still open, and kept" >&2; exit 7\n')
        # Not `origin.git`: with the remote gone, git would still find `origin` by that name.
        git_in(".", "init", "-q", "--bare", "elsewhere.git")
        git_in(".", "remote", "add", "origin", "elsewhere.git")

    def publish(self, *names):
        for name in names:
            git_in(".", "push", "-q", "origin", f"HEAD:refs/heads/{name}")

    def test_each_local_sync_head_is_offered_and_each_answer_is_a_line(self):
        self.assertEqual([f"kept {BRANCH}: git-branch-drop.sh {BRANCH} exited 4: {BRANCH} is in use, and kept",
                          "dropped chore/harness-sync-1234567, superseded"],
                         sorted(run_cli(["sync-drop"]).splitlines(), reverse=True))

    def test_each_of_origins_sync_heads_is_offered_before_the_local_ones(self):
        self.publish("chore/harness-sync-7654321", "chore/harness-sync-1111111", "chore/harness-sync-workflow",
                     "feat/on-origin", "main")
        lines = run_cli(["sync-drop"]).splitlines()
        self.assertEqual(["kept chore/harness-sync-1111111: git-sync-head-drop.sh chore/harness-sync-1111111 exited 7: "
                          "chore/harness-sync-1111111 is still open, and kept",
                          "deleted chore/harness-sync-7654321 from origin"], lines[:2])
        self.assertEqual(4, len(lines), lines)
        self.assertNotIn("workflow", " ".join(lines))
        self.assertNotIn("on-origin", " ".join(lines))

    def test_each_line_is_out_before_the_next_head_is_offered(self):
        # A run the dispatcher's timeout cuts short is killed here, and what it already deleted has to have been said.
        self.publish("chore/harness-sync-7654321", "chore/harness-sync-1111111")

        class Piped(io.StringIO):
            """Holds what a flush has sent, since `getvalue` also shows what a killed process would have lost."""
            sent = ""

            def flush(self):
                self.sent = self.getvalue()

        real_helper, seen, out = dr.helper, [], Piped()

        def watch(root, script, *args):
            seen.append(out.sent)
            return real_helper(root, script, *args)

        with mock.patch.object(dr, "helper", watch), contextlib.redirect_stdout(out):
            db.main(["sync-drop"])
        self.assertEqual("", seen[0])
        self.assertIn("chore/harness-sync-1111111", seen[1])

    def test_an_origin_it_cannot_read_leaves_the_local_heads_offered(self):
        git_in(".", "remote", "remove", "origin")
        lines = run_cli(["sync-drop"]).splitlines()
        self.assertIn("origin's sync heads were not read: ", lines[0])
        self.assertEqual("dropped chore/harness-sync-1234567, superseded", lines[1])
        self.assertEqual(3, len(lines), lines)

    def test_no_sync_branch_is_said_so(self):
        for name in ("chore/harness-sync-1234567", "chore/harness-sync-workflow", "feat/other"):
            git_in(".", "branch", "-D", name)
        git_in(".", "worktree", "remove", "--force", WORKTREE)
        git_in(".", "branch", "-D", BRANCH)
        self.assertEqual("no sync branch\n", run_cli(["sync-drop"]))


class ATemporaryWorktree(Bound):
    def test_one_the_executor_added_under_its_temp_directory_leaves_no_registration(self):
        record_sync(oid=self.oid)
        fake = Path("fake")
        fake.mkdir()
        (fake / "claude.py").write_text(
            "import os, subprocess, sys\n"
            "sys.stdin.buffer.read()\n"
            'subprocess.run(["git", "worktree", "add", "-q", "--detach", os.path.join(os.environ["TMP"], "rendered")],'
            " check=True)\n"
            "print('{}')\n", encoding="utf-8")
        with mock.patch.object(dr, "CLAUDE_COMMAND", [sys.executable, str((fake / "claude.py").resolve())]):
            run_cli(["run", RUN, GID])
        self.assertEqual([], list((self.root / "system-temp").iterdir()))
        self.assertNotIn("rendered", git_in(".", "worktree", "list", "--porcelain"))

    def test_a_refused_prune_still_writes_the_session_record(self):
        record_sync(oid=self.oid)
        real = dr.git

        def refuse(root, *args):
            if args[:2] == ("worktree", "prune"):
                raise db.Refusal("prune refused")
            return real(root, *args)

        with self.fake(), mock.patch.object(dr, "git", refuse):
            run_cli(["run", RUN, GID])
        self.assertTrue((state_dir() / f"{GID}.session.json").is_file())


class AMergeWithNoResult(Bound):
    """An executor that merged and wrote nothing, or nothing valid: `result` asks /ship step 0's own predicate."""

    def github(self, state="MERGED", head=None, base="main", merge_oid=OID, number=1690):
        row = {"number": number, "state": state, "url": "https://example.invalid/pr", "headRefOid": head or self.oid,
               "baseRefName": base}
        view = {"state": state, "mergeable": "UNKNOWN", "mergeStateStatus": "UNKNOWN", "headRefOid": head or self.oid,
                "mergeCommit": {"oid": merge_oid} if merge_oid else None}
        stub(".", "pr-for-branch.sh", f"echo '{json.dumps([row])}'\n")
        stub(".", "pr-state.sh", f"echo '{json.dumps(view)}'\n")

    def ended(self, result=None, gid="S1"):
        if gid == "S1":
            record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
            (state_dir() / "S1.task.txt").write_text("Narrow the grants\n", encoding="utf-8")
        self.run_group(gid=gid)
        if result is not None:
            self.write_result(result, gid=gid)
        return run_cli(["result", RUN, gid]).splitlines()[0]

    def test_a_merge_at_the_tip_is_claimed_and_verifies(self):
        self.github()
        self.assertEqual("S1: merged-claimed pr #1690 - merged with no review round", self.ended())
        entry = group_state(RUN, "S1")
        self.assertEqual(("merged-claimed", 1690, OID, 0), (entry["status"], entry["pr"], entry["merge_oid"],
                                                            entry["crashes"]))
        self.assertEqual([dr.NO_RESULT_MERGED], entry["decisions"])
        self.assertEqual(f"verified {OID}\n", run_cli(["verify", RUN, "S1"]))

    def test_an_invalid_result_beside_a_merge_is_claimed_too(self):
        self.github()
        self.assertTrue(self.ended({"group": "S1"}).startswith("S1: merged-claimed pr #1690"))

    def test_a_sync_keeps_its_own_pull_request(self):
        self.github(number=PR)
        record_sync(oid=self.oid)
        self.assertTrue(self.ended(gid=GID).startswith(f"{GID}: merged-claimed pr #{PR}"))
        self.assertEqual(PR, group_state(RUN, GID)["pr"])

    def test_a_sync_away_from_its_proved_head_is_still_the_crash(self):
        # The proof binds one head, so the merge at another tip is `run`'s refusal to name, as *The proof binds* says.
        self.github(number=PR)
        record_sync(oid=OID)
        directory = state_dir()
        (directory / f"{GID}.session.json").write_text(json.dumps(
            {"exit": 0, "started": NOW.isoformat(), "ended": NOW.isoformat(), "seconds": 1,
             "stdout": json.dumps(SUCCESS), "stderr": ""}), encoding="utf-8")
        self.assertEqual(f"{GID}: relaunch - no result; exit 0", run_cli(["result", RUN, GID]).splitlines()[0])

    def test_anything_short_of_the_predicate_is_still_the_crash(self):
        cases = {"open": {"state": "OPEN"}, "closed": {"state": "CLOSED"}, "another base": {"base": "release"},
                 "later work": {"head": OID}, "no merge oid": {"merge_oid": None}}
        for name, fields in cases.items():
            with self.subTest(case=name):
                self.github(**fields)
                self.assertEqual("S1: relaunch - no result; exit 0", self.ended())

    def test_a_dirty_tree_is_still_the_crash(self):
        self.github()
        (self.tree / "dirty").write_text("x\n", encoding="utf-8")
        self.assertEqual("S1: relaunch - no result; exit 0", self.ended())

    def test_an_untracked_file_is_dirty_whatever_status_shows(self):
        self.github()
        git_in(self.tree, "config", "status.showUntrackedFiles", "no")
        (self.tree / "dirty").write_text("x\n", encoding="utf-8")
        self.assertEqual("", git_in(self.tree, "status", "--porcelain"))
        self.assertEqual("S1: relaunch - no result; exit 0", self.ended())

    def test_a_read_that_fails_or_answers_out_of_shape_is_still_the_crash(self):
        self.github()
        for script, body in (("pr-for-branch.sh", "exit 1\n"), ("pr-for-branch.sh", "echo not-json\n"),
                             ("pr-for-branch.sh", "echo '{}'\n")):
            with self.subTest(script=script, body=body):
                stub(".", script, body)
                self.assertEqual("S1: relaunch - no result; exit 0", self.ended())
        self.github()
        stub(".", "pr-state.sh", "exit 1\n")
        self.assertEqual("S1: relaunch - no result; exit 0", self.ended())


class RunningASync(Bound):

    def test_a_sync_group_reads_no_task_text(self):
        self.sync_launch()
        self.assertEqual("", self.run_group())
        seen = json.loads(Path("fake/seen.json").read_text(encoding="utf-8"))
        self.assertEqual(dr.sync_prompt(PR, RUN), seen["stdin"])
        self.assertEqual(os.path.realpath(WORKTREE), os.path.realpath(seen["cwd"]))
        self.assertEqual("sonnet", seen["argv"][seen["argv"].index("--model") + 1])

    def test_a_group_with_rows_still_needs_its_task_text(self):
        record(RUN, "S1", status="running")
        self.assertIn("refused: could not read S1's task text", self.run_group(gid="S1"))

    def test_a_relaunch_runs_only_on_the_head_it_was_bound_to(self):
        # The executor takes <oid> from HEAD, so a tree a crash left elsewhere would be a head nobody proved.
        self.sync_launch()
        (self.tree / "h").write_text("moved\n", encoding="utf-8")
        git_in(self.tree, "add", "h")
        git_in(self.tree, "commit", "-q", "-m", "moved")
        moved = git_in(self.tree, "rev-parse", "HEAD")
        self.assertIn(f"refused: {WORKTREE} is at {moved}, not the proved {self.oid}", self.run_group())
        self.assertFalse(Path("fake/seen.json").exists())
        self.assertIn(f"{GID}: crashed-before-launch", run_cli(["result", RUN, GID]))

    def test_a_moved_head_whose_pull_request_merged_says_so(self):
        # Step 6's rebase moves the head, so a crash after it leaves a tree that reads as a head nobody proved
        # though the merge landed; the refusal names the merge rather than leaving a bare crash.
        self.sync_launch()
        (self.tree / "h").write_text("moved\n", encoding="utf-8")
        git_in(self.tree, "add", "h")
        git_in(self.tree, "commit", "-q", "-m", "moved")
        moved = git_in(self.tree, "rev-parse", "HEAD")
        for state, said in (("MERGED", True), ("OPEN", False)):
            with self.subTest(state=state), mock.patch.object(dr, "helper", lambda *a, state=state: json.dumps(
                    {"state": state})):
                out = self.run_group()
                self.assertIn(f"{WORKTREE} is at {moved}, not the proved {self.oid}", out)
                self.assertEqual(said, f"#{PR} is MERGED, so the head moved because the merge landed" in out)

    def test_a_dirty_tree_is_not_called_a_landed_merge(self):
        self.sync_launch()
        (self.tree / "dirt").write_text("x\n", encoding="utf-8")
        with mock.patch.object(dr, "helper", lambda *a: json.dumps({"state": "MERGED"})):
            out = self.run_group()
        self.assertIn("is not clean", out)
        self.assertNotIn("merge landed", out)

    def test_the_sync_result_validates_with_no_issues(self):
        self.assertEqual(sorted(dr.RESULT_KEYS), sorted(SYNC_MERGED))
        for result in (SYNC_MERGED, SYNC_CLOSED):
            with self.subTest(outcome=result["outcome"]):
                self.assertIsNone(dr._result_problem(result, GID, []))
        self.assertIn("are not []", dr._result_problem({**SYNC_MERGED, "issues": [1]}, GID, []))
        self.assertIn("is not sync-5", dr._result_problem({**SYNC_MERGED, "group": "sync-6"}, GID, []))
        self.sync_launch()
        self.assertEqual("", self.run_group())
        self.write_result(SYNC_MERGED)
        self.assertIn(f"{GID}: merged-claimed pr #{PR}", run_cli(["result", RUN, GID]))
        self.assertEqual("merged-claimed", group_state(RUN, GID)["status"])

    def test_a_sync_keeps_its_own_pull_request_whatever_its_result_says(self):
        # A closed sync's result may name no pr, and a claim of another's must not reach `verify`.
        self.sync_launch()
        self.run_group()
        self.write_result(SYNC_CLOSED)
        run_cli(["result", RUN, GID])
        self.assertEqual((PR, "stopped"), (group_state(RUN, GID)["pr"], group_state(RUN, GID)["status"]))

    def test_a_superseded_sync_is_not_relaunched(self):
        self.sync_launch()
        self.run_group()
        self.write_result(SYNC_CLOSED)
        self.assertIn(f"{GID}: stopped - closed unmerged", run_cli(["result", RUN, GID]))
        self.assertEqual("", run_cli(["sync-close", RUN, GID]))
        self.assertEqual(("closed", "stopped"), (group_state(RUN, GID)["status"], group_state(RUN, GID)["closed_from"]))
        self.assertIn(f"refused: {GID} is closed, not waiting for an executor", self.run_group())
        self.assertIn(f"refused: {GID} is already in this run", self.sync_launch())
        self.assertEqual([], resume_lines())
        # A new `take` is a new group, whose own head is proved and bound.
        git_in(".", "worktree", "add", "-q", "-b", "chore/harness-sync-1234567", ".claude/worktrees/sync-1234567")
        head = git_in(".claude/worktrees/sync-1234567", "rev-parse", "HEAD")
        self.assertEqual("", self.sync_launch(pr=6, branch="chore/harness-sync-1234567",
                                              worktree=".claude/worktrees/sync-1234567", oid=head))
        self.assertEqual("running", group_state(RUN, "sync-6")["status"])


class SyncClose(InRoot):

    def test_sync_close_keeps_the_outcome(self):
        for status in ("verified", "handed-back", "stopped", "crash-hand-back", "crashed-before-launch", "window"):
            with self.subTest(status=status):
                record_sync(status=status, merge_oid=OID)
                self.assertEqual("", run_cli(["sync-close", RUN, GID]))
                self.assertEqual(("closed", status),
                                 (group_state(RUN, GID)["status"], group_state(RUN, GID)["closed_from"]))

    def test_a_sync_not_yet_ended_is_not_closed(self):
        for status in ("running", "relaunch", "week", "merged-claimed", "closed"):
            with self.subTest(status=status):
                record_sync(status=status)
                self.assertIn(f"refused: {GID} is {status}, not a sync that ended", run_cli(["sync-close", RUN, GID]))
                self.assertEqual(status, group_state(RUN, GID)["status"])

    def test_a_sync_whose_worktree_is_gone_can_be_closed(self):
        # `resume` refuses it, `wake` holds for it and `sync-launch` waits on it, so this is its one way out.
        for status in dr.ACTIVE:
            with self.subTest(status=status):
                record_sync(status=status, worktree="gone/z")
                os.rmdir("gone/z")
                self.assertEqual("", run_cli(["sync-close", RUN, GID]))
                entry = group_state(RUN, GID)
                self.assertEqual(("closed", "crash-hand-back", GONE),
                                 (entry["status"], entry["closed_from"], entry["reason"]))

    def gone(self, **fields):
        record_sync(worktree="gone/z", **fields)
        os.rmdir("gone/z")

    def test_a_sync_closed_on_a_gone_worktree_reaches_a_human(self):
        # How its executor ended is unknown, so it may have merged or left work: nobody else reads it.
        self.gone()
        run_cli(["sync-close", RUN, GID])
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        out = run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-01"], body_source=lambda n: "",
                      open_source=lambda: {1440}, branch_source=NO_BRANCH)
        needs = out.split("## Needs a human\n", 1)[1].split("\n## ", 1)[0]
        self.assertIn(f"{GID} #{PR} crash-hand-back: {GONE}", needs)

    def test_an_unread_session_record_is_read_before_a_gone_worktree_closes(self):
        self.gone()
        record = state_dir() / f"{GID}.session.json"
        record.write_text(json.dumps({"started": NOW.isoformat(), "stdout": "{}"}), encoding="utf-8")
        self.assertIn(f"refused: {GID} has an exit not yet read; run result first", run_cli(["sync-close", RUN, GID]))
        self.assertEqual("running", group_state(RUN, GID)["status"])
        self.assertNotIn("reason", group_state(RUN, GID))

    def test_a_session_record_already_read_does_not_hold_the_close(self):
        self.gone(status="relaunch", absorbed=NOW.isoformat())
        record = state_dir() / f"{GID}.session.json"
        record.write_text(json.dumps({"started": NOW.isoformat(), "stdout": "{}"}), encoding="utf-8")
        self.assertEqual("", run_cli(["sync-close", RUN, GID]))
        self.assertEqual("closed", group_state(RUN, GID)["status"])

    def test_a_refused_marker_is_read_before_a_gone_worktree_closes(self):
        self.gone()
        marker = state_dir() / f"{GID}.refused.json"
        marker.write_text(json.dumps({"refused": "stop file", "at": NOW.isoformat()}), encoding="utf-8")
        self.assertIn(f"refused: {GID} has an exit not yet read; run result first", run_cli(["sync-close", RUN, GID]))
        self.assertEqual("running", group_state(RUN, GID)["status"])
        self.assertIn(f"{GID}: crashed-before-launch", run_cli(["result", RUN, GID]))

    def test_a_gone_worktree_with_a_live_executor_is_not_closed(self):
        record_sync(worktree="gone/z")
        os.rmdir("gone/z")
        (state_dir() / f"{GID}.pid").write_text(f"{os.getpid()}\n", encoding="utf-8")
        self.assertIn(f"refused: {GID}'s executor is still running", run_cli(["sync-close", RUN, GID]))
        self.assertEqual("running", group_state(RUN, GID)["status"])

    def test_a_group_with_rows_is_not_closed_this_way(self):
        record(RUN, "S1", status="verified")
        self.assertIn("refused: S1 is not a sync group", run_cli(["sync-close", RUN, "S1"]))
        self.assertEqual("verified", group_state(RUN, "S1")["status"])


class Wake(InRoot):

    def wake(self, text=FIXTURE):
        sync_state()
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(text)
        with mock.patch.object(dr, "PROJECTS", self.root / "projects"):
            return run_cli(["wake", RUN, "--tasks", "todo.md", "--today", "2026-11-01"], body_source=lambda n: "",
                           open_source=lambda: {1440}, branch_source=NO_BRANCH).splitlines()

    def test_no_group_launches_while_a_sync_runs(self):
        self.assertEqual("launch: S1 S3", self.wake()[-1])
        for status in dr.ACTIVE:
            with self.subTest(status=status):
                record_sync(status=status, resets_at="2020-01-01T00:00:00+00:00")
                lines = self.wake()
                # A refused account ends the night, which launches nothing either.
                self.assertEqual("end: account" if status == "account" else "launch: none", lines[-1])
                self.assertIn(f"running: {GID}", lines)
        record_sync(status="closed", closed_from="verified")
        self.assertEqual("launch: S1 S3", self.wake()[-1])

    def test_no_group_launches_while_a_sync_merge_is_claimed_and_unverified(self):
        # `main` is not confirmed to hold it until `verify` has read GitHub, and the next group would run on that.
        record_sync(status="merged-claimed")
        lines = self.wake()
        self.assertEqual("launch: none", lines[-1])
        self.assertIn(f"held: {GID} merge claimed, not yet verified", lines)
        record_sync(status="verified")
        self.assertEqual("launch: S1 S3", self.wake()[-1])

    def test_a_sync_running_does_not_end_the_night(self):
        record_sync()
        self.assertEqual("launch: none", self.wake(tasks_of(line(depends="OWNER")))[-1])

    def test_the_stop_file_still_ends_the_night_over_a_sync(self):
        record_sync()
        (state_dir() / "STOP").write_text("", encoding="utf-8")
        self.assertEqual("end: stop-file", self.wake()[-1])

    def test_a_group_after_the_merge_reads_no_sync_as_held(self):
        record_sync(status="verified")
        self.assertEqual("launch: S1 S3", self.wake()[-1])


class Resume(InRoot):

    def test_a_stopped_sync_runs_no_tasks_back(self):
        for status in dr.HANDED:
            with self.subTest(status=status):
                record_sync(status=status)
                self.assertEqual([f"sync-close {GID}: {status}"], resume_lines())
        record_sync(status="verified")
        self.assertEqual([f"sync-done {GID}: verified, not yet closed"], resume_lines())
        record_sync(status="window", resets_at="2026-11-02T01:30:00+00:00")
        self.assertEqual([f"sync-wait {GID}: the window resets 2026-11-02T01:30:00+00:00, then relaunch"],
                         resume_lines())

    def test_tasks_done_and_back_cannot_reach_a_sync(self):
        record_sync(status="verified")
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        for action in ("done", "back"):
            with self.subTest(action=action):
                self.assertIn(f"refused: {GID!r} is not a group id",
                              run_cli(["tasks", action, "--tasks", "todo.md", "--run", RUN, GID]))
        self.assertEqual(FIXTURE, Path("todo.md").read_text(encoding="utf-8"))

    def test_the_gone_worktree_row_closes_a_sync(self):
        table = flat(DISPATCH.read_text(encoding="utf-8").split("## Resume", 1)[1])
        row = table.split("refused <g>: its worktree … is gone |", 1)[1].split(" |", 1)[0]
        self.assertIn("a sync group, sync-close", row)

    def test_every_line_a_sync_resumes_on_has_a_row_and_none_leads_to_tasks(self):
        table = DISPATCH.read_text(encoding="utf-8").split("## Resume", 1)[1]
        rows = {cells[1].strip("` ").split(" <g>")[0]: cells[2] for cells in
                (row.split("|") for row in table.splitlines() if row.startswith("| `"))}
        for name in ("sync-close", "sync-done", "sync-wait"):
            with self.subTest(line=name):
                self.assertIn("sync-close", rows[name])
                self.assertNotIn("tasks back", rows[name])
                self.assertNotIn("tasks done", rows[name])

    def test_a_sync_holds_no_run_open(self):
        # `start` reopens a reported run only for rows, and a sync has none, whichever way it ended. The list is
        # readable here but not consulted; the next case is the same with a list that cannot be read.
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        for status in ("closed", "verified", "running", "stopped"):
            with self.subTest(status=status):
                record_sync(status=status)
                (state_dir() / "report.md").write_text("done\n", encoding="utf-8")
                self.assertEqual((None, None), dr.open_run(".", "todo.md"))

    def test_an_unclosed_sync_is_not_read_against_a_list_that_cannot_be_read(self):
        # Empty keys can match no row, so an unreadable list must not keep a run open for a group holding none.
        for status in ("verified", "running", "stopped"):
            with self.subTest(status=status):
                record_sync(status=status)
                (state_dir() / "report.md").write_text("done\n", encoding="utf-8")
                self.assertEqual((None, None), dr.open_run(".", "no-such-list.md"))

    def test_a_group_with_rows_over_an_unreadable_list_still_keeps_its_run_open(self):
        record(RUN, "S1", status="running")
        (state_dir() / "report.md").write_text("done\n", encoding="utf-8")
        name, why = dr.open_run(".", "no-such-list.md")
        self.assertEqual(RUN, name)
        self.assertIn("could not be read", why)

    def test_a_record_missing_its_keys_still_keeps_its_run_open(self):
        record_sync()
        state = dr.load_state(state_dir())
        del state["groups"][GID]["keys"]
        dr.save_state(state_dir(), state)
        (state_dir() / "report.md").write_text("done\n", encoding="utf-8")
        self.assertEqual(RUN, dr.open_run(".", "no-such-list.md")[0])

    def test_a_closed_sync_is_not_read_against_a_list_that_cannot_be_read(self):
        record_sync(status="closed", closed_from="verified")
        (state_dir() / "report.md").write_text("done\n", encoding="utf-8")
        self.assertEqual((None, None), dr.open_run(".", "no-such-list.md"))


class Report(InRoot):

    def report(self):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)
        return run_cli(["report", RUN, "--tasks", "todo.md", "--today", "2026-11-01"], body_source=lambda n: "",
                       open_source=lambda: {1440}, branch_source=NO_BRANCH).splitlines()

    def section(self, lines, heading):
        start = lines.index(f"## {heading}") + 1
        rest = [i for i, text in enumerate(lines[start:], start) if text.startswith("## ")]
        return lines[start:rest[0] if rest else len(lines)]

    def test_the_report_lists_syncs(self):
        record_sync(status="closed", closed_from="verified", merge_oid=OID, rounds=SYNC_MERGED["rounds"],
                    review_end="clean", filed=[1701], decisions=["kept the target's own note"])
        record_sync(pr=6, status="closed", closed_from="stopped", reason="closed unmerged")
        record_sync(pr=7, status="closed", closed_from="handed-back", reason="a local test\nfails", worktree="gone/x",
                    title="chore(harness): sync")
        os.rmdir("gone/x")
        lines = self.report()
        self.assertEqual("Syncs", dr.REPORT_SECTIONS[-1])
        self.assertEqual(7, len(dr.REPORT_SECTIONS))
        self.assertEqual([f"## {name}" for name in dr.REPORT_SECTIONS], [t for t in lines if t.startswith("## ")])
        syncs = self.section(lines, "Syncs")
        self.assertEqual(3, len(syncs), syncs)
        for fact in (GID, f"#{PR}", f"merged as {OID}", "full raised 1 fixed 0 refused 1", "review clean",
                     "filed #1701", "kept the target's own note"):
            self.assertIn(fact, syncs[0])
        self.assertIn("sync-6 #6 stopped: closed unmerged", syncs[1])
        self.assertIn(f"{WORKTREE} left in place", syncs[1])
        self.assertIn('sync-7 #7 "chore(harness): sync" handed-back: a local test fails', syncs[2])
        self.assertIn("worktree gone/x removed", syncs[2])
        self.assertEqual(["none"], self.section(lines, "Merged"))
        # The hand-back's reason is what a human must do; a closed pull request was superseded and asks nothing.
        self.assertEqual([syncs[2]], self.section(lines, "Needs a human"))
        self.assertEqual(["none"], self.section(lines, "Interrupted"))
        self.assertEqual(["#1701"], self.section(lines, "Filed overnight"))

    def test_every_sync_a_human_owes_is_named_for_one(self):
        for status in dr.HANDED:
            with self.subTest(status=status):
                record_sync(status="closed", closed_from=status, reason="a local suite fails")
                needs = self.section(self.report(), "Needs a human")
                self.assertEqual(1, len(needs), needs)
                self.assertIn(f"{GID} #{PR} {status}: a local suite fails", needs[0])
                self.assertIn(f"worktree {WORKTREE} left in place", needs[0])

    def test_only_a_closed_pull_request_is_left_out(self):
        # A failed merge check ends `stopped` too, and a hand-back could name the same words: neither is a supersede.
        record_sync(status="closed", closed_from="stopped", reason="closed unmerged")
        self.assertEqual(["none"], self.section(self.report(), "Needs a human"))
        record_sync(status="closed", closed_from="handed-back", reason="closed unmerged")
        self.assertIn("handed-back: closed unmerged", self.section(self.report(), "Needs a human")[0])

    def test_with_no_sync_the_section_says_none(self):
        sync_state()
        self.assertEqual(["none"], self.section(self.report(), "Syncs"))

    def test_a_closed_sync_is_not_reported_unconfirmed(self):
        record_sync(status="closed", closed_from="verified", merge_oid=OID, review_end="clean",
                    rounds=SYNC_MERGED["rounds"])
        text = "\n".join(self.report())
        self.assertNotIn("not confirmed in main", text)
        self.assertNotIn(f"{GID} closed", text)

    def test_an_unconfirmed_sync_claims_no_rows(self):
        record_sync(status="verified", merge_oid=OID)
        lines = self.report()
        needs = self.section(lines, "Needs a human")
        self.assertEqual(1, len(needs), needs)
        self.assertIn(f"{GID} #{PR} merged as {OID}, not confirmed in main", needs[0])
        self.assertIn(f"worktree {WORKTREE} is left in place", needs[0])
        self.assertNotIn("In progress", needs[0])
        self.assertEqual(["none"], self.section(lines, "Syncs"))
        # No row keeps the run open, so no resume line follows it.
        self.assertEqual(["none"], self.section(lines, "Interrupted"))

    def test_a_sync_whose_worktree_is_gone_claims_no_rows(self):
        record_sync(status="running", worktree="gone/y")
        os.rmdir("gone/y")
        needs = self.section(self.report(), "Needs a human")
        self.assertEqual([f"{GID} running: its worktree gone/y is gone"], needs)

    def test_a_sync_merged_with_no_review_is_named_for_a_human(self):
        record_sync(status="closed", closed_from="verified", merge_oid=OID, rounds=[], unreviewed=True)
        lines = self.report()
        self.assertEqual([f"{GID} #{PR} {dr.UNREVIEWED}, as {OID}"], self.section(lines, "Needs a human"))
        self.assertIn(dr.UNREVIEWED, self.section(lines, "Syncs")[0])


class Runbook(unittest.TestCase):

    def text(self):
        return flat(DISPATCH.read_text(encoding="utf-8"))

    def grants(self):
        front = re.search(r"^allowed-tools: (.*)$", DISPATCH.read_text(encoding="utf-8"), re.MULTILINE).group(1)
        return [grant.strip() for grant in front.split(",")]

    def test_the_grants_name_the_list_the_verify_and_the_worktree(self):
        self.assertIn("Bash(py -3.12 .claude/scripts/sync_pr.py:*)", self.grants())
        self.assertIn("Bash(bash .claude/scripts/git-worktree-sync.sh:*)", self.grants())

    def test_the_proof_is_made_in_the_main_checkout_at_step_0_and_after_each_exit(self):
        text = self.text()
        step_0 = text[text.index("3. Read GitHub and git"):text.index("4. Read the board")]
        self.assertIn("py -3.12 .claude/scripts/sync_pr.py list", step_0)
        self.assertIn("py -3.12 .claude/scripts/sync_pr.py verify <n> --head <oid>", step_0)
        reads = text[text.index("Then run bash .claude/scripts/gh-issue-list.sh, sync_pr.py list"):]
        self.assertIn("verify on its take", reads[:200])

    def test_the_board_names_the_sync_and_the_interview_does_not_ask(self):
        text = self.text()
        self.assertIn("name sync-<n> beside its lines", text)
        interview = text[text.index("1. **The interview.**"):text.index("Take them one at a time")]
        self.assertIn("and never a sync", interview)

    def test_step_2_launches_the_sync_before_wake(self):
        text = self.text()
        loop = text[text.index("2. **The night loop.**"):text.index("**Launch a group**")]
        self.assertIn("**On a night that starts with no group active, a sync found at step 0 goes first and alone.**",
                      loop)
        self.assertIn("a sync opening mid-night, or found by a resumed run with a group active, waits for the running "
                      "groups, new launches can overtake it, and the morning's list shows it", loop)
        self.assertLess(loop.index("dispatch_board.py sync-ready <run> <n>"),
                        loop.index("git-worktree-sync.sh .claude/worktrees/sync-<sha7> <head>"),
                        "a sync that only waits would leave a worktree nothing removes")
        self.assertLess(loop.index("git-worktree-sync.sh .claude/worktrees/sync-<sha7> <head>"),
                        loop.index("sync-launch <run> <n> --branch <head> --worktree .claude/worktrees/sync-<sha7> "
                                   "--oid <oid>"))
        self.assertLess(loop.index("sync-launch"), loop.index("dispatch_board.py wake <run>"))
        self.assertIn("run <run> sync-<n>, the same on a relaunch", loop)
        self.assertIn("one naming a running group or a red main only waits for a later exit", loop)
        # A handed-back sync leaves both, and the helper refuses both.
        self.assertIn("Skip the helper where that worktree already exists on that branch", loop)

    def test_an_exit_closes_a_sync_and_touches_no_row(self):
        text = self.text()
        part = text[text.index("**On an executor's exit**"):text.index("- relaunch: start run again")]
        self.assertIn("a sync group runs neither tasks done nor tasks back and ends on sync-close <run> <g>, after "
                      "the removal and the drop on a merge", part)
        self.assertIn("A failed pull or containment read runs none of tasks done, the removal, the drop and sync-close",
                      part)

    def test_the_morning_drops_what_each_sync_left_and_says_so(self):
        text = self.text()
        morning = text[text.index("3. **The morning report.**"):text.index("## Resume")]
        self.assertLess(morning.index("dispatch_board.py sync-drop"), morning.index("dispatch_board.py report <run>"))
        self.assertIn("sync-drop's lines", morning)

    def test_the_report_has_seven_sections(self):
        text = self.text()
        self.assertIn("prints the seven sections' facts", text)
        self.assertIn("**Usage**, **Syncs**", text)
        self.assertIn("list's rest rows and each failed verify of a take, under *Syncs*", text)
        self.assertIn("seven sections", flat(ARGUMENT.read_text(encoding="utf-8")))
        self.assertNotIn("six sections", flat(ARGUMENT.read_text(encoding="utf-8")))

    def test_the_argument_says_why_a_sync_goes_first_and_alone(self):
        argued = flat(ARGUMENT.read_text(encoding="utf-8"))
        section = argued[argued.index("## Sync pull requests"):]
        self.assertIn("about 15 minutes, once a night", section)
        self.assertIn("on a night that starts with no group active one found at step 0 goes first and alone", section)
        self.assertIn("A sync opening mid-night, or found by a resumed run with a group active, gets neither promise",
                      section)
        self.assertIn("it waits for the running groups and new launches can overtake it", section)
        self.assertNotIn("is met before wake can fill the slots", section)
        self.assertIn("(why: docs/commands/dispatch.md, *Sync pull requests*)", self.text())


if __name__ == "__main__":
    unittest.main()
