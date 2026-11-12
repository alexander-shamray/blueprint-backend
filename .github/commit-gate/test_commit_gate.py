#!/usr/bin/env python3
"""Negative cases for the commit gate, each beside a positive control."""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from commit_gate import check, collect, main, subject_problem


def commit(
    subject: str = "chore(ci): build each commit",
    *,
    parents: int = 1,
    trailers: tuple[tuple[str, str], ...] = (("PR", "#7"),),
    sha: str = "a" * 40,
) -> dict:
    return {
        "sha": sha,
        "parents": ["b" * 40] * parents,
        "subject": subject,
        "trailers": list(trailers),
    }


class Judgement(unittest.TestCase):
    def test_a_well_formed_range_passes(self) -> None:
        self.assertEqual(check(7, [commit(), commit("docs: the form")]), [])

    def test_an_empty_range_is_refused_not_passed(self) -> None:
        self.assertEqual(len(check(7, [])), 1)

    def test_a_merge_commit_fails(self) -> None:
        problems = check(7, [commit(parents=2)])
        self.assertEqual(len(problems), 1)
        self.assertIn("merge commit", problems[0])

    def test_a_missing_trailer_fails(self) -> None:
        problems = check(7, [commit(trailers=())])
        self.assertIn("carries no `PR: #7` trailer", problems[0])

    def test_another_pull_requests_trailer_fails(self) -> None:
        problems = check(7, [commit(trailers=(("PR", "#8"),))])
        self.assertIn("not `PR: #7`", problems[0])

    def test_a_wrong_trailer_beside_the_right_one_fails(self) -> None:
        self.assertEqual(len(check(7, [commit(trailers=(("PR", "#7"), ("PR", "#8")))])), 1)

    def test_a_trailer_of_another_key_is_not_the_trailer(self) -> None:
        self.assertEqual(len(check(7, [commit(trailers=(("Reverts", "#7"),))])), 1)

    def test_a_trailer_prefix_is_not_the_number(self) -> None:
        self.assertEqual(len(check(7, [commit(trailers=(("PR", "#70"),))])), 1)

    def test_the_other_trailers_beside_it_are_welcome(self) -> None:
        trailers = (("Co-Authored-By", "Claude"), ("Claude-Session", "https://x"), ("PR", "#7"))
        self.assertEqual(check(7, [commit(trailers=trailers)]), [])

    def test_every_commit_is_judged_not_the_first(self) -> None:
        problems = check(7, [commit(), commit(trailers=(), sha="c" * 40)])
        self.assertEqual(len(problems), 1)
        self.assertTrue(problems[0].startswith("c" * 12))


class Subjects(unittest.TestCase):
    def test_the_types_the_command_lists_pass(self) -> None:
        for subject in (
            "docs: a",
            "docs(harness): a",
            "feat(catalog): a",
            "fix: a",
            "chore: a",
            "refactor: a",
            "test: a",
        ):
            with self.subTest(subject=subject):
                self.assertIsNone(subject_problem(subject))

    def test_feat_without_a_scope_fails(self) -> None:
        self.assertIsNotNone(subject_problem("feat: a"))

    def test_an_unknown_type_fails(self) -> None:
        self.assertIsNotNone(subject_problem("update: a"))

    def test_an_unknown_type_is_told_the_closed_set_and_the_ci_type(self) -> None:
        problem = subject_problem("ci(observability): a") or ""
        self.assertIn("docs, feat(<scope>), fix, chore, refactor or test", problem)
        self.assertIn("chore(ci) or fix(ci), never ci(...)", problem)

    def test_a_subject_with_no_colon_space_fails(self) -> None:
        for subject in ("docs a", "docs:a", "docs: ", ""):
            with self.subTest(subject=subject):
                self.assertIsNotNone(subject_problem(subject))

    def test_each_leftover_is_named(self) -> None:
        for subject in ("fixup! docs: a", "squash! docs: a", "amend! docs: a", "wip", "WIP: a", "wip docs: a"):
            with self.subTest(subject=subject):
                self.assertIn("leftover", subject_problem(subject) or "")

    def test_a_word_that_merely_starts_with_wip_is_not_a_leftover(self) -> None:
        self.assertNotIn("leftover", subject_problem("wipe: a") or "")

    def test_a_git_written_revert_is_judged_by_its_inner_subject(self) -> None:
        self.assertIsNone(subject_problem('Revert "docs: a"'))
        self.assertIsNotNone(subject_problem('Revert "update: a"'))
        self.assertIn("leftover", subject_problem('Revert "wip: a"') or "")


