"""The traffic loop and the three scripts that need it, against doubles. No Docker.

    cd tools/game-day && py -3.12 -m unittest
"""

import re
import threading
import time
import types
import unittest
from unittest import mock

import game_day
import harness
import test_harness as base
from test_refused_reads import Stack


def squash(text):
    return re.sub(r"\s+", "", text)


RULES = (harness.ROOT / "deploy/observability/alerts/platform-alerts.yaml").read_text(encoding="utf-8")


def runbook(name):
    return (harness.ROOT / "docs" / "runbooks" / name).read_text(encoding="utf-8")


class Recorder:
    """Stands in for the loop inside a scenario, noting where in the calls it started and stopped."""

    def __init__(self, calls):
        self.calls = calls
        self.running = False

    def start(self, max_seconds):
        self.running = True
        self.calls.append(("traffic", max_seconds))

    def stop(self):
        self.running = False
        self.calls.append(("traffic-stop",))

    def counts(self):
        return {"sent": 0}


def instant(predicate, deadline, what, **_):
    """wait_until that asks once, so a scenario's wait is its predicate and not a sleep."""
    ok, observed = predicate()
    if not ok:
        raise harness.Timeout(f"{what}: {observed}")
    return 0.0


class Rate(unittest.TestCase):
    def test_the_rate_gives_a_p99_its_hundred_observations_in_the_widest_window(self):
        per_service = harness.LATENCY_WINDOW_SECONDS / (harness.TRAFFIC_TICK_SECONDS * harness.TRAFFIC_ROUTES)
        self.assertGreaterEqual(per_service, harness.WINDOW_OBSERVATIONS)
        self.assertEqual(3.0, harness.TRAFFIC_TICK_SECONDS)

    def test_the_latency_window_is_the_rules_and_is_the_widest_of_the_three(self):
        windows = {name: int(re.search(rf"alert: {name}\n.*?\[(\d+)m", RULES, re.S).group(1))
                   for name in ("ErrorRateGateway", "ErrorRateService", "Latency")}
        self.assertEqual(max(windows.values()) * 60, harness.LATENCY_WINDOW_SECONDS)

    def test_the_rate_stays_under_the_gateways_two_limiters(self):
        program = (harness.ROOT / "src/Gateway/Gateway.Api/Program.cs").read_text(encoding="utf-8")
        per_minute = 60 / (harness.TRAFFIC_TICK_SECONDS * harness.TRAFFIC_ROUTES)
        self.assertLess(per_minute, int(re.search(r"PermitLimit = (\d+)", program).group(1)))
        self.assertLess(per_minute, int(re.search(r"TokenLimit = (\d+)", program).group(1)))

    def test_the_two_routes_are_the_gateways_and_the_cancel_reason_is_the_contracts(self):
        routes = (harness.ROOT / "src/Gateway/Gateway.Api/appsettings.json").read_text(encoding="utf-8")
        self.assertIn('"Path": "/api/v1/catalog/{**catch-all}", "Methods": [ "GET" ]', routes)
        self.assertIn('"Path": "/api/v1/orders/{**catch-all}"', routes)
        contract = (harness.ROOT / "src/BuildingBlocks/Common.Contracts/Ordering/V1/Commands.cs").read_text(
            encoding="utf-8")
        self.assertIn('CustomerRequest = "customer_request"', contract)


