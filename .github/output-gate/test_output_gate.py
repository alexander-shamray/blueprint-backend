#!/usr/bin/env python3
"""What the gate is looking at, and what it does when it finds nothing.

Every output check is satisfied by an empty set, and only the subject check
refuses one, so most of what follows is about that check, not the verdict."""
from __future__ import annotations

import io
import contextlib
import tempfile
import unittest
from pathlib import Path

import output_gate


def tree(root: Path, projects: dict[str, str], restored: list[str] | None = None,
         built: list[str] | None = None, listed: list[str] | None = None,
         solution: str = "Platform.slnx") -> None:
    """A repository in miniature: some projects, a solution, some output.

    `projects` maps a project to its directory; `restored`, `built`, `listed`
    name those with an `artifacts/obj/` entry, an `artifacts/bin/` one and a
    solution entry, all by default, so a test overriding one is about that."""
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
    (root / solution).write_text(
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

    def test_a_project_under_tools_in_the_solution_is_walked(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "BffReplay": "tools/bff-replay"})

        code, output = run(self.root)

        self.assertEqual(code, 0)
        self.assertIn("2 project(s) under src/, tests/ and tools/", output)

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
        """Every root is walked. `tests/` is source too (Section 4.1)."""
        tree(self.root, {"Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"})
        (self.root / "tests/Catalog.Domain.Tests/bin").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("tests/Catalog.Domain.Tests/bin/", output)

    def test_an_obj_beside_a_project_under_tools_fails(self) -> None:
        """`tools/` holds a project the solution builds, so it is source too."""
        tree(self.root, {"BffReplay": "tools/bff-replay"})
        (self.root / "tools/bff-replay/obj").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("tools/bff-replay/obj/", output)

    def test_output_is_caught_at_any_depth_not_only_beside_a_csproj(self) -> None:
        """The claim is about the tree, not about project directories.

        A `bin/` in a directory holding no `.csproj` is still a build's
        leavings, and a gate keyed on project directories would walk past it.
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
        self.assertIn("the walk over src/, tests/ or tools/ did not find", output)

    def test_a_project_outside_the_walked_roots_is_not_called_missing(self) -> None:
        """On disk, and outside this walk — two different things to be told.

        A solution entry under a root `SOURCE_ROOTS` does not name is a subject
        failure, not a missing file, and saying otherwise misdirects a reader.
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
        # The gate's own words: it names the second possibility rather than calling the file missing.
        self.assertIn("the file is missing, or it sits outside the roots this gate walks", output)
        self.assertNotIn("Catalog.Sample.csproj is on disk and absent from", output)

    def test_a_project_moved_without_the_solution_following_it_fails(self) -> None:
        """Reconciled by path, so a move is visible where a stem is not.

        The two views hold one project name and different directories; a gate
        comparing names alone reports nothing, and they stay out of step.
        """
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src/Catalog/Catalog.Domain/'
            'Catalog.Domain.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("the walk over src/, tests/ or tools/ did not find", output)
        self.assertIn("absent from Platform.slnx", output)

    def test_a_subject_mismatch_suppresses_the_other_findings(self) -> None:
        """Three reports of one defect is a worse report than one.

        With the walk and the solution disagreeing, the missing-output and
        residue checks are about no real repository, so they would be noise.
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


class Solution(TemporaryRepository):
    """The solution is found at the root, not named, and only one is taken."""

    def test_a_solution_with_another_name_is_reconciled(self) -> None:
        """A target's solution is called what it is called there."""
        tree(self.root, {"Admin.Api": "src/Admin.Api"}, solution="BlueprintAdmin.slnx")

        code, output = run(self.root)

        self.assertEqual(code, 0, output)
        self.assertIn("1 project(s)", output)

    def test_the_found_solution_is_the_one_its_findings_name(self) -> None:
        tree(self.root,
             {"Admin.Api": "src/Admin.Api", "Admin.Web": "src/Admin.Web"},
             listed=["Admin.Api"], solution="BlueprintAdmin.slnx")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("src/Admin.Web/Admin.Web.csproj is on disk and absent from "
                      "BlueprintAdmin.slnx", output)
        self.assertNotIn("Platform.slnx", output)

    def test_the_build_it_asks_for_is_of_the_found_solution(self) -> None:
        tree(self.root, {"Admin.Api": "src/Admin.Api"}, restored=[], built=[],
             solution="BlueprintAdmin.slnx")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("`dotnet restore BlueprintAdmin.slnx`", output)
        self.assertIn("`dotnet build BlueprintAdmin.slnx`", output)

    def test_no_solution_at_the_root_is_refused_rather_than_crashing(self) -> None:
        tree(self.root, {"Admin.Api": "src/Admin.Api"})
        (self.root / "Platform.slnx").unlink()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)
        self.assertIn("no .slnx at the repository root", output)

    def test_a_solution_below_the_root_is_not_taken_for_the_root_s(self) -> None:
        """Only the root's: a sample or a fixture solution deeper down is not CI's."""
        tree(self.root, {"Admin.Api": "src/Admin.Api"})
        (self.root / "Platform.slnx").rename(self.root / "src" / "Platform.slnx")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("no .slnx at the repository root", output)

    def test_several_solutions_at_the_root_are_refused_by_name(self) -> None:
        """Which one CI builds is not the gate's to guess."""
        tree(self.root, {"Admin.Api": "src/Admin.Api"}, solution="BlueprintAdmin.slnx")
        tree(self.root, {"Admin.Api": "src/Admin.Api"}, solution="Other.slnx")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)
        self.assertIn("2 .slnx files at the repository root "
                      "(BlueprintAdmin.slnx, Other.slnx)", output)


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
        """This pair passes a case-sensitive check and collides in `artifacts/`.

        On a case-insensitive filesystem the two stems share one
        `artifacts/obj/` and `artifacts/bin/` entry.
        """
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
        entry, so a lookup by name passes output it never located.
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


if __name__ == "__main__":
    unittest.main()
