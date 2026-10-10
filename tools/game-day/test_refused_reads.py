"""The Keycloak hands, the settle window and the refused-read scenarios against doubles. No Docker.

    cd tools/game-day && py -3.12 -m unittest
"""

import json
import re
import subprocess
import time
import types
import unittest
import urllib.parse
from unittest import mock

import game_day
import harness
import test_harness as base

ADMIN_ROUTE = "/admin/realms/commerce"


class Keycloak:
    """Keycloak's admin API for one service account, recording each call and holding its roles."""

    def __init__(self, held=(), token_status=200, admin_status=None, trace=None):
        self.held = list(held)
        self.token_status = token_status
        self.admin_status = admin_status or {}
        self.calls = []
        # What the token route hands out, so a test can see it come back as the bearer.
        self.issued = f"issued-{id(self)}"
        # A list shared with the compose double, so a test can say what happened before what.
        self.trace = trace if trace is not None else []

    def __call__(self, method, url, headers, body):
        if "/protocol/openid-connect/token" in url:
            return self.token_status, json.dumps({"access_token": self.issued})
        path = url.split(ADMIN_ROUTE, 1)[1]
        payload = json.loads(body) if body else None
        self.calls.append((method, path, payload, headers.get("Authorization")))
        if (method, path) in self.admin_status:
            return self.admin_status[(method, path)], "refused"
        if path.startswith("/users?"):
            return 200, json.dumps([{"id": "user-1"}])
        if path.startswith("/clients?"):
            return 200, json.dumps([{"id": "client-1"}])
        if path.startswith("/clients/client-1/roles/"):
            return 200, json.dumps({"id": "role-1", "name": path.rsplit("/", 1)[1].replace("%3A", ":")})
        if path == "/users/user-1/role-mappings/clients/client-1":
            if method == "GET":
                return 200, json.dumps([{"name": name} for name in self.held])
            names = [role["name"] for role in payload]
            self.trace.append(("revoke" if method == "DELETE" else "grant",))
            # A repeat grant is a no-op, as Keycloak's is.
            self.held = [h for h in self.held if h not in names] if method == "DELETE" else sorted({*self.held, *names})
            return 204, ""
        return 404, path


class RealmCalls(unittest.TestCase):
    def test_it_logs_in_as_the_admin_composes_keycloak_bootstraps(self):
        text = (harness.ROOT / "deploy" / "compose" / "infrastructure.yml").read_text(encoding="utf-8")
        bootstrap = dict(re.findall(r"KC_BOOTSTRAP_ADMIN_(\w+): (\S+)", text))
        self.assertEqual((bootstrap["USERNAME"], bootstrap["PASSWORD"]), harness.KEYCLOAK_ADMIN)

    def test_roles_are_the_names_of_the_clients_roles_the_account_holds_directly_sorted(self):
        realm = harness.Realm(Keycloak(["b:two", "a:one"]))
        self.assertEqual(["a:one", "b:two"], realm.roles("service-account-x", "commerce-api"))

    def test_revoke_deletes_the_roles_mapping_with_its_representation_as_the_body(self):
        keycloak = Keycloak(["orders:delivery-address"])
        harness.Realm(keycloak).revoke("service-account-x", "commerce-api", "orders:delivery-address")
        method, path, payload, auth = keycloak.calls[-1]
        self.assertEqual(("DELETE", "/users/user-1/role-mappings/clients/client-1"), (method, path))
        self.assertEqual("orders:delivery-address", payload[0]["name"])
        self.assertEqual(f"Bearer {keycloak.issued}", auth)
        self.assertEqual([], keycloak.held)

    def test_grant_posts_the_mapping(self):
        keycloak = Keycloak()
        harness.Realm(keycloak).grant("service-account-x", "commerce-api", "orders:delivery-address")
        self.assertEqual("POST", keycloak.calls[-1][0])
        self.assertEqual(["orders:delivery-address"], keycloak.held)

    def test_a_role_name_with_a_colon_is_escaped_in_the_path(self):
        keycloak = Keycloak()
        harness.Realm(keycloak).grant("service-account-x", "commerce-api", "orders:delivery-address")
        self.assertIn("/clients/client-1/roles/orders%3Adelivery-address", [call[1] for call in keycloak.calls])

    def test_a_refusal_from_the_admin_api_is_an_error_and_never_a_silent_pass(self):
        keycloak = Keycloak(admin_status={("DELETE", "/users/user-1/role-mappings/clients/client-1"): 403})
        with self.assertRaises(harness.GameDayError) as caught:
            harness.Realm(keycloak).revoke("service-account-x", "commerce-api", "orders:delivery-address")
        self.assertIn("403", str(caught.exception))

    def test_no_admin_token_is_an_error(self):
        with self.assertRaises(harness.GameDayError):
            harness.Realm(Keycloak(token_status=401)).roles("service-account-x", "commerce-api")

    def test_an_unknown_account_is_an_error(self):
        class Nobody(Keycloak):
            def __call__(self, method, url, headers, body):
                return (200, "[]") if "/users?" in url else super().__call__(method, url, headers, body)

        with self.assertRaises(harness.GameDayError):
            harness.Realm(Nobody()).roles("service-account-x", "commerce-api")


