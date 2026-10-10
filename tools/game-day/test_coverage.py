"""The coverage test: every runbook has a script or a stated reason not to.

    cd tools/game-day && py -3.12 -m unittest

The cases around the real tree prove it can fail, which a gate that cannot never does.
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

    def test_this_pull_request_delivers_the_three_scripts(self):
        self.assertTrue({"outbox_broker", "projection_lag", "outbox_abandoned"} <= coverage.scripts())

    def test_a_reason_is_never_empty_and_owed_names_a_real_pull_request(self):
        for name, reason in coverage.NOT_ON_COMPOSE.items():
            self.assertGreater(len(reason.split()), 4, name)
        for name, pulls in coverage.OWED.items():
            self.assertTrue(set(pulls) <= coverage.OWED_PULL_REQUESTS, name)


class Problems(unittest.TestCase):
    """The rules over a fixture, which is how a case proves a runbook dropped in fails."""

    def problems(self, runbooks, scripts=(), not_on_compose=None, owed=None, shared=()):
        return coverage.problems(set(runbooks), set(scripts), not_on_compose or {}, owed or {}, set(shared))

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
        self.assertEqual([], self.problems({"a.md"}, owed={"a.md": ("PR-2",)}))

    def test_a_script_beside_a_reason_that_says_it_cannot_run_fails(self):
        found = self.problems({"a.md"}, {"a"}, not_on_compose={"a.md": "no kube-state-metrics here"})
        self.assertIn("a.md: has a script and is listed NOT_ON_COMPOSE", found[0])

    def test_a_delivered_script_must_leave_owed(self):
        found = self.problems({"a.md"}, {"a"}, owed={"a.md": ("PR-2",)})
        self.assertIn("a.md: has a script and is still listed OWED", found[0])

    def test_a_shared_runbook_may_have_a_script_and_still_be_owed_its_other_half(self):
        self.assertEqual([], self.problems({"a.md"}, {"a"}, owed={"a.md": ("PR-4",)}, shared={"a.md"}))

    def test_both_tables_fail(self):
        found = self.problems({"a.md"}, not_on_compose={"a.md": "no kube-state-metrics here"}, owed={"a.md": ("PR-2",)})
        self.assertIn("a.md: listed in both", "\n".join(found))

    def test_owed_must_name_a_pull_request(self):
        self.assertIn("OWED must name", self.problems({"a.md"}, owed={"a.md": ("PR-9",)})[0])
        self.assertIn("OWED must name", self.problems({"a.md"}, owed={"a.md": ()})[0])

    def test_an_empty_reason_fails(self):
        self.assertIn("carries no reason", "\n".join(self.problems({"a.md"}, not_on_compose={"a.md": " "})))

    def test_a_listing_for_a_deleted_runbook_fails(self):
        found = "\n".join(self.problems(set(), owed={"gone.md": ("PR-2",)}))
        self.assertIn("gone.md: listed, but docs/runbooks/ has no such file", found)

    def test_a_script_with_no_runbook_fails(self):
        self.assertIn("no runbook of that name", self.problems(set(), {"orphan"})[0])


class Scenarios(unittest.TestCase):
    """The module shape every later script follows, held by what the header promises."""

    def modules(self):
        return [(name, importlib.import_module(f"scenarios.{name}")) for name in sorted(coverage.scripts())]

    def test_there_are_scenarios_to_check(self):
        self.assertGreaterEqual(len(self.modules()), 3)

    def test_each_module_is_named_for_its_runbook_and_alert(self):
        rules = LOADED_RULES.read_text(encoding="utf-8")
        for name, module in self.modules():
            with self.subTest(name):
                self.assertEqual(name, coverage.module_name(module.RUNBOOK))
                runbook = (coverage.RUNBOOKS / module.RUNBOOK).read_text(encoding="utf-8")
                self.assertIn(f"`{module.ALERT}`", runbook, "the runbook does not name the alert")
                self.assertIn(f"alert: {module.ALERT}\n", rules, "a rule the Compose Prometheus loads")

    def test_each_header_names_the_alert_its_cause_its_first_step_and_its_restore(self):
        for name, module in self.modules():
            with self.subTest(name):
                header = module.__doc__
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

    def test_a_deadline_covers_the_rules_own_threshold(self):
        rules = LOADED_RULES.read_text(encoding="utf-8")
        for name, module in self.modules():
            with self.subTest(name):
                block = rules.split(f"alert: {module.ALERT}\n", 1)[1].split("- alert:", 1)[0]
                self.assertNotIn("for:", block, "a `for:` here must be in the deadline; update this scenario")
                threshold = re.search(r"\}\) > (\d+)\n", block + "\n")
                if threshold and "outbox_oldest_age" in block:
                    self.assertGreaterEqual(module.DEADLINE.signal_seconds, int(threshold.group(1)))


if __name__ == "__main__":
    unittest.main()
