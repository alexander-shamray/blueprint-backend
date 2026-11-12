"""`harness_timings.py`: one line a run outside the checkout, a record that never fails the run, and its reader."""

import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import harness_timings as timings

SCRIPTS = Path(__file__).resolve().parent
CLI = SCRIPTS / "harness_timings.py"


class WhereTheLinesGo(unittest.TestCase):

    def test_the_variable_wins_wherever_the_host(self):
        self.assertEqual(Path("/x/t.jsonl"), timings.timings_path({timings.ENV: "/x/t.jsonl", "LOCALAPPDATA": "C:/a"}))

    def test_otherwise_the_users_cache_and_never_the_checkout(self):
        path = timings.timings_path({"LOCALAPPDATA": "C:/Users/u/AppData/Local", "XDG_CACHE_HOME": "/c"})
        expected = Path("C:/Users/u/AppData/Local") if os.name == "nt" else Path("/c")
        self.assertEqual(expected / "blueprint-harness" / "timings.jsonl", path)
        self.assertNotIn(str(SCRIPTS.parent.parent), str(timings.timings_path({})))


class ARunIsOneLine(unittest.TestCase):

    def setUp(self):
        holder = tempfile.TemporaryDirectory()
        self.addCleanup(holder.cleanup)
        self.path = Path(holder.name) / "deep" / "timings.jsonl"

    def test_each_record_appends_one_line_carrying_where_and_when(self):
        self.assertTrue(timings.record({"script": "a", "wall": 1.0, "exit": 0}, self.path))
        self.assertTrue(timings.record({"script": "b", "wall": 2.0, "exit": 1}, self.path))
        lines = [json.loads(line) for line in self.path.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(["a", "b"], [line["script"] for line in lines])
        self.assertLessEqual({"at", "host", "os", "wall", "exit"}, set(lines[0]))

    def test_a_record_that_cannot_be_written_is_said_and_never_raised(self):
        self.path.parent.mkdir()
        blocked = self.path.parent / "file"
        blocked.write_text("", encoding="utf-8")
        said = io.StringIO()
        with contextlib.redirect_stderr(said):
            self.assertFalse(timings.record({"wall": 1.0}, blocked / "timings.jsonl"))
        self.assertIn("not recorded", said.getvalue())

    def test_the_checkout_is_named_for_its_repository(self):
        read = timings.checkout(SCRIPTS)
        self.assertTrue(read["repository"])
        self.assertRegex(read["commit"] or "", r"^[0-9a-f]{40}$")


class TheReader(unittest.TestCase):

    def test_the_percentile_is_a_value_some_run_took(self):
        values = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100]
        self.assertEqual(50, timings.percentile(values, .5))
        self.assertEqual(90, timings.percentile(values, .9))
        self.assertEqual(7, timings.percentile([7], .9))

    def test_a_line_that_does_not_read_is_counted_and_skipped(self):
        runs, bad = timings.entries('{"wall": 3}\nnot json\n{"script": "x"}\n\n[1]\n')
        self.assertEqual([{"wall": 3}], runs)
        self.assertEqual(3, bad)

    def test_the_report_groups_by_repository_script_and_mode_and_ranks_classes(self):
        runs = [{"repository": "r", "script": "s", "mode": "whole", "wall": w, "exit": e, "lock_wait": 0,
                 "classes": {"m.Slow": w / 2, "m.Fast": 1.0}} for w, e in ((100, 0), (200, 1), (300, 0))]
        runs.append({"repository": "r", "script": "s", "mode": "selective", "wall": 5, "exit": 0})
        lines = timings.report(runs, top=1)
        whole = next(line for line in lines if " whole " in line)
        self.assertEqual(["r", "s", "whole", "3", "200", "300", "300", "0", "1"], whole.split())
        self.assertTrue(any(" selective " in line for line in lines))
        self.assertIn("m.Slow", lines[-1])
        self.assertNotIn("m.Fast", "\n".join(lines))


class TheCommandLine(unittest.TestCase):

    def run_cli(self, path, *args):
        return subprocess.run([sys.executable, str(CLI), *args], cwd=str(SCRIPTS), capture_output=True,
                              text=True, encoding="utf-8", env=dict(os.environ, **{timings.ENV: str(path)}),
                              timeout=120)

    def test_record_then_report(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "t.jsonl"
            made = self.run_cli(path, "record", "--script", "npm-checks.sh", "--wall", "12.34", "--exit", "1")
            self.assertEqual(0, made.returncode, made.stderr)
            line = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(("npm-checks.sh", "whole", 12.3, 1), (line["script"], line["mode"], line["wall"],
                                                                   line["exit"]))
            shown = self.run_cli(path, "report")
            self.assertEqual(0, shown.returncode, shown.stderr)
            self.assertIn("npm-checks.sh", shown.stdout)

    def test_a_report_with_nothing_recorded_says_so(self):
        with tempfile.TemporaryDirectory() as tmp:
            shown = self.run_cli(Path(tmp) / "none.jsonl", "report")
        self.assertEqual(0, shown.returncode, shown.stderr)
        self.assertIn("no run has been recorded", shown.stdout)


if __name__ == "__main__":
    unittest.main()
