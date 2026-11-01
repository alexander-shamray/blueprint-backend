import ast
import contextlib
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

import dispatch_board as db

TODAY = date(2026, 11, 1)
NO_BRANCH = lambda _branch: frozenset()  # noqa: E731


def stub(root, name, body):
    """A stand-in for one of the repository's helper scripts under `root`, LF-only, as bash needs."""
    scripts = Path(root) / ".claude" / "scripts"
    scripts.mkdir(parents=True, exist_ok=True)
    with open(scripts / name, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("#!/usr/bin/env bash\n" + body)

NOTE = "# TODO\n\n> Rules for filling this file.\n> 1. Four sections follow.\n\n"
IN_PROGRESS = """## In progress

| Group | # | What | Branch or PR | State |
|---|---|---|---|---|
| — | owed (not filed) | `/dispatch` implementation, plan `docs/superpowers/plans/2026-11-01-dispatch.md` | `chore/dispatch-command` | started |

"""
REMAINING_HEAD = """## Remaining

| Wave | Group | # | Task | Labels | Depends on | Model | Parallel with / conflicts |
|---|---|---|---|---|---|---|---|
"""
REMAINING_ROWS = """| 1 | S1 | 1682 | Narrow the read grant in `definitions.json` and add a `check_permissions.py` probe | security, code | none | Opus | with 1683: same files |
| 1 | S1 | 1683 | Narrow read to own fault exchanges in `deploy/rabbitmq/definitions.json` | security, code | none | Opus | with 1682: same files |
| 1 | S3 | 1681 | Bind only the switch keys in `src/Gateway/Program.cs` | security, code | none | Opus | none |
| 2 | M1 | 1685 | Add a test for `PersonalDataErasure.cs` | test-gap, code | none | Sonnet | M2: separate projects |
| 2 | M2 | 1686 | Test the guard in `OrderLine.cs` | test-gap, code | none | Sonnet | M1: separate projects |
| 2 | J1 | 1440 | Write `docs/ci-cost-comparison.md` | enhancement, code | 2026-11-09 | Opus | none |
"""
TRACKING = """
## Tracking

| Wave | Group | # | Task | Labels | Depends on | Model | Parallel with / conflicts |
|---|---|---|---|---|---|---|---|
| 5 | — | 1406 | Flaky tests tracker | bug, flaky, tracking | none | — | never closed |

## Paused

| # | Task | Labels | Why paused | Resume when |
|---|---|---|---|---|
| 1677 | Record k6 timings | bug, paused | Paused by the owner | 2026-11-05 |
| D2 (owed, not filed) | Step-7 equivalents | n/a | Depends on 857 | 857 merged |
"""
FIXTURE = NOTE + IN_PROGRESS + REMAINING_HEAD + REMAINING_ROWS + TRACKING


def tasks_of(*rows):
    return NOTE + IN_PROGRESS + REMAINING_HEAD + "".join(row + "\n" for row in rows) + TRACKING


def group_of(row):
    return db.groups(db.parse_tasks(tasks_of(row)))[0]


def line(depends="none", labels="code", task="Do `x/y.cs`", conflicts="none", number=1700, gid="Z1", wave=1):
    return f"| {wave} | {gid} | {number} | {task} | {labels} | {depends} | Opus | {conflicts} |"


def section(text, name):
    body = text.split(f"## {name}\n", 1)[1]
    return re.split(r"\n## ", body, 1)[0]


class ParseTests(unittest.TestCase):
    def test_the_four_sections_parse_in_order(self):
        tasks = db.parse_tasks(FIXTURE)
        self.assertEqual([1682, 1683, 1681, 1685, 1686, 1440], [r.number for r in tasks.remaining])
        self.assertEqual([1], [r.wave for r in tasks.remaining][:1])
        self.assertEqual(["S1", "S1", "S3", "M1", "M2", "J1"], [r.group for r in tasks.remaining])
        self.assertEqual(["#1406"], [r.key for r in tasks.tracking])
        self.assertEqual(["#1677"], [r.key for r in tasks.paused if r.number])
        self.assertEqual(1, len(tasks.in_progress))
        self.assertTrue(tasks.remaining[0].line.startswith("| 1 | S1 | 1682 |"))

    def test_a_fifth_section_or_a_missing_one_is_refused(self):
        with self.assertRaisesRegex(db.Refusal, "unexpected section 'Done'"):
            db.parse_tasks(FIXTURE + "\n## Done\n\nnone\n")
        with self.assertRaisesRegex(db.Refusal, "section 'Paused' is missing"):
            db.parse_tasks(FIXTURE.split("## Paused")[0])
        swapped = FIXTURE.replace("## Tracking", "## Tmp").replace("## Paused", "## Tracking").replace(
            "## Tmp", "## Paused")
        with self.assertRaisesRegex(db.Refusal, "not In progress, Remaining"):
            db.parse_tasks(swapped)

    def test_a_remaining_header_with_other_columns_is_refused(self):
        bad = FIXTURE.replace("| Wave | Group | # | Task |", "| Wave | Group | # | What |", 1)
        with self.assertRaisesRegex(db.Refusal, "Remaining header"):
            db.parse_tasks(bad)

    def test_a_group_spanning_two_waves_is_refused(self):
        bad = tasks_of(line(number=1, gid="S1", wave=1), line(number=2, gid="S1", wave=2))
        with self.assertRaisesRegex(db.Refusal, "S1 spans waves 1 and 2"):
            db.groups(db.parse_tasks(bad))

    def test_a_group_with_two_models_is_refused(self):
        bad = tasks_of(line(number=1, gid="S1"), line(number=2, gid="S1").replace("Opus", "Sonnet"))
        with self.assertRaisesRegex(db.Refusal, "S1 spans models Opus and Sonnet"):
            db.groups(db.parse_tasks(bad))

    def test_an_owed_row_gets_a_stable_key(self):
        first = db.parse_tasks(FIXTURE).in_progress[0]
        again = db.parse_tasks(FIXTURE).in_progress[0]
        digest = hashlib.sha1(first.cells[2].encode("utf-8")).hexdigest()[:8]
        self.assertEqual(f"owed:{digest}", first.key)
        self.assertEqual(first.key, again.key)
        self.assertIsNone(first.number)
        paused_owed = [r for r in db.parse_tasks(FIXTURE).paused if r.number is None][0]
        self.assertNotEqual(first.key, paused_owed.key)

    def test_groups_come_in_table_order(self):
        found = db.groups(db.parse_tasks(FIXTURE))
        self.assertEqual(["S1", "S3", "M1", "M2", "J1"], [g.id for g in found])
        self.assertEqual(2, len(found[0].rows))
        self.assertEqual(("Opus", 1), (found[0].model, found[0].wave))

    def test_a_number_in_two_tables_is_refused(self):
        bad = FIXTURE.replace("| 1677 |", "| 1682 |")
        with self.assertRaisesRegex(db.Refusal, "#1682 is in Remaining and Paused"):
            db.parse_tasks(bad)


class ReadyTests(unittest.TestCase):
    def ready(self, row, open_issues=frozenset(), today=TODAY, bodies=None):
        return db.ready(group_of(row), set(open_issues), today, bodies)

    def test_a_group_is_ready_only_when_every_row_is(self):
        both = db.groups(db.parse_tasks(tasks_of(line(number=1, gid="A1"), line(number=2, gid="A1", labels="question"))))[0]
        self.assertEqual((False, "waits on a human"), db.ready(both, set(), TODAY))
        ok = db.groups(db.parse_tasks(tasks_of(line(number=1, gid="A1"), line(number=2, gid="A1"))))[0]
        self.assertEqual((True, ""), db.ready(ok, set(), TODAY))

    def test_owner_in_depends_on_waits_on_a_human(self):
        self.assertEqual((False, "waits on a human"), self.ready(line(depends="OWNER")))
        self.assertEqual((False, "waits on a human"), self.ready(line(depends="OWNER; after 1440"), {1440}))

    def test_an_open_issue_in_depends_on_is_not_ready_and_a_closed_one_is(self):
        for text in ("after #1440", "1440's usage scenarios"):
            self.assertEqual((False, "depends on open #1440"), self.ready(line(depends=text), {1440}))
            self.assertEqual((True, ""), self.ready(line(depends=text), {99}))

    def test_a_future_date_is_not_ready_and_a_past_one_is(self):
        self.assertEqual((False, "waits until 2026-11-09"), self.ready(line(depends="2026-11-09")))
        self.assertEqual((True, ""), self.ready(line(depends="2026-10-30")))
        self.assertEqual((True, ""), self.ready(line(depends="2026-11-01")))
        mixed = line(depends="after 1440's Window B closes 2026-11-08")
        self.assertEqual((False, "waits until 2026-11-08"), self.ready(mixed))

    def test_depends_on_text_with_no_number_date_or_none_is_not_ready(self):
        ok, why = self.ready(line(depends="owner's window choice; a thirteen-day span after it"))
        self.assertFalse(ok)
        self.assertIn("cannot judge", why)

    def test_a_question_label_or_an_owed_row_is_not_ready(self):
        self.assertEqual((False, "waits on a human"), self.ready(line(labels="question, low")))
        owed = line().replace("| 1700 |", "| owed (not filed) |")
        ok, why = self.ready(owed)
        self.assertFalse(ok)
        self.assertIn("owed", why)

    def test_a_row_whose_paths_reach_claude_is_not_ready(self):
        self.assertEqual((False, "edits .claude/"), self.ready(line(task="Edit `.claude/scripts/x.py`")))
        self.assertEqual((False, "edits .claude/"), self.ready(line(conflicts="`staging/.claude/commands`")))
        self.assertEqual((False, "edits .claude/"), self.ready(line(number=7), bodies={7: "see .claude/hooks/a.sh"}))
        self.assertEqual((True, ""), self.ready(line(task="Edit `docs/a.md`")))
        for body in ("edit .claude/commands/ship.md frontmatter", "the .claude/hooks tree", r"under .Claude\skills"):
            self.assertEqual((False, "edits .claude/"), self.ready(line(number=7), bodies={7: body}), body)
        self.assertEqual((True, ""), self.ready(line(number=7), bodies={7: "see claude and my.claudex"}))

    def test_a_bare_number_in_depends_on_is_not_an_issue(self):
        self.assertFalse(self.ready(line(depends="14 days after 1440 closes"), {1440})[0])
        ok, why = self.ready(line(depends="14 days after 1440 closes"), {99})
        self.assertFalse(ok)
        self.assertIn("cannot judge", why)
        ok, why = self.ready(line(depends="a thirteen-day span"))
        self.assertFalse(ok)
        self.assertEqual((False, "depends on open #1440"), self.ready(line(depends="waits on 1440"), {1440}))


class PathTests(unittest.TestCase):
    def claim(self, paths, gid="Z1", groups=(), issues=(), unknown=False):
        return db.Claim(frozenset(paths), frozenset(groups), frozenset(issues), unknown)

    def over(self, a, b, a_id="A1", a_issues=(), b_id="B1", b_issues=()):
        return db.overlaps(a, a_id, set(a_issues), b, b_id, set(b_issues))

    def test_paths_are_taken_from_backticks_and_bare_file_names(self):
        found = db.extract_paths("Edit `src/A/b.cs:28`, then definitions.json and docs/x.md. Run `gh issue list`.")
        self.assertEqual({"src/a/b.cs", "definitions.json", "docs/x.md"}, found)
        self.assertEqual({"deploy/rabbitmq"}, db.extract_paths("the `deploy/rabbitmq/` folder"))

    def test_slash_commands_and_dotted_identifiers_are_not_paths(self):
        self.assertEqual(frozenset(), db.extract_paths("`/ship` `OrderLine.Total` `--dispatched` `gh issue list`"))
        found = db.extract_paths("`src/Services/Catalog/` and `deploy/rabbitmq/definitions.json` and `/ship`")
        self.assertEqual({"src/services/catalog", "deploy/rabbitmq/definitions.json"}, found)
        claim = db.group_claim(group_of(line(task="Run `/ship` on `OrderLine.Total`")), {1700: "no files"})
        self.assertTrue(claim.unknown)

    def test_paths_are_normalised_case_slashes_and_dot_prefix(self):
        found = db.extract_paths(r"`.\Src\Gateway\Program.cs` and `./Docs/A.MD`")
        self.assertEqual({"src/gateway/program.cs", "docs/a.md"}, found)

    def test_a_bare_file_name_overlaps_any_path_ending_in_it(self):
        bare, deep = self.claim({"definitions.json"}), self.claim({"deploy/rabbitmq/definitions.json"})
        self.assertTrue(self.over(bare, deep))
        self.assertTrue(self.over(deep, bare))
        self.assertFalse(self.over(self.claim({"efinitions.json"}), deep))
        self.assertFalse(self.over(bare, self.claim({"other.json"})))

    def test_a_directory_overlaps_every_path_under_it(self):
        directory, inside = self.claim({"src/services/catalog"}), self.claim({"src/services/catalog/a.cs"})
        self.assertTrue(self.over(directory, inside))
        self.assertTrue(self.over(inside, directory))
        self.assertFalse(self.over(directory, self.claim({"src/services/catalogue/a.cs"})))

    def test_a_conflicts_cell_naming_a_group_makes_the_two_exclusive(self):
        groups = db.groups(db.parse_tasks(FIXTURE))
        m1 = next(g for g in groups if g.id == "M1")
        claim = db.group_claim(m1, {1685: "x.cs"})
        self.assertEqual({"M2"}, claim.groups)
        other = self.claim({"somewhere/else.cs"})
        self.assertTrue(self.over(claim, other, a_id="M1", b_id="M2"))
        self.assertTrue(self.over(other, claim, a_id="M2", b_id="M1"))
        self.assertFalse(self.over(claim, other, a_id="M1", b_id="J1"))

    def test_a_conflicts_cell_naming_an_issue_makes_its_group_exclusive(self):
        claim = db.group_claim(group_of(line(conflicts="with 1682: same files")), {1700: "a.cs"})
        self.assertEqual({1682}, claim.issues)
        other = self.claim({"elsewhere/b.cs"})
        self.assertTrue(self.over(claim, other, b_issues={1682}))
        self.assertFalse(self.over(claim, other, b_issues={1999}))

    def test_a_dateless_conflicts_cell_names_no_issue(self):
        claim = db.group_claim(group_of(line(conflicts="not inside Window B 2026-11-08")), {1700: "a.cs"})
        self.assertEqual(frozenset(), claim.issues)

    def test_a_claim_with_no_paths_is_exclusive_with_everything(self):
        empty = db.group_claim(group_of(line(task="Think hard", number=1700)), {1700: "no files named"})
        self.assertTrue(empty.unknown)
        self.assertTrue(self.over(empty, self.claim({"a/b.cs"})))
        self.assertTrue(self.over(self.claim({"a/b.cs"}), self.claim(set(), unknown=True)))
        missing = db.group_claim(group_of(line()), {})
        self.assertTrue(missing.unknown)

    def test_another_sessions_in_progress_row_holds_its_paths(self):
        row = db.parse_tasks(FIXTURE).in_progress[0]
        held = db.in_progress_claim(row, {})
        self.assertIn("docs/superpowers/plans/2026-11-01-dispatch.md", held.paths)
        self.assertNotIn("chore/dispatch-command", held.paths)
        mine = self.claim({"docs/superpowers/plans/2026-11-01-dispatch.md"})
        self.assertTrue(self.over(mine, held))
        groups = db.groups(db.parse_tasks(FIXTURE))
        claims = {g.id: db.Claim(frozenset({"x/" + g.id + ".cs"}), frozenset(), frozenset(), False) for g in groups}
        claims["S3"] = mine
        ok = {g.id: (True, "") for g in groups}
        self.assertNotIn("S3", db.pick(groups, ok, claims, [], [held], 5))

    def test_an_in_progress_row_naming_a_branch_claims_what_the_branch_changed(self):
        foreign = "| — | 1690 | Another session's fix | `fix/other`, `.claude/worktrees/other` | started |"
        text = FIXTURE.replace(IN_PROGRESS.splitlines()[4], foreign)
        row = db.parse_tasks(text).in_progress[0]
        self.assertEqual("fix/other", db.row_branch(row))
        self.assertIsNone(db.row_branch(db._row("In progress", "| — | 1690 | x | #1690 | open |")))
        self.assertTrue(db.in_progress_claim(row, {1690: "no files named"}).unknown)
        held = db.in_progress_claim(row, {1690: "no files named"}, frozenset({"src/gateway/program.cs"}))
        self.assertFalse(held.unknown)
        self.assertEqual({"src/gateway/program.cs"}, held.paths)
        bodies = {1682: "a1.cs", 1683: "a2.cs", 1681: "src/gateway/program.cs", 1685: "a.cs", 1686: "b.cs",
                  1440: "docs/x.md", 1690: "no files named"}
        asked = []
        lines = db.board(text, TODAY, bodies, set(),
                         lambda branch: asked.append(branch) or frozenset({"src/gateway/program.cs"}))
        self.assertEqual(["fix/other"], asked)
        self.assertIn("S3 | wave 1 | ready | taken by #1690", lines[1])
        self.assertIn("S1 | wave 1 | ready | free", lines[0])

    def test_branch_paths_read_the_diff_from_origin_main_local_first_then_the_remote(self):
        calls = []

        def fake(root, *args):
            calls.append(args)
            if args[2] == "origin/main...fix/other":
                raise db.Refusal("unknown revision")
            return "src/Gateway/Program.cs\ndocs/A.md\n\n"
        with mock.patch.object(db, "git", fake):
            self.assertEqual({"src/gateway/program.cs", "docs/a.md"}, db.branch_paths(".", "fix/other"))
        self.assertEqual([("diff", "--name-only", "origin/main...fix/other", "--"),
                          ("diff", "--name-only", "origin/main...origin/fix/other", "--")], calls)
        with mock.patch.object(db, "git", side_effect=db.Refusal("no such ref")):
            self.assertEqual(frozenset(), db.branch_paths(".", "fix/other"))
        with mock.patch.object(db, "git", side_effect=AssertionError("git was asked")):
            self.assertEqual(frozenset(), db.branch_paths(".", "-x"))
            self.assertEqual(frozenset(), db.branch_paths(".", None))

    def test_pick_takes_the_earliest_wave_then_table_order(self):
        groups = db.groups(db.parse_tasks(tasks_of(
            line(number=1, gid="B1", wave=2), line(number=2, gid="A1", wave=1), line(number=3, gid="A2", wave=1))))
        claims = {g.id: self.claim({g.id.lower() + ".cs"}) for g in groups}
        ok = {g.id: (True, "") for g in groups}
        self.assertEqual(["A1", "A2", "B1"], db.pick(groups, ok, claims, [], [], 3))
        self.assertEqual(["A1"], db.pick(groups, ok, claims, [], [], 1))

    def test_a_later_wave_runs_beside_an_earlier_one_when_free(self):
        groups = db.groups(db.parse_tasks(tasks_of(
            line(number=1, gid="A1", wave=1), line(number=2, gid="B1", wave=2), line(number=3, gid="B2", wave=2))))
        claims = {"A1": self.claim({"a.cs"}), "B1": self.claim({"a.cs"}), "B2": self.claim({"b.cs"})}
        ok = {g.id: (True, "") for g in groups}
        self.assertEqual(["B2"], db.pick(groups, ok, claims, ["A1"], [], 2))
        not_ready = dict(ok, B2=(False, "waits on a human"))
        self.assertEqual([], db.pick(groups, not_ready, claims, ["A1"], [], 2))

    def test_pick_never_returns_more_than_the_free_slots(self):
        groups = db.groups(db.parse_tasks(tasks_of(*[line(number=n, gid=f"G{n}") for n in range(1, 6)])))
        claims = {g.id: self.claim({g.id.lower() + ".cs"}) for g in groups}
        ok = {g.id: (True, "") for g in groups}
        self.assertEqual(2, len(db.pick(groups, ok, claims, [], [], 2)))
        self.assertEqual([], db.pick(groups, ok, claims, [], [], 0))

    def test_pick_keeps_two_overlapping_groups_apart(self):
        groups = db.groups(db.parse_tasks(tasks_of(line(number=1, gid="A1"), line(number=2, gid="A2"))))
        claims = {"A1": self.claim({"a.cs"}), "A2": self.claim({"src/a.cs"})}
        ok = {g.id: (True, "") for g in groups}
        self.assertEqual(["A1"], db.pick(groups, ok, claims, [], [], 2))

    def test_a_running_groups_issue_named_by_a_candidate_holds_it_back(self):
        groups = db.groups(db.parse_tasks(tasks_of(
            line(number=1, gid="A1"), line(number=2, gid="A2"))))
        claims = {"A1": self.claim({"a.cs"}), "A2": db.Claim(frozenset({"b.cs"}), frozenset(), frozenset({1}), False)}
        ok = {g.id: (True, "") for g in groups}
        self.assertEqual([], db.pick(groups, ok, claims, ["A1"], [], 2))


class WriteTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)
        self.path = self.dir / "todo.md"
        self.run_dir = self.dir / "run"

    def write(self, text):
        with open(self.path, "w", encoding="utf-8", newline="") as handle:
            handle.write(text)

    def read(self):
        with open(self.path, encoding="utf-8", newline="") as handle:
            return handle.read()

    def cli(self, *args):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = db.main(["tasks", args[0], "--tasks", str(self.path), "--run", str(self.run_dir), *args[1:]])
        return code, err.getvalue()

    def s1(self):
        return next(g for g in db.groups(db.parse_tasks(FIXTURE)) if g.id == "S1")

    def test_a_move_puts_the_rows_in_progress_with_branch_and_worktree(self):
        text = db.move_to_in_progress(FIXTURE, self.s1(), "fix/s1", ".claude/worktrees/s1", TODAY)
        progress = section(text, "In progress")
        self.assertIn("| S1 | #1682 | Narrow the read grant", progress)
        self.assertIn("| S1 | #1683 |", progress)
        self.assertIn("`fix/s1`, `.claude/worktrees/s1` | dispatched 2026-11-01 |", progress)
        self.assertIn("owed (not filed)", progress)
        remaining = section(text, "Remaining")
        self.assertNotIn("1682", remaining)
        self.assertIn("| 1 | S3 | 1681 |", remaining)
        again = db.parse_tasks(text)
        self.assertEqual(3, len(again.in_progress))

    def test_done_deletes_the_rows_and_writes_none_when_empty(self):
        moved = db.move_to_in_progress(FIXTURE, self.s1(), "b", "w", TODAY)
        keys = {r.key for r in db.parse_tasks(moved).in_progress}
        self.assertEqual(keys, {"#1682", "#1683", db.parse_tasks(FIXTURE).in_progress[0].key})
        left = db.delete_in_progress(moved, {"#1682"})
        self.assertEqual(2, len(db.parse_tasks(left).in_progress))
        empty = db.delete_in_progress(moved, keys)
        self.assertEqual([], db.parse_tasks(empty).in_progress)
        self.assertIn("## In progress\n\nnone\n\n## Remaining", empty)

    def test_back_returns_the_rows_to_their_wave_marked_owner(self):
        rows = [r.line for r in self.s1().rows]
        moved = db.move_to_in_progress(FIXTURE, self.s1(), "b", "w", TODAY)
        back = db.return_to_remaining(moved, rows, {"#1682", "#1683"})
        tasks = db.parse_tasks(back)
        # S1's wave is 1, and the S3 row already there stays first: a returned group joins the end of its wave.
        self.assertEqual(["#1681", "#1682", "#1683", "#1685", "#1686", "#1440"], [r.key for r in tasks.remaining])
        self.assertEqual({"OWNER"}, {r.cells[db.DEPENDS] for r in tasks.remaining[1:3]})
        self.assertEqual(1, len(tasks.in_progress))
        s1 = next(g for g in db.groups(tasks) if g.id == "S1")
        self.assertEqual((False, "waits on a human"), db.ready(s1, set(), TODAY))
        # A row with a dependency keeps it behind the mark; one returning to a later wave lands in it.
        late = db.return_to_remaining(
            db.move_to_in_progress(FIXTURE, next(g for g in db.groups(db.parse_tasks(FIXTURE)) if g.id == "J1"),
                                   "b", "w", TODAY),
            [r.line for r in db.parse_tasks(FIXTURE).remaining[-1:]], {"#1440"})
        back_row = next(r for r in db.parse_tasks(late).remaining if r.number == 1440)
        self.assertEqual("OWNER; 2026-11-09", back_row.cells[db.DEPENDS])

    def test_a_row_not_where_it_should_be_is_refused(self):
        group = self.s1()
        moved = db.move_to_in_progress(FIXTURE, group, "b", "w", TODAY)
        with self.assertRaisesRegex(db.Refusal, "#1682 is not in Remaining"):
            db.move_to_in_progress(moved, group, "b", "w", TODAY)
        with self.assertRaisesRegex(db.Refusal, "#9 is not in In progress"):
            db.delete_in_progress(FIXTURE, {"#9"})
        with self.assertRaisesRegex(db.Refusal, "already in a table"):
            db.return_to_remaining(FIXTURE, [r.line for r in group.rows], set())

    def test_place_adds_a_filed_issue_to_its_wave_and_group(self):
        text = db.place(FIXTURE, 1, "S1", 1700, task="New finding in `a.cs`")
        remaining = db.parse_tasks(text).remaining
        self.assertEqual([1682, 1683, 1700, 1681], [r.number for r in remaining[:4]])
        new = remaining[2]
        self.assertEqual(("S1", 1, "Opus", "OWNER"), (new.group, new.wave, new.cells[db.MODEL], new.cells[db.DEPENDS]))
        fresh = db.place(FIXTURE, 3, "Q1", 1701, depends="none", model="Sonnet")
        self.assertEqual(1701, db.parse_tasks(fresh).remaining[-1].number)
        with self.assertRaisesRegex(db.Refusal, "already has a row"):
            db.place(FIXTURE, 1, "S1", 1682)
        with self.assertRaisesRegex(db.Refusal, "S1 spans waves 1 and 2"):
            db.place(FIXTURE, 2, "S1", 1702)

    def test_a_move_rereads_the_file_and_keeps_another_sessions_edit(self):
        self.write(FIXTURE)

        def fetch(_):
            # Another session's edit lands after `board` has read the file and before the move.
            self.write(FIXTURE.replace("| 1683 | ", "| 1683 | x "))
            return "a.cs"
        with contextlib.redirect_stdout(io.StringIO()):
            db.main(["board", "--tasks", str(self.path), "--today", "2026-11-01"],
                    body_source=fetch, open_source=set, branch_source=NO_BRANCH)
        events = []
        real_read, real_write = db._read, db._write
        with mock.patch.object(db, "_read", lambda p: events.append(("read", Path(p).name)) or real_read(p)), \
                mock.patch.object(db, "_write", lambda p, t: events.append(("write", Path(p).name)) or real_write(p, t)):
            self.assertEqual((0, ""), self.cli("move", "S1"))
        # The tasks file is read once, then written, with nothing carried over from `board`.
        self.assertEqual([("read", "todo.md"), ("write", "todo.md")], events[:2])
        text = self.read()
        self.assertIn("| S1 | #1683 | x Narrow read", section(text, "In progress"))
        self.assertIn("| S1 | #1682 |", section(text, "In progress"))

    def test_a_refused_write_leaves_no_temporary_file_and_no_state_record(self):
        self.write(FIXTURE)
        with mock.patch.object(db.os, "replace", side_effect=PermissionError("held open")):
            code, err = self.cli("move", "S1")
        self.assertEqual(2, code)
        self.assertIn("refused: could not write", err)
        self.assertEqual(FIXTURE, self.read())
        self.assertEqual(["todo.md"], sorted(p.name for p in self.dir.iterdir() if p.is_file()))
        self.assertFalse((self.run_dir / "state.json").exists())

    def test_a_move_records_the_rows_in_the_runs_state_and_back_reads_them_there(self):
        self.write(FIXTURE)
        self.assertEqual((0, ""), self.cli("move", "S1"))
        entry = db.load_state(self.run_dir)["groups"]["S1"]
        self.assertEqual(["#1682", "#1683"], entry["keys"])
        self.assertEqual([r.line for r in self.s1().rows], entry["rows"])
        self.assertFalse((self.run_dir / "groups").exists())
        self.assertEqual((0, ""), self.cli("back", "S1"))
        self.assertEqual("returned", db.load_state(self.run_dir)["groups"]["S1"]["status"])
        self.write(FIXTURE)
        db.save_state(self.run_dir, {"groups": {}})
        moved = db.move_to_in_progress(FIXTURE, self.s1(), "b", "w", TODAY)
        self.write(moved)
        code, err = self.cli("back", "S1")
        self.assertEqual(2, code)
        self.assertIn("does not record S1", err)

    def test_a_group_id_that_is_not_a_letter_and_number_is_refused(self):
        self.write(FIXTURE)
        for bad in ("../x", "s1", "S1/../../y"):
            code, err = self.cli("move", bad)
            self.assertEqual(2, code, bad)
            self.assertIn("is not a group id", err)
        self.assertFalse((self.dir / "x.json").exists())

    def test_place_refuses_a_group_an_in_progress_row_holds(self):
        moved = db.move_to_in_progress(FIXTURE, self.s1(), "b", "w", TODAY)
        with self.assertRaisesRegex(db.Refusal, "S1 is held by an In progress row"):
            db.place(moved, 1, "S1", 1700)

    def test_the_old_four_column_in_progress_header_is_refused(self):
        old = FIXTURE.replace("| Group | # | What | Branch or PR | State |", "| # | What | Branch or PR | State |")
        with self.assertRaisesRegex(db.Refusal, "In progress header"):
            db.parse_tasks(old)

    def test_a_running_group_no_longer_a_candidate_still_holds_the_group_naming_it(self):
        moved = db.move_to_in_progress(FIXTURE, next(g for g in db.groups(db.parse_tasks(FIXTURE))
                                                      if g.id == "M1"), "b", "w", TODAY)
        tasks = db.parse_tasks(moved)
        running = [r for r in tasks.in_progress if r.group == "M1"][0]
        held = db.in_progress_claim(running, {1685: "src/m1.cs"})
        self.assertEqual("M1", held.ident)
        m2 = next(g for g in db.groups(tasks) if g.id == "M2")
        claim = db.group_claim(m2, {1686: "src/m2.cs"})
        self.assertEqual([], db.pick([m2], {"M2": (True, "")}, {"M2": claim}, [], [held], 2))
        free = db.pick([m2], {"M2": (True, "")}, {"M2": claim}, [], [], 2)
        self.assertEqual(["M2"], free)

    def test_the_cli_moves_finishes_and_returns_a_group(self):
        self.write(FIXTURE)
        self.assertEqual((0, ""), self.cli("move", "S1", "--branch", "fix/s1", "--worktree", "wt"))
        self.assertIn("`fix/s1`, `wt`", self.read())
        self.assertEqual((0, ""), self.cli("back", "S1"))
        self.assertEqual(1, len(db.parse_tasks(self.read()).in_progress))
        back = {r.number: r for r in db.parse_tasks(self.read()).remaining}
        self.assertEqual("OWNER", back[1682].cells[db.DEPENDS])
        self.write(FIXTURE)
        self.cli("move", "S1")
        self.assertEqual((0, ""), self.cli("done", "S1"))
        self.assertEqual(1, len(db.parse_tasks(self.read()).in_progress))
        self.assertNotIn("1682", self.read())

    def test_the_cli_refuses_with_exit_two_and_writes_nothing(self):
        self.write(FIXTURE)
        code, err = self.cli("move", "Q9")
        self.assertEqual(2, code)
        self.assertIn("refused: Q9 is not a group in Remaining", err)
        code, err = self.cli("done", "S1")
        self.assertEqual(2, code)
        self.assertEqual(FIXTURE, self.read())
        self.write(FIXTURE.replace("## Paused", "## Parked"))
        self.assertEqual(2, self.cli("move", "S1")[0])

    def test_the_cli_places_a_row(self):
        self.write(FIXTURE)
        self.assertEqual((0, ""), self.cli("place", "1", "S3", "1700", "--depends", "none"))
        self.assertEqual([1681, 1700], [r.number for r in db.groups(db.parse_tasks(self.read()))[1].rows])

    def test_the_files_line_endings_survive_a_write(self):
        self.write(FIXTURE.replace("\n", "\r\n"))
        self.assertEqual((0, ""), self.cli("move", "S1"))
        text = self.read()
        self.assertEqual(text.count("\n"), text.count("\r\n"))
        self.assertIn("| S1 | #1682 |", text)
        self.assertEqual((0, ""), self.cli("done", "S1"))
        text = self.read()
        self.assertEqual(text.count("\n"), text.count("\r\n"))
        self.assertEqual(1, len(db.parse_tasks(text).in_progress))


class BoardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "todo.md"
        with open(self.path, "w", encoding="utf-8", newline="") as handle:
            handle.write(FIXTURE)

    def run_board(self, calls, extra=()):
        def fetch(n):
            calls.append(n)
            return {1682: "touches definitions.json", 1683: "deploy/rabbitmq/definitions.json",
                    1681: "src/gateway/program.cs", 1685: "a.cs", 1686: "b.cs", 1440: "docs/x.md"}.get(n, "")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = db.main(["board", "--tasks", str(self.path), "--today", "2026-11-01", *extra],
                           body_source=fetch, open_source=lambda: {1440}, branch_source=NO_BRANCH)
        return code, out.getvalue().splitlines()

    def test_board_prints_one_line_per_group(self):
        code, lines = self.run_board([])
        self.assertEqual(0, code)
        self.assertEqual(5, len(lines))
        self.assertTrue(lines[0].startswith("S1 | wave 1 | ready | free | "), lines[0])
        self.assertIn("J1 | wave 2 | not ready: waits until 2026-11-09", lines[4])

    def test_board_caches_bodies_under_the_run(self):
        calls = []
        run = Path(self.tmp.name) / "run"
        self.run_board(calls, ("--run", str(run)))
        self.assertEqual(6, len(calls))
        self.assertTrue((run / "bodies" / "1682.txt").is_file())
        again = []
        self.run_board(again, ("--run", str(run)))
        self.assertEqual([], again)

    def test_board_reads_bodies_and_the_open_set_through_the_helpers(self):
        # No source injected: the real ones run the repository's helpers through Git's bash, here stubs.
        root = Path(self.tmp.name)
        bodies = {1682: "touches definitions.json", 1683: "deploy/rabbitmq/definitions.json",
                  1681: "src/gateway/program.cs", 1685: "a.cs", 1686: "b.cs", 1440: "docs/x.md"}
        cases = "".join(f"  {n}) echo '{json.dumps({'number': n, 'title': 't', 'state': 'OPEN', 'body': b})}' ;;\n"
                        for n, b in bodies.items())
        stub(root, "gh-issue-text.sh", f'case "$1" in\n{cases}esac\n')
        listed = [{"number": 1440, "title": "t", "state": "OPEN", "labels": []},
                  {"number": 1682, "title": "t", "state": "CLOSED", "labels": []}]
        stub(root, "gh-issue-list.sh", f"echo '{json.dumps(listed)}'\n")
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = db.main(["board", "--tasks", str(self.path), "--today", "2026-11-01", "--root", str(root),
                            "--run", str(root / "run")], branch_source=NO_BRANCH)
        self.assertEqual(0, code, err.getvalue())
        lines = out.getvalue().splitlines()
        self.assertTrue(lines[0].startswith("S1 | wave 1 | ready | free | "), lines[0])
        self.assertIn("t\ntouches definitions.json", (root / "run" / "bodies" / "1682.txt").read_text("utf-8"))
        self.assertEqual({1440}, db.open_issues(str(root)))

    def test_a_helper_that_fails_is_a_refusal(self):
        root = Path(self.tmp.name)
        stub(root, "gh-issue-text.sh", "echo 'gh: not logged in' >&2\nexit 4\n")
        with self.assertRaisesRegex(db.Refusal, "gh-issue-text.sh 1682 exited 4: gh: not logged in"):
            db.issue_body(str(root), 1682)


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
MERGED_S1 = {"group": "S1", "issues": [1682, 1683], "outcome": "merged", "pr": 1690, "merge_oid": OID,
             "rounds": [{"kind": "full", "raised": 5, "fixed": 5, "refused": 0},
                        {"kind": "recheck", "raised": 2, "fixed": 1, "refused": 1}],
             "review_end": "clean", "filed": [1701], "reason": None,
             "workspace": {"worktree": WORKTREE, "removed": False},
             "decisions": ["kept the narrower grant, the issue's recommended option"]}
