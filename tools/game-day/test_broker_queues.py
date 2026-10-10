"""The broker-queue runbooks: error-queue, skipped-queue and queue-backlog's backlog half, against doubles.

    cd tools/game-day && py -3.12 -m unittest

The causes publish as dead-letter-operator and the first steps run tools/dead-letters as that account, so the cases
read the account's grant, the endpoints and the contract from where they are declared rather than copying them.
"""

import base64
import json
import os
import re
import subprocess
import unittest
from types import SimpleNamespace

import harness
from scenarios import error_queue, queue_backlog, skipped_queue

SRC = harness.ROOT / "src"
DEFINITIONS = harness.ROOT / "deploy" / "compose" / "rabbitmq" / "definitions.json"


def constant(path, name):
    return re.search(rf'const string {name} = "([^"]+)"', (SRC / path).read_text(encoding="utf-8")).group(1)


class Compose:
    def __init__(self, queues="", running=False):
        self.calls = []
        self.queues = queues
        self.is_running = running

    def rabbitmqctl(self, *args, stdin=None):
        self.calls.append(("rabbitmqctl", args, stdin))
        return self.queues if args[0] == "list_queues" else ""

    def stop(self, service):
        self.calls.append(("stop", service))

    def start(self, service):
        self.calls.append(("start", service))

    def running(self, service):
        return self.is_running


class BrokerCalls(unittest.TestCase):
    def broker(self, compose=None, send=None, run=None):
        return harness.Broker(compose or Compose(), send=send, base="http://broker:15672", run=run,
                              python="py", secret=lambda: "s3cret")

    def test_the_password_goes_in_on_stdin_and_is_cleared_after(self):
        compose = Compose()
        broker = self.broker(compose)
        broker.open()
        broker.close()
        self.assertEqual([("rabbitmqctl", ("change_password", harness.OPERATOR), "s3cret"),
                          ("rabbitmqctl", ("clear_password", harness.OPERATOR), None)], compose.calls)

    def test_a_publish_is_masstransits_envelope_to_the_endpoints_exchange_as_the_operator(self):
        sent = []

        def send(method, url, headers, body):
            sent.append((method, url, headers, json.loads(body)))
            return 200, '{"routed": true}'

        broker = self.broker(send=send)
        broker.open()
        message_id = broker.publish("ordering-commands", "urn:message:A:B", {"x": 1})
        method, url, headers, body = sent[0]
        self.assertEqual(("POST", "http://broker:15672/api/exchanges/%2F/ordering-commands/publish"), (method, url))
        self.assertEqual(f"{harness.OPERATOR}:s3cret", base64.b64decode(headers["Authorization"][6:]).decode())
        envelope = json.loads(body["payload"])
        self.assertEqual(harness.MASSTRANSIT_JSON, body["properties"]["content_type"])
        self.assertEqual(message_id, body["properties"]["message_id"])
        self.assertEqual(message_id, envelope["messageId"])
        self.assertEqual(envelope["correlationId"], body["properties"]["correlation_id"])
        self.assertEqual((["urn:message:A:B"], {"x": 1}), (envelope["messageType"], envelope["message"]))

    def test_an_unrouted_publish_is_an_error(self):
        broker = self.broker(send=lambda *_: (200, '{"routed": false}'))
        with self.assertRaises(harness.GameDayError):
            broker.publish("nowhere", "urn:message:A:B", {})

    def tool_run(self, document, returncode=0):
        calls = []

        def run(argv, **kwargs):
            calls.append((argv, kwargs))
            return subprocess.CompletedProcess(argv, returncode, json.dumps(document), "")

        return run, calls

    def test_the_tool_runs_as_the_runbooks_run_it_with_the_password_in_its_environment(self):
        run, calls = self.tool_run({"schema": 1, "queues": []})
        broker = self.broker(run=run)
        broker.open()
        os.environ["DEAD_LETTERS_CREDENTIALS"] = "elsewhere"
        try:
            self.assertEqual((0, {"schema": 1, "queues": []}), broker.tool("list"))
        finally:
            del os.environ["DEAD_LETTERS_CREDENTIALS"]
        argv, kwargs = calls[0]
        self.assertEqual(["py", str(harness.DEAD_LETTERS), "list", "--json"], argv)
        self.assertNotIn("s3cret", " ".join(argv))
        self.assertEqual(("s3cret", "http://broker:15672"),
                         (kwargs["env"]["DEAD_LETTERS_PASSWORD"], kwargs["env"]["DEAD_LETTERS_URL"]))
        self.assertNotIn("DEAD_LETTERS_CREDENTIALS", kwargs["env"])
        self.assertEqual("utf-8", kwargs["encoding"])

    def test_a_tool_that_prints_no_json_is_an_error(self):
        broker = self.broker(run=lambda argv, **_: subprocess.CompletedProcess(argv, 2, "", "boom"))
        with self.assertRaises(harness.GameDayError):
            broker.tool("list")

    def test_a_discard_names_each_id_keeps_a_record_and_removes_it_after(self):
        run, calls = self.tool_run({"actions": [{"message_id": "a", "action": "discarded"}], "not_found": []})
        self.assertEqual(["a"], self.broker(run=run).discard("q_error", ["a"]))
        argv = calls[0][0]
        self.assertEqual(["discard", "q_error", "--message-id", "a", "--execute", "--record"], argv[2:8])
        self.assertFalse(os.path.exists(argv[8]))

    def test_a_discard_of_an_id_already_gone_is_not_an_error_so_a_restore_runs_twice(self):
        run, _ = self.tool_run({"actions": [], "not_found": ["a"]}, returncode=1)
        self.assertEqual([], self.broker(run=run).discard("q_error", ["a"]))

    def test_a_discard_that_fails_or_misses_an_id_is_an_error(self):
        for document in ({"actions": [{"message_id": "a", "action": "failed"}], "not_found": []},
                         {"actions": [], "not_found": []}, {"error": "no credential"}):
            with self.subTest(document):
                run, _ = self.tool_run(document, returncode=1)
                with self.assertRaises(harness.GameDayError):
                    self.broker(run=run).discard("q_error", ["a"])

    def test_nothing_to_discard_runs_nothing(self):
        self.assertEqual([], self.broker(run=lambda *_, **__: self.fail("ran")).discard("q_error", []))

    def test_queues_are_read_through_the_container(self):
        compose = Compose("bff-order-events\t1120\t0\nordering-commands\t0\t1\n")
        self.assertEqual({"bff-order-events": (1120, 0), "ordering-commands": (0, 1)},
                         self.broker(compose).queues())


