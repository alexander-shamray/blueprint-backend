"""The --dispatched token changes only what the dispatch spec's section 6.2 lists.

Nobody is awake to read what an executor did with it, so each case reads the
command text the executor is given, and the file it writes is held to the
validator the dispatcher reads it with."""

import json
import re
import unittest
from pathlib import Path

import dispatch_board
import dispatch_run

ROOT = Path(__file__).resolve().parents[2]
SHIP = ROOT / ".claude" / "commands" / "ship.md"
BRANCH = ROOT / ".claude" / "commands" / "branch.md"

# The grants as they stood before the token: one that needed a new grant would
# be a wider change than section 6.2's, so these are literals, not a read.
GRANTS_BEFORE = (
    "Read", "Grep", "Glob", "Write", "Skill", "Agent(branch-reviewer)", "EnterWorktree", "ExitWorktree",
    "Bash(git status:*)", "Bash(git diff:*)", "Bash(git branch --list:*)", "Bash(git branch --show-current)",
    "Bash(git branch -a)", "Bash(git log:*)", "Bash(git fetch origin:*)",
    "Bash(bash .claude/scripts/git-branch-create.sh:*)", "Bash(bash .claude/scripts/git-worktree-fork.sh:*)",
    "Bash(bash .claude/scripts/git-switch-existing.sh:*)", "Bash(bash .claude/scripts/git-rebase-onto-main.sh:*)",
    "Bash(git rev-parse:*)", "Bash(git worktree list:*)", "Bash(ls:*)", "Bash(git add:*)", "Bash(git commit:*)",
    "Bash(bash .claude/scripts/git-unstage.sh:*)", "Bash(git push -u origin:*)", "Bash(git push origin:*)",
    "Bash(wc:*)", "Bash(gh pr create:*)", "Bash(bash .claude/scripts/pr-state.sh:*)",
    "Bash(bash .claude/scripts/pr-for-branch.sh:*)", "Bash(bash .claude/scripts/git-trailer-pr.sh:*)",
    "Bash(gh pr checks:*)", "Bash(bash .claude/scripts/gh-pr-merge.sh:*)", "Bash(git pull --ff-only)",
    "Bash(git merge-base --is-ancestor:*)", "Bash(bash .claude/scripts/git-worktree-remove.sh:*)",
    "Bash(git worktree prune:*)", "Bash(bash .claude/scripts/pr-locality.sh:*)",
    "Bash(bash .claude/scripts/gh-issue-create.sh:*)", "Bash(bash .claude/scripts/system-sleep.sh)",
)
BRANCH_GRANTS_BEFORE = (
    "Read", "Grep", "EnterWorktree", "Bash(git status:*)", "Bash(git diff:*)", "Bash(git branch --list:*)",
    "Bash(git branch --show-current)", "Bash(git branch -a)", "Bash(git log:*)", "Bash(git fetch origin:*)",
    "Bash(bash .claude/scripts/git-branch-create.sh:*)", "Bash(bash .claude/scripts/git-worktree-fork.sh:*)",
    "Bash(bash .claude/scripts/git-switch-existing.sh:*)", "Bash(git rev-parse:*)", "Bash(git worktree list:*)",
    "Bash(ls:*)",
)

SKIP_WRITE = "Under --dispatched, skip this write"
WHY = "(why: docs/commands/ship.md, *Dispatched: the one token /dispatch passes*)"


def flat(text):
    # Backticks and line breaks are layout, not wording: a rewrap must not fail a case.
    return " ".join(text.replace("`", "").split())


def between(text, start, end):
    """The text from `start` up to the first `end` after it; a missing marker fails the case as a missing rule."""
    first = text.find(start)
    last = text.find(end, first + len(start)) if first >= 0 else -1
    if last < 0:
        raise AssertionError(f"{start!r} then {end!r} is not in the text")
    return text[first:last]


def ship():
    return SHIP.read_text(encoding="utf-8")


def ship_step_6():
    return between(ship(), "6. **Merge, then tear", "7. **Sleep, only when asked.**")


def ship_report():
    text = ship()
    return text[text.index("\n## Report"):]


def branch_step_5():
    return between(BRANCH.read_text(encoding="utf-8"), "5. **Create the workspace", "\n## Report")


def grants(path):
    front = path.read_text(encoding="utf-8").split("---")[1]
    line = next(ln for ln in front.splitlines() if ln.startswith("allowed-tools:"))
    return tuple(item.strip() for item in line.split(":", 1)[1].split(","))


def result_skeleton():
    """The JSON block the Report section shows, parsed: what an executor copies."""
    report = ship_report()
    block = between(report, "```json", "\n```")
    return json.loads(block[len("```json"):])


