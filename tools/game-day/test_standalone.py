"""--cause and --restore against a stack that remembers: every scenario, apart, leaves the stack as it found it.
No Docker.

    cd tools/game-day && py -3.12 -m unittest test_standalone
"""

import contextlib
import importlib
import io
import json
import os
import re
import subprocess
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

import game_day
import harness
import runbook_coverage as coverage

SERVICES = ("rabbitmq", "sql", "ordering-api", "web-bff", "shipping-worker", "notifications-worker")
BACKLOG_QUEUE = "bff-order-events"
# What a cause leaves that a restore cannot take back, per script: the orders it placed, and the contacts it dropped.
ORDERS_PLACED = {"outbox_broker:0", "projection_lag:0", "address_refused:0", "contact_refused:0", "queue_backlog:0"}
CONTACTS_DROPPED = {"contact_refused"}


class Killed(BaseException):
    """The process dying where it stands, which no `except Exception` in the scenario sees."""


class Stack:
    """The parts of the Compose stack a scenario touches, each one a state a restore has to bring back."""

    def __init__(self):
        self.calls = []
        self.services = {name: "running" for name in SERVICES}
        # A row and a message that are not the game day's, so a restore that deletes wider than its own is seen.
        self.rows = {("ordering.OutboxMessages", "real-row")}
        self.renamed = False
        self.contacts_dropped = False
        self.roles = {}
        for module in (importlib.import_module("scenarios.address_refused"),
                       importlib.import_module("scenarios.contact_refused")):
            self.roles[(module.GRANT.account, module.GRANT.client)] = {module.GRANT.role}
        self.queues = {"ordering-commands_error": [("real-1", "elsewhere")],
                       "ordering-stock-events_skipped": [("real-2", "elsewhere")], BACKLOG_QUEUE: []}
        self.account_open = False
        self.orders = 0
        self.serial = 0
        self.world = harness.World(self.compose(), self.alerts(), self.order_book(), None, lambda message: None,
                                   realm=self.realm(), traffic=self.traffic(), broker=self.broker())

    def snapshot(self):
        return (dict(self.services), frozenset(self.rows), self.renamed,
                {key: frozenset(value) for key, value in self.roles.items()},
                {name: tuple(messages) for name, messages in self.queues.items()}, self.account_open)

    def verbs(self):
        return [call[0] for call in self.calls]

    def compose(self):
        outer = self

        class Compose:
            def stop(self, service): outer._set(service, "stopped")
            def start(self, service): outer._set(service, "running")
            def restart(self, service): outer._set(service, "running", "restart")
            def pause(self, service): outer._set(service, "paused")
            def unpause(self, service):
                # Docker refuses to unpause what is not paused, and the harness lets that pass.
                if outer.services[service] == "paused":
                    outer._set(service, "running")
            def running(self, service): return outer.services[service] == "running"
            def logs(self, service, since): return ""
            def exec_sql(self, sql, database="Ordering"): return outer._sql(sql)

        return Compose()

    def _set(self, service, state, verb=None):
        self.calls.append((verb or {"stopped": "stop", "running": "start", "paused": "pause"}[state], service))
        self.services[service] = state

    def _sql(self, sql):
        self.calls.append(("sql",))
        if sql.strip() == "SELECT 1;":
            return "1"
        table = re.search(r"(?:INSERT INTO|DELETE FROM) ([\w.]+)", sql)
        if "sp_rename" in sql:
            # The restore's rename is guarded by IF OBJECT_ID, and puts back only what the cause renamed.
            self.renamed = "IF OBJECT_ID" not in sql
        elif "INSERT INTO" in sql and "ROW_NUMBER" in sql:
            self.rows.add((table.group(1), re.search(r"N'(GameDay[\w.]+)'", sql).group(1)))
        elif "INSERT INTO" in sql:
            self.rows.add((table.group(1), re.search(r"'([0-9a-f-]{36})'", sql).group(1)))
        elif "ContactRecords" in sql:
            self.contacts_dropped = True
        else:
            for statement in re.findall(r"DELETE FROM ([\w.]+)\s+WHERE[^;]*?'([^']+)'", sql):
                self.rows = {row for row in self.rows if row != statement}
        return ""

    def realm(self):
        outer = self

        class Realm:
            def roles(self, account, client): return sorted(outer.roles[(account, client)])
            def revoke(self, account, client, role): outer.roles[(account, client)].discard(role)
            def grant(self, account, client, role):
                outer.calls.append(("grant", role))
                outer.roles[(account, client)].add(role)

        return Realm()

    def order_book(self):
        outer = self

        class Orders:
            def place(self):
                outer.orders += 1
                return f"order-{outer.orders}"

        return Orders()

    def alerts(self):
        class Alerts:
            def loaded(self, name): return True
            def state(self, name): return "inactive"
            def labels(self, name): return []
            def query(self, expression):
                return [{"metric": {"service_name": "Ordering.Api", "http_route": "/x"}, "value": [0, "0.5"]}]

        return Alerts()

    def traffic(self):
        outer = self

        class Traffic:
            running = False
            def start(self, seconds): outer.calls.append(("traffic", seconds))
            def stop(self): pass

        return Traffic()

    def broker(self):
        outer = self

        class Broker:
            def open(self): outer.account_open = True
            def close(self): outer.account_open = False

            def publish(self, exchange, message_type, message):
                outer.serial += 1
                queue = (f"{exchange}_error" if "CancelOrder" in message_type
                         else f"{exchange}_skipped" if "Unrouted" in message_type else exchange)
                outer.queues.setdefault(queue, []).append((f"game-day-{outer.serial}", harness.SOURCE_ADDRESS))
                return f"game-day-{outer.serial}"

            def ours(self, queue):
                return [ident for ident, source in outer.queues.get(queue, []) if source == harness.SOURCE_ADDRESS]

            def discard(self, queue, ids):
                if ids:
                    outer.calls.append(("discard", queue))
                outer.queues[queue] = [m for m in outer.queues.get(queue, []) if m[0] not in ids]
                return list(ids)

            def discard_ours(self, queue):
                self.open()
                try:
                    return self.discard(queue, self.ours(queue))
                finally:
                    self.close()

            def purge(self, queue):
                outer.calls.append(("purge", queue))
                outer.queues[queue] = []

            def queues(self):
                return {name: (len(messages), 1 if name == BACKLOG_QUEUE and outer.services["web-bff"] == "running"
                               else 0) for name, messages in outer.queues.items()}

        return Broker()


