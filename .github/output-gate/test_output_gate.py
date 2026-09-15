#!/usr/bin/env python3
"""What the gate is looking at, and what it does when it finds nothing.

Every output check the gate makes is satisfied by an empty set, which is what
a renamed source root, a moved solution file or a typo in `SOURCE_ROOTS`
produces. Only the subject check refuses one, so most of what follows is about
that check rather than the verdict.
The last class reads the real repository, because a tree this gate does not
walk is invisible to every test that builds its own.

    py -3.12 -m unittest discover -s .github/output-gate
"""
from __future__ import annotations

import io
import contextlib
import subprocess
import tempfile
import unittest
from pathlib import Path

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


def tree(root: Path, projects: dict[str, str], restored: list[str] | None = None,
         built: list[str] | None = None, listed: list[str] | None = None) -> None:
    """A repository in miniature: some projects, a solution, some output.

    `projects` maps a project name to the directory it lives in, relative to
    the root. `restored` names the projects with an `artifacts/obj/` entry and
    `built` those with an `artifacts/bin/` one; `listed` names the ones the
    solution carries. All three default to every project, which is what makes a
    test that overrides one of them a test about exactly that difference — and
    what lets `restored` and `built` be set apart to reproduce a tree that has
    been restored and not compiled.
    """
    restored = list(projects) if restored is None else restored
    built = list(projects) if built is None else built
    listed = list(projects) if listed is None else listed

    for name, directory in projects.items():
        project_directory = root / directory
        project_directory.mkdir(parents=True, exist_ok=True)
        (project_directory / f"{name}.csproj").write_text("<Project />", encoding="utf-8")

    for name in restored:
        (root / "artifacts" / "obj" / name).mkdir(parents=True, exist_ok=True)
    for name in built:
        (root / "artifacts" / "bin" / name).mkdir(parents=True, exist_ok=True)

    entries = "".join(
        f'  <Project Path="{projects[name]}/{name}.csproj" />\n' for name in listed)
    (root / "Platform.slnx").write_text(
        f"<Solution>\n{entries}</Solution>\n", encoding="utf-8")


def run(root: Path) -> tuple[int, str]:
    """The gate's exit code and everything it printed."""
    captured = io.StringIO()
    with contextlib.redirect_stdout(captured):
        code = output_gate.main(["--repo", str(root)])
    return code, captured.getvalue()


class TemporaryRepository(unittest.TestCase):
    """Each test gets its own tree; nothing here touches the checkout."""

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / "src").mkdir()
        (self.root / "tests").mkdir()


class CleanTree(TemporaryRepository):

    def test_a_built_tree_with_no_output_beside_the_source_passes(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"})

        code, output = run(self.root)

        self.assertEqual(code, 0)
        # The count is in the success line because a gate that says only "OK"
        # says the same thing whether it looked at every project or at none.
        self.assertIn("2 project(s)", output)

    def test_a_directory_merely_named_like_output_elsewhere_is_not_a_finding(self) -> None:
        """`bin` and `obj` are matched as directory names, not as substrings."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src" / "Services" / "Catalog" / "Catalog.Domain" / "Binding").mkdir()
        (self.root / "src" / "Services" / "Catalog" / "Catalog.Domain" / "Objects").mkdir()

        code, _output = run(self.root)

        self.assertEqual(code, 0)


class Residue(TemporaryRepository):

    def test_an_obj_beside_a_project_fails(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src/Services/Catalog/Catalog.Domain/obj").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("src/Services/Catalog/Catalog.Domain/obj/", output)

    def test_a_bin_under_tests_fails(self) -> None:
        """Both roots are walked. `tests/` is source too (Section 4.1)."""
        tree(self.root, {"Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"})
        (self.root / "tests/Catalog.Domain.Tests/bin").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("tests/Catalog.Domain.Tests/bin/", output)

    def test_output_is_caught_at_any_depth_not_only_beside_a_csproj(self) -> None:
        """The claim is about the tree, not about project directories.

        A `bin/` in a directory that holds no `.csproj` is still a build's
        leavings in a source tree, and a gate keyed on project directories
        would walk straight past it.
        """
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src/Services/Catalog/Catalog.Domain/Products/obj").mkdir(parents=True)

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("Products/obj/", output)

    def test_case_does_not_let_output_through(self) -> None:
        """`.gitignore` writes `[Oo]bj/`; this gate agrees with it."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src/Services/Catalog/Catalog.Domain/Obj").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("Obj/", output)

    def test_a_nested_layout_is_reported_once_at_its_outermost_directory(self) -> None:
        """One finding, and it names the directory to delete."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src/Services/Catalog/Catalog.Domain/obj/Debug/net10.0").mkdir(parents=True)
        (self.root / "src/Services/Catalog/Catalog.Domain/obj/Debug/bin").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)


class Subject(TemporaryRepository):
    """The half that stops a green result from being about nothing."""

    def test_an_empty_walk_is_a_failure_rather_than_a_pass(self) -> None:
        """An empty tree and solution satisfy every other assertion."""
        tree(self.root, {})

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("no projects found at all", output)

    def test_a_project_on_disk_and_not_in_the_solution_fails(self) -> None:
        tree(self.root,
             {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
              "Payments.Domain": "src/Services/Payments/Payments.Domain"},
             listed=["Catalog.Domain"])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("absent from Platform.slnx", output)
        self.assertIn("Payments.Domain", output)

    def test_a_project_in_the_solution_and_not_on_disk_fails(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src/Services/Catalog/Catalog.Domain/'
            'Catalog.Domain.csproj" />\n  <Project Path="src/Gateway/Gateway.Api/'
            'Gateway.Api.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("the walk over src/ or tests/ did not find", output)

    def test_a_project_outside_the_walked_roots_is_not_called_missing(self) -> None:
        """On disk, and outside this walk — two different things to be told.

        A solution entry under a root `SOURCE_ROOTS` does not name is a subject
        failure, not a missing file, and a diagnostic saying otherwise sends a
        reader to the wrong place.
        """
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        outside = self.root / "samples/Catalog.Sample"
        outside.mkdir(parents=True)
        (outside / "Catalog.Sample.csproj").write_text("<Project />", encoding="utf-8")
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src/Services/Catalog/Catalog.Domain/'
            'Catalog.Domain.csproj" />\n  <Project Path="samples/Catalog.Sample/'
            'Catalog.Sample.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("samples/Catalog.Sample/Catalog.Sample.csproj", output)
        self.assertIn("did not find", output)
        self.assertNotIn("is not on disk", output)

    def test_a_project_moved_without_the_solution_following_it_fails(self) -> None:
        """Reconciled by path, so a move is visible where a stem is not.

        The two views hold the same project name and different directories. A
        gate comparing names alone sees one set and reports nothing, which is
        the defect that keeps a solution and a tree silently out of step.
        """
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src/Catalog/Catalog.Domain/'
            'Catalog.Domain.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("did not find", output)
        self.assertIn("absent from Platform.slnx", output)

    def test_a_subject_mismatch_suppresses_the_other_findings(self) -> None:
        """Three reports of one defect is a worse report than one.

        With the walk and the solution disagreeing, neither the missing-output
        check nor the residue check is about the repository the solution
        describes, so their findings would be noise around the one that matters.
        """
        tree(self.root,
             {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
              "Payments.Domain": "src/Services/Payments/Payments.Domain"},
             restored=[], built=[], listed=["Catalog.Domain"])
        (self.root / "src/Services/Catalog/Catalog.Domain/obj").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)
        self.assertNotIn("artifacts/obj/", output)

    def test_a_solution_written_with_backslashes_is_read(self) -> None:
        """A `.slnx` that has been through a Windows tool spells paths its way."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src\\Services\\Catalog\\Catalog.Domain\\'
            'Catalog.Domain.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 0, output)


