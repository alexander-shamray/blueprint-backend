#!/usr/bin/env python3
"""What the gate would catch, which running it against this repository does not say.

    py -3.12 -m unittest discover -s .github/issue-form-gate
"""
from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import issue_form_gate as gate

HELPER = """\
case "$kind" in
  security|bug|test-gap) ;;
  *) exit 2 ;;
esac
case "$severity" in
  critical|high|medium|low) ;;
  *) exit 2 ;;
esac
"""

CONTRACT = """\
- **The issue vocabulary is wider than the label helper**: kind is
  `security`, `bug`, `test-gap` or `documentation`, and the helper does
  not create the last.
- **A reply is not a resolution.** Kind is `nothing` here.
"""

LABELS = {"security", "bug", "test-gap", "documentation", "critical", "high", "medium", "low",
          "harness", "code", "test"}


def form(kind: str = "bug security test-gap documentation", severity: str = "critical high medium low",
         area: str = "harness code documentation test", extra: str = "") -> str:
    def dropdown(name: str, words: str) -> str:
        options = "".join(f"        - {word}\n" for word in words.split())
        return (f"  - type: dropdown\n    id: {name}\n    attributes:\n      label: {name}\n"
                f"      options:\n{options}    validations:\n      required: true\n")
    return ("name: Finding\nbody:\n  - type: markdown\n    attributes:\n      value: text\n"
            + dropdown("kind", kind) + dropdown("severity", severity) + dropdown("area", area) + extra
            + "  - type: textarea\n    id: found\n    attributes:\n      label: What was found\n")


class IssueFormGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        root = Path(self._tmp.name)
        self.templates = root / "ISSUE_TEMPLATE"
        self.templates.mkdir()
        self.helper = root / "gh-issue-create.sh"
        self.contract = root / "change-locality.md"
        self.helper.write_text(HELPER, encoding="utf-8")
        self.contract.write_text(CONTRACT, encoding="utf-8")
        self.write("config.yml", "blank_issues_enabled: false\n")
        self.write("finding.yml", form())

    def write(self, name: str, text: str) -> None:
        (self.templates / name).write_text(text, encoding="utf-8")

    def check(self, labels: set[str] = LABELS) -> list[str]:
        return gate.check(self.templates, self.helper, self.contract, labels)

    def assert_one(self, needle: str) -> None:
        problems = self.check()
        self.assertEqual(1, len(problems), problems)
        self.assertIn(needle, problems[0])

    def test_a_form_matching_its_owners_passes(self) -> None:
        self.assertEqual([], self.check())

    def test_a_kind_the_contract_lacks_fails(self) -> None:
        self.write("finding.yml", form(kind="bug security test-gap documentation enhancement"))
        problems = self.check(LABELS | {"enhancement"})
        self.assertEqual(1, len(problems), problems)
        self.assertIn("extra ['enhancement']", problems[0])

    def test_a_kind_the_contract_names_but_the_form_drops_fails(self) -> None:
        self.write("finding.yml", form(kind="bug security test-gap"))
        self.assert_one("missing ['documentation']")

    def test_a_severity_the_helper_lacks_fails(self) -> None:
        self.write("finding.yml", form(severity="critical high medium low trivial"))
        problems = self.check(LABELS | {"trivial"})
        self.assertEqual(1, len(problems), problems)
        self.assertIn("extra ['trivial']", problems[0])

    def test_a_severity_the_helper_gains_fails(self) -> None:
        self.helper.write_text(HELPER.replace("critical|high", "blocker|critical|high"), encoding="utf-8")
        self.assert_one("missing ['blocker']")

    def test_a_repeated_option_fails(self) -> None:
        self.write("finding.yml", form(severity="critical high medium low low"))
        self.assert_one("repeats an option")

    def test_a_helper_kind_outside_the_contract_fails(self) -> None:
        self.helper.write_text(HELPER.replace("security|bug|test-gap", "security|bug|test-gap|chore"),
                               encoding="utf-8")
        self.assert_one("['chore']")

    def test_an_option_that_is_not_a_label_fails(self) -> None:
        self.assert_one_label_missing("harness")

    def assert_one_label_missing(self, label: str) -> None:
        problems = self.check(LABELS - {label})
        self.assertEqual(1, len(problems), problems)
        self.assertIn(f"option {label} is not a label", problems[0])

    def test_a_form_attaching_fixed_labels_fails(self) -> None:
        for key in ("labels: [enhancement]\n", "labels:\n  - bug\n", '"labels": [bug]\n'):
            with self.subTest(key=key):
                self.write("finding.yml", form().replace("body:\n", key + "body:\n"))
                self.assert_one("top-level labels key")

    def test_a_field_named_labels_inside_the_body_passes(self) -> None:
        field = "  - type: input\n    id: labels\n    attributes:\n      label: labels\n"
        self.write("finding.yml", form() + field)
        self.assertEqual([], self.check())

    def test_a_missing_dropdown_fails(self) -> None:
        self.write("finding.yml", form().replace("id: area", "id: areas"))
        problems = self.check()
        self.assertTrue(any("no area dropdown" in p for p in problems), problems)
        self.assertTrue(any("dropdown areas is not one of" in p for p in problems), problems)

    def test_every_form_is_read(self) -> None:
        self.write("second.yaml", form(kind="bug"))
        problems = self.check()
        self.assertEqual(1, len(problems), problems)
        self.assertIn("second.yaml's kind", problems[0])

    def test_a_markdown_template_beside_the_form_fails(self) -> None:
        self.write("bug_report.md", "---\nname: Bug\nabout: anything\n---\n")
        self.assert_one("bug_report.md is neither config.yml nor a form")

    def test_a_directory_beside_the_form_fails(self) -> None:
        (self.templates / "drafts").mkdir()
        self.assert_one("drafts is neither config.yml nor a form")

    def test_blank_issues_left_on_fails(self) -> None:
        self.write("config.yml", "blank_issues_enabled: true\n")
        self.assert_one("blank_issues_enabled: false")

    def test_a_missing_config_fails(self) -> None:
        (self.templates / "config.yml").unlink()
        self.assert_one("blank_issues_enabled: false")

    def test_a_directory_with_no_form_fails(self) -> None:
        (self.templates / "finding.yml").unlink()
        self.assert_one("holds no issue form")

    def test_a_missing_directory_fails(self) -> None:
        self.templates = self.templates / "absent"
        self.assert_one("is not a directory")

    def test_options_after_a_blank_line_are_still_read(self) -> None:
        self.write("finding.yml", form().replace("        - low\n", "\n        - low\n        - trivial\n"))
        problems = self.check(LABELS | {"trivial"})
        self.assertEqual(1, len(problems), problems)
        self.assertIn("extra ['trivial']", problems[0])

    def test_a_flow_style_options_list_is_refused(self) -> None:
        self.write("finding.yml", form().replace("      options:\n        - critical\n        - high\n"
                                                 "        - medium\n        - low\n",
                                                 "      options: [critical, high, medium, low]\n"))
        with self.assertRaisesRegex(gate.InputRefused, "dropdown severity has no options"):
            self.check()

    def test_an_option_that_is_not_a_bare_name_is_refused(self) -> None:
        self.write("finding.yml", form().replace("        - low\n", "        - 'low'\n"))
        with self.assertRaisesRegex(gate.InputRefused, "not a bare label name"):
            self.check()

    def test_a_dropdown_twice_is_refused(self) -> None:
        self.write("finding.yml", form() + form().split("body:\n", 1)[1])
        with self.assertRaisesRegex(gate.InputRefused, "appears twice"):
            self.check()

    def test_a_contract_without_the_bullet_is_refused(self) -> None:
        self.contract.write_text("- **Something else**: kind is `bug`.\n", encoding="utf-8")
        with self.assertRaisesRegex(gate.InputRefused, "no bullet opening"):
            self.check()

    def test_the_contract_list_is_read_across_wrapped_lines(self) -> None:
        self.assertEqual(["security", "bug", "test-gap", "documentation"], gate.contract_kinds(self.contract))

    def test_a_kind_list_in_a_later_bullet_is_not_read(self) -> None:
        self.contract.write_text("- **The issue vocabulary is wider than the label helper**: see below.\n"
                                 "- **Another rule**: kind is `chore`.\n", encoding="utf-8")
        with self.assertRaisesRegex(gate.InputRefused, "has no `kind is ...` list"):
            gate.contract_kinds(self.contract)

    def test_a_helper_without_the_arm_is_refused(self) -> None:
        self.helper.write_text("echo nothing\n", encoding="utf-8")
        with self.assertRaisesRegex(gate.InputRefused, "no `case"):
            self.check()

    def test_main_refuses_an_empty_label_list(self) -> None:
        self.assertEqual(1, self.main("[]"))

    def test_main_refuses_stdin_that_is_not_json(self) -> None:
        self.assertEqual(1, self.main(""))

    def test_main_exits_by_the_check(self) -> None:
        labels = json.dumps([{"name": name} for name in LABELS])
        self.assertEqual(0, self.main(labels))
        self.write("config.yml", "blank_issues_enabled: true\n")
        self.assertEqual(1, self.main(labels))

    def main(self, stdin: str) -> int:
        return gate.main(["--templates", str(self.templates), "--helper", str(self.helper),
                          "--contract", str(self.contract)], stdin=stdin)


class ThisRepositoryTests(unittest.TestCase):
    """The reader against the files the gate actually reads, so a reshaped file fails here first."""

    def test_this_repository_s_form_carries_every_governed_dropdown(self) -> None:
        found = gate.dropdowns(gate.DEFAULT_TEMPLATES / "finding.yml")
        self.assertEqual(set(gate.GOVERNED), set(found))

    def test_this_repository_s_owners_are_read(self) -> None:
        self.assertIn("documentation", gate.contract_kinds(gate.DEFAULT_CONTRACT))
        self.assertIn("bug", gate.case_arm(gate.DEFAULT_HELPER, "kind"))
        self.assertIn("low", gate.case_arm(gate.DEFAULT_HELPER, "severity"))

    def test_this_repository_s_forms_pass_against_their_own_options_as_labels(self) -> None:
        labels = {option for options in gate.dropdowns(gate.DEFAULT_TEMPLATES / "finding.yml").values()
                  for option in options}
        problems = gate.check(gate.DEFAULT_TEMPLATES, gate.DEFAULT_HELPER, gate.DEFAULT_CONTRACT, labels)
        self.assertEqual([], problems)


if __name__ == "__main__":
    unittest.main()