def fresh(name):
    """A scenario as a new process finds it: the module imported again, with none of the last one's memory."""
    return importlib.reload(importlib.import_module(f"scenarios.{name}"))


class Apart(unittest.TestCase):
    """For every scenario, --cause and then --restore, in processes that share nothing, leaves the stack clean."""

    def roundtrip(self, name, index):
        stack = Stack()
        dying = (name, index) == ("queue_backlog", 0)
        if dying:
            # A real message the lag half's restore must not purge, which a paused consumer would otherwise invite.
            stack.queues[BACKLOG_QUEUE] = [("real-3", "elsewhere")]
        before = stack.snapshot()
        module = fresh(name)
        phase = game_day.phases(module)[index]
        # The cause the detached child would run, in this process and not waiting for the alert.
        spawn = lambda runbook, alert: self.child(phase, stack) or 4242
        # The lag half's cause is a loop of pauses, and --restore exists for the process killed inside the first.
        patches = [mock.patch.object(module, "SUSTAIN_SECONDS", 1e9 if dying else 0, create=True),
                   mock.patch.object(harness.time, "sleep", side_effect=Killed if dying else None)]
        with patches[0], patches[1]:
            self.assertEqual([], game_day.cause_only(phase, stack.world, spawn=spawn, held=lambda runbook: None))
        self.assertNotEqual(before, stack.snapshot(), f"{name}: the cause changed nothing")
        if hasattr(module, "_top_up"):
            self.addCleanup(module._top_up.halt.set)
        restored = game_day.restore_only(fresh(name), stack.world, end=lambda runbook: None)
        self.assertEqual([], restored)
        self.assertEqual(before, stack.snapshot(), f"{name}: --restore did not leave the stack as it found it")
        return stack

    def child(self, phase, stack):
        with contextlib.suppress(Killed):
            game_day.hold(phase, stack.world, sleep=lambda seconds: None, limit=0)

    def test_every_script_cause_then_restore_leaves_the_stack_clean(self):
        for name in sorted(coverage.scripts()):
            for index, phase in enumerate(game_day.phases(fresh(name))):
                with self.subTest(f"{name}:{phase.ALERT}"):
                    stack = self.roundtrip(name, index)
                    self.assertEqual(f"{name}:{index}" in ORDERS_PLACED, stack.orders > 0, "the orders it placed")
                    self.assertEqual(name in CONTACTS_DROPPED, stack.contacts_dropped)

    def test_restore_before_any_cause_changes_nothing_and_takes_nothing_wider(self):
        for name in sorted(coverage.scripts()):
            with self.subTest(name):
                stack = Stack()
                stack.queues[BACKLOG_QUEUE] = [("real-3", "elsewhere")]
                before = stack.snapshot()
                self.assertEqual([], game_day.restore_only(fresh(name), stack.world, end=lambda runbook: None))
                self.assertEqual(before, stack.snapshot())
                self.assertFalse({"restart", "purge", "discard", "stop", "pause"} & set(stack.verbs()),
                                 stack.calls)

    def test_restore_twice_after_a_cause_is_the_same_as_once(self):
        for name in sorted(coverage.scripts()):
            with self.subTest(name):
                stack = self.roundtrip(name, 0)
                before = stack.snapshot()
                self.assertEqual([], game_day.restore_only(fresh(name), stack.world, end=lambda runbook: None))
                self.assertEqual(before, stack.snapshot())

    def test_a_game_days_messages_are_discarded_and_another_s_on_the_same_queue_stay(self):
        for name, queue, real in (("error_queue", "ordering-commands_error", "real-1"),
                                  ("skipped_queue", "ordering-stock-events_skipped", "real-2")):
            with self.subTest(name):
                stack = self.roundtrip(name, 0)
                self.assertEqual([real], [ident for ident, _ in stack.queues[queue]])

    def test_a_grant_whose_read_back_fails_still_restarts_the_worker_the_retry_would_skip(self):
        for name in ("address_refused", "contact_refused"):
            with self.subTest(name):
                stack = Stack()
                module = fresh(name)
                stack.roles[(module.GRANT.account, module.GRANT.client)] = set()
                stack.world.realm.grant = lambda account, client, role: stack.roles[(account, client)].update(
                    {role, "wider"})
                with self.assertRaises(harness.GameDayError):
                    module.restore(stack.world)
                self.assertIn(("restart", module.WORKER), stack.calls)

    def test_a_grant_that_was_never_taken_restarts_no_worker(self):
        for name in ("address_refused", "contact_refused"):
            with self.subTest(name):
                stack = Stack()
                game_day.restore_only(fresh(name), stack.world, end=lambda runbook: None)
                self.assertNotIn("restart", stack.verbs())

    def test_a_restore_after_the_lag_half_was_killed_mid_pause_unpauses_and_purges_nothing(self):
        stack = Stack()
        stack.queues[BACKLOG_QUEUE] = [("real-3", "elsewhere")]
        stack.services["web-bff"] = "paused"
        game_day.restore_only(fresh("queue_backlog"), stack.world, end=lambda runbook: None)
        self.assertEqual("running", stack.services["web-bff"])
        self.assertEqual([("real-3", "elsewhere")], stack.queues[BACKLOG_QUEUE])
        self.assertNotIn("purge", stack.verbs())

    def test_a_backlog_restore_purges_only_when_the_cause_stopped_the_consumer(self):
        stack = Stack()
        stack.queues[BACKLOG_QUEUE] = [("real-3", "elsewhere")]
        game_day.restore_only(fresh("queue_backlog"), stack.world, end=lambda runbook: None)
        self.assertEqual([("real-3", "elsewhere")], stack.queues[BACKLOG_QUEUE])
        stack.services["web-bff"] = "stopped"
        game_day.restore_only(fresh("queue_backlog"), stack.world, end=lambda runbook: None)
        self.assertEqual([], stack.queues[BACKLOG_QUEUE])
        self.assertEqual("running", stack.services["web-bff"])