class TheDispatchedTokenChangesOnlyWhatTheSpecLists(unittest.TestCase):

    def test_it_is_a_standalone_token_like_sleep(self):
        text = flat(ship())
        self.assertIn("standalone token --dispatched <run-dir>", text)
        self.assertIn("standalone token --sleep", text, "the form the new token copies is gone")
        front = ship().split("---")[1]
        hint = next(ln for ln in front.splitlines() if ln.startswith("argument-hint:"))
        self.assertIn("[--sleep] [--dispatched <run-dir>]", hint)

    def test_step_1_hands_branch_the_token_with_its_value(self):
        step = flat(between(ship(), "1. **`/branch`**", "2. **Checks**"))
        self.assertIn("Under --dispatched, /branch is handed the token with its value", step)

    def test_both_task_list_writes_are_skipped(self):
        # The skip follows the write it names, so it cannot be read as about another line.
        for name, text, write in (
                ("ship.md step 6", flat(ship_step_6()), "delete this change's row from *In progress*"),
                ("branch.md step 5", flat(branch_step_5()), "add the change to *In progress*")):
            with self.subTest(where=name):
                self.assertIn(SKIP_WRITE, text)
                self.assertLess(text.index(write), text.index(SKIP_WRITE))

    def test_the_task_list_is_written_nowhere_under_the_token(self):
        # CLAUDE.md and the owner's memory tell a session that files an issue to update the list; this overrides both.
        rule = between(flat(between(ship(), "## It runs to the end", "## Resume, don't restart")),
                       "Under the standalone token --dispatched <run-dir>", "(why:")
        self.assertIn("writes the task list nowhere, even when it files or closes an issue", rule)
        self.assertIn("what it files goes only in the result's filed", rule)

    def test_step_6_neither_exits_nor_removes_the_worktree(self):
        step = ship_step_6()
        skip = between(flat(step), SKIP_WRITE, "(why:")
        self.assertIn("only lines 1 and 8 run", skip)
        self.assertIn("no ExitWorktree", skip)
        self.assertIn("no git-worktree-remove.sh", skip)
        # The numbers name lines of the eight-line block, so the block is read for them.
        numbered = {m.group(2): m.group(1) for m in re.finditer(r"^(.*)# (\d)\.(?: |$)", step, re.MULTILINE)}
        self.assertEqual(sorted(numbered), [str(n) for n in range(1, 9)])
        self.assertIn("ExitWorktree", numbered["2"])
        self.assertIn("git-worktree-remove.sh", numbered["7"])
        self.assertIn("pr-state.sh", numbered["1"], "line 1, the verification, is kept")
        self.assertIn("git worktree prune", numbered["8"], "line 8, the prune, is kept")

    def test_step_0_leaves_a_finished_worktree_to_the_dispatcher(self):
        # A relaunch onto a branch that merged meets step 0's teardown before step 6's skip.
        step = flat(between(ship(), "0. **Start from the main checkout", "1. **`/branch`**"))
        rule = between(step, "Under --dispatched, a finished branch", "(why:")
        self.assertIn("the *already merged* row's included", rule)
        for skipped in ("no ExitWorktree", "no teardown below", "no git-worktree-remove.sh"):
            with self.subTest(skipped=skipped):
                self.assertIn(skipped, rule)
        self.assertIn("verify MERGED from pr-for-branch.sh and pr-state.sh, write the result and end the run", rule)
        self.assertLess(step.index(rule), step.index("Then the teardown."), "the rule precedes what it skips")

    def test_every_ending_the_report_names_is_a_result_the_dispatcher_accepts(self):
        # Built from the endings table's own cells, so a rule the validator refuses fails here, not at night.
        skeleton = result_skeleton()
        table = between(ship_report(), "| Ending |", "\n\n")
        rows = [[cell.strip() for cell in ln.strip("|").split("|")] for ln in table.splitlines()[2:]]
        endings = {}
        for label, outcome_cell, end_cell, reason_cell in rows:
            outcome = re.match(r"`([a-z-]+)`", outcome_cell).group(1)
            merged = outcome == "merged"
            ends = [None] if end_cell.startswith("`null`") else ["clean"] + ([None] if "`null`" in end_cell else [])
            for review_end in ends:
                data = {**skeleton, "issues": [826, 827], "outcome": outcome,
                        "pr": 1700 if merged else None, "merge_oid": "a" * 40 if merged else None,
                        "rounds": [] if "rounds: []" in outcome_cell else [
                            {"kind": "full", "raised": 2, "fixed": 1, "refused": 1}],
                        "review_end": review_end, "reason": None if reason_cell == "`null`" else reason_cell}
                with self.subTest(ending=label, review_end=review_end):
                    got = dispatch_run.validate_result(data, "G1", [826, 827])
                    self.assertIsInstance(got, dispatch_run.Result, getattr(got, "why", ""))
            endings[label] = outcome
        self.assertEqual(set(endings.values()), set(dispatch_run.OUTCOMES))
        self.assertEqual(endings["Step 0 found the PR merged, the predicate holding"], "merged")
        self.assertIn("closed unmerged", table)

    def test_filed_is_defined_as_the_spec_defines_it(self):
        self.assertIn("filed is every issue filed or commented on, by number", flat(ship_report()))

    def test_the_report_leaves_the_teardown_to_the_dispatcher(self):
        merge_line = flat(between(ship_report(), "- **The merge and the workspace**", "\n\n"))
        self.assertIn('Under --dispatched, the pull, containment and removal are the dispatcher\'s: say '
                      '"teardown left to /dispatch"', merge_line)

    def test_the_three_hand_back_cases_are_named(self):
        section = flat(between(ship(), "## It runs to the end", "## Resume, don't restart"))
        for case in ("an edit under .claude/", "a resource only the owner has",
                     "no ## Owner decisions entry settles it"):
            with self.subTest(case=case):
                self.assertIn(case, section)
        self.assertIn("Every other judgement is the recommended option, named in the result's decisions", section)
        self.assertIn(flat(WHY), section)

    def test_the_result_names_every_field_of_the_spec(self):
        skeleton = result_skeleton()
        self.assertEqual(tuple(skeleton), dispatch_board.RESULT_KEYS)
        self.assertEqual(tuple(skeleton["outcome"].split(" | ")), dispatch_run.OUTCOMES)
        self.assertEqual(tuple(skeleton["review_end"].split(" | ")), dispatch_run.REVIEW_ENDS)
        self.assertEqual(tuple(skeleton["rounds"][0]), dispatch_run.ROUND_KEYS)
        self.assertEqual(tuple(skeleton["rounds"][0]["kind"].split(" | ")), dispatch_run.ROUND_KINDS)
        self.assertEqual(sorted(skeleton["workspace"]), ["removed", "worktree"])
        self.assertIs(skeleton["workspace"]["removed"], False, "under the token the session removes nothing")

    def test_the_result_path_and_group_come_from_the_argument(self):
        report = flat(ship_report())
        self.assertIn("Under --dispatched, end by writing <run-dir>/<group>.json", report)
        self.assertIn("the group read from the argument's leading #<group>-<n>[+<n>] tag", report)
        self.assertIn(flat(WHY), report)

    def test_the_prompt_the_dispatcher_builds_reads_back_as_that_path(self):
        # Read as ship.md tells the executor to read it, the prompt names the file absorb copies.
        rows = tuple(dispatch_board.Row("Remaining", 1, "G1", n, str(n), (), "") for n in (826, 827))
        group = dispatch_board.Group("G1", 1, rows, "Opus")
        text = dispatch_run.prompt(group, "narrow the grant", "20261101-220000")
        tag = re.match(r"/ship #([A-Z]\d+)-(\d+(?:\+\d+)*) ", text)
        run_dir = re.search(r" --dispatched (\S+)$", text)
        self.assertIsNotNone(tag, text)
        self.assertIsNotNone(run_dir, text)
        self.assertEqual(tag.group(1), "G1")
        self.assertEqual([int(n) for n in tag.group(2).split("+")], [826, 827])
        self.assertEqual(Path(run_dir.group(1)) / f"{tag.group(1)}.json",
                         Path("artifacts", "dispatch", "20261101-220000", "G1.json"))

    def test_the_result_is_written_on_every_stop(self):
        report = flat(ship_report())
        self.assertIn("on a merge, a hand-back, each of the seven stops and a run step 0 ended alike", report)
        # "Seven" is a count of the stop table's rows, held here so the sentence stays true.
        table = between(ship(), "**Seven things still stop the chain**", "\n\n- ")
        rows = [ln for ln in table.splitlines() if ln.startswith("| ") and not ln.startswith("| |")]
        self.assertEqual(len(rows), 7)

    def test_the_grants_did_not_change(self):
        self.assertEqual(grants(SHIP), GRANTS_BEFORE)
        self.assertEqual(grants(BRANCH), BRANCH_GRANTS_BEFORE)
        self.assertIn("Write", GRANTS_BEFORE, "the result is written with a tool the command already holds")

    def test_the_in_progress_row_carries_its_group(self):
        step = flat(branch_step_5())
        self.assertIn("(the issue's group from *Remaining*, or — where none held it, #n, what, branch and "
                      "worktree, started)", step)
        self.assertEqual(dispatch_board.HEADERS["In progress"][:2], ("Group", "#"),
                         "the row's description follows the table's column order")


if __name__ == "__main__":
    unittest.main()
