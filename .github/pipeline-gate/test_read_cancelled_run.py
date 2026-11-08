"""A cancelled push run on main: red, or replaced by a newer one (ADR-196)."""

from __future__ import annotations

import contextlib
import io
import subprocess
import unittest
from pathlib import Path

import read_cancelled_run

# The platform's own words, each read from a run on this repository.
TIME_LIMIT = "The job has exceeded the maximum execution time of 30m0s"
REPLACED = "Canceling since a higher priority waiting request for compose-refs/heads/main exists"
BESIDE_BOTH = "The operation was canceled."


class ReasonTests(unittest.TestCase):
    def test_a_job_at_its_time_limit_is_red(self) -> None:
        self.assertEqual(read_cancelled_run.read_reason([TIME_LIMIT, BESIDE_BOTH]), read_cancelled_run.HIT_TIME_LIMIT)

    def test_a_replaced_run_is_not_red(self) -> None:
        self.assertEqual(read_cancelled_run.read_reason([REPLACED, BESIDE_BOTH]), "")

    def test_a_time_limit_beside_a_replaced_job_is_red(self) -> None:
        self.assertEqual(read_cancelled_run.read_reason([REPLACED, TIME_LIMIT]), read_cancelled_run.HIT_TIME_LIMIT)

    def test_a_cancel_the_platform_does_not_explain_is_red_rather_than_silent(self) -> None:
        for messages in ([BESIDE_BOTH], []):
            with self.subTest(messages=messages):
                self.assertEqual(read_cancelled_run.read_reason(messages), read_cancelled_run.NOT_REPLACED)


class ReadingTests(unittest.TestCase):
    """The run's jobs, then the annotations of each that did not pass, read through `gh api`."""

    def setUp(self) -> None:
        self.calls: list[tuple[str, ...]] = []
        self.replies = {
            "repos/o/r/actions/runs/7/jobs?per_page=100": "1\tsuccess\n2\tskipped\n3\tcancelled\n4\tcancelled\n",
            "repos/o/r/check-runs/3/annotations?per_page=100": f"{BESIDE_BOTH}\n",
            "repos/o/r/check-runs/4/annotations?per_page=100": f"{TIME_LIMIT}\n{BESIDE_BOTH}\n",
        }

    def gh(self, *arguments: str) -> str:
        self.calls.append(arguments)
        return self.replies[arguments[2]]

    def test_only_the_jobs_that_did_not_pass_are_read(self) -> None:
        reason = read_cancelled_run.read_run(self.gh, "o/r", "7")

        self.assertEqual(reason, read_cancelled_run.HIT_TIME_LIMIT)
        self.assertEqual([call[2] for call in self.calls[1:]], [
            "repos/o/r/check-runs/3/annotations?per_page=100",
            "repos/o/r/check-runs/4/annotations?per_page=100"])

    def test_a_run_replaced_while_it_queued_has_no_job_and_is_not_red(self) -> None:
        self.replies["repos/o/r/actions/runs/7/jobs?per_page=100"] = ""

        self.assertEqual(read_cancelled_run.read_run(self.gh, "o/r", "7"), "")

    def test_a_run_whose_jobs_all_passed_is_red_rather_than_silent(self) -> None:
        self.replies["repos/o/r/actions/runs/7/jobs?per_page=100"] = "1\tsuccess\n"

        self.assertEqual(read_cancelled_run.read_run(self.gh, "o/r", "7"), read_cancelled_run.NOT_REPLACED)

    def test_every_read_follows_the_pages(self) -> None:
        read_cancelled_run.read_run(self.gh, "o/r", "7")

        self.assertTrue(self.calls)
        for call in self.calls:
            self.assertEqual(call[:2], ("api", "--paginate"))

    def test_the_command_line_prints_the_reason(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = read_cancelled_run.main(["read_cancelled_run.py", "o/r", "7"], self.gh)

        self.assertEqual((code, out.getvalue().strip()), (0, read_cancelled_run.HIT_TIME_LIMIT))

    def test_a_replaced_run_prints_nothing(self) -> None:
        self.replies["repos/o/r/check-runs/4/annotations?per_page=100"] = f"{REPLACED}\n"
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = read_cancelled_run.main(["read_cancelled_run.py", "o/r", "7"], self.gh)

        self.assertEqual((code, out.getvalue().strip()), (0, ""))

    def test_a_failed_read_fails_rather_than_reading_as_replaced(self) -> None:
        def refused(*arguments: str) -> str:
            raise subprocess.CalledProcessError(1, arguments)

        out = io.StringIO()
        with contextlib.redirect_stdout(out), self.assertRaises(subprocess.CalledProcessError):
            read_cancelled_run.main(["read_cancelled_run.py", "o/r", "7"], refused)
        self.assertEqual(out.getvalue(), "")


class TheShippedWorkflow(unittest.TestCase):
    def test_the_alert_runs_this_file_by_its_own_path(self) -> None:
        here = Path(read_cancelled_run.__file__).resolve()
        root = here.parents[2]
        workflow = (root / ".github" / "workflows" / "main-red.yml").read_text(encoding="utf-8")

        self.assertIn(f"python3 {here.relative_to(root).as_posix()} ", workflow)


if __name__ == "__main__":
    unittest.main()