class CauseOnly(unittest.TestCase):
    def test_a_plain_cause_runs_here_and_a_sustained_one_is_handed_to_a_detached_child(self):
        for name in sorted(coverage.scripts()):
            module = fresh(name)
            with self.subTest(name):
                stack = Stack()
                handed = []
                spawn = lambda runbook, alert: handed.append((runbook, alert)) or 99
                with mock.patch.object(module, "SUSTAIN_SECONDS", 0, create=True):
                    self.assertEqual([], game_day.cause_only(module, stack.world, spawn=spawn,
                                                             held=lambda runbook: None))
                if getattr(module, "SUSTAINED", False):
                    self.assertEqual([(module.RUNBOOK, module.ALERT)], handed)
                    self.assertEqual([], stack.calls, "a sustained cause waits for its child")
                else:
                    self.assertEqual([], handed)
                    self.assertNotEqual(Stack().snapshot(), stack.snapshot(), "a plain cause has done its work")

    def test_the_sustained_causes_are_the_ones_that_need_a_loop_to_stay_a_cause(self):
        sustained = {name for name in coverage.scripts() if getattr(fresh(name), "SUSTAINED", False)}
        self.assertEqual({"error_rate", "latency", "outbox_growth", "queue_backlog"}, sustained)

    def test_it_refuses_an_alert_that_is_firing_or_unloaded_before_it_causes_anything(self):
        stack = Stack()
        module = fresh("outbox_broker")
        stack.world.alerts.state = lambda name: "firing"
        self.assertIn("already firing", game_day.cause_only(module, stack.world)[0])
        stack.world.alerts.state = lambda name: "inactive"
        stack.world.alerts.loaded = lambda name: False
        self.assertIn("no such rule loaded", game_day.cause_only(module, stack.world)[0])
        self.assertEqual([], stack.calls)

    def test_a_cause_that_is_already_held_is_refused(self):
        stack = Stack()
        found = game_day.cause_only(fresh("latency"), stack.world, held=lambda runbook: 7)
        self.assertIn("already being held", found[0])

    def test_a_cause_that_raises_half_way_is_a_finding_that_points_at_restore(self):
        stack = Stack()
        module = types.SimpleNamespace(
            RUNBOOK="a.md", ALERT="A", DEADLINE=harness.Deadline(signal_seconds=1),
            cause=mock.Mock(side_effect=harness.GameDayError("docker stop exited 1")))
        found = game_day.cause_only(module, stack.world)
        self.assertIn("--restore", found[0])
        self.assertIn("docker stop exited 1", found[0])

    def test_it_does_not_wait_run_the_step_or_restore(self):
        stack = Stack()
        module = types.SimpleNamespace(
            RUNBOOK="a.md", ALERT="A", DEADLINE=harness.Deadline(signal_seconds=1), cause=mock.Mock(),
            first_step=mock.Mock(), restore=mock.Mock(), settled=mock.Mock())
        self.assertEqual([], game_day.cause_only(module, stack.world))
        module.cause.assert_called_once()
        module.first_step.assert_not_called()
        module.restore.assert_not_called()
        module.settled.assert_not_called()