def git(root: Path, *args: str, stdin: str | None = None) -> str:
    done = subprocess.run(
        ["git", "-C", str(root), "-c", "user.name=t", "-c", "user.email=t@example.com", *args],
        input=stdin, capture_output=True, text=True, encoding="utf-8", check=True,
    )
    return done.stdout.strip()


class Reading(unittest.TestCase):
    def setUp(self) -> None:
        self._dir = tempfile.TemporaryDirectory()
        self.root = Path(self._dir.name)
        git(self.root, "init", "-q", "-b", "main")
        git(self.root, "commit", "-q", "--allow-empty", "-m", "chore: base")
        self.base = git(self.root, "rev-parse", "HEAD")
        self.addCleanup(self._dir.cleanup)

    def land(self, message: str) -> None:
        git(self.root, "commit", "-q", "--allow-empty", "-F", "-", stdin=message)

    def read(self) -> list[dict]:
        import os

        previous = os.getcwd()
        os.chdir(self.root)
        try:
            return collect(self.base, "HEAD")
        finally:
            os.chdir(previous)

    def test_a_trailer_in_the_last_paragraph_is_read(self) -> None:
        self.land("docs: a\n\nThe body.\n\nCo-Authored-By: C <c@example.com>\nPR: #7\n")
        commits = self.read()
        self.assertEqual(check(7, commits), [])
        self.assertEqual(len(commits[0]["parents"]), 1)

    def test_the_same_line_in_the_body_is_not_a_trailer(self) -> None:
        self.land("docs: a\n\nThe line below is quoted, not a trailer:\n\nPR: #7\n\nMore prose after it.\n")
        self.assertEqual(len(check(7, self.read())), 1)

    def test_a_real_merge_commit_is_seen_as_one(self) -> None:
        git(self.root, "switch", "-q", "-c", "side")
        self.land("docs: side\n\nPR: #7\n")
        git(self.root, "switch", "-q", "main")
        self.land("docs: main\n\nPR: #7\n")
        git(self.root, "merge", "-q", "--no-ff", "-m", "docs: merge\n\nPR: #7", "side")
        problems = check(7, self.read())
        self.assertTrue(any("merge commit" in p for p in problems))

    def test_main_reports_the_failure_as_an_exit_code(self) -> None:
        self.land("docs: a\n\nPR: #7\n")
        self.land("docs: b\n")
        import os

        previous = os.getcwd()
        os.chdir(self.root)
        try:
            self.assertEqual(main(["--pr", "7", self.base, "HEAD"]), 1)
            self.assertEqual(main(["--pr", "7", self.base, "no-such-ref"]), 2)
        finally:
            os.chdir(previous)


WORKFLOW = Path(__file__).resolve().parent.parent / "workflows" / "gates.yml"
DEPENDABOT_LOGIN = "dependabot[bot]"
DEPENDABOT_MAKER = ("49699333+dependabot[bot]@users.noreply.github.com", "noreply@github.com")
HUMAN = "someone@example.com"
STANDS_DOWN = "steps.dependabot.outputs.own != 'true'"
# The take, the judge and the suite: a step of this gate by its id or the directory it runs in.
OWN_STEP = re.compile(r"(?m)^        (?:id: commit(?:_take)?|working-directory: \.github/commit-gate)$")


def find_bash() -> str | None:
    # Git for Windows' bash first: a bare `bash` on a Windows PATH can be WSL's.
    if sys.platform == "win32" and shutil.which("git"):
        top = Path(shutil.which("git")).resolve().parent.parent
        for candidate in (top / "bin" / "bash.exe", top.parent / "bin" / "bash.exe"):
            if candidate.is_file():
                return str(candidate)
    return shutil.which("bash")


BASH = find_bash()


def setUpModule() -> None:
    # Not a skip: a skip reports a pass for a gate nothing has run.
    if BASH is None:
        raise RuntimeError("bash is needed to run the workflow's deciding step")


def workflow_steps() -> list[str]:
    # The comments between steps dropped, or the one ahead of the next step reads as the end of this one's script.
    text = re.sub(r"(?m)^      #.*\n", "", WORKFLOW.read_text(encoding="utf-8"))
    return re.split(r"\n(?=      - )", text.split("\n    steps:\n", 1)[1])


def deciding_step() -> str:
    return next(s for s in workflow_steps() if "id: dependabot" in s)


def run_block(step: str) -> str:
    lines = step.split("\n")
    at = next(i for i, line in enumerate(lines) if line.strip() == "run: |")
    return "\n".join(line[10:] for line in lines[at + 1:]) + "\n"


