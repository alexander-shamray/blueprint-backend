"""The suite budget's suite, over throwaway modules and the review helpers' real directory.

    cd tools/suite-budget && py -3.12 -m unittest
"""

import contextlib
import io
import math
import subprocess
import tempfile
import textwrap
import time
import unittest
from pathlib import Path
from unittest import mock

import suite_budget

PASSING = """
import unittest
class Passes(unittest.TestCase):
    def test_passes(self):
        pass
"""
FAILING = PASSING.replace("pass\n", "self.fail('red')\n")
# An arrow is outside cp1252, the encoding a Windows child writes a file in and a session's pipe reads.
FAILING_ARROW = PASSING.replace("pass\n", "self.fail('red \\u2192 green')\n")
SLEEPING = """
import time, unittest
class Sleeps(unittest.TestCase):
    def test_sleeps(self):
        time.sleep(60)
"""
# The grandchild inherits the module's output, as a helper's bash does; it marks that it started, and again if it
# outlives the kill.
SPAWNING = """
import subprocess, sys, time, unittest
STARTED, MARK = {started!r}, {mark!r}
GRANDCHILD = f"import time; open({{STARTED!r}}, 'w').close(); time.sleep(6); open({{MARK!r}}, 'w').close()"
class Spawns(unittest.TestCase):
    def test_spawns(self):
        subprocess.Popen([sys.executable, "-c", GRANDCHILD])
        time.sleep(60)
"""


def discovered(directory):
    """The module files CI's discover loads from a directory, read from unittest itself."""
    found, pending = set(), [unittest.TestLoader().discover(str(directory), top_level_dir=str(directory))]
    while pending:
        for test in pending.pop():
            if isinstance(test, unittest.TestSuite):
                pending.append(test)
            else:
                found.add(type(test).__module__ + ".py")
    return sorted(found)


class Verdicts(unittest.TestCase):
    def test_a_pass_inside_the_ceiling_is_ok(self):
        self.assertEqual(suite_budget.verdict(9.0, 0, 10), "ok")

    def test_a_pass_past_the_ceiling_is_over(self):
        self.assertEqual(suite_budget.verdict(11.0, 0, 10), "over")

    def test_a_failure_is_failed_whatever_its_time(self):
        self.assertEqual(suite_budget.verdict(1.0, 1, 10), "failed")

    def test_a_module_stopped_by_the_timeout_is_hung(self):
        self.assertEqual(suite_budget.verdict(30.0, None, 10), "hung")


