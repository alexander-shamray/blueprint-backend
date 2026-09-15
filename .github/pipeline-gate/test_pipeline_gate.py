#!/usr/bin/env python3
"""What the gates would catch, which running them against this repository does not say.

Most checks here are negative cases, because a gate only ever observed green
has not been shown to look at anything; the positive controls say what a clean
input looks like.

Some tests have no defect in them at all. Their subject is the gate's own
parser: an empty pattern list, an empty matrix, an empty stage, each a state in
which every other check passes over an inventory it never read.

    py -3.12 -m unittest discover -s .github/pipeline-gate
"""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import pipeline_gate

# A workflow with the two blocks the gates read, at ci.yml's real indentation.
# Indentation is load-bearing here: `read_filters` finds the end of the block
# by it, and `check_images` matches filter names on a twelve-space indent.
WORKFLOW = """\
jobs:
  changes:
    steps:
      - uses: dorny/paths-filter@v4
        with:
          predicate-quantifier: 'some-with-excludes'
          filters: |
            shared: &shared
              - 'Directory.Packages.props'
              - 'src/BuildingBlocks/**'
            catalog:
              - *shared
              - 'src/Services/Catalog/**'
            gateway:
              - *shared
              - 'src/Gateway/**'
            deploy:
              - 'deploy/**'
              - '!deploy/compose/**'

    outputs:
      catalog: ${{ steps.changes.outputs.catalog }}
      gateway: ${{ steps.changes.outputs.gateway }}

  images:
    strategy:
      matrix:
        include:
          - filter: catalog
            image: catalog-api
            dockerfile: src/Services/Catalog/Catalog.Api/Dockerfile
          - filter: gateway
            image: gateway
            dockerfile: src/Gateway/Gateway.Api/Dockerfile
"""


class Fixture(unittest.TestCase):
    """A checkout shaped like this one, small enough to break on purpose."""

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

        for path in (
            "src/BuildingBlocks/Common.Domain",
            "src/Services/Catalog/Catalog.Api",
            "src/Gateway/Gateway.Api",
        ):
            (self.root / path).mkdir(parents=True)
        for path in (
            "src/Services/Catalog/Catalog.Api/Dockerfile",
            "src/Gateway/Gateway.Api/Dockerfile",
        ):
            (self.root / path).write_text("FROM scratch\n", encoding="utf-8")

        self.workflow = self.root / ".github" / "workflows" / "ci.yml"
        self.workflow.parent.mkdir(parents=True)
        self.write(WORKFLOW)

        # The module reads ROOT and WORKFLOW as module-level constants, which
        # is right for a script and awkward for a suite. Patched rather than
        # parameterised so the production call sites stay argument-free.
        original = pipeline_gate.WORKFLOW
        pipeline_gate.WORKFLOW = self.workflow
        self.addCleanup(lambda: setattr(pipeline_gate, "WORKFLOW", original))

    def write(self, text: str) -> None:
        self.workflow.write_text(text, encoding="utf-8")