class TrafficLoop(unittest.TestCase):
    def loop(self, send, **kwargs):
        return harness.Traffic(send=send, token=lambda: "tok", tick=0.001, **kwargs)

    def wait_for(self, condition):
        deadline = time.monotonic() + 5
        while not condition() and time.monotonic() < deadline:
            time.sleep(0.005)
        self.assertTrue(condition())

    def test_it_alternates_an_anonymous_read_and_an_authenticated_cancel_and_counts_what_came_back(self):
        seen = []

        def send(method, url, headers, body):
            seen.append((method, url, headers.get("Authorization"), body))
            return (200, "") if method == "GET" else (404, "")

        traffic = self.loop(send)
        traffic.start(60)
        self.wait_for(lambda: len(seen) >= 6)
        traffic.stop()
        reads = [call for call in seen if call[0] == "GET"]
        cancels = [call for call in seen if call[0] == "POST"]
        self.assertTrue(all(call[2] is None for call in reads), "the read is anonymous")
        self.assertTrue(all(call[1].startswith(f"{harness.GATEWAY}/api/v1/catalog/products") for call in reads))
        self.assertTrue(all(call[2] == "Bearer tok" and call[1].endswith("/cancel") for call in cancels))
        self.assertTrue(all(b"customer_request" in call[3] for call in cancels))
        self.assertEqual(len(seen), traffic.counts()["sent"])
        self.assertEqual({200, 404}, set(traffic.counts()["statuses"]))
        self.assertLessEqual(abs(len(reads) - len(cancels)), 1)

    def test_every_cancel_names_an_order_that_does_not_exist_so_nothing_is_written(self):
        urls = []
        traffic = self.loop(lambda method, url, headers, body: (urls.append(url), (404, ""))[1])
        traffic.start(60)
        self.wait_for(lambda: len([u for u in urls if "/orders/" in u]) >= 3)
        traffic.stop()
        ids = {u for u in urls if "/orders/" in u}
        self.assertEqual(len(ids), len([u for u in urls if "/orders/" in u]), "a fresh id for each")

    def test_it_ends_at_its_bound_without_being_stopped(self):
        sent = []
        traffic = self.loop(lambda *call: (sent.append(call), (200, ""))[1])
        traffic.start(0.05)
        self.wait_for(lambda: not traffic.running)
        traffic.stop()
        self.assertGreater(len(sent), 0)

    def test_it_refuses_a_second_start_and_can_start_again_after_a_stop(self):
        traffic = self.loop(lambda *call: (200, ""))
        traffic.start(60)
        with self.assertRaises(harness.GameDayError):
            traffic.start(60)
        traffic.stop()
        self.assertFalse(traffic.running)
        traffic.start(60)
        traffic.stop()

    def test_a_request_the_database_holds_does_not_slow_the_loop_and_the_cap_bounds_what_is_out(self):
        release = threading.Event()
        started = []

        def send(method, url, headers, body):
            started.append(url)
            release.wait(10)
            return 500, ""

        traffic = self.loop(send)
        traffic.start(60)
        self.wait_for(lambda: traffic.counts()["skipped"] > 0)
        self.assertLessEqual(len(started), harness.TRAFFIC_MAX_IN_FLIGHT)
        release.set()
        traffic.stop()
        self.assertEqual({500}, set(traffic.counts()["statuses"]))

    def test_stop_waits_for_the_requests_still_out(self):
        release = threading.Event()
        traffic = self.loop(lambda *call: (release.wait(10), (200, ""))[1])
        traffic.start(60)
        self.wait_for(lambda: traffic.running)
        threading.Timer(0.1, release.set).start()
        traffic.stop()
        self.assertEqual(traffic.counts()["sent"], sum(traffic.counts()["statuses"].values()))

    def test_the_token_is_fetched_once_and_again_after_it_has_aged(self):
        fetched = []
        clock = base.Clock()
        traffic = harness.Traffic(send=lambda *call: (200, ""), token=lambda: fetched.append(1) or "t", clock=clock)
        traffic._headers()
        traffic._headers()
        self.assertEqual(1, len(fetched))
        clock.now += harness.TRAFFIC_LOGIN_REFRESH_SECONDS
        traffic._headers()
        self.assertEqual(2, len(fetched))

    def test_a_token_that_cannot_be_fetched_does_not_end_the_loop(self):
        def refused():
            raise harness.GameDayError("no token")

        traffic = harness.Traffic(send=lambda *call: (200, ""), token=refused, clock=base.Clock())
        self.assertEqual("Bearer ", traffic._headers()["Authorization"])

    def test_the_patient_sender_outwaits_a_sql_command(self):
        self.assertGreater(harness.TRAFFIC_TIMEOUT_SECONDS, 30)