HANDED_S1 = {**MERGED_S1, "outcome": "handed-back", "pr": None, "merge_oid": None, "review_end": None,
             "reason": "needs a provider key only the owner has"}
USAGE = {"input_tokens": 20, "cache_creation_input_tokens": 5265, "cache_read_input_tokens": 754597,
         "output_tokens": 2814, "output_tokens_details": {"thinking_tokens": 199}}
# The spike's envelope keys (artifacts/dispatch/spike.md); its values trimmed.
SUCCESS = {"type": "result", "subtype": "success", "is_error": False, "api_error_status": None,
           "stop_reason": "end_turn", "terminal_reason": "completed", "num_turns": 9,
           "session_id": "43b7cd26-0682-471a-a3e5-98c841a5c448", "total_cost_usd": 1.886, "usage": USAGE,
           "permission_denials": [], "result": "PR #1690 is merged; the rate limit docs were not touched."}
# No limit was observed in the spike, so these are built from the envelope's structured fields and the SDK's
# rate_limit error kind (ruling R9); they await the first real limit.
LIMIT_429 = {**SUCCESS, "subtype": "error_during_execution", "is_error": True, "api_error_status": 429,
             "terminal_reason": "error", "result": "Claude usage limit reached. Your limit resets at "
                                                   "2026-11-02T01:30:00Z."}
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
    return db.run_dir(".", run)


