#!/usr/bin/env python3
"""What the gate would catch, which running it against this repository does not say.

    py -3.12 -m unittest discover -s .github/plan-size-gate
"""
from __future__ import annotations

import tempfile
import unittest
from datetime import date
from pathlib import Path

import plan_size_gate as gate


class PlanSizeGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.plans = Path(self._tmp.name)

    def write(self, name: str, size: int) -> None:
        path = self.plans / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"x" * size)

    def test_a_new_oversized_plan_fails(self) -> None:
        self.write("2026-10-08-feature.md", gate.LIMIT_BYTES + 1)
        problems = gate.check(self.plans)
        self.assertEqual(1, len(problems))
        self.assertIn("2026-10-08-feature.md", problems[0])

    def test_a_new_plan_at_the_limit_passes(self) -> None:
        self.write("2026-10-08-feature.md", gate.LIMIT_BYTES)
        self.assertEqual([], gate.check(self.plans))

    def test_a_plan_on_the_day_the_rule_landed_is_judged(self) -> None:
        self.write(f"{gate.RULE_LANDED.isoformat()}-feature.md", gate.LIMIT_BYTES + 1)
        self.assertEqual(1, len(gate.check(self.plans)))

    def test_an_oversized_plan_from_before_the_rule_passes(self) -> None:
        self.write("2026-10-06-feature.md", gate.LIMIT_BYTES * 4)
        self.write("2026-10-08-small.md", 10)
        self.assertEqual([], gate.check(self.plans))

    def test_a_file_inside_a_new_directory_is_judged(self) -> None:
        self.write("2026-10-09-adjudication/round-1.md", gate.LIMIT_BYTES + 1)
        problems = gate.check(self.plans)
        self.assertEqual(1, len(problems))
        self.assertIn("2026-10-09-adjudication/round-1.md", problems[0])

    def test_an_undated_file_fails_rather_than_passing_unjudged(self) -> None:
        self.write("notes.md", 10)
        self.assertEqual(1, len(gate.check(self.plans)))

    def test_an_impossible_date_fails_rather_than_passing_unjudged(self) -> None:
        self.write("2026-13-45-feature.md", 10)
        self.assertEqual(1, len(gate.check(self.plans)))

    def test_a_missing_directory_fails(self) -> None:
        self.assertEqual(1, len(gate.check(self.plans / "absent")))

    def test_an_empty_directory_fails(self) -> None:
        self.assertEqual(1, len(gate.check(self.plans)))

    def test_the_cutoff_is_the_day_the_rule_landed(self) -> None:
        self.assertEqual(date(2026, 10, 7), gate.RULE_LANDED)

    def test_this_repository_s_plans_pass(self) -> None:
        self.assertEqual([], gate.check(gate.DEFAULT_PLANS))

    def test_main_exits_non_zero_on_a_failure(self) -> None:
        self.write("2026-10-08-feature.md", gate.LIMIT_BYTES + 1)
        self.assertEqual(1, gate.main(["--plans", str(self.plans)]))
        self.write("2026-10-08-feature.md", 10)
        self.assertEqual(0, gate.main(["--plans", str(self.plans)]))


if __name__ == "__main__":
    unittest.main()
