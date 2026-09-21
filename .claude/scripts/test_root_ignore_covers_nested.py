"""The root ignore file names every directory a nested ignore blanks out.

A directory whose own `.gitignore` is a single `*` is scratch by declaration.
Git honours that file wherever it sits; a reader that opens only the ignore
files named in its own configuration — the code index is one — never
sees it, so the directory stays indexed. The root file is the one both
readers agree on. The shared harness, and the reason this shells out rather
than re-implementing, are `review_helpers.py`'s.
"""

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
        [GIT, "check-ignore", "-v", "--no-index", probe],
        cwd=str(root), capture_output=True, text=True, encoding="utf-8",
    )
    if result.returncode != 0:
        return None
    # `<source>:<line>:<pattern>` then a tab and the path. The source is
    # repository-relative, so the first colon is the one after it.
    return result.stdout.strip().split(":")[0]


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


if __name__ == "__main__":
    unittest.main()