class Grants(unittest.TestCase):
    """Each cause publishes as dead-letter-operator, so its grant must cover where each sends."""

    def test_the_operator_may_write_to_each_exchange_a_cause_sends_to(self):
        definitions = json.loads(DEFINITIONS.read_text(encoding="utf-8"))
        write = next(p["write"] for p in definitions["permissions"] if p["user"] == harness.OPERATOR)
        for exchange in (error_queue.ENDPOINT, skipped_queue.ENDPOINT, queue_backlog.QUEUE):
            with self.subTest(exchange):
                self.assertRegex(exchange, write)

    def test_the_endpoints_are_the_services_own(self):
        ordering = "Services/Ordering/Ordering.Infrastructure/Messaging/DependencyInjection.cs"
        self.assertEqual(constant(ordering, "CommandsQueue"), error_queue.ENDPOINT)
        self.assertEqual(constant(ordering, "StockEventsQueue"), skipped_queue.ENDPOINT)
        self.assertEqual(constant("BFF/Web.Bff/Messaging/DependencyInjection.cs", "EventsQueue"), queue_backlog.QUEUE)

    def test_the_poison_is_a_cancel_order_whose_reason_is_no_code_the_contract_declares(self):
        commands = (SRC / "BuildingBlocks/Common.Contracts/Ordering/V1/Commands.cs").read_text(encoding="utf-8")
        namespace = re.search(r"namespace ([\w.]+);", commands).group(1)
        self.assertEqual(f"urn:message:{namespace}:CancelOrder", error_queue.MESSAGE_TYPE)
        self.assertIn("record CancelOrder(Guid OrderId, string Reason)", commands)
        self.assertNotIn(f'"{error_queue.POISON_REASON}"', commands)

    def test_the_unrouted_types_are_bound_nowhere(self):
        for message_type in (skipped_queue.MESSAGE_TYPE, queue_backlog.BACKLOG_TYPE):
            name = message_type.rsplit(":", 1)[1]
            with self.subTest(message_type):
                for path in SRC.rglob("*.cs"):
                    self.assertNotRegex(path.read_text(encoding="utf-8"), rf"\b(record|class) {name}\b", str(path))


