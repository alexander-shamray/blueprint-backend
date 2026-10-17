"""The suites are the whole of the canary's assurance: no cluster has run one.

This file holds the whole-plan and layering cases; each other `test_*.py`
holds its module's: py -3.12 -m unittest discover -s deploy/canary
"""

from __future__ import annotations

import ast
import contextlib
import io
import json
import unittest
from pathlib import Path

import analysis
import canary
import descriptor_model
import release_inputs
import repo_gate
from canary_testing import readings


class PlanDocumentTests(unittest.TestCase):
    """The shipped canary.json, against the repository it deploys."""

    def setUp(self) -> None:
        self.document = descriptor_model.load_plan()

    def test_the_shipped_plan_is_consistent(self) -> None:
        self.assertEqual(repo_gate.check(self.document), [])

    def test_a_workload_key_that_is_not_a_release_name_fails_the_plan(self) -> None:
        """Two shells read the key as one word — `deploy.yml`'s `helm upgrade`
        and `realm.yml`'s loop, which also makes a file name of it. A space is
        two releases, a glob expands against the checkout, a slash is a path
        (review round eleven). Held here, before either job sees it; the
        shipped keys are the positive control."""
        # The trailing newline is the case `match` plus `$` lets through and
        # `fullmatch` does not (review round twelve).
        for bad in ("two words", "cat*", "../etc", "Catalog", "-lead", "a" * 54, "", "catalog-api\n"):
            with self.subTest(key=bad):
                document = json.loads(json.dumps(self.document))
                document["workloads"][bad] = dict(document["workloads"]["catalog-api"])
                failures = repo_gate.check(document)
                self.assertTrue(
                    any("is not a Helm release name" in f and repr(bad) in f for f in failures),
                    failures,
                )
        for good in ("catalog-api", "a", "x" * 53, "a-1"):
            with self.subTest(key=good):
                self.assertIsNotNone(release_inputs.RELEASE_NAME.fullmatch(good))

    def test_workloads_lists_the_plan_and_not_its_comments(self) -> None:
        """`realm.yml`'s scheduled job loops over this output (ADR-043).

        One name per line and nothing else on stdout, because a shell reads
        it; and no `$comment` key, because that would be a release nothing
        installed handed to `helm get values`.
        """
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = canary.main(["canary", "workloads"])
        self.assertEqual(code, 0)
        self.assertEqual(
            out.getvalue().splitlines(),
            list(descriptor_model.entries(self.document["workloads"])),
        )
        self.assertNotIn("$comment", out.getvalue())
        self.assertIn("catalog-api", out.getvalue().splitlines())

    def test_the_ladder_is_the_one_the_chapter_cites(self) -> None:
        """§15.5's numbers: 5, 25, 50, 100, ten minutes before each step up. Not trimmed to
        what three replicas can express — that is what the refusal is for."""
        self.assertEqual(
            [step["weight"] for step in self.document["steps"]],
            [5, 25, 50, 100],
        )
        for step in self.document["steps"][:-1]:
            self.assertEqual(step["dwellMinutes"], 10)

    def test_every_threshold_analyse_reads_is_present(self) -> None:
        """`analyse` indexes these rather than `.get`-ing them, so a missing
        key is a KeyError mid-rollout with a canary already serving traffic.

        Every signal canary.py defines is read, and the verdict has to be a
        promotion: an early rollback returns before any threshold is indexed,
        and would pass this vacuously."""
        verdict = analysis.analyse(
            readings(signals=tuple(analysis.SIGNALS)),
            descriptor_model.entries(self.document["thresholds"]),
            analysis.SIGNALS,
        )

        self.assertEqual(verdict["decision"], analysis.PROMOTE, verdict["reason"])

    def test_the_plan_holds_no_query_text(self) -> None:
        """The queries are code; a plan that carries some again is refused
        rather than silently ignored beside the templates that run."""
        self.assertNotIn("signals", self.document)
        for key in ("signals", "queries"):
            with self.subTest(key=key):
                document = json.loads(json.dumps(self.document))
                document[key] = {}

                failures = repo_gate.check(document)

                self.assertTrue(any(repr(key) in f for f in failures), failures)


class LayeringTests(unittest.TestCase):
    """The README's Layout: a module imports only from those before it.

    `ORDER` is its table top to bottom; a module outside it fails the first
    case, so the order cannot stop covering the newest file.
    """

    ORDER = ("descriptor_model", "release_inputs", "csharp_sources", "analysis",
             "workflow_reads", "rollout_gate", "repo_gate", "canary")

    def modules(self) -> set[str]:
        folder = Path(canary.__file__).parent
        return {
            path.stem for path in folder.glob("*.py")
            if not path.name.startswith("test_")
            and path.stem not in {"canary_testing", "read_prometheus"}
        }

    def test_every_module_beside_the_command_line_is_in_the_order(self) -> None:
        self.assertEqual(set(self.ORDER), self.modules())

    def test_a_module_imports_only_from_those_before_it(self) -> None:
        folder = Path(canary.__file__).parent
        for position, name in enumerate(self.ORDER):
            tree = ast.parse((folder / f"{name}.py").read_text(encoding="utf-8"))
            imported = {
                node.module for node in ast.walk(tree)
                if isinstance(node, ast.ImportFrom) and node.module in self.ORDER
            } | {
                alias.name for node in ast.walk(tree) if isinstance(node, ast.Import)
                for alias in node.names if alias.name in self.ORDER
            }
            with self.subTest(module=name):
                self.assertLessEqual(imported, set(self.ORDER[:position]))

    def test_the_scan_sees_an_import_between_modules(self) -> None:
        """The subject: `repo_gate` imports from the modules above it, so a scan
        that found no import at all would pass the case above for any order."""
        tree = ast.parse(Path(repo_gate.__file__).read_text(encoding="utf-8"))
        names = {node.module for node in ast.walk(tree) if isinstance(node, ast.ImportFrom)}
        self.assertTrue({"descriptor_model", "analysis"} <= names, names)


if __name__ == "__main__":
    unittest.main()
