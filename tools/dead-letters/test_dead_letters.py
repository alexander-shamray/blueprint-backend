"""The dead-letter tool's suite, against an in-memory double of the Management API.

    cd tools/dead-letters && py -3.12 -m unittest
"""

import base64
import copy
import datetime
import io
import json
import tempfile
import unittest
from unittest import mock
import urllib.parse
from pathlib import Path

import dead_letters

ENV = {"DEAD_LETTERS_PASSWORD": "not-a-real-password"}
NOW = datetime.datetime(2026, 10, 7, 9, 30, tzinfo=datetime.timezone.utc)
AUDIT_FIELDS = ["ts", "operator", "broker_user", "vhost", "queue", "message_id", "action", "destination", "reason"]


def message(identity, *, input_address="rabbitmq://rabbitmq/ordering-commands", body=None, headers=None):
    """A message as the Management API's get returns it with `encoding: base64`."""
    payload = body if body is not None else json.dumps({
        "messageId": identity, "messageType": ["urn:message:Common.Contracts.Ordering.V1:ConfirmOrder"],
        "message": {"orderId": "o-1"}})
    fault = {"MT-Fault-Message": "boom", "MT-Fault-ExceptionType": "System.TimeoutException",
             "MT-Fault-RetryCount": 5}
    if input_address:
        fault["MT-Fault-InputAddress"] = input_address
    return {
        "payload_bytes": len(payload.encode()),
        "redelivered": False,
        "exchange": "ordering-commands_error",
        "routing_key": "",
        "message_count": 0,
        "properties": {
            "message_id": identity,
            "correlation_id": f"c-{identity}",
            "delivery_mode": 2,
            "content_type": "application/vnd.masstransit+json",
            "headers": headers if headers is not None else fault,
        },
        "payload": base64.b64encode(payload.encode()).decode(),
        "payload_encoding": "base64",
    }


class FakeApi:
    """The four calls the tool makes, over queues each fed by a fanout exchange of the same name."""

    def __init__(self, queues, unbound=()):
        self.queues = {name: list(messages) for name, messages in queues.items()}
        self.unbound = set(unbound)
        self.calls = []

    def __call__(self, method, path, body):
        self.calls.append((method, path, copy.deepcopy(body)))
        parts = [urllib.parse.unquote(p) for p in path.split("?")[0].split("/")[2:]]
        if parts[0] == "queues" and len(parts) == 2:
            return [{"name": name, "messages": len(held)} for name, held in self.queues.items()]
        if parts[0] == "queues" and parts[3] == "get":
            held = self.queues[parts[2]]
            got = copy.deepcopy(held[: body["count"]])
            if body["ackmode"] == "ack_requeue_false":
                del held[: body["count"]]
            else:
                for kept in held[: body["count"]]:
                    kept["redelivered"] = True
            return got
        if parts[0] == "exchanges" and parts[3] == "publish":
            name = parts[2]
            if name in self.unbound:
                return {"routed": False}
            if name not in self.queues:
                raise dead_letters.Refused(f"POST {path}: HTTP 404 not_found")
            self.queues[name].append({
                "redelivered": False, "exchange": name, "routing_key": body["routing_key"],
                "properties": copy.deepcopy(body["properties"]),
                "payload": body["payload"], "payload_encoding": body["payload_encoding"]})
            return {"routed": True}
        raise AssertionError(f"the tool made a call outside its four: {method} {path}")

    def destructive(self):
        return [c for c in self.calls if (c[2] or {}).get("ackmode") == "ack_requeue_false"
                or c[1].endswith("/publish")]


def run(api, *argv, env=ENV):
    out, err = io.StringIO(), io.StringIO()
    code = dead_letters.main(list(argv), environ=env, send=api, out=out, err=err, clock=lambda: NOW, who="oncall")
    return code, out.getvalue(), err.getvalue()


def audit_lines(err):
    return [json.loads(line) for line in err.splitlines() if line.startswith("{")]