class GrantGivenBack(unittest.TestCase):
    grant = harness.Grant("service-account-x", "commerce-api", "orders:delivery-address")

    def test_it_is_granted_and_read_back(self):
        keycloak = Keycloak()
        self.grant.give_back(harness.Realm(keycloak))
        self.assertEqual(["POST", "GET"], [call[0] for call in keycloak.calls if "role-mappings" in call[1]])

    def test_a_wider_hold_after_the_restore_is_an_error_naming_what_is_held(self):
        with self.assertRaises(harness.GameDayError) as caught:
            self.grant.give_back(harness.Realm(Keycloak(["orders:admin"])))
        self.assertIn("orders:admin", str(caught.exception))


class HarnessAdditions(unittest.TestCase):
    def test_restart_is_one_compose_verb(self):
        calls = []

        def run(argv, **kwargs):
            calls.append(argv)
            return subprocess.CompletedProcess(argv, 0, "", "")

        harness.Compose(run).restart("shipping-worker")
        self.assertEqual(["restart", "shipping-worker"], calls[0][4:])

    def test_query_asks_prometheus_through_grafana_and_returns_the_vector(self):
        seen = []

        def send(method, url, headers, body):
            seen.append(url)
            return 200, json.dumps({"data": {"result": [{"metric": {}, "value": [1, "3"]}]}})

        found = harness.Alerts(send).query('up{job="a b"}')
        self.assertEqual("3", found[0]["value"][1])
        self.assertIn(harness.PROMETHEUS_PROXY + "/api/v1/query?query=up%7Bjob%3D%22a+b%22%7D", seen[0])

    def test_an_unreachable_prometheus_is_an_error_for_a_query_too(self):
        with self.assertRaises(harness.GameDayError):
            harness.Alerts(lambda *args: (0, "refused")).query("up")


class LokiException(unittest.TestCase):
    def sent(self, **kwargs):
        seen = []

        def send(method, url, headers, body):
            seen.append(urllib.parse.unquote_plus(url))
            return 200, json.dumps({"data": {"result": []}})

        harness.Logs(send).search("Shipping.Worker", "backs off", **kwargs)
        return seen[0]

    def test_without_an_exception_the_query_is_the_line_filter_alone(self):
        self.assertNotIn("exception_message", self.sent())

    def test_an_exception_filters_on_the_structured_metadata_and_is_anchored_by_dots(self):
        self.assertIn('| exception_message =~ ".*a b.*"', self.sent(exception="a b"))

    def test_backslashes_in_an_exception_survive_logql_string_escaping(self):
        self.assertIn(r'.*permission\\(s\\).*', self.sent(exception=r"permission\(s\)"))

    def test_a_literal_pattern_escapes_what_re2_treats_as_syntax_and_leaves_a_space_alone(self):
        self.assertEqual(r"permission\(s\) where a\.b", harness.regex_literal("permission(s) where a.b"))
        self.assertEqual("no-dash here", harness.regex_literal("no-dash here"))