class Runs(unittest.TestCase):
    def setUp(self):
        self.directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        for name, body in (("test_passing.py", PASSING), ("test_failing.py", FAILING),
                           ("test_sleeping.py", SLEEPING)):
            (self.directory / name).write_text(textwrap.dedent(body), encoding="utf-8")

    def test_each_module_runs_alone_with_its_own_verdict(self):
        results = {m: suite_budget.run(self.directory, m, 30) for m in ("test_passing.py", "test_failing.py")}
        self.assertEqual(results["test_passing.py"].verdict, "ok")
        self.assertEqual(results["test_failing.py"].verdict, "failed")
        self.assertIn("AssertionError: red", results["test_failing.py"].output)

    def test_a_module_past_its_kill_point_is_stopped_and_named_hung(self):
        result = suite_budget.run(self.directory, "test_sleeping.py", 2, kill_factor=1)
        self.assertEqual(result.verdict, "hung")
        self.assertGreaterEqual(result.seconds, 2)
        self.assertLess(result.seconds, 30)

    def test_a_hung_module_s_own_processes_are_stopped_with_it(self):
        started, mark = self.directory / "started", self.directory / "outlived"
        body = SPAWNING.format(started=str(started), mark=str(mark))
        (self.directory / "test_spawning.py").write_text(textwrap.dedent(body), encoding="utf-8")
        result = suite_budget.run(self.directory, "test_spawning.py", 2, kill_factor=1)
        self.assertEqual(result.verdict, "hung")
        self.assertGreaterEqual(result.seconds, 2)
        self.assertLess(result.seconds, 6)
        self.assertTrue(started.exists(), "the grandchild never started, so the kill proves nothing")
        time.sleep(8)
        self.assertFalse(mark.exists(), "the module's grandchild outlived the kill")

    def test_the_modules_timed_are_the_ones_discover_loads(self):
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        for name in ("testbudgetnoscore.py", "test_budget_scored.py", "budget_helper.py", "budget_test.py"):
            (directory / name).write_text(textwrap.dedent(PASSING), encoding="utf-8")
        self.assertEqual(suite_budget.modules(directory), discovered(directory))
        self.assertIn("testbudgetnoscore.py", suite_budget.modules(directory))

    def test_a_failed_tree_kill_still_stops_the_child(self):
        def refused(*args, **kwargs):
            raise subprocess.TimeoutExpired(args[0], 60)
        with mock.patch.object(suite_budget.subprocess, "run", refused), \
                mock.patch.object(suite_budget.os, "killpg", side_effect=ProcessLookupError, create=True):
            result = suite_budget.run(self.directory, "test_sleeping.py", 2, kill_factor=1)
        self.assertEqual(result.verdict, "hung")
        self.assertGreaterEqual(result.seconds, 2)
        self.assertLess(result.seconds, 30)

    def test_a_module_name_that_matches_nothing_fails(self):
        self.assertEqual(suite_budget.run(self.directory, "test_absent.py", 30).verdict, "failed")

    def test_the_report_names_each_module_and_shows_a_failure_s_output(self):
        results = [suite_budget.run(self.directory, m, 30) for m in ("test_passing.py", "test_failing.py")]
        text = suite_budget.report(results)
        self.assertRegex(text, r"test_passing\.py +\d+\.\d +30  ok")
        self.assertRegex(text, r"test_failing\.py +\d+\.\d +30  failed")
        self.assertIn("--- test_failing.py (failed)", text)
        self.assertIn("AssertionError: red", text)

    def test_the_report_marks_a_module_with_no_row_unmeasured(self):
        self.assertNotIn("test_passing.py", dict(suite_budget.rows()))
        written = io.StringIO()
        with contextlib.redirect_stdout(written), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(suite_budget.main(["--dir", str(self.directory), "test_passing.py"]), 0)
        line = rf"test_passing\.py +\d+\.\d +{suite_budget.DEFAULT_CEILING}  ok  unmeasured"
        self.assertRegex(written.getvalue(), line)
        self.assertIn("unmeasured: no row in docs/testing.md", written.getvalue())
        measured = suite_budget.report([suite_budget.Result("test_passing.py", 1.0, 30, "ok")])
        self.assertNotIn("unmeasured", measured)

    def test_a_failure_s_non_ascii_output_survives_to_a_cp1252_report(self):
        (self.directory / "test_arrow.py").write_text(textwrap.dedent(FAILING_ARROW), encoding="utf-8")
        self.assertIn("red → green", suite_budget.run(self.directory, "test_arrow.py", 30).output)
        written = io.BytesIO()
        stdout = io.TextIOWrapper(written, encoding="cp1252")
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(suite_budget.main(["--dir", str(self.directory), "test_arrow.py"]), 1)
        stdout.flush()
        self.assertIn(b"red \\u2192 green", written.getvalue())

    def test_the_run_exits_non_zero_when_any_module_is_not_ok(self):
        for module, expected in (("test_passing.py", 0), ("test_failing.py", 1)):
            with self.subTest(module), contextlib.redirect_stdout(io.StringIO()), \
                    contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(suite_budget.main(["--dir", str(self.directory), module]), expected)


class TheRealSuite(unittest.TestCase):
    """What the runner covers is the review helpers' directory as it stands, not a list kept beside it."""

    directory = suite_budget.ROOT / suite_budget.SUITE

    def test_every_module_ci_runs_from_the_suite_directory_is_timed(self):
        loaded = discovered(self.directory)
        self.assertTrue(loaded)
        self.assertEqual(suite_budget.modules(self.directory), loaded)

    def test_every_ceiling_names_a_module_or_its_row(self):
        recorded = {module for module, _ in suite_budget.rows()}
        self.assertLessEqual(set(suite_budget.CEILINGS), set(suite_budget.modules(self.directory)) | recorded)

    def test_every_ceiling_is_positive(self):
        self.assertTrue(all(c > 0 for c in suite_budget.CEILINGS.values()), suite_budget.CEILINGS)

    def test_each_ceiling_is_twice_its_recorded_figure_rounded_up_to_ten(self):
        rows = suite_budget.rows()
        figures = dict(rows)
        self.assertEqual(len(rows), len(figures), "a module has two rows")
        # A named ceiling follows a row, so a module with none has only the default to be judged by.
        unrowed = set(suite_budget.modules(self.directory)) - set(figures)
        self.assertFalse(unrowed & set(suite_budget.CEILINGS), "a named ceiling has no row")
        for module, seconds in figures.items():
            with self.subTest(module):
                due = math.ceil(seconds * 2 / 10) * 10
                if due > suite_budget.DEFAULT_CEILING:
                    self.assertEqual(suite_budget.CEILINGS.get(module), due)
                else:
                    self.assertNotIn(module, suite_budget.CEILINGS)

    def test_a_module_with_no_row_is_held_to_the_default_ceiling(self):
        # A sync's module arrives before its row (docs/testing.md, *Each of its modules has a time budget*).
        arrived = "test_arrived_with_a_sync.py"
        with mock.patch.object(suite_budget, "modules", return_value=[*suite_budget.modules(self.directory), arrived]):
            self.test_each_ceiling_is_twice_its_recorded_figure_rounded_up_to_ten()
            with mock.patch.dict(suite_budget.CEILINGS, {arrived: 100}), self.assertRaises(AssertionError):
                self.test_each_ceiling_is_twice_its_recorded_figure_rounded_up_to_ten()


if __name__ == "__main__":
    unittest.main()