class RestoreOnly(unittest.TestCase):
    def phases(self, order, fail=()):
        def phase(alert, nxt=None):
            def restore(world):
                order.append(alert)
                if alert in fail:
                    raise harness.GameDayError(f"{alert} would not start")
            return types.SimpleNamespace(RUNBOOK="a.md", ALERT=alert, restore=restore, NEXT=nxt)

        return phase("First", phase("Second"))

    def test_the_phases_restore_last_first_because_the_second_is_caused_on_the_first(self):
        order = []
        self.assertEqual([], game_day.restore_only(self.phases(order), Stack().world, end=lambda runbook: None))
        self.assertEqual(["Second", "First"], order)

    def test_a_restore_that_raises_is_a_finding_and_the_others_still_run(self):
        order = []
        found = game_day.restore_only(self.phases(order, fail={"Second"}), Stack().world, end=lambda runbook: None)
        self.assertEqual(["Second", "First"], order)
        self.assertEqual(1, len(found))
        self.assertIn("Second: the restore failed", found[0])

    def test_it_does_not_wait_for_an_alert_to_resolve(self):
        stack = Stack()
        stack.world.alerts.state = lambda name: "firing"
        said = []
        stack.world.say = said.append
        module = fresh("address_refused")
        with mock.patch.object(harness.time, "sleep", side_effect=AssertionError("it waited")):
            self.assertEqual([], game_day.restore_only(module, stack.world, end=lambda runbook: None))
        self.assertTrue(any("is firing after the restore; its window clears it within 1980s" in line
                            for line in said), said)

    def test_it_ends_the_process_a_cause_left_running_first(self):
        order = []
        said = []
        stack = Stack()
        stack.world.say = said.append
        game_day.restore_only(self.phases(order), stack.world, end=lambda runbook: order.append("end") or "ended it")
        self.assertEqual(["end", "Second", "First"], order)
        self.assertIn("ended it", said)