class FilterTests(Fixture):
    def test_the_fixture_is_clean(self) -> None:
        """Without this the negative cases below prove nothing: a check that
        fails on everything catches every defect and is worthless."""
        self.assertEqual(pipeline_gate.check_filters(self.root), [])

    def test_a_new_service_with_no_filter_is_caught(self) -> None:
        """§15.1's quieter half. `src/Services/` is spoken for by its
        siblings' filters, so the inventory looks complete right up until one
        service stops being deployed."""
        (self.root / "src/Services/Inventory").mkdir(parents=True)

        problems = pipeline_gate.check_filters(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("src/Services/Inventory", problems[0])

    def test_a_new_top_level_deployable_with_no_filter_is_caught(self) -> None:
        """The loud half, and the one that actually happened: `src/Gateway/**`
        and `src/BFF/**` were both deployables CI never rebuilt."""
        (self.root / "src/Worker").mkdir(parents=True)

        problems = pipeline_gate.check_filters(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("src/Worker", problems[0])

    def test_a_missing_predicate_quantifier_is_caught(self) -> None:
        """Without it the `!deploy/compose/**` exclusion is decoration, and a
        compose-only change deploys. Nothing else in the file would say so."""
        self.write(WORKFLOW.replace("predicate-quantifier: 'some-with-excludes'", ""))

        problems = pipeline_gate.check_filters(self.root)

        self.assertTrue(any("predicate-quantifier" in problem for problem in problems))

    def test_an_unparsed_block_fails_rather_than_passing_empty(self) -> None:
        """The gate's own subject. Every check above asks whether a directory
        appears in the pattern list; against an empty list they all pass and
        the gate reports a complete inventory it never read."""
        self.write(WORKFLOW.replace("filters: |", "filters: >"))

        problems = pipeline_gate.check_filters(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("parser", problems[0])

    def test_a_parent_is_covered_by_its_children(self) -> None:
        """§15.1's model, and the reason `covers` matches on a prefix.

        No filter names `src/Services`; it is "spoken for by its siblings'
        filters", which is exactly why that section asks for the children to be
        checked separately. A rule demanding `src/Services/**` would fail on
        the correct workflow.
        """
        self.assertTrue(pipeline_gate.covers("src/Services/Catalog/**", "src/Services"))
        self.assertTrue(pipeline_gate.covers("src/Services/Catalog/**", "src/Services/Catalog"))
        self.assertFalse(pipeline_gate.covers("src/Gateway/**", "src/Services"))

    def test_a_file_level_pattern_satisfies_the_check(self) -> None:
        """The reach's cost, pinned so it is visible rather than discovered.

        This gate proves every deployable is reachable by some filter, not that
        any filter is complete. Closing it would mean rejecting the prefix
        match the test above requires, so the residual is stated in `covers`
        and asserted here rather than left to be found later and read as a bug.
        """
        self.assertTrue(
            pipeline_gate.covers("src/Gateway/Program.cs", "src/Gateway"),
            "if this ever becomes False, `covers` got stricter and the parent "
            "case above is what to re-check",
        )

    def test_a_negated_pattern_covers_nothing(self) -> None:
        """`!deploy/compose/**` excludes; reading it as coverage would let an
        exclusion satisfy the inventory it exists to carve out of."""
        self.assertFalse(pipeline_gate.covers("!src/Gateway/**", "src/Gateway"))


class ImageTests(Fixture):
    def test_the_fixture_is_clean(self) -> None:
        self.assertEqual(pipeline_gate.check_images(self.root), [])

    def test_a_dockerfile_no_matrix_entry_builds_is_caught(self) -> None:
        """The gap compose.yml names: filtered to deploy/compose/**, that smoke
        surfaces a broken Dockerfile on the next compose PR rather than on the
        change that broke it."""
        (self.root / "src/BFF/Web.Bff").mkdir(parents=True)
        (self.root / "src/BFF/Web.Bff/Dockerfile").write_text("FROM scratch\n", encoding="utf-8")

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("src/BFF/Web.Bff/Dockerfile", problems[0])

    def test_a_matrix_entry_naming_no_dockerfile_is_caught(self) -> None:
        """The reverse direction. Path-filtered, this fails on the day the
        entry is next selected — months after the rename, in a job nobody
        connects to it."""
        (self.root / "src/Gateway/Gateway.Api/Dockerfile").unlink()

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("does not exist", problems[0])

    def test_an_entry_reading_an_undefined_filter_is_caught(self) -> None:
        """The one neither direction can see, and the worst of the three.

        `needs.changes.outputs[matrix.filter]` on a name no filter defines is
        the empty string, not an error — so the `if` is false, the step is
        skipped, and the job goes green having built nothing. Both halves of
        the inventory are still perfectly consistent.
        """
        self.write(WORKFLOW.replace("- filter: gateway", "- filter: edge"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("'edge'", problems[0])

    def test_a_dockerfile_paired_with_the_wrong_filter_is_caught(self) -> None:
        """The failure neither direction of the inventory can reach.

        Pairing the gateway's Dockerfile with `filter: catalog` names a filter
        that exists and builds a Dockerfile that exists — both directions pass,
        and the name check passes too, because the name is real. What is wrong
        is the wiring: a gateway-only change then builds no gateway image, and
        a Catalog change builds one nobody asked for. Well-formed and pointed
        at the wrong service.
        """
        self.write(WORKFLOW.replace(
            "          - filter: gateway\n            image: gateway",
            "          - filter: catalog\n            image: gateway"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("wired to the wrong service", problems[0])

    def test_a_filter_the_changes_job_does_not_export_is_caught(self) -> None:
        """Defining a filter and exporting it are two different things.

        `needs.changes.outputs.gateway` reads a JOB output, which exists only
        because an `outputs:` entry maps it from the step. Delete that one line
        and the filter still exists, both inventory directions still pass, the
        name check still passes — and a gateway-only change makes the images
        job's own condition false, so every gateway build is skipped. The
        unconditional guard step inside the job catches this for a leg that
        runs; it cannot catch a job that never starts.
        """
        self.write(WORKFLOW.replace(
            "      gateway: ${{ steps.changes.outputs.gateway }}\n", ""))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("does not export it", problems[0])

    def test_the_filter_parser_keeps_them_apart(self) -> None:
        """The subject of the check above. `read_filters` flattens, which is
        right for asking whether a directory is reachable by ANY filter and
        useless for asking which — a parser that returned one bag would make
        every pairing look correct."""
        by_name = pipeline_gate.read_filters_by_name(WORKFLOW)

        self.assertEqual(sorted(by_name), ["catalog", "deploy", "gateway", "shared"])
        self.assertIn("src/Gateway/**", by_name["gateway"])
        self.assertNotIn("src/Gateway/**", by_name["catalog"])

    def test_an_unparsed_matrix_fails_rather_than_passing_empty(self) -> None:
        """The gate's own subject again, from the images side."""
        self.write(WORKFLOW.replace("dockerfile:", "file:"))

        problems = pipeline_gate.check_images(self.root)

        self.assertEqual(len(problems), 1)
        self.assertIn("parser", problems[0])


def trx(total: int, assembly: str, tests: list[str]) -> str:
    # The class name is deliberately NOT derived from the assembly: two
    # projects holding a `Suite` is what the identity has to survive, and a
    # fixture that made them differ would pass whether or not the production
    # key carries the assembly.
    results = "".join(
        f'<UnitTest name="{name}"><TestMethod codeBase="/x/{assembly}.dll" '
        f'className="Suite" name="{name}" /></UnitTest>'
        for name in tests
    )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f'<Results />{results}'
        f'<ResultSummary><Counters total="{total}" passed="{total}" failed="0" /></ResultSummary>'
        "</TestRun>"
    )


class StageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

        self.solution = self.root / "Platform.slnx"
        self.solution.write_text(
            '<Solution>'
            '<Project Path="tests/Catalog.Domain.Tests/Catalog.Domain.Tests.csproj" />'
            '<Project Path="tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj" />'
            '<Project Path="tests/Catalog.TestSupport/Catalog.TestSupport.csproj" />'
            '<Project Path="src/Services/Catalog/Catalog.Api/Catalog.Api.csproj" />'
            "</Solution>",
            encoding="utf-8",
        )

    def stage(self, name: str, *documents: str) -> Path:
        directory = self.root / name
        directory.mkdir()
        for index, document in enumerate(documents):
            (directory / f"{index}.trx").write_text(document, encoding="utf-8")
        return directory

    def test_test_support_is_not_a_test_project(self) -> None:
        """§4.1, and the same predicate Directory.Build.props scopes CA1707
        with: EndsWith("Tests"), so `Platform.IntegrationTests` counts and
        `Catalog.TestSupport` does not."""
        self.assertEqual(
            pipeline_gate.test_projects(self.solution),
            {"Catalog.Domain.Tests", "Platform.IntegrationTests"},
        )

    def test_a_clean_run_passes(self) -> None:
        """All three stages, because that is what a clean run is."""
        architecture = self.stage(
            "architecture", trx(18, "Catalog.Domain.Tests", ["arch"])
        )
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a", "b"]))
        integration = self.stage(
            "integration", trx(160, "Platform.IntegrationTests", ["c"])
        )

        self.assertEqual(
            pipeline_gate.check_stages([architecture, unit, integration], self.solution),
            [],
        )

    def test_an_empty_stage_is_caught(self) -> None:
        """§12.1's trap: `dotnet test` exits zero on a filter that
        selects nothing, so the step is green and the stage does not exist."""
        unit = self.stage("unit", trx(0, "Catalog.Domain.Tests", []))
        integration = self.stage("integration", trx(160, "Platform.IntegrationTests", ["c"]))

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertTrue(any("ran no tests" in problem for problem in problems))

    def test_a_stage_below_its_floor_is_caught(self) -> None:
        unit = self.stage("unit", trx(12, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage("integration", trx(160, "Platform.IntegrationTests", ["c"]))

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertTrue(any("below its floor" in problem for problem in problems))

    def test_a_test_project_in_no_stage_is_caught(self) -> None:
        """The structural half, which has no number in it and therefore no
        number to go stale. A suite that stopped being selected is a suite that
        stopped running with nothing going red."""
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage("integration", trx(160, "Catalog.Domain.Tests", ["c"]))

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertTrue(any("Platform.IntegrationTests ran in no stage" in p for p in problems))

    def test_a_test_in_two_stages_is_caught(self) -> None:
        """ci.yml claims the filters are "exhaustive and disjoint by
        construction"; this is what makes the second half a check. An overlap
        on the integration stage is a container set paid for twice."""
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a", "shared"]))
        integration = self.stage(
            "integration",
            trx(160, "Platform.IntegrationTests", ["c"]),
            trx(0, "Catalog.Domain.Tests", ["shared"]),
        )

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertTrue(any("ran in both" in problem for problem in problems))

    def test_the_same_test_name_in_two_assemblies_is_not_an_overlap(self) -> None:
        """The identity carries the assembly, and without it this is a red
        build with no defect behind it. `ArchitectureTests` is very nearly a
        class of the same name in more than one project already."""
        architecture = self.stage(
            "architecture", trx(18, "Catalog.Domain.Tests", ["arch"])
        )
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage("integration", trx(160, "Platform.IntegrationTests", ["a"]))

        problems = pipeline_gate.check_stages(
            [architecture, unit, integration], self.solution
        )

        self.assertEqual(problems, [])

    def test_a_declared_stage_nobody_passed_is_caught(self) -> None:
        """The other direction, and the one that was missing.

        Rejecting an undeclared directory says nothing about a declared stage
        nobody ran. Drop the architecture invocation and its argument, let
        those tests fall into the unit filter, and the project-coverage, floor
        and overlap checks all pass while the gate reports two stages — a gate
        cannot fail on a file that is not there.
        """
        unit = self.stage("unit", trx(600, "Catalog.Domain.Tests", ["a"]))
        integration = self.stage("integration", trx(160, "Platform.IntegrationTests", ["c"]))

        problems = pipeline_gate.check_stages([unit, integration], self.solution)

        self.assertEqual(len(problems), 1)
        self.assertIn("architecture stage was never read", problems[0])

    def test_an_undeclared_stage_directory_is_caught(self) -> None:
        """A results directory nobody declared has no floor, and a check with
        no threshold is a check that did not run."""
        smoke = self.stage("smoke", trx(600, "Catalog.Domain.Tests", ["a"]))

        problems = pipeline_gate.check_stages([smoke], self.solution)

        self.assertTrue(any("not one of the stages" in problem for problem in problems))

    def test_a_stage_with_no_trx_is_not_zero_tests(self) -> None:
        """A logger that did not run and a filter that selected nothing are
        different defects, and only one of them is about the tests."""
        empty = self.root / "unit"
        empty.mkdir()

        with self.assertRaises(SystemExit) as raised:
            pipeline_gate.read_stage(empty)

        self.assertIn("no *.trx", str(raised.exception))

    def test_reading_no_stage_at_all_fails_rather_than_passing(self) -> None:
        """The gate's own subject, third instance."""
        problems = pipeline_gate.check_stages([], self.solution)

        self.assertTrue(any("vacuously" in problem for problem in problems))


if __name__ == "__main__":
    unittest.main()
