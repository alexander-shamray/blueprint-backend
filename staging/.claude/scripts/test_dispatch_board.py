import contextlib
import hashlib
import io
import json
import re
import tempfile
import unittest
from datetime import date
from pathlib import Path
from unittest import mock

import dispatch_board as db
import dispatch_run as dr

TODAY = date(2026, 11, 1)
RUN = "20261101-220000"
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
        self.run_dir = self.dir / "artifacts" / "dispatch" / RUN

    def write(self, text):
        with open(self.path, "w", encoding="utf-8", newline="") as handle:
            handle.write(text)

    def read(self):
        with open(self.path, encoding="utf-8", newline="") as handle:
            return handle.read()

    def cli(self, *args):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = db.main(["tasks", args[0], "--tasks", str(self.path), "--root", str(self.dir), "--run", RUN,
                            *args[1:]])
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
        self.start()
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
        self.assertEqual([("read", "todo.md"), ("write", "todo.md")], [e for e in events if e[1] == "todo.md"])
        text = self.read()
        self.assertIn("| S1 | #1683 | x Narrow read", section(text, "In progress"))
        self.assertIn("| S1 | #1682 |", section(text, "In progress"))

    def test_a_refused_write_leaves_no_temporary_file_and_no_state_record(self):
        self.start()
        before = (self.run_dir / "state.json").read_bytes()
        self.write(FIXTURE)
        with mock.patch.object(db.os, "replace", side_effect=PermissionError("held open")):
            code, err = self.cli("move", "S1")
        self.assertEqual(2, code)
        self.assertIn("refused: could not write", err)
        self.assertEqual(FIXTURE, self.read())
        self.assertEqual(["todo.md"], sorted(p.name for p in self.dir.iterdir() if p.is_file()))
        self.assertEqual(["state.json"], sorted(p.name for p in self.run_dir.iterdir()))
        self.assertEqual(before, (self.run_dir / "state.json").read_bytes())

    def test_a_refused_list_write_undoes_the_moves_state_record(self):
        self.start()
        started = (self.run_dir / "state.json").read_text(encoding="utf-8")
        self.write(FIXTURE)
        real_write = db._write

        def refuse_the_list(path, text):
            if Path(path).name == "todo.md":
                raise db.Refusal("could not write todo.md: held open")
            real_write(path, text)
        with mock.patch.object(db, "_write", refuse_the_list):
            self.assertEqual(2, self.cli("move", "S1")[0])
        self.assertEqual(started, (self.run_dir / "state.json").read_text(encoding="utf-8"))
        dr.save_state(self.run_dir, {"run": "x", "groups": {"S3": {"keys": ["#1681"]}}})
        before = (self.run_dir / "state.json").read_text(encoding="utf-8")
        with mock.patch.object(db, "_write", refuse_the_list):
            self.assertEqual(2, self.cli("move", "S1")[0])
        self.assertEqual(before, (self.run_dir / "state.json").read_text(encoding="utf-8"))
        self.assertEqual(FIXTURE, self.read())

    def test_a_move_records_the_rows_in_the_runs_state_and_back_reads_them_there(self):
        self.start()
        self.write(FIXTURE)
        self.assertEqual((0, ""), self.cli("move", "S1"))
        entry = dr.load_state(self.run_dir)["groups"]["S1"]
        self.assertEqual(["#1682", "#1683"], entry["keys"])
        self.assertEqual([r.line for r in self.s1().rows], entry["rows"])
        self.assertFalse((self.run_dir / "groups").exists())
        self.assertEqual((0, ""), self.cli("back", "S1"))
        self.assertEqual("returned", dr.load_state(self.run_dir)["groups"]["S1"]["status"])
        self.write(FIXTURE)
        dr.save_state(self.run_dir, {"groups": {}})
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
        self.start()
        self.write(FIXTURE)
        self.assertEqual((0, ""), self.cli("move", "S1", "--branch", "fix/s1", "--worktree", "wt"))
        self.assertIn("`fix/s1`, `wt`", self.read())
        self.assertEqual((0, ""), self.cli("back", "S1"))
        self.assertEqual(1, len(db.parse_tasks(self.read()).in_progress))
        back = {r.number: r for r in db.parse_tasks(self.read()).remaining}
        self.assertEqual("OWNER", back[1682].cells[db.DEPENDS])
        self.write(FIXTURE)
        self.cli("move", "S1")
        self.verified("S1")
        self.assertEqual((0, ""), self.cli("done", "S1"))
        self.assertEqual(1, len(db.parse_tasks(self.read()).in_progress))
        self.assertNotIn("1682", self.read())

    def test_the_cli_refuses_with_exit_two_and_writes_nothing(self):
        self.start()
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
        self.start()
        self.write(FIXTURE)
        self.assertEqual((0, ""), self.cli("place", "1", "S3", "1700", "--depends", "none"))
        self.assertEqual([1681, 1700], [r.number for r in db.groups(db.parse_tasks(self.read()))[1].rows])

    def board(self):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return db.main(["board", "--tasks", str(self.path), "--today", "2026-11-01", "--root", str(self.dir),
                            "--run", RUN], body_source=lambda n: "", open_source=set, branch_source=NO_BRANCH)

    def start(self):
        dr.save_state(self.run_dir, {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})

    def verified(self, gid):
        state = dr.load_state(self.run_dir)
        state["groups"][gid]["status"] = "verified"
        dr.save_state(self.run_dir, state)

    def test_every_tasks_action_refuses_a_run_start_never_opened_and_writes_nothing(self):
        # A mistyped run name would otherwise create a phantom run, and `done` would delete whatever the letter held.
        self.write(FIXTURE)
        actions = {"move": ["move", "S1"], "done": ["done", "S1"], "back": ["back", "S1"],
                   "place": ["place", "1", "S3", "1700"]}
        for name, args in actions.items():
            with self.subTest(action=name, state="missing"):
                code, err = self.cli(*args)
                self.assertEqual(2, code)
                self.assertIn("the run was never started", err)
                self.assertEqual(FIXTURE, self.read())
                self.assertFalse(self.run_dir.exists())
        self.start()
        (self.run_dir / "state.json").write_text("{not json", encoding="utf-8")
        for name, args in actions.items():
            with self.subTest(action=name, state="corrupt"):
                code, err = self.cli(*args)
                self.assertEqual(2, code)
                self.assertIn("is not JSON", err)
                self.assertEqual(FIXTURE, self.read())
                self.assertEqual("{not json", (self.run_dir / "state.json").read_text(encoding="utf-8"))

    def test_done_deletes_only_this_runs_verified_rows_and_back_only_its_own(self):
        # Another session's row carries the letter S1 too; the run's own move recorded only #1682 and #1683.
        foreign = "| S1 | #1690 | Another session's work | `fix/other`, `.claude/worktrees/other` | started |"
        text = FIXTURE.replace("| started |\n", f"| started |\n{foreign}\n", 1)
        self.start()
        self.write(text)
        code, err = self.cli("done", "S1")
        self.assertEqual(2, code)
        self.assertIn("does not record S1; it was not moved by this run", err)
        self.assertEqual((0, ""), self.cli("move", "S1"))
        moved = self.read()
        for status in (None, "running", "merged-claimed", "handed-back"):
            with self.subTest(status=status):
                state = dr.load_state(self.run_dir)
                state["groups"]["S1"]["status"] = status
                dr.save_state(self.run_dir, state)
                code, err = self.cli("done", "S1")
                self.assertEqual(2, code)
                self.assertIn("not a verified merge", err)
                self.assertEqual(moved, self.read())
        self.verified("S1")
        code, err = self.cli("back", "S1")
        self.assertEqual(2, code)
        self.assertIn("never sent back", err)
        self.assertEqual((0, ""), self.cli("done", "S1"))
        self.assertEqual([None, 1690], [r.number for r in db.parse_tasks(self.read()).in_progress])
        self.write(moved)
        state = dr.load_state(self.run_dir)
        state["groups"]["S1"]["status"] = "handed-back"
        dr.save_state(self.run_dir, state)
        self.assertEqual((0, ""), self.cli("back", "S1"))
        self.assertIn(foreign, self.read())
        self.assertLessEqual({"#1682", "#1683"}, {r.key for r in db.parse_tasks(self.read()).remaining})

    def rebuild(self, text):
        path = self.dir / "rebuilt.md"
        path.write_bytes(text.encode("utf-8"))
        return self.cli("rewrite", "--from", str(path))

    def test_a_rewrite_replaces_the_list_with_the_rebuild_from_what_board_read(self):
        # The rebuild is the session's judgement written under the run; the helper is the only writer of the list.
        self.start()
        self.write(FIXTURE.replace("\n", "\r\n"))
        self.assertEqual(0, self.board())
        self.assertEqual(FIXTURE.replace("\n", "\r\n"), (self.run_dir / "tasks.read.md").read_bytes().decode())
        rebuilt = FIXTURE.replace("| 2026-11-09 | Opus |", "| OWNER; 2026-11-09 | Opus |")
        self.assertEqual((0, ""), self.rebuild(rebuilt))
        self.assertEqual(rebuilt.replace("\n", "\r\n"), self.read())

    def test_a_rewrite_refuses_a_list_changed_since_board_read_it(self):
        self.start()
        self.write(FIXTURE)
        code, err = self.rebuild(FIXTURE)
        self.assertEqual(2, code)
        self.assertIn("run board --run first", err)
        self.board()
        edited = FIXTURE.replace("Write `docs/ci-cost-comparison.md`", "Another session's edit")
        self.write(edited)
        code, err = self.rebuild(FIXTURE)
        self.assertEqual(2, code)
        self.assertIn("the task list changed since board read it", err)
        self.assertEqual(edited, self.read())

    def test_a_rewrite_refuses_a_rebuild_that_drops_the_note_or_this_runs_rows_or_does_not_parse(self):
        self.start()
        self.write(FIXTURE)
        self.assertEqual((0, ""), self.cli("move", "S1"))
        self.board()
        moved = self.read()
        held = [line for line in moved.split("\n") if line.startswith("| S1 | #")]
        self.assertEqual(2, len(held))
        cases = {"the note above the sections": moved.replace("Rules for filling", "Rules"),
                 "changes the In progress section": moved.replace(held[0] + "\n", ""),
                 "unexpected section 'Done'": moved + "\n## Done\n\nnone\n"}
        for why, rebuilt in cases.items():
            with self.subTest(why=why):
                code, err = self.rebuild(rebuilt)
                self.assertEqual(2, code)
                self.assertIn(why, err)
                self.assertEqual(moved, self.read())

    def test_a_fresh_run_cannot_drop_or_edit_another_sessions_in_progress_row(self):
        # At step 0 state.json lists no group, so the guard is the whole section, not this run's groups.
        self.start()
        self.write(FIXTURE)
        self.board()
        foreign = next(line for line in FIXTURE.split("\n") if line.startswith("| — | owed"))
        cases = {"dropped": FIXTURE.replace(foreign + "\n", "", 1),
                 "edited": FIXTURE.replace(foreign, foreign.replace("| started |", "| done |"), 1)}
        for why, rebuilt in cases.items():
            with self.subTest(row=why):
                self.assertNotEqual(FIXTURE, rebuilt)
                code, err = self.rebuild(rebuilt)
                self.assertEqual(2, code)
                self.assertIn("changes the In progress section", err)
                self.assertEqual(FIXTURE, self.read())

    def test_a_rewrite_or_a_board_without_a_started_run_is_refused(self):
        self.write(FIXTURE)
        code, err = self.rebuild(FIXTURE)
        self.assertEqual(2, code)
        self.assertIn("the run was never started", err)
        self.assertEqual(2, self.board())
        self.assertFalse(self.run_dir.exists())
        self.start()
        (self.run_dir / "state.json").write_text("{not json", encoding="utf-8")
        code, err = self.rebuild(FIXTURE)
        self.assertEqual(2, code)
        self.assertIn("is not JSON", err)
        self.assertEqual(FIXTURE, self.read())

    def test_a_run_that_is_not_a_timestamp_is_refused_before_anything_is_written(self):
        self.write(FIXTURE)
        fetched = []
        for run in ("../x", "notatime", str(self.dir / "run")):
            with self.subTest(run=run):
                err = io.StringIO()
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
                    code = db.main(["board", "--tasks", str(self.path), "--root", str(self.dir), "--run", run],
                                   body_source=fetched.append, open_source=set, branch_source=NO_BRANCH)
                self.assertEqual(2, code)
                self.assertIn("is not a yyyymmdd-hhmmss timestamp", err.getvalue())
        self.assertEqual([], fetched)
        self.assertEqual(["todo.md"], sorted(p.name for p in self.dir.iterdir()))
        bad = io.StringIO()
        with contextlib.redirect_stderr(bad):
            code = db.main(["tasks", "place", "--tasks", str(self.path), "--root", str(self.dir), "--run", "../x",
                            "1", "S3", "1700"])
        self.assertEqual(2, code)
        self.assertIn("is not a yyyymmdd-hhmmss timestamp", bad.getvalue())
        self.assertEqual(FIXTURE, self.read())

    def test_place_records_the_issue_in_the_runs_state(self):
        # The morning report's Filed overnight names what the dispatcher placed as well as what results named.
        self.start()
        self.write(FIXTURE)
        self.assertEqual((0, ""), self.cli("place", "1", "S3", "1700"))
        self.assertEqual([1700], dr.load_state(self.run_dir)["placed"])

    def test_back_keeps_how_the_group_ended(self):
        self.start()
        self.write(FIXTURE)
        self.cli("move", "S1")
        state = dr.load_state(self.run_dir)
        state["groups"]["S1"].update(status="handed-back", reason="needs a provider key")
        dr.save_state(self.run_dir, state)
        self.assertEqual((0, ""), self.cli("back", "S1"))
        entry = dr.load_state(self.run_dir)["groups"]["S1"]
        self.assertEqual(("returned", "handed-back"), (entry["status"], entry["closed_from"]))

    def test_the_files_line_endings_survive_a_write(self):
        self.start()
        self.write(FIXTURE.replace("\n", "\r\n"))
        self.assertEqual((0, ""), self.cli("move", "S1"))
        text = self.read()
        self.assertEqual(text.count("\n"), text.count("\r\n"))
        self.assertIn("| S1 | #1682 |", text)
        self.verified("S1")
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
        run = Path(self.tmp.name) / "artifacts" / "dispatch" / RUN
        dr.save_state(run, {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        self.run_board(calls, ("--root", self.tmp.name, "--run", RUN))
        self.assertEqual(6, len(calls))
        self.assertTrue((run / "bodies" / "1682.txt").is_file())
        again = []
        self.run_board(again, ("--root", self.tmp.name, "--run", RUN))
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
        run = root / "artifacts" / "dispatch" / RUN
        dr.save_state(run, {"run": RUN, "pairs": 2, "lang": "en", "groups": {}})
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = db.main(["board", "--tasks", str(self.path), "--today", "2026-11-01", "--root", str(root),
                            "--run", RUN], branch_source=NO_BRANCH)
        self.assertEqual(0, code, err.getvalue())
        lines = out.getvalue().splitlines()
        self.assertTrue(lines[0].startswith("S1 | wave 1 | ready | free | "), lines[0])
        self.assertIn("t\ntouches definitions.json", (run / "bodies" / "1682.txt").read_text("utf-8"))
        self.assertEqual({1440}, db.open_issues(str(root)))

    def test_a_helper_that_fails_is_a_refusal(self):
        root = Path(self.tmp.name)
        stub(root, "gh-issue-text.sh", "echo 'gh: not logged in' >&2\nexit 4\n")
        with self.assertRaisesRegex(db.Refusal, "gh-issue-text.sh 1682 exited 4: gh: not logged in"):
            db.issue_body(str(root), 1682)


if __name__ == "__main__":
    unittest.main()
