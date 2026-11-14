"""A red first attempt of a push run on main is rerun once, and files only if the second is red (ADR-210)."""

from __future__ import annotations

import contextlib
import io
import json
import subprocess
import tempfile
import unittest
import unittest.mock
from pathlib import Path

import read_cancelled_run
import rerun_red_run

REPO = "o/r"
RUN = "7"
JOBS = [
    {"name": "build", "conclusion": "success", "steps": []},
    {"name": "integration (ordering)", "conclusion": "failure", "steps": ["Run the shard"]},
    {"name": "images", "conclusion": "cancelled", "steps": []},
]
TRACKER = [{"number": 9, "title": "Main is red"}, {"number": 1406, "title": "Flaky tests tracker"}]
ENV = {"WORKFLOW_ID": "42", "WORKFLOW": "CI", "HEAD_SHA": "abc123", "RUN_URL": "https://run/7", "CONCLUSION": "failure"}


def refused(*arguments: str) -> str:
    raise subprocess.CalledProcessError(1, ["gh", *arguments])


class Rerun(unittest.TestCase):
    """The rerun under a `gh` stub that records every call and answers the run read from a queue."""

    def setUp(self) -> None:
        self.calls: list[tuple[str, ...]] = []
        self.attempts = [("1", "completed", "failure"), ("2", "in_progress", ""), ("2", "completed", "success")]
        self.answers: dict[str, object] = {}
        self.now = 0.0
        self.out = io.StringIO()

    def gh(self, *arguments: str) -> str:
        self.calls.append(arguments)
        for key, answer in self.answers.items():
            if key in " ".join(arguments):
                return answer(*arguments) if callable(answer) else str(answer)
        if arguments[:2] == ("api", "--paginate") and "/attempts/1/jobs" in arguments[2]:
            return "".join(json.dumps({**job, "steps": job["steps"]}) + "\n" for job in JOBS)
        if arguments[0] == "api" and arguments[1] == f"repos/{REPO}/actions/runs/{RUN}":
            return "\t".join(self.attempts.pop(0) if len(self.attempts) > 1 else self.attempts[0]) + "\n"
        if arguments[:2] == ("issue", "list"):
            return json.dumps(TRACKER)
        return ""

    def sleep(self, seconds: float) -> None:
        self.now += seconds

    def run_it(self) -> dict[str, str]:
        with contextlib.redirect_stdout(self.out):
            return rerun_red_run.rerun(self.gh, REPO, RUN, ENV, self.sleep, lambda: self.now)

    def commented(self) -> list[tuple[str, ...]]:
        return [call for call in self.calls if call[:2] == ("issue", "comment")]

    def test_a_second_attempt_that_passes_files_nothing_and_writes_the_flake_down(self) -> None:
        outputs = self.run_it()

        self.assertEqual(outputs["second"], "passed")
        self.assertIn(("run", "rerun", RUN, "--failed", "-R", REPO), self.calls)
        [comment] = self.commented()
        self.assertEqual(comment[2], "1406")
        body = comment[-1]
        self.assertIn("- `integration (ordering)` (failure): `Run the shard`", body)
        self.assertIn("- `images` (cancelled): no step failed", body)
        self.assertNotIn("`build`", body)
        self.assertIn("at abc123 ended `failure` on its first attempt", body)

    def test_the_failed_jobs_are_read_before_the_rerun_replaces_them(self) -> None:
        self.run_it()

        reads = [index for index, call in enumerate(self.calls) if "/attempts/1/jobs" in " ".join(call)]
        rerun = self.calls.index(("run", "rerun", RUN, "--failed", "-R", REPO))
        self.assertEqual(len(reads), 1)
        self.assertLess(reads[0], rerun)

    def test_a_red_second_attempt_is_red_and_says_what_failed_first(self) -> None:
        self.attempts = [("2", "completed", "failure")]

        outputs = self.run_it()

        self.assertEqual((outputs["second"], outputs["conclusion"]), ("red", "failure"))
        self.assertIn("the second attempt ended `failure` too", outputs["why"])
        self.assertIn("`Run the shard`", outputs["why"])

    def test_the_first_attempt_still_reading_completed_is_not_taken_for_the_second(self) -> None:
        self.attempts = [("1", "completed", "failure"), ("1", "queued", ""), ("2", "completed", "failure")]

        self.assertEqual(self.run_it()["second"], "red")
        self.assertEqual(self.now, 2 * rerun_red_run.POLL_SECONDS)

    def test_a_cancelled_second_attempt_is_read_as_adr_196_reads_a_first(self) -> None:
        self.attempts = [("2", "completed", "cancelled")]
        for reason, second in (("", "replaced"), (read_cancelled_run.HIT_TIME_LIMIT, "red")):
            with self.subTest(reason=reason), unittest.mock.patch.object(
                    read_cancelled_run, "read_run", return_value=reason):
                outputs = self.run_it()
                self.assertEqual(outputs["second"], second)
                self.assertIn(reason, outputs["why"])

    def test_a_cancelled_second_attempt_whose_read_fails_is_red(self) -> None:
        self.attempts = [("2", "completed", "cancelled")]
        with unittest.mock.patch.object(read_cancelled_run, "read_run", side_effect=refused):
            self.assertEqual(self.run_it()["second"], "red")

    def test_newer_runs_are_read_before_the_rerun(self) -> None:
        self.run_it()

        [runs] = [index for index, call in enumerate(self.calls) if "/workflows/42/runs?branch=main" in call[1]]
        self.assertEqual(self.calls[runs][-1], f".workflow_runs[] | select(.id > {RUN}) | .id")
        self.assertLess(runs, self.calls.index(("run", "rerun", RUN, "--failed", "-R", REPO)))

    def test_a_newer_run_on_main_is_the_one_heard_so_this_one_is_neither_rerun_nor_filed(self) -> None:
        # The read selects by id alone, so a newer run that already finished stands this one down too.
        self.answers["/workflows/42/runs"] = "8\n"

        outputs = self.run_it()

        self.assertEqual(outputs, {"second": "superseded", "conclusion": "", "why": ""})
        self.assertFalse(any(call[:2] == ("run", "rerun") for call in self.calls))
        self.assertEqual(self.commented(), [])

    def test_newer_runs_that_cannot_be_read_file_at_once_and_rerun_nothing(self) -> None:
        self.answers["/workflows/42/runs"] = refused

        outputs = self.run_it()

        self.assertEqual(outputs["second"], "unread")
        self.assertIn("could not be read", outputs["why"])
        self.assertIn("`Run the shard`", outputs["why"])
        self.assertFalse(any(call[:2] == ("run", "rerun") for call in self.calls))

    def test_a_refused_rerun_is_filed_at_once_and_waits_for_nothing(self) -> None:
        self.answers["run rerun"] = refused
        self.answers["--jq .run_attempt"] = "1\n"

        outputs = self.run_it()

        self.assertEqual(outputs["second"], "refused")
        self.assertIn("GitHub refused to rerun", outputs["why"])
        self.assertIn("`Run the shard`", outputs["why"])
        self.assertEqual(self.commented(), [])
        self.assertEqual(self.now, 0)

    def test_a_rerun_refused_because_a_person_reran_it_waits_on_that_attempt(self) -> None:
        self.answers["run rerun"] = refused
        self.answers["--jq .run_attempt"] = "2\n"
        self.attempts = [("2", "completed", "success")]

        self.assertEqual(self.run_it()["second"], "passed")

    def test_a_rerun_that_outlasts_the_deadline_is_filed_rather_than_waited_on(self) -> None:
        self.attempts = [("2", "in_progress", "")]

        outputs = self.run_it()

        self.assertEqual(outputs["second"], "unfinished")
        self.assertGreaterEqual(self.now, rerun_red_run.DEADLINE_MINUTES * 60)
        self.assertIn(f"after {rerun_red_run.DEADLINE_MINUTES} minutes", outputs["why"])

    def test_a_run_read_that_fails_is_read_again(self) -> None:
        reads = iter([refused, None])

        def flaky(*arguments: str) -> str:
            failing = next(reads, None)
            if failing:
                failing(*arguments)
            return "2\tcompleted\tsuccess\n"
        self.answers[f"repos/{REPO}/actions/runs/{RUN} "] = flaky

        self.assertEqual(self.run_it()["second"], "passed")

    def test_unreadable_jobs_still_rerun_and_say_so(self) -> None:
        self.answers["/attempts/1/jobs"] = refused
        self.attempts = [("2", "completed", "failure")]

        outputs = self.run_it()

        self.assertIn(rerun_red_run.UNREAD, outputs["why"])
        self.assertIn(("run", "rerun", RUN, "--failed", "-R", REPO), self.calls)

    def test_no_tracker_or_a_refused_comment_warns_and_the_rerun_goes_on(self) -> None:
        for key, answer in (("issue list", "[]"), ("issue comment", refused)):
            with self.subTest(key=key):
                self.answers = {key: answer}
                self.out = io.StringIO()
                self.attempts = [("2", "completed", "success")]
                self.assertEqual(self.run_it()["second"], "passed")
                self.assertIn("::warning::", self.out.getvalue())


