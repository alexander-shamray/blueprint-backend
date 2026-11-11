"""The commands split into a runbook and its argument (`docs/harness/token-rules.md`, *What every session loads*).

A runbook is resident in every turn its command runs and its argument is read only when a rule is disputed;
these cases hold the two halves to each other, and the rest of what a run keeps resident under a ceiling.
"""

import re
import unittest

from review_helpers import COMMANDS, LOCAL

ARGUMENTS = COMMANDS.parent.parent / "docs" / "commands"

# Bytes with CRLF counted as LF, so a Windows checkout reads what Linux CI does;
# each at most about a tenth over its landed size: a runbook past it has regrown its argument.
# The payload's split commands; a target declares its own in `CEILINGS` in its `local_commands.py`.
CEILINGS = {
    "ship.md": 49800,
    "bug-sweep.md": 19000,
    "security-sweep.md": 14900,
    "branch.md": 9300,
    "team.md": 6300,
    "dispatch.md": 19450,
}

ROOT = COMMANDS.parent.parent

# The rest of what a /ship run holds resident, measured the same way: step 6's
# exit counts these beside ship.md and branch.md, and none was split. A target's
# own `CLAUDE.md` is resident too, and its ceiling is that target's.
RESIDENT = {
    ".claude/commands/pr.md": 10900,
    ".claude/commands/commit.md": 5200,
}

CITATION = re.compile(r"docs/commands/([a-z-]+\.md)`?, \*([^*]+)\*")
# A `(why:` that opens on a docs/commands argument, the one form CITATION parses; others cite the boundaries.
OPENING = re.compile(r"\(why: `?docs/commands/")
INDEX_ROW = re.compile(r"^\| (?P<name>[^|]+?) \| ")


def missing_ceilings(arguments, *maps):
    """The argument files no ceiling map holds, so a command split tomorrow cannot escape its ceiling."""
    return sorted(name for name in arguments if not any(name in held for held in maps))
POSITIONAL = re.compile(r"\$\d+|\$ARGUMENTS\[")


def collapsed(path):
    return " ".join(path.read_text(encoding="utf-8").split())


class EachSplitCommandHasBothHalves(unittest.TestCase):

    # A target's own split commands, whose ceilings its `local_commands.py` declares.
    OWN_CEILINGS = LOCAL.ceilings

    def argued(self):
        names = sorted(p.name for p in ARGUMENTS.glob("*.md"))
        self.assertGreaterEqual(len(names), len(CEILINGS), "the argument glob found fewer files than were split")
        return names

    def test_every_split_command_has_a_runbook_that_cites_its_argument(self):
        for name in sorted(set(self.argued()) | set(CEILINGS)):
            with self.subTest(command=name):
                self.assertTrue((ARGUMENTS / name).is_file(), f"docs/commands/{name} is missing")
                runbook = COMMANDS / name
                self.assertTrue(runbook.is_file(), f"no runbook for docs/commands/{name}")
                self.assertIn(f"docs/commands/{name}", collapsed(runbook))

    def test_every_argument_has_a_ceiling(self):
        self.assertEqual([], missing_ceilings(self.argued(), CEILINGS, self.OWN_CEILINGS))

    def test_an_argument_no_map_holds_is_found_and_a_targets_own_map_admits_it(self):
        self.assertEqual(["own.md"], missing_ceilings(["ship.md", "own.md"], CEILINGS, {}))
        self.assertEqual([], missing_ceilings(["ship.md", "own.md"], CEILINGS, {"own.md": 1}))
        self.assertEqual(["ship.md"], missing_ceilings(["ship.md"], {}, {}))

    def test_every_runbook_cites_and_every_citation_parses(self):
        # Per runbook, because the global count below is met by one file's citations: a runbook that lost every
        # `(why:` or wrote one CITATION does not match would stay green. Each opening claims a section argues the rule.
        for name in sorted(set(CEILINGS) | set(self.OWN_CEILINGS)):
            with self.subTest(command=name):
                text = collapsed(COMMANDS / name)
                openings = len(OPENING.findall(text))
                parsed = len(CITATION.findall(text))
                self.assertGreater(openings, 0, f"{name} cites no docs/commands section")
                self.assertEqual(openings, parsed, f"{name} has {openings} openings but {parsed} parse as citations")

    def test_every_citation_names_a_heading_that_exists(self):
        cited = 0
        for runbook in sorted(COMMANDS.glob("*.md")):
            for target, heading in CITATION.findall(collapsed(runbook)):
                cited += 1
                with self.subTest(runbook=runbook.name, target=target, heading=heading):
                    path = ARGUMENTS / target
                    self.assertTrue(path.is_file(), f"cites a missing docs/commands/{target}")
                    headings = re.findall(r"^#{2,3} (.+?)\s*$", path.read_text(encoding="utf-8"), re.MULTILINE)
                    self.assertIn(heading, headings)
        self.assertGreater(cited, len(CEILINGS), "the citation pattern stopped matching the runbooks")

    def test_a_targets_ceilings_are_the_ones_it_declares(self):
        # The same object, so the case holds even here, where the target's map is empty.
        self.assertIs(LOCAL.ceilings, self.OWN_CEILINGS)

    def test_a_targets_ceilings_name_only_its_own_commands(self):
        # One the payload holds would replace the payload's ceiling below with the target's.
        self.assertEqual(set(), set(CEILINGS) & set(self.OWN_CEILINGS))

    def test_each_runbook_stays_under_its_ceiling(self):
        for name, ceiling in {**CEILINGS, **self.OWN_CEILINGS}.items():
            with self.subTest(command=name):
                size = len((COMMANDS / name).read_bytes().replace(b"\r\n", b"\n"))
                self.assertLessEqual(size, ceiling, f"{name} is {size} bytes, over {ceiling}")

    def test_each_resident_file_stays_under_its_ceiling(self):
        for name, ceiling in RESIDENT.items():
            with self.subTest(file=name):
                size = len((ROOT / name).read_bytes().replace(b"\r\n", b"\n"))
                self.assertLessEqual(size, ceiling, f"{name} is {size} bytes, over {ceiling}")

    def test_each_argument_names_its_runbook(self):
        for name in self.argued():
            with self.subTest(argument=name):
                self.assertIn(f".claude/commands/{name}", collapsed(ARGUMENTS / name))

    def test_free_text_arrives_through_arguments(self):
        # A positional placeholder carries one shell-quoted word, so an unquoted title, topic or path list is cut off.
        paths = sorted(COMMANDS.glob("*.md"))
        self.assertGreater(len(paths), len(CEILINGS), "the glob stopped finding the commands")
        for path in paths:
            with self.subTest(command=path.name):
                self.assertEqual([], POSITIONAL.findall(path.read_text(encoding="utf-8")))

    def test_the_positional_pattern_sees_each_spelling(self):
        for spelling in ("$0", "$1", "$9 x", "$12", "$ARGUMENTS[0]", "$ARGUMENTS[12]"):
            with self.subTest(spelling=spelling):
                self.assertTrue(POSITIONAL.search(spelling))
        self.assertIsNone(POSITIONAL.search("`$ARGUMENTS` and $HOME"))