class SettleWindow(unittest.TestCase):
    """A rule over a 30m window outlasts the cause's sum, and the scenario says by how much."""

    def run_with(self, settle):
        fake = base.Fake(["inactive", "firing"] + ["firing"] * 400 + ["inactive"])
        if settle:
            fake.scenario.SETTLE = harness.Deadline(signal_seconds=30 * 60)
        return fake.run()

    def test_the_default_bound_gives_up_on_a_window_that_is_still_open(self):
        self.assertIn("the restore did not settle", "\n".join(self.run_with(settle=False)))

    def test_a_scenario_that_names_a_settle_is_given_that_long(self):
        self.assertEqual([], self.run_with(settle=True))


class Stack:
    """A world whose every call is recorded in order."""

    def __init__(self, sql_results=(), loki=(), stdout="", running=True, query=None, held=()):
        self.calls = []
        self.sql_results = list(sql_results)
        outer = self
        keycloak = Keycloak(held, trace=self.calls)

        class Compose:
            def stop(self, service): outer.calls.append(("stop", service))
            def start(self, service): outer.calls.append(("start", service))
            def restart(self, service): outer.calls.append(("restart", service))
            def pause(self, service): outer.calls.append(("pause", service))
            def unpause(self, service): outer.calls.append(("unpause", service))
            def running(self, service): return running
            def logs(self, service, since): return stdout

            def exec_sql(self, sql, database="Ordering"):
                outer.calls.append(("sql", database, " ".join(sql.split())))
                return outer.sql_results.pop(0) if len(outer.sql_results) > 1 else (outer.sql_results or [""])[0]

        class Orders:
            def place(self):
                outer.calls.append(("place",))
                return "o-1"

        class Logs:
            def search(self, service_name, pattern, since_seconds=900, limit=20, exception=None):
                outer.calls.append(("loki", service_name, exception))
                entries = [entry if isinstance(entry, tuple) else (entry, "") for entry in loki]
                return [line for line, message in entries
                        if re.search(pattern, line) and (exception is None or re.search(exception, message))]

        class Alerts:
            def query(self, expression):
                return query(expression) if query else []

        self.keycloak = keycloak
        self.world = harness.World(Compose(), Alerts(), Orders(), Logs(), lambda message: None, realm=harness.Realm(keycloak))
        self.world.caused_at = time.time() - 100

    def verbs(self):
        return [call[0] for call in self.calls]


def shipping_account():
    return {"service-account-shipping-worker": ["orders:delivery-address"]}