class Holding(unittest.TestCase):
    def test_the_child_is_this_script_with_hold_and_the_phase_detached_and_its_pid_is_kept(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory) / "game-day"
            seen = {}

            def popen(argv, **options):
                seen.update(argv=argv, **options)
                return types.SimpleNamespace(pid=31337)

            pid = game_day.spawn_hold("error-rate.md", "ErrorRateService", state, popen)
            self.assertEqual(31337, pid)
            self.assertEqual(["-u", str(Path(game_day.__file__).resolve()), "error-rate.md", "--hold", "--phase",
                              "ErrorRateService"], seen["argv"][1:])
            self.assertEqual(subprocess.DEVNULL, seen["stdin"])
            if os.name == "nt":
                self.assertTrue(seen["creationflags"] & subprocess.DETACHED_PROCESS)
            else:
                self.assertTrue(seen["start_new_session"])
            self.assertEqual("31337", (state / "error_rate.pid").read_text(encoding="utf-8"))
            self.assertTrue((state / "error_rate.log").exists())

    def test_a_pid_file_names_a_holder_only_while_that_process_is_ours(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory)
            self.assertIsNone(game_day.holder("latency", state))
            (state / "latency.pid").write_text("5150", encoding="utf-8")
            with mock.patch.object(game_day, "_is_ours", return_value=False):
                self.assertIsNone(game_day.holder("latency", state))
            with mock.patch.object(game_day, "_is_ours", return_value=True):
                self.assertEqual(5150, game_day.holder("latency", state))
            (state / "latency.pid").write_text("not a number", encoding="utf-8")
            self.assertIsNone(game_day.holder("latency", state))

    def test_release_ends_the_tree_forgets_the_pid_and_is_quiet_when_nothing_was_held(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory)
            self.assertIsNone(game_day.release("latency", state, run=mock.Mock()))
            (state / "latency.pid").write_text("5150", encoding="utf-8")
            run = mock.Mock()
            with mock.patch.object(game_day, "_is_ours", return_value=True), \
                    mock.patch.object(game_day.os, "killpg", create=True) as killpg:
                said = game_day.release("latency", state, run=run)
            self.assertIn("5150", said)
            self.assertFalse((state / "latency.pid").exists())
            if os.name == "nt":
                self.assertEqual(["taskkill", "/PID", "5150", "/T", "/F"], run.call_args.args[0])
            else:
                killpg.assert_called_once()

    def test_a_holder_that_ended_on_its_own_before_the_kill_is_not_an_error(self):
        if os.name == "nt":
            self.skipTest("taskkill reports it in the exit code, which is not read")
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory)
            (state / "latency.pid").write_text("5150", encoding="utf-8")
            with mock.patch.object(game_day, "_is_ours", return_value=True),                     mock.patch.object(game_day.os, "killpg", side_effect=ProcessLookupError):
                self.assertIn("5150", game_day.release("latency", state, run=mock.Mock()))

    def test_a_process_is_ours_by_its_command_line_and_not_by_being_python(self):
        for shown, ours in (("python -u C:/x/game_day.py latency --hold --phase Latency", True),
                            ("python -m http.server", False), ("", False)):
            with self.subTest(shown):
                run = mock.Mock(return_value=types.SimpleNamespace(stdout=shown))
                self.assertEqual(ours, game_day._is_ours(5150, run))

    def test_a_stale_pid_file_is_removed_and_nothing_is_killed(self):
        with tempfile.TemporaryDirectory() as directory:
            state = Path(directory)
            (state / "latency.pid").write_text("5150", encoding="utf-8")
            run = mock.Mock()
            with mock.patch.object(game_day, "_is_ours", return_value=False):
                self.assertIsNone(game_day.release("latency", state, run=run))
            self.assertFalse((state / "latency.pid").exists())
            run.assert_not_called()

    def test_the_hold_stays_while_a_game_day_thread_works_and_ends_when_none_does(self):
        import threading
        release = threading.Event()
        worker = threading.Thread(target=release.wait, name="game-day-top-up", daemon=True)
        stack = Stack()
        module = types.SimpleNamespace(cause=lambda world: worker.start())
        polls = []

        def sleep(seconds):
            polls.append(seconds)
            if len(polls) == 3:
                release.set()
                worker.join()

        self.assertEqual([], game_day.hold(module, stack.world, sleep=sleep))
        self.assertEqual(3, len(polls))

    def test_the_hold_is_bounded(self):
        import threading
        release = threading.Event()
        worker = threading.Thread(target=release.wait, name="game-day-traffic", daemon=True)
        clock = iter(range(0, 100000, 1000))
        module = types.SimpleNamespace(cause=lambda world: worker.start())
        try:
            game_day.hold(module, Stack().world, sleep=lambda seconds: None, clock=lambda: next(clock), limit=2500)
        finally:
            release.set()


