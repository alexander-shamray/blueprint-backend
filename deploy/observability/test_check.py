"""check.py's per-service outbox-gauge check, over a throwaway source tree."""

import pathlib
import shutil
import tempfile
import unittest
from unittest import mock

import check

DISPATCHER = "services.AddHostedService<OutboxDispatcher>();"
METRICS = "services.AddSingleton<OutboxMetrics>();"
UNINSTRUMENTED = "Svc hosts OutboxDispatcher and registers no OutboxMetrics"


class OutboxMetricsTests(unittest.TestCase):
    def tree(self, registrations: str, domain: bool = True) -> pathlib.Path:
        """A src/Services tree holding one service, `Svc`, whose infrastructure registers `registrations`."""
        root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        service = root / "src" / "Services" / "Svc"
        if domain:
            (service / "Svc.Domain").mkdir(parents=True)
            (service / "Svc.Domain" / "Svc.Domain.csproj").write_text("<Project />\n", encoding="utf-8")
        infrastructure = service / "Svc.Infrastructure"
        infrastructure.mkdir(parents=True)
        (infrastructure / "DependencyInjection.cs").write_text(registrations, encoding="utf-8")
        return root

    def failures_for(self, root: pathlib.Path) -> list[str]:
        """Every failure the check raises over `root`, with the real tree's exemptions set aside."""
        with (
            mock.patch.object(check, "ROOT", root),
            mock.patch.object(check, "OUTBOX_METRICS_EXEMPT", {}),
            mock.patch.object(check, "failures", []) as failures,
        ):
            check.check_outbox_metrics_per_service()
            return list(failures)

    def test_a_service_registering_both_is_instrumented(self) -> None:
        found = self.failures_for(self.tree(f"{DISPATCHER}\n{METRICS}\n"))

        self.assertFalse([f for f in found if f.startswith("Svc ")], found)

    def test_a_dispatcher_without_the_metrics_is_refused(self) -> None:
        """The control for the cases below: it is the same shape with the registration absent."""
        found = self.failures_for(self.tree(f"{DISPATCHER}\n"))

        self.assertTrue(any(f.startswith(UNINSTRUMENTED) for f in found), found)

    def test_a_line_commented_out_registration_is_not_one(self) -> None:
        found = self.failures_for(self.tree(f"{DISPATCHER}\n// {METRICS}\n"))

        self.assertTrue(any(f.startswith(UNINSTRUMENTED) for f in found), found)

    def test_a_block_commented_out_registration_is_not_one(self) -> None:
        found = self.failures_for(self.tree(f"{DISPATCHER}\n/* {METRICS} */\n"))

        self.assertTrue(any(f.startswith(UNINSTRUMENTED) for f in found), found)

    def test_a_comment_naming_the_dispatcher_does_not_make_a_host(self) -> None:
        """Counted, it would add a service with no Domain project to the hosts and fail the other direction."""
        found = self.failures_for(self.tree(f"// {DISPATCHER}\n", domain=False))

        self.assertFalse([f for f in found if f.startswith("Svc ")], found)
        self.assertTrue(any("found no service hosting OutboxDispatcher" in f for f in found), found)


