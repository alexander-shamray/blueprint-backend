"""/ship's sync row: a harness sync pull request is reviewed once, for this target.

Nobody reads a dispatched run as it happens, so each case reads the command
text an executor is given, as `test_ship_dispatched.py` does.
"""

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHIP = ROOT / ".claude" / "commands" / "ship.md"
ARGUMENT = ROOT / "docs" / "commands" / "ship.md"
BASE = ROOT / ".harness" / "settings.base.json"

HEADING = "A sync pull request is reviewed once, for this target"
HELPER = "bash .claude/scripts/git-worktree-sync.sh"
SYNC_PR = "py -3.12 .claude/scripts/sync_pr.py"
FILER = "bash .claude/scripts/gh-issue-harness.sh"


def flat(text):
    # Backticks and line breaks are layout, not wording: a rewrap must not fail a case.
    return " ".join(text.replace("`", "").split())


def ship():
    return SHIP.read_text(encoding="utf-8")


def section():
    text = ship()
    start = text.index(f"## {HEADING}")
    return text[start:text.index("\n## ", start + 4)]


def bullet(name):
    """One bullet of the section, whitespace-collapsed, from its bold lead to the next bullet or the citation."""
    for part in re.split(r"\n(?=- \*\*|\(why:)", section()):
        if flat(part).startswith(f"- **{name}"):
            return flat(part)
    raise AssertionError(f"no bullet opens {name!r}")


def frontmatter(key):
    front = ship().split("---")[1]
    return next(ln for ln in front.splitlines() if ln.startswith(f"{key}:")).split(":", 1)[1].strip()


