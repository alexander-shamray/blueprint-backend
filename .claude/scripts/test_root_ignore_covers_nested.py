"""The root ignore file names every directory a nested ignore blanks out.

A directory whose own `.gitignore` is a single `*` is scratch by declaration.
Git honours that file wherever it sits; a reader that opens only the ignore
files named in its own configuration — the code index is one — never
sees it, so the directory stays indexed. The root file is the one both
readers agree on. The shared harness, and the reason this shells out rather
than re-implementing, are `review_helpers.py`'s.
"""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from review_helpers import (
    GIT,
    NEWLINE,
    SCRIPTS,
    setUpModule,
)

REPO = SCRIPTS.parent.parent


def blanking_directories(root):
    """The directories under `root` whose own ignore file is a bare `*`."""
    found = []
    for path in sorted(root.rglob(".gitignore")):
        if path.parent == root:
            continue
        if ".git" in path.relative_to(root).parts[:-1]:
            continue
        lines = [
            line.strip() for line in
            path.read_text(encoding="utf-8-sig").splitlines()
        ]
        rules = [line for line in lines if line and not line.startswith("#")]
        if rules == ["*"]:
            found.append(path.parent)
    return found


def matching_ignore_file(root, directory):
    """The ignore file git says excludes `directory`, or None if none does."""
    probe = directory.relative_to(root).as_posix()
    result = subprocess.run(
        [GIT, "check-ignore", "-v", "-z", "--stdin", "--no-index"], input=probe + chr(0),
        cwd=str(root), capture_output=True, text=True, encoding="utf-8",
    )
    if result.returncode != 0:
        return None
    # `-z` ends the source, line, pattern and path each with a NUL, so a drive
    # colon in an absolute source, or one in the pattern, shifts no field.
    # Exit 0 is also a negated pattern, which un-ignores the path: no match.
    source, _, pattern = result.stdout.split("\0")[:3]
    return None if pattern.startswith("!") else source


class EveryBlankedDirectoryIsNamedAtTheRoot(unittest.TestCase):
    """The root ignore file, not only a nested one, excludes each of them.

    The failure this refuses is invisible in `git status` and visible only in
    the index: scratch competes with code in every search result until
    somebody measures the index and asks why. A blanked directory is untracked
    by construction, so a fresh checkout has no subject here and this passes
    vacuously; the control below is what keeps the walk honest instead.
    """

    def test_each_of_them_is_excluded_by_the_root_file(self):
        for directory in blanking_directories(REPO):
            with self.subTest(directory=directory.relative_to(REPO).as_posix()):
                self.assertEqual(
                    matching_ignore_file(REPO, directory), ".gitignore",
                    "name this directory in the repository's root .gitignore",
                )


class TheCheckFailsWhereTheRootRuleIsAbsent(unittest.TestCase):
    """The positive control the check needs to mean anything.

    It plants a blanked directory, so the walk is asserted to find one
    somewhere whatever the checkout holds, and asserts the check reports it
    when the root file does not name it. Without that, a check that never
    reports and a check that cannot report read the same on a green run.
    """

    def scratch_repository(self, root_rule):
        tmp = tempfile.TemporaryDirectory(prefix="ignorescope-")
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name)
        subprocess.run([GIT, "init", "-q", str(root)], check=True)
        scratch = root / "scratch"
        scratch.mkdir()
        (scratch / ".gitignore").write_text(
            "*" + NEWLINE, encoding="utf-8", newline=NEWLINE)
        (root / ".gitignore").write_text(
            root_rule + NEWLINE, encoding="utf-8", newline=NEWLINE)
        return root, scratch

    def test_a_nested_rule_alone_does_not_satisfy_the_check(self):
        root, scratch = self.scratch_repository("unrelated/")

        self.assertEqual(blanking_directories(root), [scratch])
        self.assertIsNone(matching_ignore_file(root, scratch))

    def test_the_root_rule_is_what_satisfies_it(self):
        root, scratch = self.scratch_repository("scratch/")

        self.assertEqual(blanking_directories(root), [scratch])
        self.assertEqual(matching_ignore_file(root, scratch), ".gitignore")

    def test_a_negation_in_the_global_excludes_file_is_not_a_match(self):
        # That file's source is absolute, so a colon in its path, a drive's on Windows and in the name elsewhere,
        # is what a colon split would take for the line.
        root, scratch = self.scratch_repository("scratch/")
        excludes = root.parent / (root.name + ("-" if os.name == "nt" else ":") + "excludes")
        excludes.write_text("scratch/" + NEWLINE + "!scratch/" + NEWLINE, encoding="utf-8", newline=NEWLINE)
        self.addCleanup(excludes.unlink)
        (root / ".gitignore").write_text("", encoding="utf-8")
        subprocess.run([GIT, "config", "core.excludesFile", excludes.as_posix()], cwd=str(root), check=True)

        self.assertIsNone(matching_ignore_file(root, scratch))

    def test_a_root_negation_is_not_a_match(self):
        # `check-ignore -v` exits 0 for a negated pattern too, and names it as the rule.
        root, scratch = self.scratch_repository("scratch/" + NEWLINE + "!scratch/")

        self.assertEqual(blanking_directories(root), [scratch])
        self.assertIsNone(matching_ignore_file(root, scratch))


if __name__ == "__main__":
    unittest.main()