class InstrumentUnitTests(unittest.TestCase):
    """A unit outside UNIT_SUFFIX and not an annotation is refused rather than dropped."""

    def instruments(self, unit: str) -> tuple[set[str], list[str]]:
        root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        (root / "src").mkdir()
        (root / "src" / "Metrics.cs").write_text(
            f'_d = meter.CreateHistogram<double>("request.duration", unit: "{unit}");\n', encoding="utf-8")
        with (
            mock.patch.object(check, "ROOT", root),
            mock.patch.object(check, "failures", []) as failures,
        ):
            return check.declared_instruments(), list(failures)

    def test_a_mapped_unit_becomes_its_suffix(self) -> None:
        names, found = self.instruments("s")

        self.assertEqual([], found)
        self.assertIn("request_duration_seconds_bucket", names)

    def test_an_annotation_unit_is_dropped(self) -> None:
        names, found = self.instruments("{request}")

        self.assertEqual([], found)
        self.assertIn("request_duration_bucket", names)

    def test_a_semicolon_inside_a_description_does_not_end_the_arguments(self) -> None:
        root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        (root / "src").mkdir()
        (root / "src" / "Metrics.cs").write_text(
            '_c = meter.CreateCounter<long>("relay.unavailable", unit: "{attempt}",\n'
            '    description: "A \\"fault; the row backs off.");\n', encoding="utf-8")
        with (
            mock.patch.object(check, "ROOT", root),
            mock.patch.object(check, "failures", []) as failures,
        ):
            names = check.declared_instruments()

        self.assertEqual([], list(failures))
        self.assertIn("relay_unavailable_total", names)

    def declared(self, source: str) -> tuple[set[str], list[str]]:
        root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        (root / "src").mkdir()
        (root / "src" / "Metrics.cs").write_text(source, encoding="utf-8")
        with (
            mock.patch.object(check, "ROOT", root),
            mock.patch.object(check, "failures", []) as failures,
        ):
            return check.declared_instruments(), list(failures)

    def test_a_positional_unit_is_refused_rather_than_read_as_none(self) -> None:
        names, found = self.declared('_d = meter.CreateHistogram<double>("request.duration", "ms");\n')

        self.assertTrue(any("by position" in f for f in found), found)
        self.assertNotIn("request_duration_bucket", names)

    def test_a_description_continued_across_lines_is_not_positional(self) -> None:
        names, found = self.declared(
            '_c = meter.CreateCounter<long>("relay.sent", unit: "{send}",\n'
            '    description: "One half, " +\n        "and the other.");\n')

        self.assertEqual([], found)
        self.assertIn("relay_sent_total", names)

    def test_an_instrument_named_otherwise_than_first_is_counted_and_refused(self) -> None:
        for call in ('meter.CreateCounter<long>(name: "relay.sent");', "meter.CreateCounter<long>(RelaySent);"):
            with self.subTest(call=call):
                _, found = self.declared(f"_c = {call}\n")

                self.assertTrue(any("1 instrument call(s)" in f for f in found), found)

    def test_a_unit_that_is_not_a_literal_is_refused(self) -> None:
        for call, refusal in (
                ('CreateHistogram<double>("x.duration", unit: Units.Seconds);', "not a string literal"),
                ('CreateHistogram<double>("x.duration", Units.Seconds);', "by position"),
                ('CreateHistogram<double>("x.duration", global::Units.Seconds);', "by position"),
                ('CreateObservableGauge("x.duration", observeValue: () => 1, "s");', "by position"),
                ('CreateObservableGauge("x.duration", () => { return Read(); }, unit: "s");', "cannot be read"),
                ('CreateHistogram<double>("x.duration" + Suffix, unit: "s");', "cannot be read"),
                ('CreateHistogram<double>("x.duration".ToLowerInvariant(), unit: "s");', "cannot be read")):
            with self.subTest(call=call):
                names, found = self.declared(f"_d = meter.{call}\n")

                self.assertTrue(any(refusal in f for f in found), found)
                self.assertNotIn("x_duration_bucket", names)
                self.assertNotIn("x_duration", names)

    def test_strings_inside_tags_or_a_callback_are_not_a_unit(self) -> None:
        names, found = self.declared(
            '_c = meter.CreateCounter<long>("relay.sent", unit: "{send}", description: "Sends.",\n'
            '    tags: new Dictionary<string, object?> { ["lane"] = "x" });\n'
            '_g = meter.CreateObservableGauge("relay.depth", () => Read(unit: "s"), unit: "{message}");\n')

        self.assertEqual([], found)
        self.assertIn("relay_sent_total", names)
        self.assertIn("relay_depth", names)

    def test_an_unmapped_unit_is_refused(self) -> None:
        names, found = self.instruments("ms")

        self.assertTrue(any("unit 'ms'" in f for f in found), found)
        self.assertNotIn("request_duration_bucket", names)


class ParseRulesTests(unittest.TestCase):
    def rules(self, text: str) -> list[dict[str, object]]:
        """What parse_rules reads from a rules file holding `text`."""
        root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        path = root / "rules.yml"
        path.write_text(text, encoding="utf-8")
        return check.parse_rules(path)

    def test_a_block_expression_keeps_a_line_that_opens_with_a_subtraction(self) -> None:
        found = self.rules(
            "groups:\n"
            "  - name: g\n"
            "    rules:\n"
            "      - alert: Gap\n"
            "        expr: |\n"
            "          sum(rate(sent_total[5m]))\n"
            "          - sum(rate(acked_total[5m]))\n"
            "          > 0\n"
            "        for: 5m\n"
            "        annotations:\n"
            "          runbook_url: https://example.invalid/gap.md\n")

        self.assertEqual(
            found,
            [{
                "alert": "Gap",
                "expr": "sum(rate(sent_total[5m])) - sum(rate(acked_total[5m])) > 0",
                "runbook": "https://example.invalid/gap.md",
            }])


class DashboardTests(unittest.TestCase):
    def test_a_panel_inside_a_collapsed_row_is_checked(self) -> None:
        root = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        (root / "dashboards").mkdir()
        (root / "dashboards" / "rows.json").write_text(
            '{"panels": [{"type": "row", "collapsed": true,'
            ' "panels": [{"targets": [{"expr": "sum(unpublished_total)"}]}]}]}',
            encoding="utf-8")

        with (
            mock.patch.object(check, "OBSERVABILITY", root),
            mock.patch.object(check, "failures", []) as failures,
        ):
            check.check_dashboards(set())

        self.assertTrue(any("`unpublished_total`" in f for f in failures), failures)


if __name__ == "__main__":
    unittest.main()