class TheSyncRow(unittest.TestCase):

    def test_the_section_follows_the_plan_section_and_precedes_the_steps(self):
        text = ship()
        order = [text.index(f"## {h}") for h in ("A plan is reviewed once, for contradiction", HEADING, "Steps")]
        self.assertEqual(order, sorted(order))

    def test_the_resume_table_has_a_sync_row(self):
        table = ship().split("## Resume, don't restart", 1)[1].split("\n## ", 1)[0]
        rows = [ln for ln in table.splitlines() if ln.startswith("| On a branch named `chore/harness-sync-")]
        self.assertEqual(1, len(rows), "one row for a sync branch")
        row = flat(rows[0])
        self.assertIn(HEADING, row)
        self.assertIn("never steps 3 and 4", row)

    def test_a_sync_branch_is_never_committed_to(self):
        steps = bullet("Steps 3 and 4")
        self.assertIn("never committed to or pushed but by step 6's rebase helper, which publishes", steps)
        # Nothing in the section tells a sync run to commit, push or open a PR, in any spelling.
        for word in ("/commit", "/pr", "git commit", "git push", "git add"):
            self.assertNotIn(word, flat(section()), word)
        # Step 6's own route for a non-empty workspace commits, pushes and re-enters step 5; a sync takes none of it.
        step = bullet("Step 6")
        self.assertIn("its non-empty-workspace route reports what the tree holds and ends unmerged, or hands it back, "
                      "and commits nothing", step)

    def test_the_tree_must_be_the_proved_one_and_clean_before_anything_in_it_runs(self):
        # A pre-existing worktree with HEAD equal can hold a modified sync_pr.py or suite: the commit is not the files.
        lead = flat(section().split("\n- **", 1)[0])
        rule = ("Before anything in the tree runs, git rev-parse HEAD must equal <oid> and git status --short must be "
                "empty, which is what makes its own sync_pr.py and suites the proved ones")
        self.assertIn(rule, lead)
        self.assertIn("a tree that differs is reported, with what it holds, and ends unmerged", lead)
        self.assertLess(lead.index(HELPER), lead.index(rule))
        self.assertNotIn("A dirty tree", bullet("Steps 3 and 4"))

    def test_a_sync_taken_again_enters_the_worktree_it_left(self):
        # The unmerged ending leaves the worktree and branch, so the helper would refuse a second make.
        lead = flat(section().split("\n- **", 1)[0])
        self.assertIn("or, where .claude/worktrees/sync-<sha7> already exists on that branch, enters that", lead)
        self.assertIn("taken again from the main checkout, after the proof", bullet("Step 5"))

    def test_step_5_does_not_pull(self):
        # `--track` makes the opening `git pull --ff-only` work, and a push after the proof would move HEAD off <oid>.
        step = bullet("Step 5")
        self.assertIn("skips its opening synchronise (git fetch, git pull --ff-only), which would move HEAD off <oid>",
                      step)

    def test_severity_is_mapped_and_a_low_goes_where_its_code_is(self):
        step = bullet("Step 5")
        self.assertIn("a bug as high and a medium as itself", step)
        self.assertIn("A payload low goes into the one harness issue", step)
        # The helper only creates on a first filing: on a re-take it comments on the open issue of that title.
        self.assertIn("which the helper adds a comment to when one is open under that title", step)
        self.assertIn("a target-local low into the target's own, filed with gh-issue-create.sh", step)
        # The title-only check files nothing for a low a re-take newly raises, so the run's report is where it stays.
        self.assertIn("a low that issue does not list stays in the run's report", step)
        self.assertIn("A low that issue does not list stays in the run's report",
                      flat(ARGUMENT.read_text(encoding="utf-8")))
        self.assertIn("where an open issue already holds that title nothing is filed",
                      flat(ARGUMENT.read_text(encoding="utf-8")))
        # A dispatched merge has no slot for the report, so the low names the issue and rides in the result's decisions.
        clause = "naming the open issue's number, and under --dispatched in the result's decisions"
        self.assertIn(clause, step)
        self.assertIn(clause, flat(ARGUMENT.read_text(encoding="utf-8")))
        self.assertNotIn("joins", step)
        self.assertIn("gh-issue-harness.sh bug <severity> <area>", step)
        self.assertIn("the area by tree: .claude/ is harness, other prose documentation, the rest code, with test "
                      "beside harness or code for a suite", step)
        # A target-local low is filed once: the open issues are read first, and a title already open files nothing.
        self.assertIn("after bash .claude/scripts/gh-issue-list.sh shows no open issue of that title, else nothing is "
                      "filed and the report says so", step)

    def test_no_prose_here_names_what_a_target_does_not_hold(self):
        for text in (flat(section()), flat(ARGUMENT.read_text(encoding="utf-8"))):
            self.assertIsNone(re.search(r"\b(Task|Focus|section|plan) [0-9]|Builder's call|design's section", text))
        for path in sorted(Path(__file__).parent.glob("*.py")):
            if path.name.startswith(("test_ship_sync", "test_sync_pr", "test_gh_issue_harness", "test_git_worktree_sync")):
                self.assertIsNone(re.search(r"\bTask [0-9]", path.read_text(encoding="utf-8")), path.name)

    def test_the_counts_the_section_makes_false_are_qualified(self):
        self.assertIn("three more things, beyond the sync section's own,", flat(ship()))
        # The seven are a list of what stops the chain, and the sync section ends a run on a failed verify, a failing
        # suite or a filed finding, none of them among the seven.
        self.assertIn("**Seven things still stop the chain**, and nothing else does, bar the sync section's own endings",
                      flat(ship()))
        self.assertIn("beyond the seven stops and the sync section's own", flat(ARGUMENT.read_text(encoding="utf-8")))

    def test_the_sync_row_ranks_against_the_rows_it_overlaps(self):
        table = ship().split("## Resume, don't restart", 1)[1].split("\n## ", 1)[0]
        row = flat(next(ln for ln in table.splitlines() if ln.startswith("| On a branch named `chore/harness-sync-")))
        self.assertIn("the closed and merged rows win over it, and it over the dirty and open-PR rows", row)
        failed = bullet("A failed verify reviews nothing")
        self.assertIn("then pr-state.sh <n> is read: CLOSED is the closed-unmerged stop (stopped, closed unmerged), "
                      "MERGED takes step 6's route for a merge another route made", failed)
        self.assertNotIn("pr-for-branch.sh <head>", failed)

    def test_the_rows_that_say_a_run_stays_in_its_worktree_except_the_sync_row(self):
        text = flat(ship())
        self.assertEqual(4, text.count("a sync run's attended resume aside"), text.count("attended resume aside"))

    def test_no_clause_says_the_dispatcher_proves_the_head(self):
        # The clause is written with the code that makes it true; until then nothing here claims it.
        for text in (flat(ship()), flat(ARGUMENT.read_text(encoding="utf-8"))):
            self.assertNotIn("dispatcher bound", text)
            self.assertNotIn("dispatcher proved", text)

    def test_a_dispatched_run_with_no_proof_is_scoped_in_the_argument_and_the_boundaries(self):
        boundaries = flat((ROOT / "docs" / "harness-boundaries.md").read_text(encoding="utf-8"))
        self.assertNotIn("A dispatched sync's step 2 runs", boundaries)
        self.assertIn("A sync's step 2 runs the target-local suites with it", boundaries)
        self.assertIn("a --dispatched run holding no proof hands back before anything in the tree runs", boundaries)
        argued = flat(ARGUMENT.read_text(encoding="utf-8"))
        self.assertIn("A --dispatched sync run starts inside its worktree and holds no proof, so it hands back", argued)

    def test_the_first_proof_is_made_on_main(self):
        lead = flat(section().split("\n- **", 1)[0])
        self.assertIn("From the main checkout, on main,", lead)
        self.assertIn("a main checkout on another branch verifies with that branch's copy, so the run reports and "
                      "ends unmerged", lead)
        argued = flat(ARGUMENT.read_text(encoding="utf-8"))
        self.assertNotIn("the verifier is never the tree it verifies", argued)

    def test_a_failed_verify_reviews_nothing(self):
        lead = flat(section().split("\n- **", 1)[0])
        self.assertIn(f"{SYNC_PR} list gives row <n>'s <head>", lead)
        self.assertIn(f"{SYNC_PR} verify <n> --head <oid> exits 0 there, before any worktree", lead)
        failed = bullet("A failed verify reviews nothing")
        self.assertIn("a moved head, a HEAD that differs or a dirty tree included", failed)
        self.assertIn("report its lines and end unmerged, or hand it back under --dispatched", failed)
        self.assertIn("none of the seven stops", failed)
        # The verify comes before the worktree is opened, so a head that fails it is never checked out.
        self.assertLess(lead.index("verify <n> --head <oid>"), lead.index(HELPER))

    def test_the_worktree_is_compared_to_the_proved_oid_before_anything_in_it_runs(self):
        # A verify run inside the tree it verifies is the tree's own sync_pr.py, so the proof is made in the main
        # checkout and the worktree is shown to be that head before it is trusted.
        lead = flat(section().split("\n- **", 1)[0])
        rule = "Before anything in the tree runs, git rev-parse HEAD must equal <oid>"
        self.assertIn(rule, lead)
        self.assertLess(lead.index("verify <n> --head <oid>"), lead.index(HELPER))
        self.assertLess(lead.index(HELPER), lead.index(rule))
        self.assertIn("Every later verify and review-diff passes --head <oid>", lead)
        self.assertIn("sync-<n>-r1.diff --head <oid>", bullet("Step 5"))

    def test_a_run_inside_a_sync_worktree_holds_no_proof(self):
        lead = flat(section().split("\n- **", 1)[0])
        # ExitWorktree answers an error to a session that did not enter the worktree itself.
        self.assertIn("A run that starts inside a sync worktree holds no proof: if it entered the worktree this "
                      'session, it leaves with ExitWorktree({action: "keep"}), proves as above, and enters with '
                      "EnterWorktree; any other reports and ends unmerged, to be started again from the main checkout",
                      lead)
        # A dispatched run always starts inside the worktree, so it has no route to a proof.
        self.assertIn("a --dispatched run holding no proof hands back before anything in the tree runs", lead)
        table = ship().split("## Resume, don't restart", 1)[1].split("\n## ", 1)[0]
        row = flat(next(ln for ln in table.splitlines() if ln.startswith("| On a branch named `chore/harness-sync-")))
        self.assertIn("its proof first, which a run already in the worktree holds none of", row)

    def test_step_1_opens_the_worktree_on_the_existing_branch(self):
        lead = flat(section().split("\n- **", 1)[0])
        self.assertIn(f"{HELPER} .claude/worktrees/sync-<sha7> <head> in place of /branch and enters it", lead)
        self.assertIn(f"Bash(py -3.12 -m unittest discover:*)", [g.strip() for g in frontmatter("allowed-tools").split(",")])
        self.assertIn("chore/harness-sync-[0-9a-f]{7}", lead)

    def test_step_2_runs_the_target_local_suites_as_ci_does(self):
        step = bullet("Step 2")
        self.assertIn("each path sync_pr.py local-suites prints", step)
        self.assertIn("py -3.12 -m unittest discover -s <dir> -p <file>", step)
        self.assertIn("nothing else in *Ship checks*", step)
        # Step 2 otherwise says to fix what the checks find, and a sync branch is never committed to.
        self.assertIn("a failing suite is a target-local finding: filed in the target with gh-issue-create.sh, the "
                      "sync ends unmerged or is handed back, and no round runs", step)
        # A re-take keeps HEAD at <oid>, so it reruns the head's old suite: only a later sync from the fixed main clears it.
        self.assertIn("A re-take of this head does not clear it, since it reruns the head's old copy, so a later sync "
                      "cut from the fixed main supersedes it", step)
        self.assertIn("a target-local finding in a local file merges first", bullet("Step 5"))

    def test_the_sync_round_reads_review_diff(self):
        step = bullet("Step 5")
        self.assertIn("one round", step)
        self.assertIn("sync_pr.py review-diff <n> artifacts/review/sync-<n>-r1.diff", step)
        self.assertIn("never Write", step)
        self.assertNotIn("git diff", step, "the branch diff holds the managed files a sync review must not read")
        for severity in ("bug", "medium", "low", "nit"):
            self.assertIn(severity, step)
        self.assertIn("Low findings left by <repo>#<n>: <title>", step)

    def test_step_6_verifies_again_after_a_rebase(self):
        step = bullet("Step 6")
        self.assertIn("After its rebase helper, which publishes, the new HEAD is the head, and sync_pr.py verify <n> --head <oid> "
                      "runs on it before gh-pr-merge.sh", step)
        self.assertIn("replacing the step 5 recheck", step)
        self.assertIn("a failed verify is handed back", step)
        # Step 6 merges the head that was proved: a differing headRefOid is a moved sync and merges nothing.
        self.assertIn("its <oid> must be that head: a headRefOid that differs is a moved sync, a failed verify, "
                      "and nothing merges", step)
        # And no round follows: the recheck the step otherwise re-enters is named only to be replaced.
        self.assertNotIn("re-enter", step)

    def test_a_dispatched_sync_names_its_group(self):
        step = bullet("Under --dispatched")
        self.assertIn("the group is sync-<n>", step)
        self.assertIn("issues is []", step)
        report = flat(ship()[ship().index("\n## Report"):])
        self.assertIn("or sync-<n> for sync #<n>", report)

    def test_the_argument_is_cited_and_argued(self):
        self.assertIn(f"(why: docs/commands/ship.md, *{HEADING}*)", flat(section()).replace("`", ""))
        self.assertIn(f"\n## {HEADING}\n", ARGUMENT.read_text(encoding="utf-8").replace("\r\n", "\n"))
        self.assertIn("sync #<n>", frontmatter("argument-hint"))

    def test_the_grants_name_every_read_a_dispatched_sync_makes(self):
        grants = [g.strip() for g in frontmatter("allowed-tools").split(",")]
        for grant in (f"Bash({SYNC_PR}:*)", f"Bash({HELPER}:*)", "Bash(py -3.12 -m unittest discover:*)",
                      "Bash(bash .claude/scripts/gh-issue-list.sh)"):
            self.assertIn(grant, grants)
        # The same two reach a session that is not /ship through the settings the sync renders.
        allow = json.loads(BASE.read_text(encoding="utf-8"))["permissions"]["allow"]
        self.assertIn(f"Bash({HELPER}:*)", allow)
        self.assertIn(f"Bash({SYNC_PR}:*)", allow)
        # The harness filing helper is /ship's grant alone: a settings allow would reach every command, the sweeps too.
        self.assertIn(f"Bash({FILER}:*)", grants)
        self.assertEqual([], [a for a in allow if "gh-issue-harness" in a])

    def test_a_payload_finding_is_filed_by_the_harness_helper(self):
        step = bullet("Step 5")
        self.assertIn("bash .claude/scripts/gh-issue-harness.sh bug <severity> <area>", step)
        self.assertNotIn("gh-issue-create.sh bug", step)


if __name__ == "__main__":
    unittest.main()
