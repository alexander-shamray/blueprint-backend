#!/usr/bin/env python3
"""The plan-size gate over this repository's own plans.

`test_plan_size_gate.py` is the payload's and judges plans it writes per case; this one, split out of it by
blueprint-harness#5, is local because only this repository keeps `docs/superpowers/plans/`.

    py -3.12 -m unittest discover -s .github/plan-size-gate
"""
from __future__ import annotations

import unittest

import plan_size_gate as gate


class ThisRepositorysPlans(unittest.TestCase):
    def test_this_repository_s_plans_pass(self) -> None:
        self.assertEqual([], gate.check(gate.DEFAULT_PLANS))


if __name__ == "__main__":
    unittest.main()
