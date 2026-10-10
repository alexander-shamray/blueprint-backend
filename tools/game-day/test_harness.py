"""The harness and the runner, against doubles. Nothing here needs Docker.

    cd tools/game-day && py -3.12 -m unittest
"""

import json
import re
import subprocess
import time
import types
import unittest

import game_day
import harness


class Clock:
    """A clock that only moves when the code under test sleeps."""

    def __init__(self):
        self.now = 0.0

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class WaitUntil(unittest.TestCase):
    def test_returns_the_seconds_it_took(self):
        clock = Clock()
        took = harness.wait_until(lambda: (clock.now >= 10, "x"), 60, "x", clock=clock, sleep=clock.sleep, poll=2)
        self.assertEqual(10, took)

    def test_times_out_naming_what_it_last_saw(self):
        clock = Clock()
        with self.assertRaises(harness.Timeout) as caught:
            harness.wait_until(lambda: (False, "A is pending"), 30, "A firing", clock=clock, sleep=clock.sleep)
        self.assertIn("A firing: not within 30s; last saw A is pending", str(caught.exception))

    def test_asks_once_more_at_the_deadline(self):
        clock = Clock()
        took = harness.wait_until(lambda: (clock.now >= 30, "x"), 30, "x", clock=clock, sleep=clock.sleep, poll=7)
        self.assertEqual(30, took)

    def test_never_sleeps_past_the_deadline(self):
        clock = Clock()
        with self.assertRaises(harness.Timeout):
            harness.wait_until(lambda: (False, "x"), 10, "x", clock=clock, sleep=clock.sleep, poll=7)
        self.assertEqual(10, clock.now)


class Deadlines(unittest.TestCase):
    def test_the_sum_is_every_term_and_says_so(self):
        deadline = harness.Deadline(signal_seconds=120, for_seconds=600)
        self.assertEqual(120 + 600 + harness.EXPORT_INTERVAL_SECONDS + 2 * harness.EVALUATION_INTERVAL_SECONDS,
                         deadline.seconds)
        self.assertIn(f"= {deadline.seconds}s", deadline.derivation)

    def test_an_alert_is_reported_one_evaluation_after_the_one_that_saw_it(self):
        deadline = harness.Deadline(signal_seconds=0)
        self.assertEqual(harness.EXPORT_INTERVAL_SECONDS + harness.REPORT_LAG_SECONDS
                         + harness.EVALUATION_INTERVAL_SECONDS, deadline.seconds)
        self.assertEqual(harness.EVALUATION_INTERVAL_SECONDS, harness.REPORT_LAG_SECONDS)
        self.assertIn("reporting", deadline.derivation)

    def test_the_export_interval_is_still_the_sdk_default(self):
        """If something sets it, the constant is a guess; this fails so the deadline is re-derived."""
        for tree in ("src", "deploy/compose", "deploy/helm"):
            for path in (harness.ROOT / tree).rglob("*"):
                if path.suffix in {".cs", ".yml", ".yaml", ".json"} and path.is_file():
                    self.assertNotIn("OTEL_METRIC_EXPORT_INTERVAL", path.read_text(encoding="utf-8", errors="ignore"),
                                     str(path))


class Route(unittest.TestCase):
    def test_it_is_walk_an_order_sh_s_route(self):
        script = (harness.ROOT / "deploy" / "compose" / "walk-an-order.sh").read_text(encoding="utf-8")
        for expected in (
                'gateway=${GATEWAY:-http://localhost:5000}', 'keycloak=${KEYCLOAK:-http://localhost:8080}',
                f'product=${{PRODUCT:-{harness.PRODUCT}}}', "/realms/commerce/protocol/openid-connect/token",
                "client_id=web-app", "username=demo", "/api/v1/orders"):
            self.assertIn(expected, script)
        self.assertEqual("http://localhost:5000", harness.GATEWAY)
        self.assertEqual("http://localhost:8080", harness.KEYCLOAK)

    def test_the_order_is_the_scripts_body(self):
        calls = []

        def send(method, url, headers, body):
            calls.append((method, url, headers, body))
            if url.endswith("/token"):
                return 200, json.dumps({"access_token": "tok"})
            return 200, '"11111111-1111-1111-1111-111111111111"'

        order = harness.Orders(send).place()
        self.assertEqual("11111111-1111-1111-1111-111111111111", order)
        token_call, order_call = calls
        self.assertIn(b"username=demo", token_call[3])
        self.assertEqual("Bearer tok", order_call[2]["Authorization"])
        body = json.loads(order_call[3])
        self.assertEqual({"commandId", "items", "shippingAddress", "currency"}, set(body))
        self.assertEqual("KZ", body["shippingAddress"]["country"])

    def test_a_refusal_is_an_error_and_not_an_id(self):
        def send(method, url, headers, body):
            return (200, json.dumps({"access_token": "t"})) if url.endswith("/token") else (400, '{"title":"no"}')

        with self.assertRaises(harness.GameDayError):
            harness.Orders(send).place()

    def test_no_token_is_an_error(self):
        with self.assertRaises(harness.GameDayError):
            harness.Orders(lambda *a: (401, "no")).token()