class Standing(unittest.TestCase):
    """The deciding step of gates.yml, run as written against real commits."""

    def setUp(self) -> None:
        self._dir = tempfile.TemporaryDirectory()
        self.addCleanup(self._dir.cleanup)
        self.root = Path(self._dir.name) / "repo"
        self.root.mkdir()
        git(self.root, "init", "-q", "-b", "main")
        git(self.root, "commit", "-q", "--allow-empty", "-m", "chore: base")
        self.base = git(self.root, "rev-parse", "HEAD")

    def commit(self, author: str, committer: str) -> None:
        env = {
            **os.environ,
            "GIT_AUTHOR_NAME": "a", "GIT_AUTHOR_EMAIL": author,
            "GIT_COMMITTER_NAME": "c", "GIT_COMMITTER_EMAIL": committer,
        }
        subprocess.run(
            ["git", "-C", str(self.root), "commit", "-q", "--allow-empty", "-m", "chore: x"],
            env=env, check=True, capture_output=True,
        )

    def own(self, opener: str) -> str:
        step = deciding_step()
        declared = re.search(r"^ +DEPENDABOT: (.+)$", step, re.M)
        out = Path(self._dir.name) / "output"
        out.write_text("", encoding="utf-8")
        env = {
            **os.environ,
            "PR_AUTHOR": opener,
            "BASE_SHA": self.base,
            "HEAD_SHA": git(self.root, "rev-parse", "HEAD"),
            "DEPENDABOT": declared.group(1).strip(),
            "GITHUB_OUTPUT": out.as_posix(),
        }
        subprocess.run(
            [BASH, "-c", run_block(step)],
            cwd=self.root, env=env, check=True, capture_output=True,
            text=True, encoding="utf-8",
        )
        return re.search(r"^own=(\w+)$", out.read_text(encoding="utf-8"), re.M).group(1)

    def test_dependabots_own_commits_on_its_own_pull_request_stand_down(self) -> None:
        self.commit(*DEPENDABOT_MAKER)
        self.commit(*DEPENDABOT_MAKER)
        self.assertEqual(self.own(DEPENDABOT_LOGIN), "true")

    def test_a_human_committer_on_dependabots_pull_request_is_judged(self) -> None:
        self.commit(*DEPENDABOT_MAKER)
        self.commit(DEPENDABOT_MAKER[0], HUMAN)
        self.assertEqual(self.own(DEPENDABOT_LOGIN), "false")

    def test_a_wholly_human_commit_on_dependabots_pull_request_is_judged(self) -> None:
        self.commit(*DEPENDABOT_MAKER)
        self.commit(HUMAN, HUMAN)
        self.assertEqual(self.own(DEPENDABOT_LOGIN), "false")

    def test_a_human_author_with_githubs_committer_is_judged(self) -> None:
        self.commit(HUMAN, DEPENDABOT_MAKER[1])
        self.assertEqual(self.own(DEPENDABOT_LOGIN), "false")

    def test_dependabots_commits_on_a_pull_request_a_human_opened_are_judged(self) -> None:
        self.commit(*DEPENDABOT_MAKER)
        self.assertEqual(self.own("someone"), "false")

    def test_an_empty_range_is_judged(self) -> None:
        self.assertEqual(self.own(DEPENDABOT_LOGIN), "false")


class WorkflowShape(unittest.TestCase):
    def test_the_job_has_no_condition_so_a_human_push_is_judged(self) -> None:
        head = WORKFLOW.read_text(encoding="utf-8").split("\n    steps:\n", 1)[0]
        job = head.split("\n  gates:\n", 1)[1]
        self.assertNotRegex(job, r"(?m)^    if:")

    def test_the_checkout_runs_first_with_full_history_and_no_condition(self) -> None:
        first = workflow_steps()[0]
        self.assertIn("actions/checkout", first)
        self.assertIn("fetch-depth: 0", first)
        self.assertNotIn("if:", first)

    def test_every_step_of_the_gate_after_the_decision_stands_down_with_it(self) -> None:
        steps = workflow_steps()
        at = steps.index(deciding_step())
        own = [step for step in steps if OWN_STEP.search(step)]
        self.assertEqual(3, len(own))
        for step in own:
            self.assertGreater(steps.index(step), at)
            condition = re.search(r"(?m)^        if: (.+)$", step)
            self.assertIsNotNone(condition)
            self.assertIn(STANDS_DOWN, condition.group(1))

    def test_the_stand_down_is_posted_as_a_pass(self) -> None:
        # The steps skip, so the check a ruleset requires has to be posted as passing or it never reports.
        report = next(step for step in workflow_steps() if "id: report" in step)
        self.assertEqual(2, report.count("steps.dependabot.outputs.own == 'true' && 'success' ||"))


if __name__ == "__main__":
    unittest.main()
