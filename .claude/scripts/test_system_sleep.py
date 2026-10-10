"""system-sleep.sh: /ship's opt-in suspend, and the one command it can run.

The real suspend is never reached here: a probe `rundll32.exe` first on PATH records its argument and stands in for
it. The shared harness is `review_helpers.py`'s.
"""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from review_helpers import BASH, SCRIPTS, setUpModule  # noqa: F401

SLEEP = SCRIPTS / "system-sleep.sh"
SHIP = SCRIPTS.parent / "commands" / "ship.md"
COMMAND = "powrprof.dll,SetSuspendState 0,1,0"


def run(*args, probe=True):
    with tempfile.TemporaryDirectory() as shim:
        marker = Path(shim) / "called"
        if probe:
            stand_in = Path(shim) / "rundll32.exe"
            stand_in.write_text(f"#!/usr/bin/env bash\necho \"$*\" > '{marker.as_posix()}'\n", newline="\n")
            stand_in.chmod(0o755)
        path = f"{shim}{os.pathsep}{os.environ['PATH']}" if probe else "/usr/bin"
        done = subprocess.run([BASH, str(SLEEP), *args], capture_output=True, text=True,
                              env=dict(os.environ, PATH=path))
        called = marker.read_text().strip() if marker.exists() else None
    return done, called


class TheHelperRunsOneFixedCommand(unittest.TestCase):

    def test_no_argument_suspends_with_the_fixed_command(self):
        done, called = run()
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertEqual(COMMAND, called)

    def test_a_dry_run_names_the_command_and_suspends_nothing(self):
        done, called = run("--dry-run")
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertIn(COMMAND, done.stdout)
        self.assertIsNone(called)

    def test_any_other_argument_is_refused_and_suspends_nothing(self):
        for args in (("now",), ("--dry-run", "x"), ("shell32.dll,Other",), ("",)):
            with self.subTest(args=args):
                done, called = run(*args)
                self.assertEqual(2, done.returncode)
                self.assertIn("usage", done.stderr)
                self.assertIsNone(called)

    def test_a_machine_without_rundll32_is_refused_rather_than_skipped(self):
        done, called = run(probe=False)
        self.assertEqual(3, done.returncode)
        self.assertIn("rundll32.exe not found", done.stderr)
        self.assertIsNone(called)


class ShipGrantsTheHelperExactlyAndNoRawSuspend(unittest.TestCase):

    def test_the_grant_is_the_helper_with_no_prefix_wildcard(self):
        text = SHIP.read_text(encoding="utf-8")
        grants = [g.strip() for g in text.split("---")[1].split("allowed-tools:")[1].split(",")]
        self.assertIn("Bash(bash .claude/scripts/system-sleep.sh)", grants)
        self.assertEqual([], [g for g in grants if "rundll32" in g or "system-sleep.sh:" in g])


if __name__ == "__main__":
    unittest.main()
