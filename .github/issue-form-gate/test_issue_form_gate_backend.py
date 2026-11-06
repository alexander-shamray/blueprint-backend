#!/usr/bin/env python3
"""The issue-form gate's readers against this repository's change contract and its label helper.

`test_issue_form_gate.py` is the payload's and reads the shipped form; these cases, split out of it by
blueprint-harness#5, are local because the gate's default contract, `docs/change-locality.md`, is only this
repository's.

    py -3.12 -m unittest discover -s .github/issue-form-gate
"""
from __future__ import annotations

import unittest

import issue_form_gate as gate


class ThisRepositorysOwnersTests(unittest.TestCase):
    """The reader against the contract and helper the gate actually reads, so a reshaped file fails here first."""

    def test_this_repository_s_owners_are_read(self) -> None:
        self.assertIn("documentation", gate.contract_kinds(gate.DEFAULT_CONTRACT))
        self.assertIn("bug", gate.case_arm(gate.DEFAULT_HELPER, "kind"))
        self.assertIn("low", gate.case_arm(gate.DEFAULT_HELPER, "severity"))
        self.assertIn("code,test", gate.case_arm(gate.DEFAULT_HELPER, "area"))
        self.assertEqual({"harness", "code", "documentation", "test"}, set(gate.contract_areas(gate.DEFAULT_CONTRACT)))

    def test_this_repository_s_forms_pass_against_their_own_options_as_labels(self) -> None:
        labels = {option for options in gate.dropdowns(gate.DEFAULT_TEMPLATES / "finding.yml").values()
                  for option in options}
        problems = gate.check(gate.DEFAULT_TEMPLATES, gate.DEFAULT_HELPER, gate.DEFAULT_CONTRACT, labels)
        self.assertEqual([], problems)


if __name__ == "__main__":
    unittest.main()
