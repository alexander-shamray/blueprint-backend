#!/usr/bin/env python3
"""The output gate read against this repository: its solution, its source roots and its `.gitignore`.

`test_output_gate.py` is the payload's and builds every tree it walks, so all of it would still pass on the day
`SOURCE_ROOTS` stopped naming where this repository keeps its code; this class, split out of it by
blueprint-harness#5, is local because only this repository has the solution it reads.

    py -3.12 -m unittest discover -s .github/output-gate
"""
from __future__ import annotations

import subprocess
import unittest

import output_gate


def check_ignore(path: str) -> subprocess.CompletedProcess[str]:
    """Ask git whether it would ignore one path in this checkout.

    `--no-index` so the answer is the rules' rather than the index's, and `-v`
    so a failure can name the rule that matched instead of leaving a reader to
    find it. Exit 0 is ignored, 1 is not, and anything else is git failing —
    which the assertions report rather than read as an answer.

    The encoding is pinned rather than left to `text=True`, which takes the
    console code page on Windows: a rule this could not decode would raise
    from the wrong place, or come back short, in a helper whose whole job is
    to be believed.
    """
    return subprocess.run(
        ["git", "check-ignore", "--no-index", "-v", "--", path],
        cwd=output_gate.REPO_ROOT, capture_output=True,
        encoding="utf-8", errors="replace", check=False)


class ThisRepository(unittest.TestCase):
    """Read against the checkout, because a fixture cannot check the subject.

    Every test in `test_output_gate.py` builds the tree it then walks, so all of them would still
    pass on the day `SOURCE_ROOTS` stopped naming where this repository keeps
    its code. These are the assertions that notice.
    """

    def test_every_project_in_the_solution_lives_under_a_walked_root(self) -> None:
        listed = output_gate.solution_projects(output_gate.REPO_ROOT / "Platform.slnx")
        self.assertTrue(listed, "Platform.slnx lists no projects")

        for path in listed:
            self.assertIn(
                path.parts[0], output_gate.SOURCE_ROOTS,
                f"{path.as_posix()} is under {path.parts[0]}/, which "
                f"output_gate.SOURCE_ROOTS does not name - the gate walks past it and "
                f"reports on the rest")

    def test_the_walk_and_the_solution_agree_about_this_checkout(self) -> None:
        """The gate's own subject check, run against the repository itself."""
        walked = output_gate.walked_projects(output_gate.REPO_ROOT)
        listed = output_gate.solution_projects(output_gate.REPO_ROOT / "Platform.slnx")

        self.assertEqual(walked, listed)

    def test_this_repository_has_no_two_projects_sharing_a_name(self) -> None:
        """The precondition every lookup below the subject check relies on."""
        walked = output_gate.walked_projects(output_gate.REPO_ROOT)

        self.assertEqual(output_gate.find_duplicate_names(walked), [])

    def test_git_really_ignores_what_this_gate_refuses(self) -> None:
        """Asked of git, rather than of `.gitignore`'s text.

        `.gitignore` keeps residue out of an ordinary `git add`, so the rule
        this gate relies on has to be active. A substring check on its text
        survives the rule being commented out or negated, so git is asked
        instead, about files inside the directories because `--no-index`
        cannot tell a bare `.../obj` is a directory.

        The matching rule is asserted as well as the verdict, because another
        rule such as `[Dd]ebug/` can ignore the same path. The source file is
        the control: a `.gitignore` of `*` would pass the other two.
        """
        for path, rule in (
                ("src/Services/Catalog/Catalog.Domain/obj/project.assets.json", "[Oo]bj/"),
                ("tests/Catalog.Domain.Tests/bin/Catalog.Domain.Tests.dll", "[Bb]in/")):
            result = check_ignore(path)
            self.assertEqual(
                result.returncode, 0,
                f"git does not ignore {path} (exit {result.returncode}). "
                f"{result.stderr.strip() or 'No rule matched it.'} The gate refuses "
                f"this directory and nothing stops it reaching a commit")
            matched = result.stdout.split("\t")[0].rsplit(":", 1)[-1]
            self.assertEqual(
                matched, rule,
                f"git ignores {path}, but by {matched!r} rather than {rule!r}. The "
                f"rule this gate's residue check relies on is not the one doing the "
                f"work, so removing it would go unnoticed here")

        control = "src/Services/Catalog/Catalog.Domain/Products/Product.cs"
        result = check_ignore(control)
        self.assertEqual(
            result.returncode, 1,
            f"git ignores {control}, matched by {result.stdout.strip()!r} - a rule "
            f"that broad makes the two assertions above vacuous")


if __name__ == "__main__":
    unittest.main()