class Refusals(unittest.TestCase):
    """address_refused and contact_refused are one shape over two grants."""

    def modules(self):
        from scenarios import address_refused, contact_refused
        return [address_refused, contact_refused]

    def test_each_takes_the_grant_restarts_the_worker_then_orders_and_never_the_other_way_round(self):
        for module in self.modules():
            with self.subTest(module.RUNBOOK):
                stack = Stack(held=[module.GRANT.role])
                module.cause(stack.world)
                self.assertEqual([], stack.keycloak.held, "the grant was not taken")
                steps = [call for call in stack.calls if call[0] in ("revoke", "restart", "place")]
                self.assertEqual([("revoke",), ("restart", module.WORKER), ("place",)], steps,
                                 "the order must meet a token issued after the revoke")

    def test_each_restore_gives_the_grant_back_reads_it_back_and_restarts_the_worker(self):
        for module in self.modules():
            with self.subTest(module.RUNBOOK):
                stack = Stack()
                module.restore(stack.world)
                self.assertEqual([module.GRANT.role], stack.keycloak.held)
                self.assertEqual([("grant",), ("restart", module.WORKER)], stack.calls)

    def test_a_restore_that_leaves_a_wider_grant_is_an_error_and_not_a_pass(self):
        for module in self.modules():
            with self.subTest(module.RUNBOOK):
                with self.assertRaises(harness.GameDayError):
                    module.restore(Stack(held=["something-wider"]).world)

    def test_each_restore_is_safe_after_a_cause_that_never_ran(self):
        for module in self.modules():
            with self.subTest(module.RUNBOOK):
                stack = Stack()
                module.restore(stack.world)
                module.restore(stack.world)
                self.assertEqual(sorted(set(stack.keycloak.held)), sorted(set([module.GRANT.role])))

    def test_each_settle_is_the_rules_window(self):
        for module in self.modules():
            self.assertEqual(30 * 60, module.SETTLE.signal_seconds)

    def test_each_grant_is_the_realm_exports_one(self):
        realm = json.loads((harness.ROOT / "deploy/compose/keycloak/realm-export.json").read_text(encoding="utf-8"))
        accounts = {user["username"]: user.get("clientRoles", {}) for user in realm["users"]}
        for module in self.modules():
            with self.subTest(module.RUNBOOK):
                self.assertEqual({module.GRANT.client: [module.GRANT.role]}, accounts[module.GRANT.account])

    def test_the_address_first_step_needs_the_lost_role_in_the_exception_and_the_other_rows_do_not_pass(self):
        from scenarios import address_refused as module
        line = "Fulfilment pass for shipment 1 on order 2 failed; the row backs off."
        lost = (line, "The realm issued this host 0 permission(s) where ADR-052 names exactly one.")
        self.assertTrue(module.first_step(Stack(loki=[lost]).world)[0])
        other = (line, "The realm did not issue this host a usable token.")
        self.assertFalse(module.first_step(Stack(loki=[other]).world)[0])
        self.assertFalse(module.first_step(Stack(loki=[("Tracking pass failed; the row backs off.", lost[1])]).world)[0])
        self.assertFalse(module.first_step(Stack(loki=[]).world)[0])
        self.assertIn("stdout is empty", module.first_step(Stack(loki=[lost]).world)[1])
        self.assertNotIn("stdout is empty", module.first_step(Stack(loki=[lost], stdout="x").world)[1])

    def test_the_contact_first_step_needs_the_lost_role_in_the_exception_and_the_other_rows_do_not_pass(self):
        from scenarios import contact_refused as module
        line = "The contact read for notification 1 on order 2 was refused over this host's credential; the row backs off."
        lost = (line, "The realm issued 0 realm-management role(s) where ADR-052 names 3.")
        self.assertTrue(module.first_step(Stack(loki=[lost]).world)[0])
        self.assertFalse(module.first_step(Stack(loki=[(line, "did not issue this host a usable token")]).world)[0])
        self.assertFalse(module.first_step(Stack(loki=[]).world)[0])

    def test_the_exception_filter_reaches_loki_as_a_literal_pattern(self):
        from scenarios import address_refused as module
        stack = Stack(loki=[("Fulfilment pass for shipment 1 on order 2 failed; the row backs off.", "x")])
        module.first_step(stack.world)
        sent = [call[2] for call in stack.calls if call[0] == "loki" and call[2]]
        self.assertEqual([r"permission\(s\) where ADR-052 names exactly one"], sent)

    def test_the_line_templates_are_the_workers_own(self):
        from scenarios import address_refused, contact_refused
        fulfilment = (harness.ROOT / "src/Services/Shipping/Shipping.Infrastructure/Fulfilment/FulfilmentWorker.cs").read_text(
            encoding="utf-8")
        self.assertIn("Fulfilment pass for shipment {ShipmentId} on order {OrderId} failed; the row backs off.", fulfilment)
        self.assertTrue(re.search(address_refused.PASS_FAILED, "Fulfilment pass for shipment 1 on order 2 failed; the row backs off."))
        send = " ".join((harness.ROOT / "src/Services/Notifications/Notifications.Infrastructure/Delivery/SendWorker.cs")
                        .read_text(encoding="utf-8").replace('"', " ").split())
        self.assertIn("The contact read for notification {NotificationId} on order {OrderId} was refused over this host's", send)
        self.assertTrue(re.search(contact_refused.CONTACT_REFUSED,
                                  "The contact read for notification 1 on order 2 was refused over this host's credential; the row"))

    def test_the_contact_cause_forgets_the_stored_contacts_in_the_notifications_database_first(self):
        from scenarios import contact_refused as module
        stack = Stack(held=[module.GRANT.role])
        module.cause(stack.world)
        self.assertEqual(("sql", "Notifications", "DELETE FROM notifications.ContactRecords;"), stack.calls[0])

    def test_the_log_wording_and_the_exception_rows_are_the_runbooks_and_the_sources(self):
        from scenarios import address_refused, contact_refused
        for module, runbook in ((address_refused, "address-refused.md"), (contact_refused, "contact-refused.md")):
            with self.subTest(runbook):
                text = (harness.ROOT / "docs" / "runbooks" / runbook).read_text(encoding="utf-8")
                self.assertIn(module.CAUSE_MESSAGE, text)
                self.assertIn(module.GRANT.role, text)