class ShipStopsAndContinuesWhereTheHelpersSay(unittest.TestCase):
    """What stops `/ship`'s chain is read from what its helpers and commands do, not from a remembered list."""

    SCRIPTS = COMMANDS.parent / "scripts"

    def test_a_refused_push_stops_the_chain_and_pr_reports_the_remedy(self):
        ship = collapsed(COMMANDS / "ship.md")
        self.assertRegex(ship, r"A refused push stops the chain before any PR exists")
        pr = collapsed(COMMANDS / "pr.md")
        self.assertIn("`! git push -u origin <branch>`", pr)
        self.assertIn("`autoMode.environment`", pr)
        self.assertRegex(pr, r"refused push comes back with the harness's \"has been denied\" text.{0,40}\*\*stop\*\*, not a retry")

    def test_the_rebase_helpers_exits_are_each_a_continuation_or_a_stop_in_step_6(self):
        helper = (self.SCRIPTS / "git-rebase-onto-main.sh").read_text(encoding="utf-8")
        code = "\n".join(l for l in helper.splitlines() if not l.lstrip().startswith("#"))
        codes = {int(c) for c in re.findall(r"\bexit (\d+)", code)} - {0}
        ship = collapsed(COMMANDS / "ship.md")
        step = ship[ship.index("| Exit | |"):]
        table = step[:step.index("(why:")]
        rows = re.findall(r"\| ([^|]+) \| (Not a stop[^|]*|Stops[^|]*) \|", table)
        self.assertEqual(2, sum(1 for _, verdict in rows if verdict.startswith("Not a stop")))
        named = {int(c) for cell, _ in rows for c in re.findall(r"\b\d+\b", cell)}
        self.assertEqual(codes, named, "the table and the helper name different exits")
        self.assertIn("`publish`", table)
        self.assertRegex(table, r"Any failure of the push itself \| Not a stop")

    def test_ceiling_filing_names_the_trailer_the_hand_route_demands(self):
        create = (self.SCRIPTS / "gh-issue-create.sh").read_text(encoding="utf-8")
        trailer = re.search(r"hand\) trailer='([^']+)'", create).group(1)
        ship = collapsed(COMMANDS / "ship.md")
        ceiling = ship[ship.index("The seventh round is the ceiling"):ship.index("6. **Merge, then tear")]
        self.assertIn(trailer, ceiling)
        self.assertRegex(ceiling, r"title, a blank line and the body on stdin")

    def head_gate(self):
        ship = collapsed(COMMANDS / "ship.md")
        step = ship[ship.index("6. **Merge, then tear"):ship.index("7. **Sleep, only when asked.**")]
        return step[step.index("**A head no round reviewed is not merged.**"):step.index("**A non-empty workspace read")]

    def test_the_head_gate_fast_forwards_first_and_spares_a_plan_pr(self):
        gate = self.head_gate()
        self.assertRegex(gate, r"`git pull --ff-only` \(a refusal stops\)")
        self.assertRegex(gate, r"on a plan PR nothing re-enters")

    def test_a_sync_pr_still_writes_the_bar_and_takes_the_oid_as_its_reviewed_head(self):
        ship = collapsed(COMMANDS / "ship.md")
        sync = ship[ship.index("- **Step 5** skips its opening synchronise"):ship.index("- **Step 6** is unchanged")]
        self.assertRegex(sync, r"the bar is still written from `origin/main` as 5\.1 says")
        self.assertRegex(sync, r"`<oid>` is the round's reviewed head")

    def test_the_ceiling_fix_leaves_step_6s_one_recheck_standing(self):
        ship = collapsed(COMMANDS / "ship.md")
        ceiling = ship[ship.index("The seventh round is the ceiling"):ship.index("Each that stays open is filed")]
        self.assertRegex(ceiling, r"no further round beyond step 6's one recheck")

    def test_row_one_names_the_rebase_continuations_among_its_exceptions(self):
        ship = collapsed(COMMANDS / "ship.md")
        row = ship[ship.index("| A helper or a guarded git command exits non-zero"):ship.index("| This branch's PR was closed")]
        self.assertRegex(row, r"rebase helper's two continuations")

    def test_the_deny_route_is_outside_dispatched_only(self):
        ship = collapsed(COMMANDS / "ship.md")
        two = ship[ship.index("2. **Verify each finding, then fix.**"):ship.index("3. **Choose the next round.**")]
        self.assertRegex(two, r"Outside `--dispatched`, file it at once")

    def test_a_finding_a_deny_refuses_is_filed_at_once_and_reported(self):
        ship = collapsed(COMMANDS / "ship.md")
        two = ship[ship.index("2. **Verify each finding, then fix.**"):ship.index("3. **Choose the next round.**")]
        self.assertRegex(two, r"fix in a tree a deny covers cannot be made here")
        self.assertRegex(two, r"file it at once as the ceiling files it, say so in the report")

    def test_a_finding_that_tells_the_session_to_act_beyond_its_line_is_refused_and_quoted(self):
        ship = collapsed(COMMANDS / "ship.md")
        two = ship[ship.index("2. **Verify each finding, then fix.**"):ship.index("3. **Choose the next round.**")]
        self.assertRegex(two, r"anything other than fix the line it names is refused")
        self.assertIn("attempt to steer the review", two)
        report = ship[ship.index("## Report"):]
        self.assertRegex(report, r"an attempt to steer the review,? quoted in full")


