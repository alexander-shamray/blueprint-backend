"""The comment gate over this repository's own C# sources.

`test_comment_gate.py` is the payload's and holds every case a target without C# runs; this one, split out of it by
blueprint-harness#5, is local because only this repository tracks the thousand-odd `.cs` files it reads.
"""

import subprocess
import unittest

import comment_gate as gate
from test_comment_gate import ROOT


class TheShippedCSharp(unittest.TestCase):
    def test_no_tracked_csharp_line_runs_past_the_budget(self):
        listed = subprocess.run(
            ["git", "-C", str(ROOT), "ls-files", "-z", "--", "*.cs"],
            capture_output=True, check=True).stdout
        paths = [p for p in listed.decode("utf-8").split("\0") if p]
        self.assertGreater(len(paths), 1000)
        wide = []
        for path in paths:
            text = (ROOT / path).read_text(encoding="utf-8-sig")
            every = set(range(1, text.count("\n") + 2))
            wide.extend(f"{p}:{n}" for p, n, message in
                        gate.judge(path, text, every) if "columns" in message)
        self.assertEqual(wide, [])


if __name__ == "__main__":
    unittest.main()