class ListAndInspect(unittest.TestCase):
    def test_list_names_only_dead_letter_queues_with_their_endpoint(self):
        api = FakeApi({"ordering-commands": [message("a")], "ordering-commands_error": [message("b")],
                       "bff-order-events_skipped": [], "ordering-commands_delay": []})
        code, out, _ = run(api, "list", "--json")
        self.assertEqual(0, code)
        self.assertEqual(
            {"schema": 1, "command": "list", "vhost": "/", "queues": [
                {"name": "bff-order-events_skipped", "kind": "skipped", "endpoint": "bff-order-events", "messages": 0},
                {"name": "ordering-commands_error", "kind": "error", "endpoint": "ordering-commands", "messages": 1},
            ]},
            json.loads(out))

    def test_inspect_reads_with_a_requeueing_get_and_reports_the_runbooks_fields(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")]})
        code, out, err = run(api, "inspect", "ordering-commands_error", "--json")
        self.assertEqual(0, code)
        self.assertEqual([], api.destructive())
        self.assertEqual("ack_requeue_true", api.calls[-1][2]["ackmode"])
        self.assertEqual(1, len(api.queues["ordering-commands_error"]))
        document = json.loads(out)
        self.assertEqual(["schema", "command", "vhost", "queue", "kind", "endpoint", "messages"], list(document))
        shown = document["messages"][0]
        self.assertEqual("m-1", shown["message_id"])
        self.assertEqual("c-m-1", shown["correlation_id"])
        self.assertEqual(["urn:message:Common.Contracts.Ordering.V1:ConfirmOrder"], shown["message_type"])
        self.assertEqual("boom", shown["fault_message"])
        self.assertEqual("rabbitmq://rabbitmq/ordering-commands", shown["input_address"])
        self.assertEqual("", err)

    def test_a_live_endpoint_queue_is_refused_before_any_read(self):
        api = FakeApi({"ordering-commands": [message("m-1")]})
        code, out, err = run(api, "inspect", "ordering-commands")
        self.assertEqual(2, code)
        self.assertIn("ordering-commands: not a dead-letter queue", err)
        self.assertIn("cannot drain an endpoint's live work", err)
        self.assertEqual([], api.calls)

    def test_a_refusal_under_json_is_a_json_document(self):
        code, out, _ = run(FakeApi({}), "discard", "ordering-commands", "--all", "--json")
        self.assertEqual(2, code)
        document = json.loads(out)
        self.assertEqual({"schema", "command", "error"}, set(document))
        self.assertEqual("discard", document["command"])


class ADryRunIsTheDefault(unittest.TestCase):
    def test_a_replay_without_execute_moves_nothing_and_says_what_it_would_do(self):
        # A record file named without --execute is the near miss the flag exists for.
        api = FakeApi({"ordering-commands_error": [message("m-1")], "ordering-commands": []})
        with tempfile.TemporaryDirectory() as directory:
            code, out, err = run(api, "replay", "ordering-commands_error", "--message-id", "m-1",
                                 "--record", str(Path(directory) / "record.jsonl"))
        self.assertEqual(0, code)
        self.assertIn("would-replay   m-1 -> ordering-commands", out)
        self.assertIn("dry run: nothing left ordering-commands_error. Pass --execute --record FILE to replay", out)
        self.assertEqual([], api.destructive())
        self.assertEqual(1, len(api.queues["ordering-commands_error"]))
        self.assertEqual([], api.queues["ordering-commands"])
        self.assertEqual([], audit_lines(err))

    def test_a_discard_without_execute_moves_nothing(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")]})
        code, out, _ = run(api, "discard", "ordering-commands_error", "--all", "--json")
        self.assertEqual(0, code)
        document = json.loads(out)
        self.assertTrue(document["dry_run"])
        self.assertEqual([{"message_id": "m-1", "action": "would-discard", "destination": None, "reason": None}],
                         document["actions"])
        self.assertEqual([], api.destructive())

    def test_execute_without_a_record_file_is_refused_before_any_call(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")]})
        code, _, err = run(api, "discard", "ordering-commands_error", "--all", "--execute")
        self.assertEqual(2, code)
        self.assertIn("discard --execute needs --record FILE", err)
        self.assertEqual([], api.calls)

    def test_a_move_names_its_messages_or_says_all(self):
        code, _, err = run(FakeApi({}), "replay", "ordering-commands_error", "--execute")
        self.assertEqual(2, code)
        self.assertIn("--message-id ID (repeatable) or take them all with --all", err)


class AReplayIsTheSameMessage(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.record = str(Path(self.directory.name) / "record.jsonl")

    def tearDown(self):
        self.directory.cleanup()

    def test_the_id_headers_properties_and_bytes_reach_the_endpoint_unchanged(self):
        original = message("m-1", body='{"messageId":"m-1","message":{"lines":[1,2]}}é')
        api = FakeApi({"ordering-commands_error": [copy.deepcopy(original)], "ordering-commands": []})
        code, out, _ = run(api, "replay", "ordering-commands_error", "--message-id", "m-1",
                           "--execute", "--record", self.record)
        self.assertEqual(0, code, out)
        self.assertEqual([], api.queues["ordering-commands_error"])
        [replayed] = api.queues["ordering-commands"]
        self.assertEqual(original["properties"], replayed["properties"])
        self.assertEqual(base64.b64decode(original["payload"]), base64.b64decode(replayed["payload"]))
        self.assertEqual("base64", replayed["payload_encoding"])

    def test_messages_not_named_go_back_to_the_queue_they_came_from(self):
        api = FakeApi({"ordering-commands_error": [message("m-1"), message("m-2")], "ordering-commands": []})
        code, out, err = run(api, "replay", "ordering-commands_error", "--message-id", "m-2",
                             "--execute", "--record", self.record, "--json")
        self.assertEqual(0, code)
        self.assertEqual(["m-2"], [m["properties"]["message_id"] for m in api.queues["ordering-commands"]])
        [kept] = api.queues["ordering-commands_error"]
        self.assertEqual(message("m-1")["properties"], kept["properties"])
        self.assertEqual(["returned", "replayed"], [a["action"] for a in json.loads(out)["actions"]])
        self.assertEqual(["returned", "replayed"], [a["action"] for a in audit_lines(err)])

    def test_a_fault_raised_on_another_endpoint_is_refused_and_kept(self):
        api = FakeApi({"ordering-commands_error": [message("m-1", input_address="rabbitmq://h/inventory-commands")],
                       "ordering-commands": []})
        code, out, _ = run(api, "replay", "ordering-commands_error", "--all", "--execute", "--record", self.record)
        self.assertEqual(1, code)
        self.assertIn("MT-Fault-InputAddress names `inventory-commands`, not `ordering-commands`", out)
        self.assertEqual([], api.queues["ordering-commands"])
        self.assertEqual(1, len(api.queues["ordering-commands_error"]))

    def test_a_publish_routed_nowhere_returns_the_message_and_stops(self):
        api = FakeApi({"ordering-commands_error": [message("m-1"), message("m-2")], "ordering-commands": []},
                      unbound={"ordering-commands"})
        code, out, _ = run(api, "replay", "ordering-commands_error", "--all", "--execute", "--record", self.record)
        self.assertEqual(1, code)
        self.assertIn("failed         m-1 -> ordering-commands_error (the broker routed it nowhere", out)
        self.assertEqual(["m-2", "m-1"],
                         [m["properties"]["message_id"] for m in api.queues["ordering-commands_error"]])

    def test_a_take_the_broker_refuses_stops_the_run_and_reports_it(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")], "ordering-commands": []})
        real_call = api.__call__

        def refuse_takes(method, path, body):
            if (body or {}).get("ackmode") == "ack_requeue_false":
                raise dead_letters.Refused(f"POST {path}: HTTP 401 not_authorised")
            return real_call(method, path, body)

        code, out, _ = run(refuse_takes, "replay", "ordering-commands_error", "--all", "--execute",
                           "--record", self.record, "--json")
        self.assertEqual(1, code)
        [failed] = json.loads(out)["actions"]
        self.assertEqual("failed", failed["action"])
        self.assertIn("HTTP 401", failed["reason"])
        self.assertEqual(1, len(api.queues["ordering-commands_error"]))

    def test_a_take_whose_answer_is_lost_says_the_message_may_be_in_neither_place(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")], "ordering-commands": []})
        real_call = api.__call__

        def lose_takes(method, path, body):
            if (body or {}).get("ackmode") == "ack_requeue_false":
                real_call(method, path, body)
                raise dead_letters.AnswerLost(f"POST {path}: the answer was lost (TimeoutError: timed out)")
            return real_call(method, path, body)

        code, out, _ = run(lose_takes, "replay", "ordering-commands_error", "--all", "--execute",
                           "--record", self.record, "--json")
        self.assertEqual(1, code)
        [failed] = json.loads(out)["actions"]
        self.assertEqual("failed", failed["action"])
        self.assertIn("in neither ordering-commands_error nor --record", failed["reason"])

    def test_a_replay_whose_answer_is_lost_is_not_returned_and_says_it_may_have_landed(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")], "ordering-commands": []})
        real_call = api.__call__

        def lose_replays(method, path, body):
            if "/exchanges/" in path and "ordering-commands_error" not in path:
                real_call(method, path, body)
                raise dead_letters.AnswerLost(f"POST {path}: the answer was lost (TimeoutError: timed out)")
            return real_call(method, path, body)

        code, out, _ = run(lose_replays, "replay", "ordering-commands_error", "--all", "--execute",
                           "--record", self.record, "--json")
        self.assertEqual(1, code)
        [failed] = json.loads(out)["actions"]
        self.assertEqual("failed", failed["action"])
        self.assertIn("may have reached ordering-commands, so it is not returned", failed["reason"])
        self.assertEqual([], api.queues["ordering-commands_error"], "returned too, it would be on both")

    def test_a_return_whose_answer_is_lost_says_it_may_be_back_rather_than_gone(self):
        api = FakeApi({"ordering-commands_error": [message("m-1"), message("m-2")], "ordering-commands": []})
        real_call = api.__call__

        def lose_returns(method, path, body):
            if "/exchanges/" in path and "ordering-commands_error" in path:
                real_call(method, path, body)
                raise dead_letters.AnswerLost(f"POST {path}: the answer was lost (TimeoutError: timed out)")
            return real_call(method, path, body)

        _, out, _ = run(lose_returns, "replay", "ordering-commands_error", "--message-id", "m-2", "--execute",
                        "--record", self.record, "--json")
        returned = next(a for a in json.loads(out)["actions"] if a["message_id"] == "m-1")
        self.assertIn("may be back on ordering-commands_error", returned["reason"])
        self.assertNotIn("not on ordering-commands_error any more", returned["reason"])

    def test_a_skipped_queue_replays_to_its_endpoint(self):
        api = FakeApi({"bff-order-events_skipped": [message("s-1", input_address=None)], "bff-order-events": []})
        code, _, _ = run(api, "replay", "bff-order-events_skipped", "--all", "--execute", "--record", self.record)
        self.assertEqual(0, code)
        self.assertEqual(1, len(api.queues["bff-order-events"]))

    def test_an_id_not_on_the_queue_is_reported_and_fails_the_run(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")], "ordering-commands": []})
        code, out, _ = run(api, "replay", "ordering-commands_error", "--message-id", "nope", "--json")
        self.assertEqual(1, code)
        self.assertEqual(["nope"], json.loads(out)["not_found"])


class TheAuditTrail(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def test_one_line_per_action_with_who_when_which_queue_which_id_and_what(self):
        api = FakeApi({"ordering-commands_error": [message("m-1"), message("m-2")]})
        log = self.root / "audit.jsonl"
        code, _, err = run(api, "discard", "ordering-commands_error", "--all", "--execute",
                           "--record", str(self.root / "record.jsonl"), "--audit-log", str(log))
        self.assertEqual(0, code)
        lines = audit_lines(err)
        self.assertEqual(2, len(lines))
        for line, identity in zip(lines, ("m-1", "m-2")):
            self.assertEqual(AUDIT_FIELDS, list(line))
            self.assertEqual({"ts": "2026-10-07T09:30:00+00:00", "operator": "oncall",
                              "broker_user": "dead-letter-operator", "vhost": "/",
                              "queue": "ordering-commands_error", "message_id": identity, "action": "discarded",
                              "destination": None, "reason": None}, line)
        self.assertEqual(err.splitlines(), log.read_text(encoding="utf-8").splitlines())

    def test_a_take_that_fails_is_an_audit_line(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")], "ordering-commands": []})
        real_call = api.__call__

        def refuse_takes(method, path, body):
            if (body or {}).get("ackmode") == "ack_requeue_false":
                raise dead_letters.AnswerLost(f"POST {path}: the answer was lost (TimeoutError: timed out)")
            return real_call(method, path, body)

        _, _, err = run(refuse_takes, "discard", "ordering-commands_error", "--all", "--execute",
                        "--record", str(self.root / "record.jsonl"))
        [line] = audit_lines(err)
        self.assertEqual("failed", line["action"])
        self.assertIn("in neither ordering-commands_error nor --record", line["reason"])

    def test_an_unrecorded_message_whose_return_fails_is_written_out_and_not_called_safe(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")], "ordering-commands": []},
                      unbound={"ordering-commands_error"})
        with mock.patch.object(dead_letters.Run, "record", side_effect=OSError("No space left on device")):
            _, out, err = run(api, "discard", "ordering-commands_error", "--all", "--execute",
                              "--record", str(self.root / "record.jsonl"), "--json")
        [failed] = json.loads(out)["actions"]
        self.assertIn("--record does not hold it", failed["reason"])
        self.assertNotIn("the --record file holds it", failed["reason"])
        [dumped] = [line for line in err.splitlines() if line.startswith("unrecorded ")]
        self.assertEqual("m-1", json.loads(dumped[len("unrecorded "):])["message"]["properties"]["message_id"])

    def test_a_discarded_message_is_in_the_record_before_it_is_gone(self):
        api = FakeApi({"ordering-commands_error": [message("m-1")]})
        record = self.root / "record.jsonl"
        run(api, "discard", "ordering-commands_error", "--message-id", "m-1", "--execute", "--record", str(record))
        [kept] = [json.loads(line) for line in record.read_text(encoding="utf-8").splitlines()]
        self.assertEqual("ordering-commands_error", kept["queue"])
        self.assertEqual(message("m-1")["payload"], kept["message"]["payload"])
        self.assertEqual(message("m-1")["properties"], kept["message"]["properties"])
        self.assertEqual([], api.queues["ordering-commands_error"])


class TheCredential(unittest.TestCase):
    def test_no_option_carries_a_password(self):
        options = [o for action in dead_letters.parser()._subparsers._group_actions[0].choices["replay"]._actions
                   for o in action.option_strings]
        self.assertFalse([o for o in options if "pass" in o.lower()], options)

    def test_the_runbooks_curl_config_is_read(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "rabbit.curl"
            path.write_text('user = "dead-letter-operator:not-a-real-password"\n', encoding="utf-8")
            self.assertEqual(("dead-letter-operator", "not-a-real-password"),
                             dead_letters.load_credential(str(path), {}))

    def test_a_quoted_password_is_unescaped_as_curl_reads_it(self):
        # The runbooks escape a backslash and a double quote for curl's config syntax; the tool must read the
        # same password curl sends, or it fails to authenticate on exactly the passwords the escape exists for.
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "rabbit.curl"
            path.write_text('user = "dead-letter-operator:a\\\\b\\"c d:e@f\\""\n', encoding="utf-8")
            self.assertEqual(("dead-letter-operator", 'a\\b"c d:e@f"'),
                             dead_letters.load_credential(str(path), {}))

    def test_a_bare_value_ends_at_its_first_space_as_curl_reads_it(self):
        self.assertEqual("dead-letter-operator:secret", dead_letters.curl_value("dead-letter-operator:secret tail"))

    def test_no_credential_is_refused(self):
        code, _, err = run(FakeApi({}), "list", env={})
        self.assertEqual(2, code)
        self.assertIn("no credential", err)

    def test_a_url_that_is_not_http_is_refused(self):
        with self.assertRaisesRegex(dead_letters.Refused, "must be http or https"):
            dead_letters.http_transport("file:///etc/passwd", "u", "p")


class TheTransport(unittest.TestCase):
    def test_an_answer_lost_after_the_send_is_reported_as_lost_not_as_a_traceback(self):
        # urllib wraps only the send, so a timeout reading the answer arrives as a bare OSError.
        send = dead_letters.http_transport("http://localhost:15672", "u", "p")
        with mock.patch("urllib.request.urlopen", side_effect=TimeoutError("timed out")):
            with self.assertRaisesRegex(dead_letters.AnswerLost, r"the answer was lost \(TimeoutError"):
                send("POST", "/api/queues/%2F/q_error/get", {"count": 1})


if __name__ == "__main__":
    unittest.main()