class AlertState(unittest.TestCase):
    def alerts(self, payload, status=200):
        seen = []

        def send(method, url, headers, body):
            seen.append(url)
            return status, json.dumps(payload)

        return harness.Alerts(send, "http://g"), seen

    def test_firing_beats_pending_and_the_name_must_match(self):
        alerts, seen = self.alerts({"data": {"alerts": [
            {"state": "pending", "labels": {"alertname": "A"}},
            {"state": "firing", "labels": {"alertname": "A"}},
            {"state": "firing", "labels": {"alertname": "B"}}]}})
        self.assertEqual("firing", alerts.state("A"))
        self.assertEqual("inactive", alerts.state("C"))
        self.assertEqual("http://g/api/datasources/proxy/uid/prometheus/api/v1/alerts", seen[0])

    def test_pending_is_reported(self):
        alerts, _ = self.alerts({"data": {"alerts": [{"state": "pending", "labels": {"alertname": "A"}}]}})
        self.assertEqual("pending", alerts.state("A"))

    def test_an_unreachable_prometheus_is_an_error_and_never_inactive(self):
        alerts, _ = self.alerts({}, status=502)
        with self.assertRaises(harness.GameDayError):
            alerts.state("A")

    def test_loaded_reads_the_rule_groups(self):
        alerts, _ = self.alerts({"data": {"groups": [{"rules": [{"name": "A"}]}]}})
        self.assertTrue(alerts.loaded("A"))
        self.assertFalse(alerts.loaded("B"))


class ComposeArgv(unittest.TestCase):
    def compose(self, returncode=0, stdout=""):
        calls = []

        def run(argv, **kwargs):
            calls.append((argv, kwargs))
            return subprocess.CompletedProcess(argv, returncode, stdout, "")

        return harness.Compose(run), calls

    def test_each_control_is_one_compose_verb_against_the_compose_file(self):
        compose, calls = self.compose()
        compose.stop("rabbitmq")
        compose.start("rabbitmq")
        compose.pause("sql")
        compose.unpause("sql")
        self.assertEqual([["stop", "rabbitmq"], ["start", "rabbitmq"], ["pause", "sql"], ["unpause", "sql"]],
                         [argv[4:] for argv, _ in calls])
        self.assertTrue(all(argv[:3] == ["docker", "compose", "-f"] and argv[3].endswith("docker-compose.yml")
                            for argv, _ in calls))

    def test_output_is_decoded_as_utf8_and_never_the_ansi_page(self):
        compose, calls = self.compose()
        compose.logs("ordering-api")
        self.assertEqual("utf-8", calls[0][1]["encoding"])

    def test_sql_goes_in_on_stdin_and_the_password_is_on_no_argv(self):
        compose, calls = self.compose(stdout="  1\n")
        self.assertEqual("1", compose.exec_sql("SELECT 1", "Ordering"))
        argv, kwargs = calls[0]
        self.assertEqual("SELECT 1", kwargs["input"])
        self.assertNotIn("Local_Dev_Pa55w0rd!", " ".join(argv))
        self.assertIn("$MSSQL_SA_PASSWORD", " ".join(argv))

    def test_a_failing_command_raises_and_unpause_alone_tolerates_it(self):
        compose, _ = self.compose(returncode=1)
        with self.assertRaises(harness.GameDayError):
            compose.stop("rabbitmq")
        compose.unpause("sql")

    def test_redis_goes_through_redis_cli_in_the_named_service(self):
        compose, calls = self.compose(stdout="PONG\n")
        self.assertEqual("PONG", compose.exec_redis("redis-cache", "ping"))
        self.assertEqual(["exec", "-T", "redis-cache", "redis-cli", "ping"], calls[0][0][4:])