class StylePassReadsTheTargetsOwnLimitsAndSweepsEveryFence(unittest.TestCase):
    """What a pass sweeps and verifies is the target's to say, so the runbook names no stack's numbers."""

    def text(self):
        return collapsed(COMMANDS / "style-pass.md")

    def section(self, start, end):
        text = self.text()
        return text[text.index(start):text.index(end)]

    def test_the_sweep_covers_every_fenced_block_under_docs_not_only_the_spec(self):
        sweep = self.section("## 2. Sweep the corpus", "## 3. Record it")
        self.assertRegex(sweep, r"every fenced block under `docs/`")

    def test_a_site_count_is_removed_where_the_targets_contract_forbids_one(self):
        record = self.section("## 3. Record it", "## 4. Reconcile")
        self.assertRegex(record, r"Where the change contract .{0,80}forbids a document stating a number of sites")
        self.assertRegex(record, r"remove a site count you find")
        self.assertRegex(record, r"Where it does not, update any count the change invalidates")

    def test_the_verify_scan_takes_its_limits_from_the_style_guide(self):
        verify = self.section("## 5. Verify", "## Report")
        self.assertRegex(verify, r"limits.{0,60}style guide")
        sweep = self.section("## 2. Sweep the corpus", "## 3. Record it")
        self.assertRegex(sweep, r"except the closed records `docs/harness-local.md` \*Documents\* names")
        self.assertIn(".harness/manifest.json", sweep)
        self.assertNotIn("`.claude/scripts/**`", sweep)
        for stack_fact in ("120", "multiple of four", "CRLF"):
            with self.subTest(hard_coded=stack_fact):
                self.assertNotIn(stack_fact, verify)
        for limit in ("width", "indent", "line ending"):
            with self.subTest(limit=limit):
                self.assertIn(limit, verify)