def record(run, gid, pid=None, **fields):
    """A group as `tasks move` and `launch` leave it, with `fields` over it; a pid goes where `run` puts it."""
    directory = state_dir(run)
    if not (directory / "state.json").is_file():
        db.save_state(directory, {"run": run, "pairs": 2, "lang": "en", "groups": {}})
    rows = S1_ROWS if gid == "S1" else [line(gid=gid, number=1700 + int(gid[1:]))]
    parsed = [db._row("Remaining", row) for row in rows]
    slug = gid.lower()
    entry = {"keys": [r.key for r in parsed], "rows": rows, "issues": [r.number for r in parsed], "model": "Opus",
             "branch": f"chore/dispatch-{slug}", "worktree": f".claude/worktrees/dispatch-{slug}", "crashes": 0}
    entry.update(fields)
    os.makedirs(entry["worktree"], exist_ok=True)
    state = db.load_state(directory)
    state["groups"][gid] = entry
    db.save_state(directory, state)
    if pid is not None:
        (directory / f"{gid}.pid").write_text(f"{pid}\n", encoding="utf-8")


def group_state(run, gid):
    return db.load_state(state_dir(run))["groups"][gid]


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
    if not (state_dir() / "state.json").is_file() or "S1" not in db.load_state(state_dir())["groups"]:
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE)
    with mock.patch.object(db, "CLAUDE_COMMAND", [sys.executable, str((fake / "claude.py").resolve())]), \
            contextlib.redirect_stdout(io.StringIO()):
        assert db.main(["run", RUN, "S1", "--description", description]) == 0
    return json.loads((state_dir() / "S1.session.json").read_text(encoding="utf-8"))