class Fake:
    """A world whose every call is recorded, and whose alert follows a script."""

    def __init__(self, states, *, step=(True, "ok"), cause_error=None, restore_error=None, loaded=True):
        self.log = []
        self.states = list(states)
        self.step = step
        self.cause_error = cause_error
        self.restore_error = restore_error
        self.loaded = loaded
        self.clock = Clock()
        outer = self

        class Alerts:
            def state(self, name):
                outer.log.append("state")
                return outer.states.pop(0) if len(outer.states) > 1 else outer.states[0]

            def loaded(self, name):
                return outer.loaded

        self.world = harness.World(None, Alerts(), None, None, lambda message: self.log.append(f"say: {message}"))

        def cause(world):
            self.log.append("cause")
            if self.cause_error:
                raise self.cause_error

        def restore(world):
            self.log.append("restore")
            if self.restore_error:
                raise self.restore_error

        def first_step(world):
            self.log.append("first_step")
            return self.step

        def settled(world):
            state = world.alerts.state("A")
            return state == "inactive", state

        self.scenario = types.SimpleNamespace(
            ALERT="A", RUNBOOK="a.md", DEADLINE=harness.Deadline(signal_seconds=30), cause=cause,
            first_step=first_step, restore=restore, settled=settled)

    def run(self):
        return game_day.run(self.scenario, self.world, clock=self.clock, sleep=self.clock.sleep)

    def actions(self):
        return [entry for entry in self.log if not entry.startswith(("state", "say"))]


class Runner(unittest.TestCase):
    def test_a_quiet_start_a_firing_alert_a_working_step_and_a_restore_is_a_pass(self):
        fake = Fake(["inactive", "inactive", "pending", "firing", "firing", "inactive"])
        self.assertEqual([], fake.run())
        self.assertEqual(["cause", "first_step", "restore"], fake.actions())

    def test_an_alert_that_never_fires_is_a_finding_and_still_runs_the_step_and_the_restore(self):
        fake = Fake(["inactive"])
        findings = fake.run()
        self.assertEqual(1, len(findings))
        self.assertIn("the alert did not fire", findings[0])
        self.assertEqual(["cause", "first_step", "restore"], fake.actions())

    def test_a_first_step_that_fails_is_a_finding(self):
        fake = Fake(["inactive", "firing", "firing", "inactive"], step=(False, "no log line"))
        findings = fake.run()
        self.assertEqual(1, len(findings))
        self.assertIn("did not work as written: no log line", findings[0])

    def test_a_cause_that_raises_still_restores(self):
        fake = Fake(["inactive"], cause_error=harness.GameDayError("docker stop exited 1"))
        findings = fake.run()
        self.assertIn("docker stop exited 1", findings[0])
        self.assertEqual(["cause", "restore"], fake.actions())

    def test_a_cause_that_raises_something_unexpected_still_restores_and_is_not_swallowed(self):
        fake = Fake(["inactive"], cause_error=KeyError("boom"))
        with self.assertRaises(KeyError):
            fake.run()
        self.assertEqual(["cause", "restore"], fake.actions())

    def test_a_restore_that_does_not_settle_is_a_finding_naming_the_poison(self):
        fake = Fake(["inactive", "firing", "firing"])
        findings = fake.run()
        self.assertTrue(any("restore did not settle" in finding for finding in findings), findings)

    def test_a_restore_that_raises_is_a_finding(self):
        fake = Fake(["inactive", "firing", "firing", "inactive"], restore_error=harness.GameDayError("start failed"))
        self.assertTrue(any("start failed" in finding for finding in fake.run()))

    def test_an_alert_already_firing_refuses_before_any_cause(self):
        fake = Fake(["firing"])
        findings = fake.run()
        self.assertIn("already firing before the cause", findings[0])
        self.assertEqual([], fake.actions())

    def test_a_rule_that_is_not_loaded_refuses_before_any_cause(self):
        fake = Fake(["inactive"], loaded=False)
        self.assertIn("no such rule loaded", fake.run()[0])
        self.assertEqual([], fake.actions())