class Planted(unittest.TestCase):
    def test_unscanned_plants_a_booked_row_older_than_the_first_scan_age_and_not_due_for_a_poll(self):
        from scenarios import unscanned_shipment as module
        source = (harness.ROOT / "src/Services/Shipping/Shipping.Infrastructure/Observability/ShipmentStats.cs").read_text(
            encoding="utf-8")
        age_days = int(re.search(r"FirstScanAge = TimeSpan\.FromDays\((\d+)\)", source).group(1))
        plant = " ".join(module._PLANT.split())
        self.assertIn("N'Booked'", plant)
        self.assertGreater(int(re.search(r"DATEADD\(day, -(\d+), SYSDATETIMEOFFSET\(\)\)\);", plant).group(1)), age_days)
        self.assertRegex(plant, r"DATEADD\(day, 1, SYSDATETIMEOFFSET\(\)\), DATEADD\(day, -")
        stack = Stack()
        module.cause(stack.world)
        self.assertEqual("Shipping", stack.calls[0][1])

    def test_unscanned_first_step_needs_the_planted_row_in_the_second_query(self):
        from scenarios import unscanned_shipment as module
        row = f"{module.SHIPMENT_ID.upper()}|{module.SHIPMENT_ID.upper()}|crr_GAMEDAY|2026-10-06"
        self.assertTrue(module.first_step(Stack(sql_results=["NULL", row]).world)[0])
        self.assertFalse(module.first_step(Stack(sql_results=["NULL", ""]).world)[0])

    def test_unscanned_restore_deletes_by_id_and_nothing_wider(self):
        from scenarios import unscanned_shipment as module
        stack = Stack()
        module.restore(stack.world)
        self.assertEqual(f"DELETE FROM shipping.Shipments WHERE Id = '{module.SHIPMENT_ID}';", stack.calls[0][2])

    def test_unscanned_queries_are_the_runbooks(self):
        from scenarios import unscanned_shipment as module
        text = (harness.ROOT / "docs/runbooks/unscanned-shipment.md").read_text(encoding="utf-8")
        squash = lambda s: re.sub(r"\s+", "", s)
        self.assertIn(squash(module._LAST_SCAN), squash(text))
        self.assertIn(squash(module._UNSCANNED), squash(text))

    def test_unattributed_plants_an_unowned_row_that_satisfies_the_tables_checks(self):
        from scenarios import unattributed_order as module
        plant = " ".join(module._PLANT.split())
        self.assertIn("VALUES ('" + module.ORDER_ID + "', NULL, N'EUR', SYSDATETIMEOFFSET(), 1.00,", plant)
        self.assertIn("(OrderId, CustomerId, PaymentCurrency, AuthorisedAt, AuthorisedAmount, FirstSeenAt, AsOf)", plant)

    def test_unattributed_first_step_runs_the_runbooks_branch_for_an_order_ordering_never_had(self):
        from scenarios import unattributed_order as module
        row = f"{module.ORDER_ID.upper()}|2026-10-10|x"
        stack = Stack(sql_results=[row, "", ""])
        ok, detail = module.first_step(stack.world)
        self.assertTrue(ok, detail)
        self.assertEqual(["Bff", "Ordering", "Bff", "Bff"], [call[1] for call in stack.calls])

    def test_unattributed_first_step_fails_when_the_row_is_missing_when_ordering_has_it_or_the_delete_leaves_it(self):
        from scenarios import unattributed_order as module
        row = f"{module.ORDER_ID.upper()}|x"
        self.assertFalse(module.first_step(Stack(sql_results=[""]).world)[0])
        self.assertFalse(module.first_step(Stack(sql_results=[row, module.ORDER_ID.upper() + "|c|Placed"]).world)[0])
        self.assertFalse(module.first_step(Stack(sql_results=[row]).world)[0])

    def test_unattributed_delete_is_the_runbooks_guarded_one(self):
        from scenarios import unattributed_order as module
        text = re.sub(r"\s+", "", (harness.ROOT / "docs/runbooks/unattributed-order.md").read_text(encoding="utf-8"))
        generic = re.sub(r"\s+", "", module._DELETE.replace(f"'{module.ORDER_ID}'", "@OrderId"))
        self.assertIn(generic, text)
        self.assertIn(re.sub(r"\s+", "", module._FIND), text)

    def test_unattributed_is_not_backdated(self):
        from scenarios import unattributed_order as module
        self.assertNotIn("DATEADD", module._PLANT)


