#!/usr/bin/env python3
"""Negative cases for the commit gate, each beside a positive control."""

from __future__ import annotations

import subprocess
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


if __name__ == "__main__":
    unittest.main()
