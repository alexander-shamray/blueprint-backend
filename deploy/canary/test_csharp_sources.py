"""What the rollout reads out of the C# sources: consumers, sagas and health routes."""

from __future__ import annotations

import json
import re
import unittest

import analysis
import csharp_sources
import descriptor_model
import repo_gate
from canary_testing import service_tree


class ConsumerScanTests(unittest.TestCase):
    """The scan that decides which workloads owe a consume signal."""

    def setUp(self) -> None:
        self.document = descriptor_model.load_plan()

    def test_the_scan_finds_the_services_it_judges(self) -> None:
        """The subject before the assertion. A scan that found no consumer
        would let every workload off, which is the state this closes."""
        found = {
            name: csharp_sources.has_consumers(entry["serviceName"])
            for name, entry in descriptor_model.entries(self.document["workloads"]).items()
        }

        self.assertTrue(found["inventory-api"])
        self.assertTrue(found["ordering-api"])
        self.assertTrue(found["catalog-api"])
        self.assertTrue(found["payments-api"])
        self.assertFalse(found["gateway"])
        self.assertTrue(found["web-bff"])

    def test_a_consumer_service_declaring_no_consume_signal_is_named(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"]["signals"] = ["http"]

        failures = repo_gate.check(document)

        self.assertTrue(
            any("inventory-api" in f and "consume" in f for f in failures), failures)

    def test_an_argued_exemption_is_accepted(self) -> None:
        """Catalog's is the shipped one, and the plan passing is the proof."""
        self.assertIn("consumeExemption", self.workload_catalog())
        self.assertEqual(repo_gate.check(self.document), [])

    def workload_catalog(self) -> dict:
        return descriptor_model.entries(self.document["workloads"])["catalog-api"]

    def test_an_empty_exemption_does_not_count_as_an_argument(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["catalog-api"]["consumeExemption"] = "  "

        failures = repo_gate.check(document)

        self.assertTrue(any("catalog-api" in f for f in failures), failures)

    def test_an_exemption_on_a_workload_with_no_consumers_fails(self) -> None:
        """Both directions, as the outbox exemption one tree over: a stale
        exemption is a claim nobody rechecks."""
        document = json.loads(json.dumps(self.document))
        document["workloads"]["gateway"]["consumeExemption"] = "no reason at all"

        failures = repo_gate.check(document)

        self.assertTrue(any("gateway" in f for f in failures), failures)

    def test_a_message_signal_on_a_service_that_registers_nothing_fails(self) -> None:
        """The other direction: a declared consume or saga signal on a
        service with nothing to measure reads series that cannot exist, so
        every rung rolls back on a release that did nothing wrong."""
        for signal in ("consume", "saga"):
            with self.subTest(signal=signal):
                document = json.loads(json.dumps(self.document))
                document["workloads"]["gateway"]["signals"].append(signal)

                failures = repo_gate.check(document)

                self.assertTrue(
                    any("gateway" in f and signal in f for f in failures), failures)

    def test_an_exemption_beside_a_declared_consume_signal_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"]["consumeExemption"] = "unneeded"

        failures = repo_gate.check(document)

        self.assertTrue(any("inventory-api" in f for f in failures), failures)


class BulkRegistrationTests(unittest.TestCase):
    """Every MassTransit 8.5.3 registration form, singly and in bulk.

    A service registering consumers by namespace owes the same signal as one
    naming them; a scan of the single form alone would let it off.
    """

    CONSUMER_FORMS = (
        "x.AddConsumer<OrderConsumer>();",
        "x.AddConsumer(typeof(OrderConsumer));",
        "x.AddConsumers(typeof(OrderConsumer).Assembly);",
        "x.AddConsumersFromNamespaceContaining<OrderConsumer>();",
        "x.AddFutureRequestConsumer<OrderFuture, OrderRequest, Order>();",
    )
    SAGA_FORMS = (
        "x.AddSaga<OrderSaga>();",
        "x.AddSagas(typeof(OrderSaga).Assembly);",
        "x.AddSagasFromNamespaceContaining<OrderSaga>();",
        "x.AddSagaStateMachine<OrderStateMachine, OrderState>();",
        "x.AddSagaStateMachines(typeof(OrderStateMachine).Assembly);",
        "x.AddSagaStateMachinesFromNamespaceContaining<OrderStateMachine>();",
        "x.AddJobSagaStateMachines();",
        "x.AddFuture<OrderFuture>();",
        "x.AddFutures(typeof(OrderFuture).Assembly);",
        "x.AddFuturesFromNamespaceContaining<OrderFuture>();",
    )

    def test_every_consumer_form_owes_consume_and_not_saga(self) -> None:
        for form in self.CONSUMER_FORMS:
            with self.subTest(form=form):
                root = service_tree({"Svc.Infrastructure/Bus.cs": form + "\n"})

                self.assertTrue(csharp_sources.has_consumers("Svc.Api", root))
                self.assertFalse(csharp_sources.has_sagas("Svc.Api", root))

    def test_every_saga_form_owes_saga_and_not_consume(self) -> None:
        for form in self.SAGA_FORMS:
            with self.subTest(form=form):
                root = service_tree({"Svc.Infrastructure/Bus.cs": form + "\n"})

                self.assertTrue(csharp_sources.has_sagas("Svc.Api", root))
                self.assertFalse(csharp_sources.has_consumers("Svc.Api", root))

    def test_a_saga_repository_or_a_commented_registration_owes_nothing(self) -> None:
        """The negative control: a repository is not a saga, and a comment
        is not a registration."""
        root = service_tree({"Svc.Infrastructure/Bus.cs": (
            "x.AddSagaRepository<OrderState>();\n"
            "// x.AddConsumersFromNamespaceContaining<OrderConsumer>();\n"
        )})

        self.assertFalse(csharp_sources.has_consumers("Svc.Api", root))
        self.assertFalse(csharp_sources.has_sagas("Svc.Api", root))

    def test_a_comment_marker_inside_a_string_hides_nothing(self) -> None:
        """A URL is a string holding `//`, and a scan that read it as a
        comment would lose the rest of the line."""
        root = service_tree({"Svc.Infrastructure/Bus.cs": (
            'var host = "rabbitmq://broker"; x.AddConsumer<OrderConsumer>();\n'
            'var glob = @"/*"; x.AddSaga<OrderState>(); var end = "*/";\n'
        )})

        self.assertTrue(csharp_sources.has_consumers("Svc.Api", root))
        self.assertTrue(csharp_sources.has_sagas("Svc.Api", root))

    def test_a_registration_spelled_inside_a_string_is_not_one(self) -> None:
        """A string is text, not a call: a log message or a raw literal that
        names a registration owes no signal."""
        root = service_tree({"Svc.Infrastructure/Bus.cs": (
            'log.Info("calling x.AddConsumer<OrderConsumer>() next");\n'
            'var doc = """\n    x.AddSaga<OrderState>();\n    """;\n'
        )})

        self.assertFalse(csharp_sources.has_consumers("Svc.Api", root))
        self.assertFalse(csharp_sources.has_sagas("Svc.Api", root))

    def test_a_raw_string_closes_on_its_own_delimiter(self) -> None:
        """A raw literal opened with four quotes may hold three, so it ends
        at the matching run of four, and nothing inside it is a call."""
        for prefix in ("", "$$"):
            with self.subTest(prefix=prefix):
                root = service_tree({"Svc.Infrastructure/Bus.cs": (
                    f'var doc = {prefix}""""\n    a """ b x.AddConsumer<OrderConsumer>();\n'
                    '    app.MapHealthChecks("/orders");\n    """";\n'
                    'app.MapHealthChecks("/health/live");\n'
                )})

                self.assertFalse(csharp_sources.has_consumers("Svc.Api", root))
                self.assertEqual(csharp_sources.health_routes(root), {"/health/live"})


class SagaScanTests(unittest.TestCase):
    """The scan that decides which workloads owe a saga signal."""

    def setUp(self) -> None:
        self.document = descriptor_model.load_plan()

    def test_the_scan_finds_the_services_it_judges(self) -> None:
        """Ordering's fulfilment saga is the one state machine, and a scan
        that found none would let it off."""
        found = {
            name: csharp_sources.has_sagas(entry["serviceName"])
            for name, entry in descriptor_model.entries(self.document["workloads"]).items()
        }

        self.assertTrue(found["ordering-api"])
        for name in ("catalog-api", "inventory-api", "payments-api", "gateway", "web-bff"):
            self.assertFalse(found[name], name)

    def test_a_saga_is_not_a_consumer_to_the_consume_scan(self) -> None:
        """The two scans are split because the two signals are: a service
        whose only registration is a saga owes saga, not consume."""
        self.assertNotRegex(".AddSagaStateMachine<S, T>()", csharp_sources.CONSUMER_REGISTRATION)
        self.assertRegex(".AddSagaStateMachine<S, T>()", csharp_sources.SAGA_REGISTRATION)
        self.assertRegex(".AddConsumer<C>()", csharp_sources.CONSUMER_REGISTRATION)
        self.assertNotRegex(".AddConsumer<C>()", csharp_sources.SAGA_REGISTRATION)

    def test_a_saga_service_declaring_no_saga_signal_is_named(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["ordering-api"]["signals"] = ["http", "consume"]

        failures = repo_gate.check(document)

        self.assertTrue(
            any("ordering-api" in f and "saga" in f for f in failures), failures)

    def test_an_argued_saga_exemption_is_accepted(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["ordering-api"]["signals"] = ["http", "consume"]
        document["workloads"]["ordering-api"]["sagaExemption"] = "an argument"

        self.assertEqual(repo_gate.check(document), [])

    def test_an_empty_saga_exemption_does_not_count_as_an_argument(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["ordering-api"]["signals"] = ["http", "consume"]
        document["workloads"]["ordering-api"]["sagaExemption"] = "  "

        failures = repo_gate.check(document)

        self.assertTrue(any("ordering-api" in f for f in failures), failures)

    def test_a_saga_exemption_on_a_workload_with_no_saga_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["inventory-api"]["sagaExemption"] = "no saga here"

        failures = repo_gate.check(document)

        self.assertTrue(any("inventory-api" in f for f in failures), failures)

    def test_a_saga_exemption_beside_a_declared_saga_signal_fails(self) -> None:
        document = json.loads(json.dumps(self.document))
        document["workloads"]["ordering-api"]["sagaExemption"] = "unneeded"

        failures = repo_gate.check(document)

        self.assertTrue(any("ordering-api" in f for f in failures), failures)


class HealthRouteTests(unittest.TestCase):
    """The probe exclusion, against the routes Common.Web actually maps."""

    def setUp(self) -> None:
        self.document = descriptor_model.load_plan()

    def test_the_scan_finds_the_routes_it_is_checking(self) -> None:
        """A scan that found nothing would certify any selector at all."""
        routes = csharp_sources.health_routes()

        self.assertEqual(routes, {"/health/live", "/health/ready", "/health/startup"})

    def test_a_route_that_is_not_a_literal_fails_the_plan_by_location(self) -> None:
        """A route read through a constant, built by concatenation or spelled
        with an escape is one the scan cannot see as the host maps it, and a
        probe it cannot see is traffic again. The XML doc mention is the
        negative control: a comment is not a call site; and a URL before a
        call on its line does not hide the call."""
        root = service_tree({"Svc.Api/Health.cs": (
            "/// Maps <c>MapHealthChecks</c> for the probes.\n"
            'app.MapHealthChecks("/health/live");\n'
            "app.MapHealthChecks(ReadyPath, options);\n"
            'app.MapHealthChecks("/health/" + "startup", options);\n'
            'app.MapHealthChecks("/health/\\u0072eady");\n'
            'var u = "http://docs"; app.MapHealthChecks("/probe");\n'
        )})

        failures = csharp_sources._probe_routes_are_readable(root)

        self.assertEqual(csharp_sources.health_routes(root), {"/health/live", "/probe"})
        self.assertEqual(len(failures), 3, failures)
        self.assertTrue(any("Health.cs:3" in f for f in failures), failures)
        self.assertTrue(any("Health.cs:4" in f for f in failures), failures)
        self.assertTrue(any("Health.cs:5" in f for f in failures), failures)
        self.assertFalse(any("Health.cs:1" in f for f in failures), failures)

    def test_a_call_spelled_inside_a_string_maps_no_route(self) -> None:
        """Excluding a route nobody maps as a probe would hide real traffic
        on it, so text inside a string is not a call site."""
        root = service_tree({"Svc.Api/Health.cs": (
            'app.MapHealthChecks("/health/live");\n'
            'var hint = "try app.MapHealthChecks(\\"/orders\\")";\n'
        )})

        self.assertEqual(csharp_sources.health_routes(root), {"/health/live"})
        self.assertEqual(csharp_sources.unresolved_health_routes(root), [])

    def test_the_real_call_sites_are_all_literals(self) -> None:
        self.assertEqual(csharp_sources.unresolved_health_routes(descriptor_model.ROOT), [])

    def test_the_exclusion_covers_every_mapped_probe_route(self) -> None:
        """The library chart probes every five and ten seconds, which alone
        passes minimumRequests inside one dwell — so unfiltered, a canary that
        served no real request is judged healthy on its own probes. PromQL
        anchors the regex at both ends, as `fullmatch` does."""
        exclusion = csharp_sources.probe_exclusion()

        for route in csharp_sources.health_routes():
            with self.subTest(route=route):
                self.assertIsNotNone(re.fullmatch(exclusion, route))
        self.assertIsNone(re.fullmatch(exclusion, "/api/products"))

    def test_a_route_mapped_later_is_excluded_without_an_edit(self) -> None:
        root = service_tree({"Svc.Api/Health.cs": (
            'app.MapHealthChecks("/health/live");\n'
            'app.MapHealthChecks("/probe/deep");\n'
        )})

        exclusion = csharp_sources.probe_exclusion(root)

        self.assertIsNotNone(re.fullmatch(exclusion, "/probe/deep"))
        self.assertIn(f'http_route!~"{exclusion}"', analysis.queries("http", root)["requests"])

    def test_no_http_query_is_rendered_over_an_unreadable_route(self) -> None:
        """A partial exclusion counts the probes it misses as traffic, so the
        template is refused rather than rendered short."""
        root = service_tree({"Svc.Api/Health.cs": (
            'app.MapHealthChecks("/health/live");\n'
            "app.MapHealthChecks(ReadyPath);\n"
        )})

        with self.assertRaises(descriptor_model.PlanError) as raised:
            analysis.queries("http", root)

        self.assertIn("Health.cs:2", str(raised.exception))

    def test_no_http_query_is_rendered_when_the_scan_finds_no_route(self) -> None:
        with self.assertRaises(descriptor_model.PlanError):
            analysis.queries("http", service_tree({}))


if __name__ == "__main__":
    unittest.main()
