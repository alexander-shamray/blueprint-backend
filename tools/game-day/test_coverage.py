"""The coverage test: every runbook has a script or a stated reason not to.
Run: cd tools/game-day && py -3.12 -m unittest. The cases around the real tree prove it can fail.
"""

import importlib
import re
import tempfile
import unittest
from pathlib import Path

import runbook_coverage as coverage

LOADED_RULES = coverage.ROOT / "deploy" / "observability" / "alerts" / "platform-alerts.yaml"


class RealTree(unittest.TestCase):
    def test_every_runbook_is_covered_or_excused(self):
        self.assertEqual([], coverage.check())

    def test_the_runbook_set_is_read_from_the_directory_and_is_not_empty(self):
        names = coverage.runbooks()
        self.assertIn("outbox-broker.md", names)
        self.assertNotIn("README.md", names)
        self.assertEqual(len(list(coverage.RUNBOOKS.glob("*.md"))) - 1, len(names))

    def test_the_series_delivers_these_scripts(self):
        self.assertTrue({"outbox_broker", "projection_lag", "outbox_abandoned", "queue_backlog", "address_refused",
                         "contact_refused", "unscanned_shipment", "unattributed_order", "erasure_overdue",
                         "error_rate", "latency", "outbox_growth", "error_queue", "skipped_queue"}
                        <= coverage.scripts())

    def test_every_runbook_without_a_script_is_one_that_cannot_run_on_compose(self):
        self.assertFalse(hasattr(coverage, "OWED"))
        unscripted = {name for name in coverage.runbooks() if coverage.module_name(name) not in coverage.scripts()}
        self.assertEqual(set(coverage.NOT_ON_COMPOSE), unscripted)

    def test_the_rules_are_read_from_the_loaded_file_and_both_shared_runbooks_are_caused_whole(self):
        rules = coverage.loaded_rules()
        self.assertEqual({"DeliveryLagHigh", "QueueBacklogGrowing"}, rules["queue-backlog.md"])
        self.assertEqual({"ErrorRateGateway", "ErrorRateService"}, rules["error-rate.md"])
        causes = coverage.caused(coverage.scripts())
        shared = set(coverage._gate().SHARED_RUNBOOKS)
        self.assertEqual({"error-rate.md", "queue-backlog.md"}, shared)
        for name in shared:
            with self.subTest(name):
                self.assertEqual(rules[name], causes[coverage.module_name(name)])

    def test_a_reason_is_never_empty(self):
        for name, reason in coverage.NOT_ON_COMPOSE.items():
            self.assertGreater(len(reason.split()), 4, name)


class Problems(unittest.TestCase):
    """The rules over a fixture, which is how a case proves a runbook dropped in fails."""

    def problems(self, runbooks, scripts=(), not_on_compose=None, rules=None, causes=None):
        return coverage.problems(set(runbooks), set(scripts), not_on_compose or {}, rules or {}, causes or {})

    def test_a_new_runbook_with_nothing_fails(self):
        found = self.problems({"brand-new.md"})
        self.assertEqual(1, len(found))
        self.assertIn("brand-new.md: no script and no stated reason", found[0])

    def test_a_new_runbook_dropped_into_a_directory_is_seen_by_the_directory_read(self):
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "README.md").write_text("x", encoding="utf-8")
            Path(directory, "covered.md").write_text("x", encoding="utf-8")
            self.assertEqual({"covered.md"}, coverage.runbooks(Path(directory), {"README.md"}))
            self.assertEqual([], self.problems({"covered.md"}, {"covered"}))

            Path(directory, "dropped-in.md").write_text("x", encoding="utf-8")
            names = coverage.runbooks(Path(directory), {"README.md"})
            self.assertIn("dropped-in.md", names)
            self.assertIn("dropped-in.md: no script", "\n".join(self.problems(names, {"covered"})))

    def test_a_script_covers_a_runbook_named_for_it(self):
        self.assertEqual([], self.problems({"outbox-broker.md"}, {"outbox_broker"}))

    def test_a_reason_covers_a_runbook(self):
        self.assertEqual([], self.problems({"a.md"}, not_on_compose={"a.md": "no kube-state-metrics here"}))

    def test_a_script_beside_a_reason_that_says_it_cannot_run_fails(self):
        found = self.problems({"a.md"}, {"a"}, not_on_compose={"a.md": "no kube-state-metrics here"})
        self.assertIn("a.md: has a script and is listed NOT_ON_COMPOSE", found[0])

    def test_a_script_must_cause_every_loaded_rule_that_names_its_runbook(self):
        rules = {"a.md": {"First", "Second"}}
        found = self.problems({"a.md"}, {"a"}, rules=rules, causes={"a": {"First"}})
        self.assertEqual(
            ["a.md: a loaded rule sends Second here and its script does not cause it; add a phase as NEXT"], found)
        self.assertEqual([], self.problems({"a.md"}, {"a"}, rules=rules, causes={"a": {"First", "Second"}}))

    def test_a_rule_whose_runbook_cannot_run_on_compose_needs_no_cause(self):
        self.assertEqual([], self.problems({"a.md"}, not_on_compose={"a.md": "no kube-state-metrics here"},
                                           rules={"a.md": {"First"}}))

    def test_the_causes_are_read_through_next(self):
        causes = coverage.caused({"error_rate", "outbox_broker"})
        self.assertEqual({"ErrorRateGateway", "ErrorRateService"}, causes["error_rate"])
        self.assertEqual({"OutboxBrokerLaneStalled"}, causes["outbox_broker"])

    def test_an_empty_reason_fails(self):
        self.assertIn("carries no reason", "\n".join(self.problems({"a.md"}, not_on_compose={"a.md": " "})))

    def test_a_listing_for_a_deleted_runbook_fails(self):
        found = "\n".join(self.problems(set(), not_on_compose={"gone.md": "no kube-state-metrics here"}))
        self.assertIn("gone.md: listed, but docs/runbooks/ has no such file", found)

    def test_a_script_with_no_runbook_fails(self):
        self.assertIn("no runbook of that name", self.problems(set(), {"orphan"})[0])