class DuplicateNames(TemporaryRepository):
    """Two projects, one artefacts entry, and a lookup that cannot say which."""

    def test_two_projects_sharing_a_stem_fail(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        second = self.root / "src/Services/Payments/Catalog.Domain"
        second.mkdir(parents=True)
        (second / "Catalog.Domain.csproj").write_text("<Project />", encoding="utf-8")
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src/Services/Catalog/Catalog.Domain/'
            'Catalog.Domain.csproj" />\n  <Project Path="src/Services/Payments/'
            'Catalog.Domain/Catalog.Domain.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("more than one project", output)
        self.assertIn("src/Services/Payments/Catalog.Domain/Catalog.Domain.csproj", output)

    def test_two_stems_differing_only_in_case_fail(self) -> None:
        """The pair a case-sensitive check passes in CI and nowhere else."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        second = self.root / "src/Services/Payments/catalog.domain"
        second.mkdir(parents=True)
        (second / "catalog.domain.csproj").write_text("<Project />", encoding="utf-8")
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src/Services/Catalog/Catalog.Domain/'
            'Catalog.Domain.csproj" />\n  <Project Path="src/Services/Payments/'
            'catalog.domain/catalog.domain.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("more than one project", output)
        # Both spellings are named, because the pair is the finding and a
        # reader looking for one of them must find it here.
        self.assertIn("Catalog.Domain / catalog.domain", output)

    def test_a_duplicate_cannot_borrow_the_other_project_s_artefacts(self) -> None:
        """The defect the name check exists to stop, stated as a test.

        Both projects resolve to one `artifacts/obj/` and `artifacts/bin/`
        entry, so a gate that looked output up by name would find it present
        for both and report a project whose output it never located.
        """
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        second = self.root / "src/Services/Payments/Catalog.Domain"
        second.mkdir(parents=True)
        (second / "Catalog.Domain.csproj").write_text("<Project />", encoding="utf-8")
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src/Services/Catalog/Catalog.Domain/'
            'Catalog.Domain.csproj" />\n  <Project Path="src/Services/Payments/'
            'Catalog.Domain/Catalog.Domain.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)
        self.assertNotIn("does not exist", output)


class BuildRan(TemporaryRepository):
    """The positive half: the output is somewhere, and it is there."""

    def test_a_tree_nobody_touched_fails_rather_than_passing_cleanly(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"},
             restored=[], built=[])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("no restore has run here", output)

    def test_a_restored_but_uncompiled_tree_fails(self) -> None:
        """A restore alone creates `artifacts/obj/` and no `artifacts/bin/`."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"},
             built=[])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("no build has run here", output)

    def test_nothing_run_at_all_is_one_finding_and_not_one_per_project(self) -> None:
        """A different defect from a project the build skipped, worded as one."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests",
                         "Gateway.Api": "src/Gateway/Gateway.Api"},
             restored=[], built=[])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)

    def test_a_single_project_the_restore_skipped_is_named(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"},
             restored=["Catalog.Domain"])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("artifacts/obj/Catalog.Domain.Tests/", output)

    def test_a_single_project_the_build_skipped_is_named(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"},
             built=["Catalog.Domain"])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("artifacts/bin/Catalog.Domain.Tests/", output)


class ThisRepository(unittest.TestCase):
    """Read against the checkout, because a fixture cannot check the subject.

    Every test above builds the tree it then walks, so all of them would still
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
