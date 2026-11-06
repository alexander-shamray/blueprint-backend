#!/usr/bin/env python3
"""The plan-size gate over this repository's own plans.

`test_plan_size_gate.py` is the payload's; this case reads `docs/superpowers/plans/`, which only this repository keeps.
"""
from __future__ import annotations

import unittest

import plan_size_gate as gate


class ThisRepositorysPlans(unittest.TestCase):
    def test_this_repository_s_plans_pass(self) -> None:
        self.assertEqual([], gate.check(gate.DEFAULT_PLANS))


if __name__ == "__main__":
    unittest.main()