def finished_pid():
    child = subprocess.Popen([sys.executable, "-c", "pass"])
    child.wait()
    return child.pid


def end(**fields):
    base = {"exit_code": 0, "is_error": False, "rate_limited": False, "resets_at": None, "usage": {}, "denials": []}
    return db.SessionEnd(**{**base, **fields})


class InRoot(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        here = os.getcwd()
        os.chdir(self.tmp.name)
        self.addCleanup(os.chdir, here)
        self.root = Path(self.tmp.name)
        self.clock = NOW
        patcher = mock.patch.object(db, "now", lambda: self.clock)
        patcher.start()
        self.addCleanup(patcher.stop)


class RunDirectoryTests(InRoot):
    def test_pairs_outside_one_to_four_is_refused(self):
        for pairs in (0, 5, -1):
            with self.assertRaisesRegex(db.Refusal, f"--pairs {pairs} is outside 1 to 4"):
                db.new_run(".", pairs, "en", NOW)
        self.assertIn("refused: --pairs 5", run_cli(["start", "--pairs", "5"]))
        self.assertFalse((self.root / "artifacts").exists())
        self.assertEqual(RUN, db.new_run(".", 4, "en", NOW))

    def test_lang_defaults_to_en(self):
        self.assertEqual(f"{RUN}\n", run_cli(["start", "--pairs", "2"]))
        state = db.load_state(state_dir())
        self.assertEqual(("en", 2, {}), (state["lang"], state["pairs"], state["groups"]))
        self.clock = NOW + timedelta(hours=1)
        (state_dir() / "report.md").write_text("done", encoding="utf-8")
        run_cli(["start", "--pairs", "1", "--lang", "ru"])
        self.assertEqual("ru", db.load_state(state_dir("20261101-230000"))["lang"])
        self.assertIn("refused: --lang", run_cli(["start", "--pairs", "1", "--lang", "../x"]))

    def test_a_second_start_with_a_run_open_names_it_and_exits_3(self):
        run_cli(["start", "--pairs", "2"])
        self.clock = NOW + timedelta(minutes=5)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(3, db.main(["start", "--pairs", "3"]))
        self.assertEqual(f"{RUN}\n", out.getvalue())
        self.assertEqual(RUN, db.open_run("."))
        (state_dir() / "report.md").write_text("done", encoding="utf-8")
        self.assertIsNone(db.open_run("."))
        os.makedirs("artifacts/dispatch/spike-notes")
        self.assertIsNone(db.open_run("."))

    def test_a_run_name_that_is_not_a_timestamp_is_refused(self):
        for bad in ("../x", "20261101", "20261101-22000", "20261101-220000/..", ""):
            with self.subTest(bad=bad), self.assertRaisesRegex(db.Refusal, "is not a yyyymmdd-hhmmss timestamp"):
                db.run_dir(".", bad)
        self.assertEqual(Path("artifacts/dispatch") / RUN, db.run_dir("", RUN))
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
                         "20261101-220000", db.prompt(self.s1(), "Narrow the RabbitMQ read grants", RUN))
        s3 = next(g for g in db.groups(db.parse_tasks(FIXTURE)) if g.id == "S3")
        self.assertTrue(db.prompt(s3, "Bind the switch keys", RUN).startswith("/ship #S3-1681 Bind"))
        with self.assertRaises(db.Refusal):
            db.prompt(self.s1(), "fine", "../x")

    def test_a_description_carrying_a_token_is_refused(self):
        for bad in ("Narrow --dispatched artifacts/x", "Narrow --SLEEP", "two\nlines", "two\rlines",
                    "two\u2028lines", "   "):
            with self.subTest(bad=bad), self.assertRaises(db.Refusal):
                db.prompt(self.s1(), bad, RUN)

    def test_a_model_outside_opus_and_sonnet_is_refused(self):
        self.assertEqual(("opus", "sonnet"), (db.model_flag("Opus"), db.model_flag("Sonnet")))
        for bad in ("—", "Haiku", "opus", ""):
            with self.subTest(bad=bad), self.assertRaisesRegex(db.Refusal, "is not one of Opus, Sonnet"):
                db.model_flag(bad)
        self.write_tasks(FIXTURE.replace("| security, code | none | Opus |", "| security, code | none | — |"))
        self.move("S1")
        porcelain = f"worktree {self.root / WORKTREE}\nHEAD {HEAD}\nbranch refs/heads/{BRANCH}\n\n"
        with mock.patch.object(db, "git", lambda root, *args: porcelain):
            out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE])
        self.assertIn("refused: model '—' is not one of Opus, Sonnet", out)

    def write_tasks(self, text):
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(text)

    def move(self, gid, branch=BRANCH, worktree=WORKTREE):
        db.save_state(state_dir(), {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        self.assertEqual("", run_cli(["tasks", "move", "--tasks", "todo.md", "--run", str(state_dir()), gid,
                                      "--branch", branch, "--worktree", worktree]))
        make_worktree(worktree)

    def test_launch_records_the_group_after_its_move_on_a_worktree_git_shows_on_its_branch(self):
        self.write_tasks(FIXTURE)
        self.move("S1")
        listed = (f"worktree {self.root.as_posix()}\nHEAD {HEAD}\nbranch refs/heads/main\n\n"
                  f"worktree {(self.root / WORKTREE).as_posix()}\nHEAD {HEAD}\nbranch refs/heads/{BRANCH}\n\n")
        calls = []
        with mock.patch.object(db, "git", lambda root, *args: calls.append(args) or listed):
            out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE])
        self.assertEqual("", out)
        self.assertEqual([("worktree", "list", "--porcelain")], calls)
        entry = group_state(RUN, "S1")
        self.assertEqual(("running", "Opus", [1682, 1683], BRANCH, WORKTREE, 0),
                         (entry["status"], entry["model"], entry["issues"], entry["branch"], entry["worktree"],
                          entry["crashes"]))
        self.assertEqual(S1_ROWS, entry["rows"])
        for wrong in (listed.replace(f"refs/heads/{BRANCH}", "refs/heads/other"), listed.split("\n\n")[0] + "\n\n"):
            with self.subTest(wrong=wrong), mock.patch.object(db, "git", lambda root, *args: wrong):
                out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", BRANCH,
                               "--worktree", WORKTREE])
                self.assertIn(f"refused: {WORKTREE} is not a worktree on {BRANCH}", out)
        with mock.patch.object(db, "git", lambda root, *args: listed):
            out = run_cli(["launch", RUN, "S1", "--tasks", "todo.md", "--branch", "fix/other",
                           "--worktree", WORKTREE])
        self.assertIn("refused: S1's In progress rows name `chore/dispatch-s1`, not fix/other", out)
        with mock.patch.object(db, "git", lambda root, *args: listed):
            out = run_cli(["launch", RUN, "S3", "--tasks", "todo.md", "--branch", BRANCH, "--worktree", WORKTREE])
        self.assertIn("refused: S3 is not in this run", out)

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
        with mock.patch.object(db, "CLAUDE_COMMAND", None), mock.patch.object(db.shutil, "which", lambda _: None):
            self.assertIn("refused: no claude on PATH", run_cli(["run", RUN, "S1", "--description", "x"]))
        record(RUN, "S1", status="handed-back")
        self.assertIn("refused: S1 is handed-back, not waiting for an executor",
                      run_cli(["run", RUN, "S1", "--description", "x"]))