class CommandLine(unittest.TestCase):
    def test_the_outputs_are_written_delimited_so_a_multi_line_why_survives(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "out"
            rerun_red_run.write_outputs(str(output), {"second": "red", "why": "one\ntwo"})
            lines = output.read_text(encoding="utf-8").splitlines()

        self.assertTrue(lines[0].startswith("second<<EOF_"))
        self.assertEqual(lines[1:3], ["red", lines[0].split("<<")[1]])
        self.assertTrue(lines[3].startswith("why<<EOF_"))
        self.assertEqual(lines[4:6], ["one", "two"])
        self.assertNotEqual(lines[0].split("<<")[1], lines[3].split("<<")[1])

    def test_main_reruns_and_writes_the_outputs(self) -> None:
        stub = Rerun()
        stub.setUp()
        stub.attempts = [("2", "completed", "success")]
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            output = Path(directory) / "out"
            code = rerun_red_run.main(["rerun_red_run.py", REPO, RUN], stub.gh,
                                      {**ENV, "GITHUB_OUTPUT": str(output)}, stub.sleep, lambda: stub.now)
            text = output.read_text(encoding="utf-8")

        self.assertEqual(code, 0)
        self.assertIn("\npassed\n", text)


class TheShippedWorkflow(unittest.TestCase):
    def test_the_alert_runs_this_file_by_its_own_path(self) -> None:
        here = Path(rerun_red_run.__file__).resolve()
        root = here.parents[2]
        workflow = (root / ".github" / "workflows" / "main-red.yml").read_text(encoding="utf-8")

        self.assertIn(f"python3 {here.relative_to(root).as_posix()} ", workflow)


if __name__ == "__main__":
    unittest.main()