class RunnerFollowUp(unittest.TestCase):
    """A scenario's NEXT is a second alert of the same runbook, run on a stack the first restored."""

    def pair(self, first_states, second_states):
        first = base.Fake(first_states)
        second = base.Fake(second_states)
        second.scenario.ALERT = "B"
        second.scenario.settled = lambda world: (world.alerts.state("B") == "inactive", "B")
        first.scenario.NEXT = second.scenario
        # One world answers both, by alert name.
        script = {"A": list(first_states), "B": list(second_states)}

        class Alerts:
            def state(self, name):
                states = script[name]
                return states.pop(0) if len(states) > 1 else states[0]

            def loaded(self, name):
                return True

        first.world.alerts = Alerts()
        return first, second

    def test_the_second_runs_after_a_first_that_settled(self):
        first, second = self.pair(["inactive", "firing", "firing", "inactive"],
                                  ["inactive", "firing", "firing", "inactive"])
        self.assertEqual([], first.run())
        self.assertEqual(["cause", "first_step", "restore"], first.actions())
        self.assertEqual(["cause", "first_step", "restore"], second.actions())

    def test_the_second_does_not_run_on_a_stack_the_first_left_poisoned(self):
        first, second = self.pair(["inactive", "firing", "firing"], ["inactive"])
        findings = first.run()
        self.assertTrue(any("restore did not settle" in finding for finding in findings), findings)
        self.assertTrue(any("B: not run" in finding for finding in findings), findings)
        self.assertEqual([], second.actions())

    def test_a_first_step_finding_does_not_stop_the_second(self):
        first, second = self.pair(["inactive", "firing", "firing", "inactive"],
                                  ["inactive", "firing", "firing", "inactive"])
        first.step = (False, "nothing read")
        findings = first.run()
        self.assertEqual(1, len(findings))
        self.assertEqual(["cause", "first_step", "restore"], second.actions())

    def test_the_loop_is_stopped_after_each_phase_and_after_a_cause_that_raised(self):
        for error in (None, harness.GameDayError("docker stop exited 1")):
            with self.subTest(error):
                fake = base.Fake(["inactive", "firing", "firing", "inactive"], cause_error=error)
                fake.world.traffic = mock.Mock(running=True)
                fake.world.traffic.counts.return_value = {"sent": 3}
                fake.run()
                fake.world.traffic.stop.assert_called_once_with()