class Broker:
    """A broker double the scenarios drive: what was published, what the tool was asked, what it answers."""

    def __init__(self, listed=None, inspected=None, queues=None, discard_error=None):
        self.calls = []
        self.listed = listed or {}
        self.inspected = inspected or {}
        self.depths = queues or {}
        self.discard_error = discard_error

    def open(self):
        self.calls.append("open")

    def close(self):
        self.calls.append("close")

    def publish(self, exchange, message_type, message):
        self.calls.append(("publish", exchange, message_type, message))
        return "m1"

    def tool(self, *args):
        self.calls.append(("tool",) + args)
        return 0, self.listed if args[0] == "list" else self.inspected

    def discard(self, queue, ids):
        self.calls.append(("discard", queue, tuple(ids)))
        if self.discard_error:
            raise self.discard_error
        return ids

    def queues(self):
        return self.depths

    def purge(self, queue):
        self.calls.append(("purge", queue))


class Alerts:
    def __init__(self, queues=(), query=None):
        self.queues = queues
        self.answers = query or {}

    def labels(self, name):
        return [{"alertname": name, "queue": queue} for queue in self.queues]

    def state(self, name):
        return "inactive"

    def query(self, expression):
        return self.answers.get(expression, [])


def world(broker, alerts, compose=None):
    return SimpleNamespace(broker=broker, alerts=alerts, compose=compose or Compose(), say=lambda _: None)


class DeadLetterScenarios(unittest.TestCase):
    def message(self, **fields):
        base = {"message_id": "m1", "correlation_id": "c1", "message_type": [error_queue.MESSAGE_TYPE],
                "fault_message": "Unknown cancellation reason", "fault_exception_type": "ContractMappingException",
                "reason": "fault"}
        return {**base, **fields}

    def run_first_step(self, scenario, labels, message):
        broker = Broker(listed={"queues": [{"name": scenario.QUEUE, "messages": 1}]},
                        inspected={"messages": [message]})
        scenario.cause(world(broker, Alerts()))
        return scenario.first_step(world(broker, Alerts(labels))), broker

    def test_each_cause_opens_the_account_then_sends_one_message_to_its_endpoint(self):
        for scenario, message_type in ((error_queue, error_queue.MESSAGE_TYPE),
                                       (skipped_queue, skipped_queue.MESSAGE_TYPE)):
            with self.subTest(scenario.RUNBOOK):
                broker = Broker()
                scenario.cause(world(broker, Alerts()))
                self.assertEqual("open", broker.calls[0])
                self.assertEqual(("publish", scenario.ENDPOINT, message_type), broker.calls[1][:3])

    def test_the_poison_carries_the_unknown_reason(self):
        broker = Broker()
        error_queue.cause(world(broker, Alerts()))
        self.assertEqual(error_queue.POISON_REASON, broker.calls[1][3]["reason"])

    def test_the_first_step_runs_list_then_inspect_and_passes_on_the_runbooks_fields(self):
        (ok, detail), broker = self.run_first_step(error_queue, [error_queue.QUEUE], self.message())
        self.assertTrue(ok, detail)
        self.assertEqual([("tool", "list"), ("tool", "inspect", error_queue.QUEUE, "--limit", "5")], broker.calls[2:])

    def test_the_first_step_fails_when_the_alert_names_another_queue(self):
        (ok, detail), _ = self.run_first_step(error_queue, ["other_error"], self.message())
        self.assertFalse(ok)
        self.assertIn("queue label", detail)

    def test_the_first_step_fails_when_a_field_the_runbook_needs_is_missing(self):
        (ok, detail), _ = self.run_first_step(error_queue, [error_queue.QUEUE], self.message(fault_message=None))
        self.assertFalse(ok)
        self.assertIn("fault_message", detail)

    def test_the_first_step_fails_when_inspect_does_not_return_the_message_sent(self):
        (ok, _), _ = self.run_first_step(error_queue, [error_queue.QUEUE], self.message(message_id="other"))
        self.assertFalse(ok)

    def test_skipped_passes_on_the_type_in_the_body_and_fails_without_it(self):
        good = self.message(message_type=[skipped_queue.MESSAGE_TYPE], fault_message=None, reason="dead-letter")
        (ok, detail), _ = self.run_first_step(skipped_queue, [skipped_queue.QUEUE], good)
        self.assertTrue(ok, detail)
        (ok, _), _ = self.run_first_step(skipped_queue, [skipped_queue.QUEUE], self.message(message_type=None))
        self.assertFalse(ok)

    def test_the_restore_discards_what_the_cause_sent_and_clears_the_password_even_when_the_discard_fails(self):
        for scenario in (error_queue, skipped_queue):
            with self.subTest(scenario.RUNBOOK):
                broker = Broker(discard_error=harness.GameDayError("refused"))
                scenario.cause(world(broker, Alerts()))
                with self.assertRaises(harness.GameDayError):
                    scenario.restore(world(broker, Alerts()))
                self.assertEqual(["open", ("discard", scenario.QUEUE, ("m1",)), "close"], broker.calls[2:])

    def test_settled_needs_the_alert_quiet_and_the_queue_empty(self):
        ok, observed = error_queue.settled(world(Broker(queues={error_queue.QUEUE: (1, 0)}), Alerts()))
        self.assertFalse(ok)
        self.assertIn("holds 1", observed)
        self.assertTrue(error_queue.settled(world(Broker(queues={}), Alerts()))[0])