class Scenarios(unittest.TestCase):
    """The module shape every later script follows, held by what the header promises."""

    def modules(self):
        """Each script, and the second phase of one that has a NEXT, which is held to the same rules."""
        found = []
        for name in sorted(coverage.scripts()):
            module = importlib.import_module(f"scenarios.{name}")
            found.append((name, module))
            if hasattr(module, "NEXT"):
                found.append((f"{name}.NEXT", module.NEXT))
        return found

    def header(self, name):
        """A second phase has no docstring of its own: its module's header names its alert."""
        return importlib.import_module(f"scenarios.{name.split('.')[0]}").__doc__

    def test_there_are_scenarios_to_check(self):
        self.assertGreaterEqual(len(self.modules()), 3)

    def test_each_module_is_named_for_its_runbook_and_alert(self):
        rules = LOADED_RULES.read_text(encoding="utf-8")
        for name, module in self.modules():
            with self.subTest(name):
                self.assertEqual(name.split(".")[0], coverage.module_name(module.RUNBOOK))
                runbook = (coverage.RUNBOOKS / module.RUNBOOK).read_text(encoding="utf-8")
                self.assertIn(f"`{module.ALERT}`", runbook, "the runbook does not name the alert")
                self.assertIn(f"alert: {module.ALERT}\n", rules, "a rule the Compose Prometheus loads")

    def test_each_header_names_the_alert_its_cause_its_first_step_and_its_restore(self):
        for name, module in self.modules():
            with self.subTest(name):
                header = self.header(name)
                self.assertIn(module.ALERT, header)
                for label in ("Cause:", "First step:", "Restore:"):
                    self.assertIn(label, header)

    def test_a_cause_forced_through_sql_says_so_with_the_organic_route(self):
        for name, module in self.modules():
            if getattr(module, "FORCED_THROUGH_SQL", False):
                with self.subTest(name):
                    self.assertIn("Forced through SQL", module.__doc__)
                    self.assertRegex(module.__doc__, r"organic route")

    def test_each_module_provides_the_four_calls_and_a_deadline(self):
        for name, module in self.modules():
            with self.subTest(name):
                for call in ("cause", "first_step", "restore", "settled"):
                    self.assertTrue(callable(getattr(module, call)), call)
                self.assertGreater(module.DEADLINE.seconds, 0)

    def block(self, module):
        return LOADED_RULES.read_text(encoding="utf-8").split(f"alert: {module.ALERT}\n", 1)[1].split("- alert:", 1)[0]

    def test_a_deadline_covers_the_rules_own_threshold(self):
        for name, module in self.modules():
            with self.subTest(name):
                block = self.block(module)
                threshold = re.search(r"\}\) > (\d+)\n", block + "\n")
                if threshold and "outbox_oldest_age" in block:
                    self.assertGreaterEqual(module.DEADLINE.signal_seconds, int(threshold.group(1)))
                age = re.search(r"max by \(service_name\) \(bff_orders_unattributed_seconds\) > (\d+)", block)
                if age:
                    self.assertGreaterEqual(module.DEADLINE.signal_seconds, int(age.group(1)))

    def test_a_deadline_carries_the_rules_for_exactly(self):
        for name, module in self.modules():
            with self.subTest(name):
                held = re.search(r"\n\s+for: (\d+)m\n", self.block(module))
                self.assertEqual(int(held.group(1)) * 60 if held else 0, module.DEADLINE.for_seconds)

    def test_a_rule_over_a_window_names_a_settle_as_long_as_the_window(self):
        for name, module in self.modules():
            with self.subTest(name):
                window = re.search(r"(?:increase|rate)\(\w+(?:\{[^}]*\})?\[(\d+)m\]\)", self.block(module))
                if window:
                    self.assertGreaterEqual(module.SETTLE.signal_seconds, int(window.group(1)) * 60)
                else:
                    self.assertFalse(hasattr(module, "SETTLE"), "a SETTLE without a window to outlast")


if __name__ == "__main__":
    unittest.main()