class ErrorRate(unittest.TestCase):
    def module(self):
        from scenarios import error_rate
        return error_rate

    def stack(self, query=None, **kwargs):
        stack = Stack(query=query, **kwargs)
        stack.world.traffic = Recorder(stack.calls)
        return stack

    def test_the_query_is_the_runbooks(self):
        self.assertIn(squash(self.module().FIRST_QUERY), squash(runbook("error-rate.md")))

    def test_the_gateway_phase_fills_the_window_before_it_stops_ordering_api(self):
        stack = self.stack(query=lambda expression: [{"metric": {}, "value": [1, "0.3"]}])
        with mock.patch.object(harness, "wait_until", instant):
            self.module().cause(stack.world)
        self.assertEqual([("traffic", self.module().TRAFFIC_SECONDS), ("stop", "ordering-api")], stack.calls)

    def test_a_loop_whose_requests_never_reach_prometheus_is_a_finding_and_nothing_is_stopped(self):
        stack = self.stack(query=lambda expression: [])
        with mock.patch.object(harness, "wait_until", instant), self.assertRaises(harness.Timeout):
            self.module().cause(stack.world)
        self.assertNotIn("stop", stack.verbs())

    def test_the_service_phase_stops_sql_and_waits_on_the_services_that_are_not_the_gateway(self):
        asked = []
        stack = self.stack(query=lambda expression: asked.append(expression) or [{"metric": {}, "value": [1, "1"]}])
        with mock.patch.object(harness, "wait_until", instant):
            self.module().NEXT.cause(stack.world)
        self.assertIn('service_name!="Gateway.Api"', asked[0])
        self.assertEqual("stop", stack.calls[1][0])
        self.assertEqual(("stop", "sql"), stack.calls[1])

    def answers(self, services):
        return lambda expression: [
            {"metric": {"service_name": name, "http_route": "/r"}, "value": [1, "0.2"]} for name in services]

    def test_the_gateway_first_step_passes_on_an_edge_5xx_with_no_service_alerting(self):
        stack = self.stack(query=self.answers(["Gateway.Api"]))
        stack.world.alerts.state = lambda name: "inactive"
        ok, detail = self.module().first_step(stack.world)
        self.assertTrue(ok, detail)
        self.assertIn("Gateway.Api", detail)

    def test_a_series_that_reads_zero_is_no_evidence_and_the_worst_route_is_the_one_reported(self):
        rows = [{"metric": {"service_name": "Gateway.Api", "http_route": "/old"}, "value": [1, "0"]},
                {"metric": {"service_name": "Gateway.Api", "http_route": "/orders"}, "value": [1, "0.1"]},
                {"metric": {"service_name": "Catalog.Api", "http_route": "/old"}, "value": [1, "0"]}]
        stack = self.stack(query=lambda expression: rows)
        stack.world.alerts.state = lambda name: "inactive"
        ok, detail = self.module().first_step(stack.world)
        self.assertTrue(ok, detail)
        self.assertIn("/orders", detail)
        self.assertNotIn("Catalog.Api", detail)
        rows[1]["value"][1] = "0"
        self.assertFalse(self.module().first_step(stack.world)[0])

    def test_the_gateway_first_step_fails_without_an_edge_series_or_with_the_service_rule_firing(self):
        stack = self.stack(query=self.answers(["Ordering.Api"]))
        stack.world.alerts.state = lambda name: "inactive"
        self.assertFalse(self.module().first_step(stack.world)[0])
        stack = self.stack(query=self.answers(["Gateway.Api"]))
        stack.world.alerts.state = lambda name: "firing"
        ok, detail = self.module().first_step(stack.world)
        self.assertFalse(ok)
        self.assertIn("ErrorRateService", detail)

    def test_the_service_first_step_needs_a_service_behind_the_gateway(self):
        stack = self.stack(query=self.answers(["Gateway.Api"]))
        stack.world.alerts.state = lambda name: "firing"
        self.assertFalse(self.module().NEXT.first_step(stack.world)[0])
        stack = self.stack(query=self.answers(["Gateway.Api", "Ordering.Api"]))
        stack.world.alerts.state = lambda name: "firing"
        ok, detail = self.module().NEXT.first_step(stack.world)
        self.assertTrue(ok, detail)
        self.assertIn("Ordering.Api", detail)

    def test_the_gateway_phase_restores_only_ordering_api(self):
        stack = self.stack()
        self.module().restore(stack.world)
        self.assertEqual([("start", "ordering-api")], stack.calls)

    def test_the_service_phase_starts_sql_and_waits_for_a_login_before_it_returns(self):
        stack = self.stack(sql_results=["1"])
        with mock.patch.object(harness, "wait_until", instant):
            self.module().NEXT.restore(stack.world)
        self.assertEqual(("start", "sql"), stack.calls[0])
        self.assertEqual(("sql", "master", "SELECT 1;"), stack.calls[1])

    def test_sql_not_yet_answering_keeps_the_restore_waiting(self):
        stack = self.stack()
        stack.world.compose.exec_sql = mock.Mock(side_effect=harness.GameDayError("login failed"))
        ok, observed = self.module()._sql_answers(stack.world)()
        self.assertFalse(ok)
        self.assertIn("login failed", observed)

    def test_the_service_phase_settles_only_when_the_gateway_is_quiet_too(self):
        stack = self.stack()
        for service, gateway, expected in (("inactive", "inactive", True), ("inactive", "firing", False),
                                           ("firing", "inactive", False)):
            stack.world.alerts.state = {"ErrorRateService": service, "ErrorRateGateway": gateway}.get
            self.assertEqual(expected, self.module().NEXT.settled(stack.world)[0])

    def test_the_deadlines_and_the_settle_are_the_rules(self):
        module = self.module()
        self.assertEqual(300, module.DEADLINE.for_seconds)
        self.assertEqual(300, module.SETTLE.signal_seconds)
        self.assertEqual(module.DEADLINE, module.NEXT.DEADLINE)
        self.assertGreater(module.TRAFFIC_SECONDS, module.WARM_SECONDS + module.DEADLINE.seconds
                           + module.SETTLE.seconds)


