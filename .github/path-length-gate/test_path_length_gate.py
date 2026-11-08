"""The path-length gate: its refusal over a real index, then a read of this repository's tracked tree."""

from __future__ import annotations

import contextlib
import io
import subprocess
import tempfile
import unittest
import unittest.mock
from pathlib import Path

import path_length_gate as gate


def stage(root: Path, *names: str) -> None:
    """Add each name to the index and no file to the disk, so no fixture meets the limit it is testing."""
    blob = subprocess.run(
        ["git", "hash-object", "-w", "--stdin"], cwd=root, input=b"x\n", capture_output=True, check=True
    ).stdout.decode("ascii").strip()
    for name in names:
        subprocess.run(
            ["git", "update-index", "--add", "--cacheinfo", f"100644,{blob},{name}"],
            cwd=root, capture_output=True, check=True)


def run_gate(root: Path) -> tuple[int, str, str]:
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = gate.main(["--root", str(root)])
    return code, out.getvalue(), err.getvalue()


class AnIndex(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        subprocess.run(["git", "init", "-q"], cwd=self.root, capture_output=True, check=True)

    def test_a_path_one_over_the_limit_is_refused_by_name(self) -> None:
        name = "d/" + "x" * (gate.LIMIT - 1)
        stage(self.root, name, "short.md")
        code, _, err = run_gate(self.root)
        self.assertEqual(1, code)
        self.assertIn(f"{name}: {gate.LIMIT + 1} characters", err)
        self.assertNotIn("short.md", err)

    def test_a_path_at_the_limit_passes(self) -> None:
        stage(self.root, "d/" + "x" * (gate.LIMIT - 2))
        code, out, err = run_gate(self.root)
        self.assertEqual((0, ""), (code, err))
        self.assertIn(f"the longest {gate.LIMIT} characters", out)

    def test_a_non_ascii_path_is_counted_in_characters_not_in_gits_quoting(self) -> None:
        stage(self.root, "é" * gate.LIMIT)
        self.assertEqual([], gate.too_long(gate.tracked(self.root)))
        self.assertEqual(0, run_gate(self.root)[0])

    def test_an_empty_index_fails_rather_than_passes(self) -> None:
        code, _, err = run_gate(self.root)
        self.assertEqual(1, code)
        self.assertIn("no path was judged", err)


class NotARepository(unittest.TestCase):
    def test_a_refused_listing_raises_rather_than_reading_as_empty(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            ceiling = {"GIT_CEILING_DIRECTORIES": str(Path(directory).parent)}
            with unittest.mock.patch.dict("os.environ", ceiling):
                with self.assertRaises(subprocess.CalledProcessError):
                    gate.tracked(Path(directory))


class TheLimitsArithmetic(unittest.TestCase):
    def test_a_thirty_character_slug_fits_in_the_main_checkout(self) -> None:
        longest = len(gate.WORKTREES_DIR) + 30 + len("/") + gate.LIMIT
        self.assertLessEqual(longest, gate.WINDOWS_LONGEST_PATH)


class ThisRepositorysTrackedTree(unittest.TestCase):
    def setUp(self) -> None:
        self.paths = gate.tracked(gate.REPO_ROOT)

    def test_the_listing_reaches_the_trees_a_long_name_arrives_in(self) -> None:
        self.assertIn(".github/path-length-gate/path_length_gate.py", self.paths)
        files = sorted((gate.REPO_ROOT / "docs/backend-architecture/adr").glob("ADR-*.md"))
        self.assertTrue(files)
        for file in files:
            self.assertIn(file.relative_to(gate.REPO_ROOT).as_posix(), self.paths)

    def test_no_tracked_path_is_over_the_limit(self) -> None:
        self.assertEqual([], gate.too_long(self.paths))


if __name__ == "__main__":
    unittest.main()