class LokiQuery(unittest.TestCase):
    def test_it_asks_loki_through_the_proxy_for_the_service_and_the_pattern_and_returns_lines(self):
        seen = []

        def send(method, url, headers, body):
            seen.append(url)
            return 200, json.dumps({"data": {"result": [{"values": [["1", "line a"], ["2", "line b"]]}]}})

        lines = harness.Logs(send, "http://g", lambda: 1000.0).search("Ordering.Api", "Outbox message .* failed", 60)
        self.assertEqual(["line a", "line b"], lines)
        self.assertIn("/api/datasources/proxy/uid/loki/loki/api/v1/query_range?", seen[0])
        self.assertIn("service_name%3D%22Ordering.Api%22", seen[0])

    def test_backslashes_in_a_pattern_survive_logql_string_escaping(self):
        seen = []

        def send(method, url, headers, body):
            seen.append(url)
            return 200, json.dumps({"data": {"result": []}})

        harness.Logs(send, "http://g", lambda: 1000.0).search("S", r"a\.b")
        self.assertIn("a%5C%5C.b", seen[0])

    def test_an_unreachable_loki_is_an_error_and_not_no_lines(self):
        with self.assertRaises(harness.GameDayError):
            harness.Logs(lambda *a: (502, "bad"), "http://g").search("S", "x")


class SinceCause(unittest.TestCase):
    def test_a_first_step_reads_only_this_runs_window(self):
        world = harness.World(None, None, None, None, print)
        world.caused_at = time.time() - 100
        self.assertTrue(105 <= world.since_cause() <= 107)


class Cli(unittest.TestCase):
    def test_an_unknown_runbook_is_an_error(self):
        with self.assertRaises(harness.GameDayError):
            game_day.load("stuck-saga")

    def test_a_known_runbook_loads_with_or_without_its_suffix(self):
        self.assertEqual("outbox-broker.md", game_day.load("outbox-broker").RUNBOOK)
        self.assertEqual("outbox-broker.md", game_day.load("outbox-broker.md").RUNBOOK)

    def test_list_prints_every_runbook(self):
        import contextlib
        import io

        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, game_day.main(["--list"]))
        for line in ("outbox-broker.md", "NOT_ON_COMPOSE", "OWED to PR-3"):
            self.assertIn(line, out.getvalue())
        self.assertNotRegex(out.getvalue(), r"coverage:")