class Inspected(unittest.TestCase):
    """Broker.ours reads the queue and keeps what this tool sent, by the envelope's source."""

    def broker(self, messages):
        document = json.dumps({"messages": [{"message_id": ident, "body": body} for ident, body in messages]})
        run = lambda *args, **kwargs: types.SimpleNamespace(returncode=0, stdout=document, stderr="")
        compose = types.SimpleNamespace(calls=[])
        return harness.Broker(compose, run=run, base="http://broker:15672")

    def test_it_keeps_the_envelopes_whose_source_is_the_game_day(self):
        ours = json.dumps({"sourceAddress": harness.SOURCE_ADDRESS})
        others = json.dumps({"sourceAddress": "rabbitmq://rabbitmq/ordering-api"})
        found = self.broker([("a", ours), ("b", others), ("c", "not json"), ("d", None), ("e", "[1]"), ("f", ours)])
        self.assertEqual(["a", "f"], found.ours("q"))

    def test_a_queue_as_deep_as_the_inspect_limit_is_an_error_and_not_a_clean_restore(self):
        full = [(str(n), json.dumps({"sourceAddress": "elsewhere"})) for n in range(harness.INSPECT_LIMIT)]
        with self.assertRaises(harness.GameDayError) as caught:
            self.broker(full).ours("q")
        self.assertIn("unseen", str(caught.exception))

    def test_the_source_is_what_publish_sends(self):
        sent = {}

        def send(method, url, headers, body):
            sent.update(json.loads(json.loads(body)["payload"]))
            return 200, json.dumps({"routed": True})

        broker = harness.Broker(types.SimpleNamespace(), send=send, base="http://broker:15672")
        broker.publish("ordering-commands", "urn:message:X:Y", {})
        self.assertEqual(harness.SOURCE_ADDRESS, sent["sourceAddress"])

    def test_a_refused_inspect_is_an_error_and_not_an_empty_queue(self):
        refused = harness.Broker(types.SimpleNamespace(), base="http://broker:15672",
                                 run=lambda *a, **k: types.SimpleNamespace(returncode=2, stderr="", stdout=json.dumps(
                                     {"error": "401"})))
        with self.assertRaises(harness.GameDayError):
            refused.ours("q")

    def test_discard_ours_opens_discards_what_is_ours_and_closes_even_when_it_fails(self):
        broker = self.broker([])
        steps = []
        broker.open = lambda: steps.append("open")
        broker.close = lambda: steps.append("close")
        broker.ours = lambda queue: ["a"]

        def discard(queue, ids):
            steps.append(("discard", ids))
            raise harness.GameDayError("refused")

        broker.discard = discard
        with self.assertRaises(harness.GameDayError):
            broker.discard_ours("q")
        self.assertEqual(["open", ("discard", ["a"]), "close"], steps)


