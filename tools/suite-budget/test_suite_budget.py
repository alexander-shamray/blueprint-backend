"""The suite budget's suite, over throwaway modules and the review helpers' real directory.

    cd tools/suite-budget && py -3.12 -m unittest
"""

import contextlib
import io
import math
import re
import tempfile
import textwrap
import time
import unittest
from pathlib import Path

import suite_budget

PASSING = """
import unittest
class Passes(unittest.TestCase):
    def test_passes(self):
        pass
"""
FAILING = PASSING.replace("pass\n", "self.fail('red')\n")
SLEEPING = """
import time, unittest
class Sleeps(unittest.TestCase):
    def test_sleeps(self):
        time.sleep(60)
"""
# The grandchild inherits the module's output, as a helper's bash does, and leaves a mark if it outlives the kill.
SPAWNING = """
import subprocess, sys, time, unittest
MARK = {mark!r}
class Spawns(unittest.TestCase):
    def test_spawns(self):
        subprocess.Popen([sys.executable, "-c", f"import time; time.sleep(6); open({{MARK!r}}, 'w').close()"])
        time.sleep(60)
"""


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
        self.assertLess(result.seconds, 30)

    def test_a_hung_module_s_own_processes_are_stopped_with_it(self):
        mark = self.directory / "outlived"
        (self.directory / "test_spawning.py").write_text(textwrap.dedent(SPAWNING.format(mark=str(mark))),
                                                         encoding="utf-8")
        result = suite_budget.run(self.directory, "test_spawning.py", 2, kill_factor=1)
        self.assertEqual(result.verdict, "hung")
        self.assertLess(result.seconds, 6)
        time.sleep(8)
        self.assertFalse(mark.exists(), "the module's grandchild outlived the kill")

    def test_a_module_name_that_matches_nothing_fails(self):
        self.assertEqual(suite_budget.run(self.directory, "test_absent.py", 30).verdict, "failed")

    def test_the_report_names_each_module_and_shows_a_failure_s_output(self):
        results = [suite_budget.run(self.directory, m, 30) for m in ("test_passing.py", "test_failing.py")]
        text = suite_budget.report(results)
        self.assertRegex(text, r"test_passing\.py +\d+\.\d +30  ok")
        self.assertRegex(text, r"test_failing\.py +\d+\.\d +30  failed")
        self.assertIn("--- test_failing.py (failed)", text)
        self.assertIn("AssertionError: red", text)

    def test_the_run_exits_non_zero_when_any_module_is_not_ok(self):
        for module, expected in (("test_passing.py", 0), ("test_failing.py", 1)):
            with self.subTest(module), contextlib.redirect_stdout(io.StringIO()), \
                    contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(suite_budget.main(["--dir", str(self.directory), module]), expected)


class TheRealSuite(unittest.TestCase):
    """What the runner covers is the review helpers' directory as it stands, not a list kept beside it."""

    directory = suite_budget.ROOT / suite_budget.SUITE

    def test_every_module_in_the_suite_directory_is_run(self):
        on_disk = sorted(p.name for p in self.directory.iterdir() if p.name.startswith("test_") and p.suffix == ".py")
        self.assertTrue(on_disk)
        self.assertEqual(suite_budget.modules(self.directory), on_disk)

    def test_every_ceiling_names_a_module_that_exists(self):
        self.assertLessEqual(set(suite_budget.CEILINGS), set(suite_budget.modules(self.directory)))

    def test_every_ceiling_is_positive(self):
        self.assertTrue(all(c > 0 for c in suite_budget.CEILINGS.values()), suite_budget.CEILINGS)

    def test_each_ceiling_is_twice_its_recorded_figure_rounded_up_to_ten(self):
        text = (suite_budget.ROOT / "docs" / "testing.md").read_text(encoding="utf-8")
        figures = {m: float(s) for m, s in re.findall(r"`(test_\w+\.py)` \| (\d+\.\d) \|", text)}
        self.assertEqual(set(figures), set(suite_budget.modules(self.directory)))
        for module, seconds in figures.items():
            with self.subTest(module):
                due = math.ceil(seconds * 2 / 10) * 10
                if due > suite_budget.DEFAULT_CEILING:
                    self.assertEqual(suite_budget.CEILINGS.get(module), due)
                else:
                    self.assertNotIn(module, suite_budget.CEILINGS)


if __name__ == "__main__":
    unittest.main()