class Scenarios(unittest.TestCase):
    """The scenarios against a double: what each does to the stack, and what it reads back."""

    def world(self, sql_result="", loki=(), stdout=""):
        calls = []

        class Compose:
            def stop(self, service): calls.append(("stop", service))
            def start(self, service): calls.append(("start", service))
            def logs(self, service, since): return stdout

            def exec_sql(self, sql, database="Ordering"):
                calls.append(("sql", sql.strip().split(chr(10))[0][:60]))
                return sql_result

        class Orders:
            def place(self): return "o-1"

        class Logs:
            def search(self, service_name, pattern, since_seconds=900, limit=20):
                calls.append(("loki", service_name))
                return [line for line in loki if re.search(pattern, line)]

        world = harness.World(Compose(), None, Orders(), Logs(), lambda message: None)
        world.caused_at = time.time() - 100
        return world, calls

    def test_broker_stops_the_broker_and_places_an_order_then_starts_it(self):
        from scenarios import outbox_broker
        world, calls = self.world()
        outbox_broker.cause(world)
        outbox_broker.restore(world)
        self.assertEqual([("stop", "rabbitmq"), ("start", "rabbitmq")], calls)

    def test_broker_first_step_passes_on_a_failed_attempt_line_and_fails_on_a_claim_failure(self):
        from scenarios import outbox_broker
        attempt = "Outbox message 1 on lane Broker failed, attempt 3 of 10."
        self.assertFalse(outbox_broker.first_step(self.world(loki=[attempt.replace("Broker", "Local")])[0])[0],
                         "an earlier scenario's lane must not pass for this one")
        ok, detail = outbox_broker.first_step(self.world(loki=[attempt])[0])
        self.assertTrue(ok)
        self.assertIn("stdout is empty", detail)
        self.assertFalse(outbox_broker.first_step(self.world(loki=[attempt, "Outbox claim failed; retrying"])[0])[0])
        self.assertFalse(outbox_broker.first_step(self.world(loki=["nothing"])[0])[0])
        self.assertNotIn("stdout is empty", outbox_broker.first_step(self.world(loki=[attempt], stdout="x")[0])[1])

    def test_broker_first_step_is_a_finding_when_only_masstransit_speaks(self):
        from scenarios import outbox_broker
        ok, detail = outbox_broker.first_step(self.world(loki=["Retrying 00:00:34: Broker unreachable: ordering-svc"])[0])
        self.assertFalse(ok)
        self.assertIn("not running", detail)
        self.assertIn("1 `Broker unreachable` lines", detail)

    def test_projection_renames_the_table_and_restore_is_guarded_so_it_is_safe_twice(self):
        from scenarios import projection_lag
        world, calls = self.world()
        projection_lag.cause(world)
        projection_lag.restore(world)
        self.assertIn("sp_rename", projection_lag._RENAME)
        self.assertRegex(projection_lag._UNRENAME, r"IF OBJECT_ID.*IS NOT NULL AND OBJECT_ID.*IS NULL")
        self.assertEqual(2, len(calls))

    def test_projection_first_step_needs_the_log_line_and_the_query_to_name_the_table(self):
        from scenarios import projection_lag
        line = "Outbox message 1 on lane Local failed, attempt 1 of 10."
        self.assertTrue(projection_lag.first_step(self.world(
            sql_result="Local|Invalid object name 'ordering.OrderSummaries'", loki=[line])[0])[0])
        self.assertFalse(projection_lag.first_step(self.world(sql_result="", loki=[line])[0])[0])
        self.assertFalse(projection_lag.first_step(self.world(sql_result="x", loki=[])[0])[0])

    def test_abandoned_plants_one_row_at_the_ceiling_and_removes_only_it(self):
        from scenarios import outbox_abandoned
        from scenarios.outbox_abandoned import MESSAGE_ID
        self.assertIn("'Broker'", outbox_abandoned._PLANT)
        self.assertIn(", 10,", outbox_abandoned._PLANT)
        self.assertEqual(f"DELETE FROM ordering.OutboxMessages WHERE MessageId = '{MESSAGE_ID}';",
                         outbox_abandoned._REMOVE)

    def test_the_ceiling_is_the_dispatchers(self):
        source = (harness.ROOT / "src/BuildingBlocks/Common.Infrastructure/Outbox/OutboxDispatcher.cs").read_text(
            encoding="utf-8")
        self.assertRegex(source, r"MaxAttempts = 10;")

    def test_abandoned_first_step_finds_the_planted_row_by_id_on_the_broker_lane(self):
        from scenarios import outbox_abandoned
        row = f"7|{outbox_abandoned.MESSAGE_ID.upper()}|c|GameDay.Synthetic|Broker|10|2026-10-10|planted"
        self.assertTrue(outbox_abandoned.first_step(self.world(sql_result=row)[0])[0])
        self.assertFalse(outbox_abandoned.first_step(self.world(sql_result=row.replace("Broker", "Local"))[0])[0])
        self.assertFalse(outbox_abandoned.first_step(self.world(sql_result="")[0])[0])

    def test_the_log_patterns_are_the_dispatchers_own_templates(self):
        source = (harness.ROOT / "src/BuildingBlocks/Common.Infrastructure/Outbox/OutboxDispatcher.cs").read_text(
            encoding="utf-8")
        self.assertIn("Outbox claim failed; retrying next tick.", source)
        self.assertTrue(re.search(r"Outbox message \{MessageId\} on lane \{Lane\} failed, attempt \{Attempt\} of \{Max\}",
                                  source))


if __name__ == "__main__":
    unittest.main()