class Console(unittest.TestCase):
    """What the console reads and runs: the catalogue, the flags and the last line."""

    def run_main(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            try:
                code = game_day.main(list(argv))
            except SystemExit as stop:
                code = stop.code
        return code, out.getvalue(), err.getvalue()

    def test_the_catalogue_has_every_runbook_with_its_alerts_and_says_which_have_no_script(self):
        code, out, _ = self.run_main("--list", "--json")
        self.assertEqual(0, code)
        rows = json.loads(out)
        self.assertEqual(sorted(name.removesuffix(".md") for name in coverage.runbooks()),
                         [row["runbook"] for row in rows])
        self.assertTrue(all(row["alerts"] for row in rows), "every runbook has a rule that names it")
        unscripted = {row["runbook"]: row["reason"] for row in rows if not row["script"]}
        self.assertEqual({name.removesuffix(".md") for name in coverage.NOT_ON_COMPOSE}, set(unscripted))
        self.assertTrue(all(reason.strip() for reason in unscripted.values()))

    def test_a_scripted_runbooks_phases_are_its_alerts_with_their_cost_and_waits(self):
        rows = {row["runbook"]: row for row in json.loads(self.run_main("--list", "--json")[1])}
        for row in rows.values():
            if not row["script"]:
                continue
            with self.subTest(row["runbook"]):
                self.assertEqual(row["alerts"], sorted(phase["alert"] for phase in row["phases"]))
                for phase in row["phases"]:
                    self.assertEqual(harness.CONFIRMATION[phase["impact"]], phase["confirmation"])
                    self.assertGreater(phase["fires_within_seconds"], 0)
                    self.assertGreater(phase["clears_within_seconds"], 0)
        self.assertEqual(["ErrorRateGateway", "ErrorRateService"], [p["alert"] for p in rows["error-rate"]["phases"]])
        self.assertEqual("typed", rows["contact-refused"]["phases"][0]["confirmation"])

    def test_every_phase_declares_what_it_costs_from_the_three_the_harness_names(self):
        for name in sorted(coverage.scripts()):
            for phase in game_day.phases(fresh(name)):
                with self.subTest(f"{name}:{phase.ALERT}"):
                    self.assertIn(phase.IMPACT, harness.CONFIRMATION)

    def test_the_text_list_still_says_why_a_runbook_has_no_script(self):
        _, out, _ = self.run_main("--list")
        self.assertIn("stuck-saga.md", out)
        self.assertIn("NOT_ON_COMPOSE", out)

    def test_cause_and_restore_end_on_a_line_of_their_own_and_exit_by_their_findings(self):
        for flag, label in (("--cause", "cause"), ("--restore", "restore")):
            for findings, code, word in (([], 0, "OK"), (["it broke"], 1, "FAILED")):
                with self.subTest(flag, findings=findings):
                    function = "cause_only" if flag == "--cause" else "restore_only"
                    with mock.patch.object(game_day, function, return_value=findings):
                        got, out, err = self.run_main("outbox-broker", flag)
                    self.assertEqual(code, got)
                    self.assertRegex(out.splitlines()[-1], rf"^game day: {label} {word} in \d+m\d\ds$")
                    self.assertEqual("FINDING: it broke" if findings else "", err.strip())

    def test_a_runbook_without_a_script_is_exit_2_for_both_and_no_stack_is_touched(self):
        for flag in ("--cause", "--restore"):
            with self.subTest(flag):
                code, _, err = self.run_main("stuck-saga", flag)
                self.assertEqual(2, code)
                self.assertIn("no script", err)

    def test_a_phase_names_an_alert_of_the_runbook_or_it_is_exit_2(self):
        code, _, err = self.run_main("error-rate", "--cause", "--phase", "Nothing")
        self.assertEqual(2, code)
        self.assertIn("ErrorRateGateway, ErrorRateService", err)

    def test_the_flags_that_do_not_belong_together_are_refused(self):
        for argv in (("latency", "--cause", "--restore"), ("latency", "--phase", "Latency"), ("latency", "--json"),
                     ("--cause",)):
            with self.subTest(argv):
                self.assertEqual(2, self.run_main(*argv)[0])

    def test_no_flag_is_still_the_full_run(self):
        with mock.patch.object(game_day, "run", return_value=[]) as run:
            code, out, _ = self.run_main("outbox-broker")
        self.assertEqual(0, code)
        run.assert_called_once()
        self.assertRegex(out.splitlines()[-1], r"^game day: OK in \d+m\d\ds$")


if __name__ == "__main__":
    unittest.main()