class Latency(unittest.TestCase):
    def module(self):
        from scenarios import latency
        return latency

    def stack(self, query=None):
        stack = Stack(query=query)
        stack.world.traffic = Recorder(stack.calls)
        return stack

    def test_the_queries_are_the_runbooks_and_the_rules(self):
        module = self.module()
        self.assertIn(squash(module.FIRST_QUERY), squash(runbook("latency.md")))
        self.assertIn(squash(module.SERVICE_QUERY) + ">1", squash(RULES))

    def test_it_reads_a_quiet_quantile_before_it_pauses_sql(self):
        stack = self.stack(query=lambda expression: [{"metric": {"service_name": "Ordering.Api"}, "value": [1, "0.2"]}])
        with mock.patch.object(harness, "wait_until", instant):
            self.module().cause(stack.world)
        self.assertEqual([("traffic", self.module().TRAFFIC_SECONDS), ("pause", "sql")], stack.calls)

    def test_a_quantile_that_is_already_over_a_second_or_not_there_is_not_a_quiet_start(self):
        for rows in ([{"metric": {"service_name": "Ordering.Api"}, "value": [1, "1.4"]}],
                     [{"metric": {"service_name": "Ordering.Api"}, "value": [1, "NaN"]}],
                     [{"metric": {"service_name": "Catalog.Api"}, "value": [1, "0.1"]}], []):
            with self.subTest(rows):
                stack = self.stack(query=lambda expression: rows)
                with mock.patch.object(harness, "wait_until", instant), self.assertRaises(harness.Timeout):
                    self.module().cause(stack.world)
                self.assertNotIn("pause", stack.verbs())

    def answers(self, slow):
        return lambda expression: [
            {"metric": {"service_name": name, "http_route": route}, "value": [1, value]} for name, route, value in slow]

    def test_the_first_step_names_which_shape_the_answer_has(self):
        together = self.stack(query=self.answers([("Ordering.Api", "/a", "29.0"), ("Catalog.Api", "/b", "12.0")]))
        ok, detail = self.module().first_step(together.world)
        self.assertTrue(ok, detail)
        self.assertIn("everything slow together", detail)
        one = self.stack(query=self.answers([("Ordering.Api", "/a", "29.0"), ("Catalog.Api", "/b", "0.05")]))
        ok, detail = self.module().first_step(one.world)
        self.assertTrue(ok, detail)
        self.assertIn("one service slow", detail)

    def test_the_first_step_fails_when_no_route_reads_over_a_second(self):
        for slow in ([], [("Ordering.Api", "/a", "0.4")], [("Ordering.Api", "/a", "NaN")]):
            with self.subTest(slow):
                self.assertFalse(self.module().first_step(self.stack(query=self.answers(slow)).world)[0])

    def test_restore_unpauses_sql_and_is_safe_twice(self):
        stack = self.stack()
        self.module().restore(stack.world)
        self.module().restore(stack.world)
        self.assertEqual([("unpause", "sql")] * 2, stack.calls)

    def test_the_deadline_carries_the_for_and_the_settle_outlasts_the_window(self):
        module = self.module()
        self.assertEqual(600, module.DEADLINE.for_seconds)
        self.assertEqual(600, module.SETTLE.signal_seconds)
        self.assertGreater(module.TRAFFIC_SECONDS, module.WARM_SECONDS + module.DEADLINE.seconds
                           + module.SETTLE.seconds)