class QueueBacklog(unittest.TestCase):
    def fake_time(self):
        clock = base.Clock()
        return clock, types.SimpleNamespace(monotonic=clock, sleep=clock.sleep)

    def test_it_pauses_the_consumer_orders_and_unpauses_it_again_for_the_whole_for(self):
        from scenarios import queue_backlog as module
        stack = Stack()
        clock, fake = self.fake_time()
        with mock.patch.object(module, "time", fake):
            module.cause(stack.world)
        self.assertGreaterEqual(clock.now, module.FOR_SECONDS + 60)
        verbs = stack.verbs()
        self.assertEqual(["pause", "place", "unpause"] * (len(verbs) // 3), verbs)
        self.assertGreaterEqual(len(verbs) // 3, 8)
        self.assertEqual("unpause", verbs[-1], "a cause that returns leaves the consumer running")

    def test_it_never_restarts_the_consumer_because_a_new_series_hides_the_late_deliveries_from_rate(self):
        from scenarios import queue_backlog as module
        stack = Stack()
        _, fake = self.fake_time()
        with mock.patch.object(module, "time", fake):
            module.cause(stack.world)
        self.assertFalse({"stop", "start", "restart"} & set(stack.verbs()))

    def test_a_message_waits_longer_than_the_lag_rules_target(self):
        from scenarios import queue_backlog as module
        rules = (harness.ROOT / "deploy/observability/alerts/platform-alerts.yaml").read_text(encoding="utf-8")
        target = int(re.search(r"\(messaging_delivery_lag_seconds_bucket\[10m\]\)\)\s*\)\s*> (\d+)", rules).group(1))
        self.assertGreater(module.HOLD_SECONDS, 5 * target)

    def test_the_queries_are_the_runbooks_and_the_rules(self):
        from scenarios import queue_backlog as module
        squash = lambda s: re.sub(r"\s+", "", s)
        text = squash((harness.ROOT / "docs/runbooks/queue-backlog.md").read_text(encoding="utf-8"))
        self.assertIn(squash(module.CONSUME_RATE), text)
        rules = squash((harness.ROOT / "deploy/observability/alerts/platform-alerts.yaml").read_text(encoding="utf-8"))
        self.assertIn(squash(module.LAG_P95) + ">2", rules)

    def answers(self, lag="3.2", rate="0.4"):
        def query(expression):
            value = rate if "consume_ea_total" in expression else lag
            return [{"metric": {"service_name": "Web.Bff"}, "value": [1, value]}]
        return query

    @mock.patch("scenarios.queue_backlog.QUERY_WAIT_SECONDS", 0)
    def test_the_first_step_reads_both_queries_for_the_consumer_and_that_it_runs(self):
        from scenarios import queue_backlog as module
        ok, detail = module.first_step(Stack(query=self.answers()).world)
        self.assertTrue(ok, detail)
        self.assertIn("Web.Bff", detail)

    @mock.patch("scenarios.queue_backlog.QUERY_WAIT_SECONDS", 0)
    def test_the_first_step_fails_for_a_consumer_that_is_not_running_or_a_lag_that_is_not_late(self):
        from scenarios import queue_backlog as module
        self.assertFalse(module.first_step(Stack(running=False, query=self.answers()).world)[0])
        self.assertFalse(module.first_step(Stack(query=self.answers(lag="0.5")).world)[0])
        self.assertFalse(module.first_step(Stack(query=lambda expression: []).world)[0])

    def test_restore_starts_the_consumer_whatever_the_cause_left(self):
        from scenarios import queue_backlog as module
        stack = Stack()
        module.restore(stack.world)
        module.restore(stack.world)
        self.assertEqual([("unpause", "web-bff")] * 2, stack.calls)


if __name__ == "__main__":
    unittest.main()