class ResultTests(unittest.TestCase):
    def check(self, data):
        return db.validate_result(data, "S1", [1682, 1683])

    def test_a_merged_result_needs_a_pr_and_a_whole_oid(self):
        got = self.check(MERGED_S1)
        self.assertIsInstance(got, db.Result)
        self.assertEqual((1690, OID, (1701,)), (got.pr, got.merge_oid, got.filed))
        for change in ({"pr": None}, {"pr": 0}, {"pr": True}, {"pr": "1690"}, {"merge_oid": None},
                       {"merge_oid": OID[:9]}, {"merge_oid": OID.upper()}, {"merge_oid": OID + "0"},
                       {"review_end": None}):
            with self.subTest(change=change):
                self.assertIsInstance(self.check({**MERGED_S1, **change}), db.Invalid)

    def test_a_hand_back_or_stop_needs_a_reason(self):
        self.assertIsInstance(self.check(HANDED_S1), db.Result)
        self.assertIsInstance(self.check({**HANDED_S1, "outcome": "stopped", "review_end": "ceiling"}), db.Result)
        for outcome in ("handed-back", "stopped"):
            for reason in (None, "", "   ", 7):
                with self.subTest(outcome=outcome, reason=reason):
                    self.assertIsInstance(self.check({**HANDED_S1, "outcome": outcome, "reason": reason}),
                                          db.Invalid)

    def test_each_round_is_full_or_recheck_with_three_counts(self):
        good = {"kind": "recheck", "raised": 0, "fixed": 0, "refused": 0}
        self.assertIsInstance(self.check({**MERGED_S1, "rounds": [good]}), db.Result)
        self.assertIsInstance(self.check({**HANDED_S1, "rounds": []}), db.Result)
        for bad in ({**good, "kind": "partial"}, {k: v for k, v in good.items() if k != "refused"},
                    {**good, "raised": -1}, {**good, "fixed": True}, {**good, "fixed": 1.0}, {**good, "extra": 1},
                    "full"):
            with self.subTest(bad=bad):
                self.assertIsInstance(self.check({**MERGED_S1, "rounds": [bad]}), db.Invalid)
        for field, value in (("rounds", {}), ("filed", [0]), ("filed", ["1701"]), ("decisions", [3]),
                             ("workspace", {"worktree": WORKTREE}), ("workspace", {"worktree": 1, "removed": False})):
            with self.subTest(field=field, value=value):
                self.assertIsInstance(self.check({**MERGED_S1, field: value}), db.Invalid)

    def test_an_outcome_or_review_end_outside_the_spec_is_invalid(self):
        for change in ({"outcome": "landed"}, {"outcome": None}, {"review_end": "tired"},
                       {"review_end": "Clean"}):
            with self.subTest(change=change):
                self.assertIsInstance(self.check({**MERGED_S1, **change}), db.Invalid)
        for review_end in db.REVIEW_ENDS:
            self.assertIsInstance(self.check({**MERGED_S1, "review_end": review_end}), db.Result)

    def test_a_missing_or_unparsable_file_is_no_result(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "S1.json"
            self.assertIsNone(db.load_result(path, "S1", [1682, 1683]))
            for text in (b"{not json", b"\xff\xfe", b"[]", b"null"):
                path.write_bytes(text)
                got = db.load_result(path, "S1", [1682, 1683])
                self.assertIsInstance(got, db.Invalid, text)
                self.assertEqual("relaunch", db.classify(end(exit_code=0), got, 0, NOW))
            path.write_text(json.dumps(MERGED_S1), encoding="utf-8")
            self.assertIsInstance(db.load_result(path, "S1", [1682, 1683]), db.Result)
        self.assertEqual("relaunch", db.classify(end(exit_code=1, is_error=True), None, 0, NOW))

    def test_a_result_for_another_group_is_no_result(self):
        for change in ({"group": "S2"}, {"issues": [1682]},
                       {"verdict": "merged"}):
            with self.subTest(change=change):
                got = db.validate_result({**MERGED_S1, **change}, "S1",
                                         [1682, 1683])
                self.assertIsInstance(got, db.Invalid)
        for change in ({"issues": [1683, 1682]},):
            self.assertIsInstance(self.check({**MERGED_S1, **change}), db.Result)
        for change in ({"issues": [1682, 1682]}, {"issues": [1682, 1683, 1684]}, {"issues": "1682+1683"}):
            self.assertIsInstance(self.check({**MERGED_S1, **change}), db.Invalid)
        missing = {k: v for k, v in MERGED_S1.items() if k != "decisions"}
        self.assertIsInstance(self.check(missing), db.Invalid)
        self.assertIsInstance(self.check([MERGED_S1]), db.Invalid)


class ClassifyTests(InRoot):
    def result(self, data=MERGED_S1):
        return db.validate_result(data, "S1", [1682, 1683])

    def test_merged_is_claimed_and_awaits_verification(self):
        self.assertEqual("merged-claimed", db.classify(end(), self.result(), 0, NOW))
        # A valid result wins over the exit: the session may die after writing it.
        self.assertEqual("merged-claimed", db.classify(end(exit_code=1, is_error=True), self.result(), 1, NOW))

    def test_handed_back_and_stopped_are_never_relaunched(self):
        self.assertEqual("handed-back", db.classify(end(), self.result(HANDED_S1), 0, NOW))
        stopped = self.result({**HANDED_S1, "outcome": "stopped"})
        self.assertEqual("stopped", db.classify(end(exit_code=1, is_error=True), stopped, 0, NOW))
        limited = end(is_error=True, rate_limited=True, resets_at=NOW + timedelta(hours=1))
        self.assertEqual("stopped", db.classify(limited, stopped, 0, NOW))

    def test_a_limit_resetting_within_five_hours_is_the_window(self):
        for later in (timedelta(hours=1), timedelta(hours=5, minutes=10), timedelta(minutes=-30)):
            with self.subTest(later=later):
                limited = end(exit_code=1, is_error=True, rate_limited=True, resets_at=NOW + later)
                self.assertEqual("window", db.classify(limited, None, 1, NOW))
        got = db.session_end({"exit": 1, "stdout": json.dumps(LIMIT_429), "stderr": "",
                              "ended": NOW.isoformat()})
        self.assertEqual((True, True, datetime(2026, 11, 2, 1, 30, tzinfo=timezone.utc)),
                         (got.is_error, got.rate_limited, got.resets_at))
        self.assertEqual("window", db.classify(got, None, 0, NOW))

    def test_a_limit_resetting_later_is_the_week(self):
        for later in (timedelta(hours=5, minutes=11), timedelta(days=3)):
            limited = end(exit_code=1, is_error=True, rate_limited=True, resets_at=NOW + later)
            self.assertEqual("week", db.classify(limited, None, 0, NOW))
        text = "Weekly limit reached; resets 2026-11-05 09:00:00+00:00"
        got = db.session_end({"exit": 1, "stdout": text, "stderr": "", "ended": NOW.isoformat()})
        self.assertEqual(datetime(2026, 11, 5, 9, 0, tzinfo=timezone.utc), got.resets_at)
        self.assertEqual("week", db.classify(got, None, 0, NOW))

    def test_a_crash_relaunches_once_and_a_second_hands_back(self):
        crashed = db.session_end({"exit": 3221225786, "stdout": "", "stderr": "Error: socket hang up"})
        self.assertEqual((True, False), (crashed.is_error, crashed.rate_limited))
        self.assertEqual("relaunch", db.classify(crashed, None, 0, NOW))
        self.assertEqual("crash-hand-back", db.classify(crashed, None, 1, NOW))
        # A clean exit with no result is still a crash: the result is the only success.
        self.assertEqual("relaunch", db.classify(db.session_end({"exit": 0, "stdout": json.dumps(SUCCESS)}),
                                                 None, 0, NOW))

    def test_an_invalid_result_counts_as_a_crash(self):
        bad = self.result({**MERGED_S1, "group": "S2"})
        self.assertEqual("relaunch", db.classify(end(), bad, 0, NOW))
        self.assertEqual("crash-hand-back", db.classify(end(), bad, 1, NOW))

    def test_a_denial_with_no_result_is_a_hand_back(self):
        # spike.md, Prompts: a call the auto classifier refuses is a hand-back, never a widened grant at night.
        denied = {**SUCCESS, "is_error": True, "subtype": "error_during_execution",
                  "permission_denials": [{"tool_name": "Bash", "tool_use_id": "t1", "tool_input": {}}]}
        got = db.session_end({"exit": 1, "stdout": json.dumps(denied), "stderr": ""})
        self.assertEqual(["Bash"], got.denials)
        self.assertEqual("handed-back", db.classify(got, None, 0, NOW))

    def test_session_output_is_read_as_utf8_and_an_unparsed_reset_is_a_crash(self):
        raw = run_fake_claude(stdout="Usage limit reached — "
                              "лимит".encode("utf-8"),
                              code=1)
        end = db.session_end(raw)
        self.assertIn("—", raw["stdout"])
        self.assertTrue(end.rate_limited)
        self.assertIsNone(end.resets_at)
        self.assertEqual("relaunch", db.classify(end, None, 0, NOW))
        self.assertEqual("crash-hand-back", db.classify(end, None, 1, NOW))

    def test_the_envelope_is_read_by_its_structured_fields_first(self):
        ok = db.session_end({"exit": 0, "stdout": json.dumps(SUCCESS), "stderr": ""})
        # The success's result text says "rate limit": a phrase only counts on a session that failed.
        self.assertEqual((False, False, None), (ok.is_error, ok.rate_limited, ok.resets_at))
        self.assertEqual({k: USAGE[k] for k in db.TOKEN_KEYS}, ok.usage)
        self.assertEqual(SUCCESS["session_id"], ok.session_id)
        kind = db.session_end({"exit": 1, "stdout": json.dumps(LIMIT_KIND), "stderr": ""})
        self.assertTrue(kind.rate_limited)
        self.assertIsNone(kind.resets_at)
        on_stderr = db.session_end({"exit": 1, "stdout": "", "stderr": "error kind=rate_limit_error"})
        self.assertTrue(on_stderr.rate_limited)
        epoch = {**LIMIT_KIND, "error": {"type": "rate_limit", "resetsAt": int(NOW.timestamp()) + 3600}}
        self.assertEqual(NOW + timedelta(hours=1), db.session_end({"exit": 1, "stdout": json.dumps(epoch)}).resets_at)

    def test_a_clock_reset_is_the_next_such_time_after_the_session_ended(self):
        for text in ("Usage limit reached. Resets 11pm", "rate limit; resets at 23:00"):
            with self.subTest(text=text):
                got = db.session_end({"exit": 1, "stdout": text, "ended": NOW.isoformat()})
                local = got.resets_at.astimezone()
                self.assertEqual((23, 0), (local.hour, local.minute))
                self.assertTrue(NOW < got.resets_at <= NOW + timedelta(days=1))
        vague = db.session_end({"exit": 1, "stdout": "usage limit; resets in 5", "ended": NOW.isoformat()})
        self.assertIsNone(vague.resets_at)

    def test_token_keys_are_quality_sets_own(self):
        here = Path(__file__).resolve()
        owner = next((p / "tools" / "quality-sets" / "quality_sets.py" for p in here.parents
                      if (p / "tools" / "quality-sets" / "quality_sets.py").is_file()), None)
        self.assertIsNotNone(owner, "tools/quality-sets/quality_sets.py not found above this suite")
        tree = ast.parse(owner.read_text(encoding="utf-8"))
        value = next(ast.literal_eval(node.value) for node in tree.body if isinstance(node, ast.Assign)
                     and any(getattr(t, "id", None) == "TOKEN_KEYS" for t in node.targets))
        self.assertEqual(value, db.TOKEN_KEYS)


class ResultCommandTests(InRoot):
    def absorb(self, data, envelope=SUCCESS, code=0, crashes=0):
        record(RUN, "S1", status="running", branch=BRANCH, worktree=WORKTREE, crashes=crashes)
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
        with mock.patch.object(db, "PROJECTS", self.root / "projects"):
            return run_cli(["result", RUN, "S1"]).splitlines()

    def test_result_copies_validates_classifies_records_and_names_the_transcript(self):
        lines = self.absorb(MERGED_S1)
        self.assertEqual("S1: merged-claimed pr #1690", lines[0])
        slug = db.project_slug(os.path.abspath(WORKTREE))
        self.assertEqual(f"transcript: {self.root / 'projects' / slug / (SUCCESS['session_id'] + '.jsonl')}",
                         lines[1])
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

    def test_a_github_read_that_fails_or_another_pr_number_is_a_stop(self):
        self.fake_pr(number=1691)
        code, out = self.verify()
        self.assertEqual(3, code)
        self.assertEqual(f"not verified: #1690 is not a pull request from {BRANCH}\n", out)
        self.fake_pr()
        stub(self.root, "pr-state.sh", "echo 'gh: HTTP 502' >&2\nexit 1\n")
        code, out = self.verify()
        self.assertEqual(3, code)
        self.assertIn("not verified: GitHub could not be read: pr-state.sh 1690 exited 1", out)
        self.assertIn("merge not verified", group_state(RUN, "S1")["reason"])

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
            db.save_state(state_dir(), {"run": RUN, "pairs": pairs, "lang": "en", "groups": {}})
        with open("todo.md", "w", encoding="utf-8", newline="") as handle:
            handle.write(text)
        with mock.patch.object(db, "PROJECTS", self.root / "projects"):
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
        self.assertEqual(["stop file: no", "running: none", "free slots: 2", "end: drained"], lines)
        record(RUN, "S9", status="running")
        lines = self.wake(tasks_of(line(depends="OWNER")))
        self.assertEqual("launch: none", lines[-1])

    def test_wake_offers_no_more_than_pairs_minus_running(self):
        record(RUN, "S9", status="window", pid=os.getpid())
        lines = self.wake()
        self.assertIn("running: S9", lines)
        self.assertIn("free slots: 1", lines)
        self.assertEqual("launch: S1", lines[-1])
        record(RUN, "S8", status="relaunch")
        self.assertEqual(["free slots: 0", "launch: none"], self.wake()[-2:])

    def test_a_week_limit_ends_the_night_until_it_resets(self):
        record(RUN, "S9", status="week")
        self.assertEqual("end: week", self.wake()[-1])
        record(RUN, "S9", status="week", resets_at=(NOW + timedelta(days=2)).isoformat())
        self.assertEqual("end: week", self.wake()[-1])
        # A run resumed after the reset relaunches the group, which holds its slot meanwhile.
        record(RUN, "S9", status="week", resets_at=(NOW - timedelta(minutes=1)).isoformat())
        self.assertEqual(["running: S9", "free slots: 1", "launch: S1"], [self.wake()[i] for i in (1, 3, 4)])
        self.assertIn("relaunch S9", run_cli(["resume", RUN]))
        with mock.patch.object(db, "CLAUDE_COMMAND", [sys.executable, "-c", "print('{}')"]):
            self.assertEqual("", run_cli(["run", RUN, "S9", "--description", "Resume after the week"]))
        self.assertTrue((state_dir() / "S9.session.json").is_file())
        self.assertIn("S9: relaunch - no result; exit 0", run_cli(["result", RUN, "S9"]))

    def test_wake_names_each_running_executors_transcript(self):
        record(RUN, "S9", status="running", pid=os.getpid())
        folder = self.root / "projects" / db.project_slug(os.path.abspath(".claude/worktrees/dispatch-s9"))
        folder.mkdir(parents=True)
        (folder / "abc.jsonl").write_text("{}\n", encoding="utf-8")
        with mock.patch.object(db, "_created", lambda path: 9e12):
            lines = self.wake()
        self.assertIn(f"transcript S9: {folder / 'abc.jsonl'}", lines)

    def test_the_window_wait_is_capped_and_a_past_reset_returns_at_once(self):
        slept = []
        with mock.patch.object(db, "SLEEP", slept.append):
            for later, expected in ((timedelta(hours=10), 18600), (timedelta(hours=1), 3660),
                                    (timedelta(minutes=-5), 0), (timedelta(0), 0)):
                with self.subTest(later=later):
                    slept.clear()
                    record(RUN, "S1", status="window", resets_at=(NOW + later).isoformat())
                    self.assertIn("waited", run_cli(["wait", RUN, "S1"]))
                    self.assertEqual(expected, sum(slept))
                    self.assertTrue(all(step <= 60 for step in slept))
        self.assertEqual(18600, db.window_wait(NOW + timedelta(days=2), NOW))

    def test_a_stop_file_cuts_the_window_wait_short(self):
        slept = []

        def sleep(seconds):
            slept.append(seconds)
            (state_dir() / "STOP").write_text("", encoding="utf-8")
        record(RUN, "S1", status="window", resets_at=(NOW + timedelta(hours=1)).isoformat())
        with mock.patch.object(db, "SLEEP", sleep):
            self.assertIn("stop file: yes", run_cli(["wait", RUN, "S1"]))
        self.assertEqual([60], slept)
        record(RUN, "S1", status="running")
        self.assertIn("refused: S1 is running, not waiting on a window", run_cli(["wait", RUN, "S1"]))

    def test_alive_is_true_for_this_process_and_false_for_a_finished_child(self):
        self.assertTrue(db.alive(os.getpid()))
        self.assertFalse(db.alive(finished_pid()))
        for bad in (0, -1, None, "12"):
            self.assertFalse(db.alive(bad))

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


class TranscriptTests(unittest.TestCase):
    def test_the_slug_turns_every_other_character_into_a_dash(self):
        self.assertEqual("C--dev-ashamray-blueprint-backend--claude-worktrees-dispatch",
                         db.project_slug(r"C:\dev\ashamray\blueprint-backend\.claude\worktrees\dispatch"))

    def test_a_session_id_names_its_file_and_none_takes_the_newest_since_the_launch(self):
        with tempfile.TemporaryDirectory() as tmp:
            projects, worktree = Path(tmp) / "projects", os.path.join(tmp, "wt")
            folder = projects / db.project_slug(os.path.abspath(worktree))
            with mock.patch.object(db, "PROJECTS", projects):
                self.assertEqual(str(folder / "43b7cd26-0682.jsonl"),
                                 db.transcript_path(worktree, "43b7cd26-0682", 0))
                self.assertIsNone(db.transcript_path(worktree, "../../x", 0))
                self.assertIsNone(db.transcript_path(worktree, None, 0))
                folder.mkdir(parents=True)
                created = {"old.jsonl": 100.0, "new.jsonl": 205.0, "newer.txt": 300.0}
                for name in created:
                    (folder / name).write_text("{}\n", encoding="utf-8")
                with mock.patch.object(db, "_created", lambda path: created[Path(path).name]):
                    self.assertEqual(str(folder / "new.jsonl"), db.transcript_path(worktree, None, 200.0))
                    self.assertIsNone(db.transcript_path(worktree, None, 250.0))


if __name__ == "__main__":
    unittest.main()