class Backlog(unittest.TestCase):
    def setUp(self):
        self.backlog = queue_backlog.NEXT

    def first_step(self, consumers, running, depth=1200.0):
        depth_rows = [{"metric": {"queue": queue_backlog.QUEUE}, "value": [0, str(depth)]}]
        alerts = Alerts([queue_backlog.QUEUE], {queue_backlog.DEPTH: depth_rows})
        broker = Broker(queues={queue_backlog.QUEUE: (int(depth), consumers)})
        return self.backlog.first_step(world(broker, alerts, Compose(running=running)))

    def test_it_is_the_second_phase_and_its_rule_is_the_backlogs(self):
        self.assertEqual("QueueBacklogGrowing", self.backlog.ALERT)
        self.assertEqual(queue_backlog.RUNBOOK, self.backlog.RUNBOOK)
        self.assertGreater(queue_backlog.FIRST_BATCH, 1000)

    def test_the_cause_stops_the_consumer_before_it_sends_the_first_batch(self):
        broker, compose = Broker(), Compose()
        try:
            self.backlog.cause(world(broker, Alerts(), compose))
        finally:
            queue_backlog._top_up.halt.set()
        self.assertEqual([("stop", queue_backlog.CONSUMER)], compose.calls)
        self.assertEqual("open", broker.calls[0])
        self.assertEqual(queue_backlog.FIRST_BATCH, sum(1 for call in broker.calls if call[0] == "publish"))

    def test_the_first_step_reads_the_lookalike_as_the_runbook_says_to(self):
        ok, detail = self.first_step(consumers=0, running=False)
        self.assertTrue(ok, detail)
        self.assertIn("lookalike", detail)

    def test_the_first_step_fails_when_the_endpoint_still_has_a_consumer_or_the_depth_is_short(self):
        self.assertFalse(self.first_step(consumers=1, running=True)[0])
        self.assertFalse(self.first_step(consumers=0, running=False, depth=900.0)[0])

    def test_the_restore_purges_before_the_consumer_starts_and_clears_the_password(self):
        broker, compose = Broker(), Compose()
        calls = []
        broker.calls = calls
        compose.calls = calls
        self.backlog.restore(world(broker, Alerts(), compose))
        self.assertEqual([("purge", queue_backlog.QUEUE), ("start", queue_backlog.CONSUMER), "close"], calls)

    def test_the_depth_query_is_the_runbooks(self):
        runbook = (harness.ROOT / "docs" / "runbooks" / "queue-backlog.md").read_text(encoding="utf-8")
        self.assertIn(queue_backlog.DEPTH, runbook)


if __name__ == "__main__":
    unittest.main()