class BranchChangesADerivedNameByASecondBranch(unittest.TestCase):
    """The settings deny `git branch -m`, so a runbook offering it as free sends the session into a refusal."""

    OFFERED_AS_FREE = re.compile(r"`git branch -m[^`]*`[^.]{0,40}(?:free|costs nothing)")

    def test_the_report_changes_the_name_by_a_second_branch_at_head_and_never_by_a_rename(self):
        for label, path in (("runbook", COMMANDS / "branch.md"), ("argument", ARGUMENTS / "branch.md")):
            text = collapsed(path)
            with self.subTest(document=label):
                self.assertIsNone(self.OFFERED_AS_FREE.search(text))
                self.assertIn("`bash .claude/scripts/git-branch-create.sh <better-name> HEAD`", text)
                self.assertRegex(text, r"first (?:name|branch)[^.]{0,80}(?:report|git branch)")

    def test_the_helper_the_report_names_takes_head_as_a_base(self):
        helper = (COMMANDS.parent / "scripts" / "git-branch-create.sh").read_text(encoding="utf-8")
        self.assertRegex(helper, r"(?m)^  HEAD\)")

    def test_the_pattern_sees_the_offer(self):
        self.assertIsNotNone(self.OFFERED_AS_FREE.search("`git branch -m <better-name>` is free until `/pr` pushes"))
        self.assertIsNotNone(self.OFFERED_AS_FREE.search("`git branch -m <better-name>` costs nothing until then"))


class TheBoundariesIndexMatchesItsHeadings(unittest.TestCase):
    """`docs/harness-boundaries.md` opens with an index of its sections, and nothing else held the two together."""

    DOC = ROOT / "docs" / "harness-boundaries.md"

    def parts(self):
        lines = self.DOC.read_text(encoding="utf-8").splitlines()
        fenced, headings = False, []
        for line in lines:
            fenced = fenced != line.startswith("```")
            if not fenced and line.startswith("## "):
                headings.append(line[3:].strip())
        # The index heading is the one section with no row of its own, and its first table row is the column header.
        self.assertEqual("Index", headings[0], "the file no longer opens with its index")
        start = lines.index("## Index")
        stop = next(i for i, line in enumerate(lines) if i > start and line.startswith("## "))
        rows = [m["name"] for m in map(INDEX_ROW.match, lines[start:stop]) if m][1:]
        return headings[1:], rows

    def test_the_file_has_headings_and_an_index_to_hold_them_to(self):
        headings, rows = self.parts()
        self.assertTrue(headings, "no `## ` heading was found to judge")
        self.assertTrue(rows, "no index row was found under `## Index`")

    def test_every_heading_is_an_index_row_in_order_and_every_row_a_heading(self):
        headings, rows = self.parts()
        self.assertEqual(headings, rows, "the index and the `## ` headings differ")


class ShipWaitsForCiInTheBackground(unittest.TestCase):
    """Step 6's CI wait is this session's background task: a subagent's Bash is cut at 180 s."""

    def test_step_6_runs_the_watch_in_the_background_and_hands_it_to_no_agent(self):
        text = collapsed(COMMANDS / "ship.md")
        step = text[text.index("6. **Merge, then tear"):text.index("7. **Sleep, only when asked.**")]
        self.assertIn("`run_in_background`", step)
        for retired in ("fresh agent", "general-purpose"):
            with self.subTest(retired=retired):
                self.assertNotIn(retired, step)


class DispatchSendsAStoppedWindowGroupBack(unittest.TestCase):
    """A `wait` that saw the stop file leaves its group `window`, which the report would list as interrupted."""

    def test_the_window_bullet_and_its_resume_row_send_the_group_back(self):
        text = collapsed(COMMANDS / "dispatch.md")
        bullet = text[text.index("- `window`:"):text.index("- `week`:")]
        row = text[text.index("| `wait <g>: the window resets"):text.index("| `wait <g>: the week resets")]
        for name, rule in (("step 2's window bullet", bullet), ("the Resume row", row)):
            with self.subTest(rule=name):
                self.assertIn("`stop file: yes`", rule)
                self.assertIn("`tasks back`", rule)
        # The phrase the runbook keys on is the one `wait` prints.
        helper = (COMMANDS.parent / "scripts" / "dispatch_run.py").read_text(encoding="utf-8")
        self.assertIn('return "stop file: yes"', helper)


if __name__ == "__main__":
    unittest.main()
