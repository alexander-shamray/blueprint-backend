"""The repository gate behind `canary.py check`, and the command line's whole-plan cases."""

from __future__ import annotations

import contextlib
import io
import json
import os
import re
import tempfile
import unittest
from pathlib import Path

import analysis
import csharp_sources
import descriptor_model
import read_prometheus
import repo_gate
from canary_testing import service_tree


class SignalTests(unittest.TestCase):
    """What each workload is judged on, and what the gate holds that to."""

    def setUp(self) -> None:
        self.document = descriptor_model.load_plan()

    def workload(self, name: str) -> dict:
        return descriptor_model.entries(self.document["workloads"])[name]

    def test_every_workload_declares_a_signal_it_actually_receives(self) -> None:
        """Inventory is the sharp case: its HTTP surface is the admin route,
        and its commands and events arrive on the broker, so HTTP alone judges
        none of its work. The edge consumes nothing."""
        declared = {
            name: set(entry.get("signals", []))
            for name, entry in descriptor_model.entries(self.document["workloads"]).items()
        }

        self.assertEqual(declared["inventory-api"], {"consume"})
        self.assertEqual(declared["payments-api"], {"consume"})
        self.assertEqual(declared["ordering-api"], {"http", "consume", "saga"})
        self.assertEqual(declared["gateway"], {"http"})
        self.assertEqual(declared["web-bff"], {"http", "consume"})
        self.assertEqual(declared["catalog-api"], {"http"})

    def test_a_workload_with_no_signal_fails_the_plan(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["gateway"]["signals"] = []

        failures = repo_gate.check(document)

        self.assertTrue(any("declares no signal" in f for f in failures), failures)

    def test_a_workload_declaring_an_unknown_signal_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["gateway"]["signals"] = ["grpc"]

        failures = repo_gate.check(document)

        self.assertTrue(any("'grpc'" in f for f in failures), failures)


class HttpExemptionTests(unittest.TestCase):
    """Every workload is an ASP.NET Core host, so leaving its HTTP traffic
    unjudged is a decision the plan argues rather than an omission."""

    def setUp(self) -> None:
        self.document = descriptor_model.load_plan()

    def test_a_workload_without_http_and_without_an_argument_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"].pop("httpExemption", None)

        failures = repo_gate.check(document)

        self.assertTrue(
            any("inventory-api" in f and "httpExemption" in f for f in failures), failures)

    def test_an_empty_http_exemption_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"]["httpExemption"] = "  "

        failures = repo_gate.check(document)

        self.assertTrue(any("inventory-api" in f for f in failures), failures)

    def test_an_http_exemption_beside_a_declared_http_signal_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["gateway"]["httpExemption"] = "unneeded"

        failures = repo_gate.check(document)

        self.assertTrue(any("gateway" in f for f in failures), failures)


class VouchingTests(unittest.TestCase):
    """Check 5, over a series no loaded alert reads."""

    ALERTS = "  - alert: X\n    expr: http_server_request_duration_seconds_count\n"

    def root_with(self, registration: str) -> Path:
        """A throwaway tree holding just the two files check 5 reads."""
        tmp = Path(tempfile.mkdtemp())
        alerts = tmp / "deploy" / "observability" / "alerts"
        alerts.mkdir(parents=True)
        (alerts / "platform-alerts.yaml").write_text(self.ALERTS, encoding="utf-8")
        web = tmp / "src" / "BuildingBlocks" / "Common.Web"
        web.mkdir(parents=True)
        (web / "ObservabilityExtensions.cs").write_text(registration, encoding="utf-8")
        return tmp

    def test_a_meter_registration_vouches_for_a_declared_series(self) -> None:
        root = self.root_with('.AddMeter("MassTransit")\n')

        failures = repo_gate._metrics_are_vouched_for(
            ["messaging_masstransit_consume_ea_total"], root, root)

        self.assertEqual(failures, [])

    def test_removing_the_registration_unvouches_it(self) -> None:
        """The declaration is not a free pass: what makes the series real is
        the meter this platform collects, so deleting that line goes red."""
        root = self.root_with('.AddMeter("Commerce.Messaging")\n')

        failures = repo_gate._metrics_are_vouched_for(
            ["messaging_masstransit_consume_ea_total"], root, root)

        self.assertTrue(any("MassTransit" in f for f in failures), failures)

    def test_a_registration_left_in_a_comment_or_a_string_vouches_for_nothing(self) -> None:
        """What collects the series is a live call, so the text of one that
        was commented out, or quoted, is not a registration."""
        for registration in (
            '// .AddMeter("MassTransit")\n',
            '/* .AddMeter("MassTransit") */\n',
            'var note = ".AddMeter(\\"MassTransit\\")";\n',
        ):
            with self.subTest(registration=registration):
                root = self.root_with(registration)

                failures = repo_gate._metrics_are_vouched_for(
                    ["messaging_masstransit_consume_ea_total"], root, root)

                self.assertTrue(any("MassTransit" in f for f in failures), failures)

    def test_a_series_the_instrument_does_not_export_is_refused(self) -> None:
        """Each spelling is the instrument's own exported name or nothing: a
        counter exports no `_count`, and a millisecond histogram no
        `_seconds` bucket, however near the prefix."""
        root = self.root_with('.AddMeter("MassTransit")\n')
        for metric in (
            "messaging_masstransit_consume_count_total",
            "messaging_masstransit_consume_duration_seconds_bucket",
            "messaging_masstransit_consume_total",
        ):
            with self.subTest(metric=metric):
                failures = repo_gate._metrics_are_vouched_for([metric], root, root)

                self.assertTrue(failures)

    def test_every_table_entry_is_its_instruments_exported_spelling(self) -> None:
        """The table is data a reviewer reads, so each key is derived again
        here from its instrument, kind and unit, and each instrument is one
        the pinned version declares."""
        for series, (meter, instrument, kind, unit) in analysis.EXPORTED_SERIES.items():
            with self.subTest(series=series):
                self.assertIn(instrument, analysis.METER_INSTRUMENTS[meter])
                self.assertIn(series, analysis.exported_series(instrument, kind, unit))

    def test_an_undeclared_series_no_alert_reads_still_fails(self) -> None:
        root = self.root_with('.AddMeter("MassTransit")\n')

        failures = repo_gate._metrics_are_vouched_for(
            ["messaging_masstransit_invented_total"], root, root)

        self.assertTrue(failures)

    def test_an_alert_vouches_for_the_series_it_reads(self) -> None:
        root = self.root_with('.AddMeter("MassTransit")\n')

        failures = repo_gate._metrics_are_vouched_for(
            ["http_server_request_duration_seconds_bucket"], root, root)

        self.assertEqual(failures, [])


class VerifiedVersionTests(unittest.TestCase):
    """The MassTransit pin against the version the series table was read from."""

    def root_with(self, version: str) -> Path:
        tmp = Path(tempfile.mkdtemp())
        (tmp / "Directory.Packages.props").write_text(
            f'<Project>\n  <ItemGroup>\n    <PackageVersion Include="MassTransit" '
            f'Version="{version}" />\n  </ItemGroup>\n</Project>\n',
            encoding="utf-8",
        )
        return tmp

    def test_the_verified_version_passes(self) -> None:
        root = self.root_with(repo_gate.MASSTRANSIT_VERIFIED)

        self.assertEqual(repo_gate._masstransit_pin_is_verified(root), [])

    def test_an_upgrade_fails_until_the_table_is_reverified(self) -> None:
        """An upgrade can rename or re-unit an instrument, and every
        consume-judged rung would then read an absent series."""
        root = self.root_with("99.0.0")

        failures = repo_gate._masstransit_pin_is_verified(root)

        self.assertTrue(
            any("99.0.0" in f and repo_gate.MASSTRANSIT_VERIFIED in f for f in failures),
            failures)

    def test_a_missing_pin_fails(self) -> None:
        tmp = Path(tempfile.mkdtemp())
        (tmp / "Directory.Packages.props").write_text("<Project />\n", encoding="utf-8")

        self.assertTrue(repo_gate._masstransit_pin_is_verified(tmp))

    def test_the_real_repository_passes(self) -> None:
        self.assertEqual(repo_gate._masstransit_pin_is_verified(descriptor_model.ROOT), [])

    def test_the_pin_is_a_declared_input(self) -> None:
        """So deploy.yml's triggers run the gate when the pin moves."""
        self.assertIn("Directory.Packages.props", repo_gate.SOURCE_INPUTS)


class SourceInputTests(unittest.TestCase):
    """SOURCE_INPUTS against the reads it claims to enumerate. It once omitted
    `deploy/observability` and check 7 stayed green, since **a gate cannot see
    a read it was never told about.**
    """

    # `root / "deploy" / "observability"` and `(root / "src")`, as written.
    READ = re.compile(r'root\s*/\s*"([a-z]+)"(?:\s*/\s*"([a-z-]+)")?')

    def script_modules(self) -> list[Path]:
        """Every module the command line is made of: `canary.py` and what it imports.

        Globbed rather than listed, so a module added beside them is read
        without anyone remembering to name it here."""
        return sorted(
            path for path in Path(repo_gate.__file__).parent.glob("*.py")
            if not path.name.startswith("test_")
            and path.name not in {"canary_testing.py", "read_prometheus.py"}
        )

    def paths_read(self) -> set[str]:
        source = "\n".join(path.read_text(encoding="utf-8") for path in self.script_modules())
        found = set()
        for first, second in self.READ.findall(source):
            # Two segments where there are two: `deploy` is too wide, since
            # deploy/compose must not trigger this gate. A declared entry that
            # is a prefix still covers a two-segment read, as in check.py.
            found.add(f"{first}/{second}" if second else first)
        return found

    def test_the_scan_reads_every_module_of_the_script(self) -> None:
        """The subject, before the reads: the files the scan opens. A glob that
        found only `canary.py` would see none of the reads the split moved."""
        names = {path.name for path in self.script_modules()}
        self.assertLessEqual(
            {"canary.py", "repo_gate.py", "analysis.py", "csharp_sources.py"}, names)

    def test_the_scan_finds_the_reads_it_is_checking(self) -> None:
        """The subject, before the assertion. A regex that matched nothing
        would pass the test below against any list at all."""
        self.assertIn("deploy/observability", self.paths_read())
        self.assertIn("src", self.paths_read())

    def test_every_path_the_script_reads_is_declared(self) -> None:
        for path in sorted(self.paths_read()):
            with self.subTest(path=path):
                self.assertTrue(
                    any(path == entry or path.startswith(f"{entry}/")
                        for entry in repo_gate.SOURCE_INPUTS),
                    f"canary.py opens {path!r} and SOURCE_INPUTS does not declare it, "
                    f"so deploy.yml's triggers do not watch it: {repo_gate.SOURCE_INPUTS}",
                )


class ImageSourceTests(unittest.TestCase):
    """The facts that describe the image are read from the tree given."""

    @staticmethod
    def _tree(route: str) -> Path:
        return service_tree({"Svc.Api/Health.cs": f'app.MapHealthChecks("{route}");\n'})

    def test_the_exclusion_is_the_given_trees_routes(self) -> None:
        self.assertEqual("/healthz/live", csharp_sources.probe_exclusion(self._tree("/healthz/live")))

    def test_two_revisions_give_two_exclusions(self) -> None:
        """ADR-050's defect in one assertion. The same code over two trees
        produces two different exclusions, so which tree it reads decides
        which requests the rung counts as traffic."""
        self.assertNotEqual(
            csharp_sources.probe_exclusion(self._tree("/healthz/live")),
            csharp_sources.probe_exclusion(self._tree("/health/live")),
        )

    def test_the_queries_carry_the_given_trees_exclusion(self) -> None:
        """Through `queries`, because that is the call read_prometheus makes
        and an exclusion nothing interpolates is not applied to anything."""
        self.assertIn(
            "/health/live",
            analysis.queries("http", self._tree("/health/live"))["errorRate"],
        )

    def test_check_reads_the_rolled_workloads_hosts_from_the_source(self) -> None:
        """Check 4 asks whether each serviceName is an entry assembly. Of the
        image, for the workload being rolled: one renamed in the image and not
        in the checkout selects no series, and an absent series promotes
        nothing and rolls every rung back."""
        failures = repo_gate.check(
            descriptor_model.load_plan(), source=self._tree("/healthz/live"),
            workload="catalog-api")

        self.assertEqual(
            ["catalog-api"],
            [f.split(".")[1].split(".serviceName")[0]
             for f in failures if "not an entry assembly" in f])

    def test_the_other_workloads_are_not_judged_against_this_image(self) -> None:
        """A tag names one workload's image. CI builds an image only for a
        service a commit changed, so an Ordering image legitimately predates a
        Catalog-only change — and judging Catalog's plan entry against it
        would refuse a rollout that is fine."""
        failures = repo_gate.check(
            descriptor_model.load_plan(), source=self._tree("/healthz/live"),
            workload="ordering-api")

        self.assertEqual(
            [], [f for f in failures if "catalog-api" in f], failures)

    def test_a_source_without_a_workload_is_refused(self) -> None:
        """Because the alternative is one image's revision answering for every
        workload, which is the defect rather than a stricter reading of it."""
        failures = repo_gate.check(
            descriptor_model.load_plan(), source=self._tree("/healthz/live"))

        self.assertTrue(
            any("not a workload in this plan" in f for f in failures), failures)

    def test_a_source_for_an_unknown_workload_is_refused(self) -> None:
        """A name no key matches scopes nothing: every tree would come from
        the checkout and the image would answer for none of them, which is
        the scoping absent rather than refused."""
        for name in ("", "catalog", "catalog-api "):
            with self.subTest(workload=name):
                failures = repo_gate.check(
                    descriptor_model.load_plan(), source=self._tree("/healthz/live"),
                    workload=name)

                self.assertTrue(
                    any("not a workload in this plan" in f for f in failures),
                    failures)

    def test_an_http_only_workload_is_not_held_to_message_series(self) -> None:
        """series_read() spans every signal the plan defines, so holding one
        image to all of them refuses a gateway image for lacking a MassTransit
        meter it has no reason to carry. The tree registers a meter, because
        one with no registration file at all fails check 5 for being
        unreadable and would answer this question without scoping anything."""
        tree = self._tree("/healthz/live")
        web = tree / "src" / "BuildingBlocks" / "Common.Web"
        web.mkdir(parents=True)
        (web / "ObservabilityExtensions.cs").write_text(
            '.AddMeter("Commerce.Messaging")\n', encoding="utf-8")

        failures = repo_gate.check(descriptor_model.load_plan(), source=tree, workload="gateway")

        self.assertEqual([], [f for f in failures if "MassTransit" in f], failures)

    def test_the_installed_image_has_to_build_the_judged_assembly(self) -> None:
        """§13.2 takes service.name from each image's entry assembly, and
        the baseline is the installed image. One from before a rename emits
        the old label, so the baseline query matches nothing and a healthy
        release rolls back at every rung."""
        candidate = self._tree("/healthz/live")
        installed = service_tree({"Svc.Api/Health.cs": 'app.MapHealthChecks("/x");\n'})

        failures = repo_gate.check(
            descriptor_model.load_plan(), source=candidate, workload="catalog-api",
            installed=installed)

        self.assertTrue(
            any("installed release's image does not build" in f for f in failures),
            failures)

    def test_check_reads_its_charts_from_the_root(self) -> None:
        """The other half of the split. The chart is installed from the
        checkout, so it is judged there — a source tree with no deploy/ at
        all raises nothing about charts."""
        failures = repo_gate.check(
            descriptor_model.load_plan(), source=self._tree("/healthz/live"),
            workload="catalog-api")

        self.assertEqual([], [f for f in failures if "not a chart under" in f])

    def test_the_default_source_is_the_checkout(self) -> None:
        """A pull request has no image and no tag, so `check` with one tree
        is the run the gate job makes."""
        self.assertEqual([], repo_gate.check(descriptor_model.load_plan()))


class TrackSourceTests(unittest.TestCase):
    """Each track's probe exclusion is scanned from its own image (ADR-050)."""

    @staticmethod
    def _tree(route: str) -> Path:
        return service_tree({"Svc.Api/Health.cs": f'app.MapHealthChecks("{route}");\n'})

    def _expressions(self, **trees) -> list[str]:
        """Every PromQL `read` emits, with the fetch replaced."""
        seen: list[str] = []
        original = read_prometheus.query
        read_prometheus.query = lambda _base, expression: seen.append(expression) or 1.0
        try:
            read_prometheus.read(
                "http://prometheus.invalid", "catalog-api", "10m",
                descriptor_model.load_plan(), **trees)
        finally:
            read_prometheus.query = original
        return seen

    def test_each_track_reads_the_routes_of_the_image_it_runs(self) -> None:
        """One exclusion for both tracks filters the stable track's probes
        by the candidate's routes, so a probe the stable image serves and
        the candidate does not is counted as its business traffic."""
        seen = self._expressions(
            candidate=self._tree("/healthz/live"), installed=self._tree("/health/ready"))

        canary_side = [e for e in seen if 'deployment_track="canary"' in e and "http_route" in e]
        stable_side = [e for e in seen if 'deployment_track="stable"' in e and "http_route" in e]

        self.assertTrue(canary_side and stable_side, seen)
        self.assertTrue(all("/healthz/live" in e for e in canary_side), canary_side)
        self.assertTrue(all("/health/ready" in e for e in stable_side), stable_side)
        self.assertFalse(any("/health/ready" in e for e in canary_side), canary_side)

    def test_an_absent_installed_tree_falls_back_to_the_candidate(self) -> None:
        """Stated rather than assumed: the gate path has one tree, and the
        rollout is held to supplying both by check 12."""
        seen = self._expressions(candidate=self._tree("/healthz/live"))

        self.assertTrue(all("/healthz/live" in e for e in seen if "http_route" in e), seen)

    def test_main_carries_both_trees_into_read(self) -> None:
        """Without this, dropping either argument from main() leaves every
        other test green and silently restores the checkout's routes."""
        seen: dict[str, Path] = {}

        def spy(_base, _workload, _window, _plan, candidate=None, installed=None):
            seen.update(candidate=candidate, installed=installed)
            return {}

        original_read, original_url = read_prometheus.read, os.environ.get("PROMETHEUS_URL")
        read_prometheus.read = spy
        os.environ["PROMETHEUS_URL"] = "http://prometheus.invalid"
        try:
            with tempfile.TemporaryDirectory() as out,                     contextlib.redirect_stdout(io.StringIO()):
                read_prometheus.main([
                    "read_prometheus.py", "--workload", "catalog-api", "--window", "10m",
                    "--source", "candidate-tree", "--installed-source", "stable-tree",
                    "--out", str(Path(out) / "readings.json"),
                ])
        finally:
            read_prometheus.read = original_read
            if original_url is None:
                del os.environ["PROMETHEUS_URL"]
            else:
                os.environ["PROMETHEUS_URL"] = original_url

        self.assertEqual(Path("candidate-tree"), seen["candidate"])
        self.assertEqual(Path("stable-tree"), seen["installed"])


if __name__ == "__main__":
    unittest.main()
