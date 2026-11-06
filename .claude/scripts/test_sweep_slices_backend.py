"""sweep-slices.sh's partition over this repository's own tree, within the budget.

`test_sweep_slices.py` is the payload's and partitions small repositories built per case; this class, split out of
it by blueprint-harness#5, is local because the rows partition this repository's tree, so a new top-level
directory no row owns fails here before a sweep passes over it.
"""

import unittest

from review_helpers import run_bash, setUpModule
from test_sweep_slices import REPO, Sweep, git, listed, parse

# The slice budget sweep-slices.sh packs to, in bytes.
BUDGET = 240000


class TheRealTree(unittest.TestCase):
    """Both partitions hold over this repository, within the budget."""

    def test_every_tracked_path_is_listed_or_counted_and_every_slice_fits(self):
        # `full`, so a ref a real sweep left in this checkout does not narrow it.
        sweep = Sweep(self, REPO)
        tracked = len(git(REPO, "ls-tree -r --name-only --full-tree HEAD").splitlines())
        for kind in ("bug", "security"):
            with self.subTest(kind=kind):
                run_bash('rm -rf "$W.slices"', W=sweep.path)
                result = sweep.slices(kind, "full")
                self.assertEqual(0, result.returncode, result.stderr)
                head, slices = parse(result.stdout)
                self.assertEqual("full", head["mode"])
                counts = head["counts"]
                self.assertEqual(tracked, counts["tracked"])
                skipped = sum(v for k, v in counts.items() if k not in ("tracked", "listed"))
                self.assertEqual(counts["tracked"], counts["listed"] + skipped)
                for row, files, size, _ in slices:
                    self.assertTrue(size <= BUDGET or files == 1, (row, files, size))
                paths = listed(slices)
                self.assertEqual(counts["listed"], len(paths))
                self.assertEqual(len(paths), len(set(paths)), "a path in two slices")
                for top in ("src/", "tests/", "tools/", ".claude/", "deploy/", "docs/"):
                    self.assertTrue(any(p.startswith(top) for p in paths), top)
                self.assertTrue(any("/" not in p for p in paths), "the root files")
                self.assertFalse(any(p.startswith("docs/superpowers/") for p in paths))


if __name__ == "__main__":
    unittest.main()