class OutboxGrowth(unittest.TestCase):
    def module(self):
        from scenarios import outbox_growth
        return outbox_growth

    def stack(self, query=None, sql_results=("",)):
        stack = Stack(query=query, sql_results=sql_results)
        stack.world.traffic = Recorder(stack.calls)
        return stack

    def test_the_queries_and_the_sql_are_the_runbooks(self):
        module = self.module()
        text = squash(runbook("outbox-growth.md"))
        for query in (module._AGE, module._COUNT, module._DERIV, module._PURGE):
            self.assertIn(squash(query), text)

    def test_the_first_batch_is_over_the_rules_threshold_and_only_over_it_by_a_margin(self):
        threshold = int(re.search(r"alert: OutboxGrowth\n.*?\) > (\d+)\n", RULES, re.S).group(1))
        self.assertGreater(self.module().FIRST_BATCH, threshold)
        self.assertLess(self.module().FIRST_BATCH, threshold * 2, "a backlog the restore has to delete")

    def test_the_planted_rows_are_a_type_of_their_own_and_not_the_abandoned_scripts(self):
        from scenarios import outbox_abandoned
        module = self.module()
        self.assertNotEqual(module.MESSAGE_TYPE, "GameDay.Synthetic")
        self.assertNotIn(outbox_abandoned.MESSAGE_ID[:13], module._PLANT)
        self.assertIn(f"MessageType = N'{module.MESSAGE_TYPE}'", module._REMOVE)
        self.assertNotIn("DELETE FROM ordering.OutboxMessages;", module._REMOVE)

    def test_it_stops_the_broker_before_it_plants_so_no_row_is_delivered(self):
        stack = self.stack()
        module = self.module()
        module.cause(stack.world)
        try:
            self.assertEqual(("stop", "rabbitmq"), stack.calls[0])
            self.assertEqual("sql", stack.calls[1][0])
            self.assertIn(f"TOP ({module.FIRST_BATCH})", stack.calls[1][2])
            self.assertIn(module.MESSAGE_TYPE, stack.calls[1][2])
        finally:
            module.restore(stack.world)

    def test_each_batch_takes_ids_after_the_last_so_none_collides(self):
        module = self.module()
        stack = self.stack()
        top_up = module._TopUp()
        top_up.plant(stack.world, 100)
        top_up.plant(stack.world, 20)
        self.assertIn("+ 0 AS i", stack.calls[0][2])
        self.assertIn("+ 100 AS i", stack.calls[1][2])
        self.assertEqual(120, top_up.planted)

    def test_the_top_up_keeps_planting_until_halted_and_records_a_failure_without_dying_silently(self):
        module = self.module()
        stack = self.stack()
        top_up = module._TopUp()
        with mock.patch.object(module, "TOP_UP_SECONDS", 0.001):
            thread = threading.Thread(target=top_up.run, args=(stack.world,), daemon=True)
            thread.start()
            deadline = time.monotonic() + 5
            while top_up.planted < module.TOP_UP_ROWS * 3 and time.monotonic() < deadline:
                time.sleep(0.005)
            top_up.halt.set()
            thread.join(5)
        self.assertGreaterEqual(top_up.planted, module.TOP_UP_ROWS * 3)
        failing = module._TopUp()
        stack.world.compose.exec_sql = mock.Mock(side_effect=harness.GameDayError("sql down"))
        with mock.patch.object(module, "TOP_UP_SECONDS", 0.001):
            failing.run(stack.world)
        self.assertEqual("sql down", failing.failure)

    def rows(self, count="1500", slope="0.4", age="900"):
        def query(expression):
            if "deriv" in expression:
                return [{"metric": {"service_name": "Ordering.Api"}, "value": [1, slope]}]
            value = age if "oldest_age" in expression else count
            return [{"metric": {"service_name": "Ordering.Api", "lane": "Broker"}, "value": [1, value]}]
        return query

    def first_step(self, query, companion="firing", failure=None):
        module = self.module()
        stack = self.stack(query=query, sql_results=["Pending|Processed|Oldest\n-------|---------|------\n1500|0|2026"])
        stack.world.alerts.state = lambda name: companion
        module._top_up = module._TopUp()
        module._top_up.failure = failure
        return module.first_step(stack.world)

    def test_the_first_step_passes_and_says_the_age_gauge_sends_the_reader_to_the_broker_runbook(self):
        ok, detail = self.first_step(self.rows())
        self.assertTrue(ok, detail)
        self.assertIn("outbox-broker.md", detail)
        self.assertIn("1500", detail)

    def test_the_first_step_fails_on_a_companion_that_is_quiet_a_count_under_the_rule_or_a_falling_one(self):
        self.assertFalse(self.first_step(self.rows(), companion="inactive")[0])
        self.assertFalse(self.first_step(self.rows(count="900"))[0])
        self.assertFalse(self.first_step(self.rows(slope="-0.1"))[0])
        self.assertFalse(self.first_step(lambda expression: [])[0])
        self.assertFalse(self.first_step(self.rows(), failure="sql down")[0])

    def test_restore_halts_the_top_up_then_deletes_the_rows_then_starts_the_broker(self):
        module = self.module()
        stack = self.stack()
        module._top_up = module._TopUp()
        module.restore(stack.world)
        module.restore(stack.world)
        self.assertEqual("sql", stack.calls[0][0])
        self.assertIn("DELETE", stack.calls[0][2])
        self.assertEqual(("start", "rabbitmq"), stack.calls[1])
        self.assertTrue(module._top_up.halt.is_set())

    def test_it_settles_only_when_both_alerts_have_resolved(self):
        module = self.module()
        stack = self.stack()
        for growth, companion, expected in (("inactive", "inactive", True), ("inactive", "firing", False),
                                            ("firing", "inactive", False)):
            stack.world.alerts.state = {"OutboxGrowth": growth, "OutboxBrokerLaneStalled": companion}.get
            self.assertEqual(expected, module.settled(stack.world)[0])

    def test_the_deadline_carries_the_for(self):
        self.assertEqual(600, self.module().DEADLINE.for_seconds)


if __name__ == "__main__":
    unittest.main()
