#!/usr/bin/env python3
"""The merge's arithmetic, which is the part that can be quietly wrong.

The reporter gates nothing, but it merges, and every failure below moves the
printed number without raising. A coverage figure that is wrong but plausible
is worse than none, because it is the one people read.

    py -3.12 -m unittest discover -s .github/coverage
"""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import domain_coverage


def report(*methods: tuple[str, str, str, list[tuple[str, int]]]) -> str:
    """A Cobertura document in the shape `Microsoft.CodeCoverage` emits.

    Written out rather than fixtured from a real run on purpose: the structural
    claim under test is that `lines-valid` counts the lines under
    `class/methods/method/lines` and not the ones under `class/lines`, so the
    two have to be independently controllable here. A captured artefact has
    them agreeing and would pass either way.
    """
    body = []
    for package, klass, method, lines in methods:
        rendered = "".join(
            f'<line number="{number}" hits="{hits}" branch="False" />'
            for number, hits in lines
        )
        body.append(
            f'<package name="{package}">'
            f'<classes><class name="{klass}" filename="X.cs">'
            # A class-level <lines> block that disagrees with the method's, so
            # a reader counting the wrong one gets a different answer.
            f'<lines><line number="999" hits="0" branch="False" /></lines>'
            f'<methods><method name="{method}" signature="()">'
            f"<lines>{rendered}</lines>"
            f"</method></methods>"
            f"</class></classes></package>"
        )
    return f'<?xml version="1.0"?><coverage>{"".join(body)}</coverage>'


class MergeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def write(self, name: str, content: str) -> Path:
        path = self.root / f"{name}.cobertura.xml"
        path.write_text(content, encoding="utf-8")
        return path

    def test_counts_method_lines_and_ignores_class_lines(self) -> None:
        """Only the method-level lines reproduce the collector's `lines-valid`.

        The fixture's class-level line 999 is never covered, so a reader that
        counted it would report a lower rate over a larger denominator.
        """
        path = self.write("one", report(("Catalog.Domain", "C", "M", [("1", 1), ("2", 0)])))
        hits = domain_coverage.merge([path])

        self.assertEqual(len(hits), 2)
        self.assertNotIn(
            "999",
            {key[4] for key in hits},
            "a class-level <line> reached the merge; lines-valid does not count those",
        )

    def test_union_covers_a_line_only_one_stage_reached(self) -> None:
        """Some lines are reached only by a test that needs a container."""
        unit = self.write("unit", report(("Ordering.Domain", "C", "M", [("1", 3), ("2", 0)])))
        integration = self.write(
            "integration", report(("Ordering.Domain", "C", "M", [("1", 0), ("2", 5)]))
        )

        hits = domain_coverage.merge([unit, integration])

        self.assertEqual(sum(1 for count in hits.values() if count > 0), 2)

    def test_reading_the_same_report_twice_changes_nothing(self) -> None:
        """The property that makes the layout safe.

        `--logger trx` leaves the run's merged attachment and the per-project
        partials that fed it, so the same line arrives more than once by
        construction. Under `max` that is idempotent; under `+` the figure
        would grow with the number of test projects.
        """
        path = self.write("once", report(("Common.Domain", "C", "M", [("1", 2), ("2", 0)])))

        single = domain_coverage.merge([path])
        doubled = domain_coverage.merge([path, path])

        self.assertEqual(single, doubled)
        self.assertEqual(len(doubled), 2)

    def test_overloads_are_separate_lines(self) -> None:
        """The signature is in the key, and dropping it silently merges lines.

        Two overloads of one method occupy different source lines; keyed
        without the signature they would collide, and the denominator would
        shrink by however many overloads the domain has. That moves the rate
        up, which is the direction nobody investigates.
        """
        path = self.root / "overloads.cobertura.xml"
        path.write_text(
            '<?xml version="1.0"?><coverage><package name="P"><classes>'
            '<class name="C" filename="X.cs"><methods>'
            '<method name="Place" signature="(int)"><lines>'
            '<line number="1" hits="1" branch="False" /></lines></method>'
            '<method name="Place" signature="(string)"><lines>'
            '<line number="1" hits="0" branch="False" /></lines></method>'
            "</methods></class></classes></package></coverage>",
            encoding="utf-8",
        )

        hits = domain_coverage.merge([path])

        self.assertEqual(len(hits), 2)
        self.assertEqual(sum(1 for count in hits.values() if count > 0), 1)

    def test_partial_classes_do_not_collide_across_files(self) -> None:
        """The class name is in the key for the same reason the signature is."""
        path = self.root / "partials.cobertura.xml"
        path.write_text(
            '<?xml version="1.0"?><coverage><package name="P"><classes>'
            '<class name="Order" filename="Order.cs"><methods>'
            '<method name="M" signature="()"><lines>'
            '<line number="1" hits="1" branch="False" /></lines></method>'
            "</methods></class>"
            '<class name="OrderLine" filename="Order.cs"><methods>'
            '<method name="M" signature="()"><lines>'
            '<line number="1" hits="0" branch="False" /></lines></method>'
            "</methods></class>"
            "</classes></package></coverage>",
            encoding="utf-8",
        )

        self.assertEqual(len(domain_coverage.merge([path])), 2)


class FindReportsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def test_a_stage_with_no_report_is_named(self) -> None:
        """"No coverage anywhere" and "the integration stage collected nothing"
        are different defects, and the second vanishes into a total."""
        stage = self.root / "integration"
        stage.mkdir()

        with self.assertRaises(SystemExit) as raised:
            domain_coverage.find_reports([stage])

        self.assertIn("integration", str(raised.exception))

    def test_reports_are_found_below_the_root(self) -> None:
        """The collector nests its attachment under a directory it names, and
        the TRX logger nests more of them deeper still."""
        nested = self.root / "guid" / "In" / "MACHINE"
        nested.mkdir(parents=True)
        (nested / "a.cobertura.xml").write_text(report(("P", "C", "M", [("1", 1)])), encoding="utf-8")

        self.assertEqual(len(domain_coverage.find_reports([self.root])), 1)


class RenderTests(unittest.TestCase):
    def test_no_lines_is_a_failure_not_a_zero(self) -> None:
        """A report that matched no assembly is the ModulePaths filter having
        gone stale, and 0% would read as a testing problem instead."""
        with self.assertRaises(SystemExit) as raised:
            domain_coverage.render({}, [Path("x.cobertura.xml")])

        self.assertIn("no lines", str(raised.exception))

    def test_the_rate_and_the_table_are_over_the_merged_set(self) -> None:
        hits = {
            ("Catalog.Domain", "C", "M", "()", "1"): 1,
            ("Catalog.Domain", "C", "M", "()", "2"): 0,
            ("Ordering.Domain", "C", "M", "()", "1"): 4,
        }

        summary = domain_coverage.render(hits, [Path("a"), Path("b")])

        self.assertIn("**66.7%** of 3 lines", summary)
        self.assertIn("union of 2 report(s)", summary)
        self.assertIn("| `Catalog.Domain` | 50.0% |", summary)
        self.assertIn("| `Ordering.Domain` | 100.0% |", summary)

    def test_it_still_says_it_does_not_gate(self) -> None:
        """§12.9's decision, asserted rather than trusted to a comment.

        A change that turns this into a gate has to delete a test that says so.
        """
        summary = domain_coverage.render({("P", "C", "M", "()", "1"): 1}, [Path("a")])

        self.assertIn("Reported, not gated", summary)


if __name__ == "__main__":
    unittest.main()
