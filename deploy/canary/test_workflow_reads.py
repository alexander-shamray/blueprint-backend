"""How deploy.yml and smoke.sh read the descriptors."""

from __future__ import annotations

import json
import re
import tempfile
import unittest
from pathlib import Path

import descriptor_model
import workflow_reads


class DescriptorReadTests(unittest.TestCase):
    """Every descriptor the workflow reads, the canary and the smoke run read.

    The subject is the text deploy.yml and smoke.sh run.
    """

    def setUp(self) -> None:
        self.workloads = descriptor_model.load_plan()["workloads"]

    def _read(self, workflow: str | None = None, smoke: str | None = None,
              workloads: dict | None = None) -> tuple[dict, list[str]]:
        """What each reader takes in, and check 8's verdict, over these."""
        workloads = self.workloads if workloads is None else workloads
        with tempfile.TemporaryDirectory() as tmp:
            paths = []
            for name, text, real in (("deploy.yml", workflow, workflow_reads.WORKFLOW), ("smoke.sh", smoke, workflow_reads.SMOKE)):
                path = Path(tmp) / name
                path.write_text(real.read_text(encoding="utf-8") if text is None else text, encoding="utf-8")
                paths.append(path)
            return workflow_reads.descriptors_read(workloads, *paths), workflow_reads._descriptors_agree(workloads, *paths)

    def test_every_descriptor_the_workflow_reads_the_canary_and_the_smoke_run_read(self) -> None:
        read = workflow_reads.descriptors_read(self.workloads)

        self.assertTrue(read["workflow"], "deploy.yml reads no descriptor, so the comparison below is vacuous")
        self.assertLessEqual(
            read["workflow"], read["smoke"],
            f"deploy.yml reads {sorted(read['workflow'] - read['smoke'])}, which smoke.sh does not render")
        self.assertEqual(workflow_reads._descriptors_agree(self.workloads), [])

    def test_a_descriptor_without_its_smoke_block_is_one_the_smoke_run_cannot_read(self) -> None:
        workloads = json.loads(json.dumps(self.workloads))
        del workloads["gateway"]["smoke"]

        read, failures = self._read(workloads=workloads)

        self.assertIn("gateway", read["workflow"])
        self.assertNotIn("gateway", read["smoke"])
        self.assertTrue(any("gateway" in f and "does not render" in f for f in failures), failures)

    def test_a_workflow_only_name_is_refused(self) -> None:
        text = workflow_reads.WORKFLOW.read_text(encoding="utf-8").replace(
            "        type: string\n",
            "        type: choice\n        options: [catalog-api, no-such-release]\n", 1)

        read, failures = self._read(workflow=text)

        self.assertIn("no-such-release", read["workflow"])
        self.assertTrue(any("by hand" in f for f in failures), failures)
        self.assertTrue(any("no-such-release" in f and "no descriptor describes" in f for f in failures), failures)

    def test_a_menu_written_one_option_per_line_is_refused(self) -> None:
        text = workflow_reads.WORKFLOW.read_text(encoding="utf-8").replace(
            "        type: string\n",
            "        type: choice\n        options:\n          - catalog-api\n          - no-such-release\n", 1)

        read, failures = self._read(workflow=text)

        self.assertIn("no-such-release", read["workflow"])
        self.assertTrue(any("by hand (catalog-api, no-such-release)" in f for f in failures), failures)
        self.assertTrue(any("no-such-release" in f and "no descriptor describes" in f for f in failures), failures)

    def test_a_choice_input_with_no_options_is_refused(self) -> None:
        text = workflow_reads.WORKFLOW.read_text(encoding="utf-8").replace(
            "        type: string\n", "        type: choice\n", 1)

        _, failures = self._read(workflow=text)

        self.assertEqual(workflow_reads._dispatch_options(workflow_reads._live(text)), set())
        self.assertTrue(any("by hand:" in f and "`type: choice`" in f for f in failures), failures)

    MENU = "        type: choice\n        options: [catalog-api, no-such-release]\n"

    def _assert_menu(self, workflow: str) -> None:
        read, failures = self._read(workflow=workflow)

        self.assertIn("no-such-release", read["workflow"])
        self.assertTrue(any("by hand (catalog-api, no-such-release)" in f for f in failures), failures)

    def test_a_menu_past_a_blank_line_in_its_input_is_refused(self) -> None:
        self._assert_menu(workflow_reads.WORKFLOW.read_text(encoding="utf-8").replace(
            "        type: string\n", "\n" + self.MENU, 1))

    def test_a_menu_past_a_blank_line_after_the_key_is_refused(self) -> None:
        text = self._replaced(workflow_reads.WORKFLOW, "      workload:\n", "      workload:\n\n")
        self._assert_menu(text.replace("        type: string\n", self.MENU, 1))

    def test_a_menu_in_a_flow_mapping_on_the_key_line_is_refused(self) -> None:
        self._assert_menu(re.sub(
            r"(?m)^      workload:\n(?:        .*\n)*",
            "      workload: {type: choice, options: [catalog-api, 'no-such-release']}\n",
            workflow_reads.WORKFLOW.read_text(encoding="utf-8"), count=1))

    CALLED = "  workflow_call:\n    inputs:\n      workload:\n        type: string\n"

    def test_a_menu_behind_a_called_workflows_workload_input_is_refused(self) -> None:
        text = workflow_reads.WORKFLOW.read_text(encoding="utf-8").replace("        type: string\n", self.MENU, 1)
        self._assert_menu(text.replace("  workflow_dispatch:\n", self.CALLED + "  workflow_dispatch:\n", 1))

    def test_a_flow_menu_behind_an_earlier_flow_workload_key_is_read(self) -> None:
        text = """\
on:
  workflow_call:
    inputs:
      workload: {type: string}
  workflow_dispatch:
    inputs:
      workload: {type: choice, options: [catalog-api]}
"""
        self.assertEqual(workflow_reads._dispatch_options(text), {"catalog-api"})

    def test_a_quoted_description_in_a_flow_mapping_is_not_a_menu(self) -> None:
        for description in ("'one of the options: see README'", '"type: choice, options: [x]"'):
            with self.subTest(description=description):
                text = f"      workload: {{description: {description}, type: string}}\n"
                self.assertIsNone(workflow_reads._dispatch_options(text))

    GUARD ='if ! python deploy/canary/canary.py chart --workload="$WORKLOAD" >/dev/null 2>&1; then'
    CASES = r'''$PYTHON "$ROOT/deploy/canary/canary.py" smoke-cases | tr -d '\r' >"$CASES"'''

    def _replaced(self, real: Path, old: str, new: str) -> str:
        """The real reader's text with `old`, which it must hold once, made `new`."""
        text = real.read_text(encoding="utf-8")
        self.assertEqual(text.count(old), 1, f"{real.name} does not hold {old!r} once")
        return text.replace(old, new)

    def _assert_no_guard(self, workflow: str) -> None:
        read, failures = self._read(workflow=workflow)

        self.assertEqual(read["workflow"], set())
        self.assertTrue(any("holds no live guard" in f for f in failures), failures)

    def test_a_workflow_without_the_guard_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(workflow_reads.WORKFLOW, self.GUARD, "if false; then"))

    def test_a_guard_named_only_in_a_trailing_comment_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(workflow_reads.WORKFLOW, self.GUARD, f"if false; then  # {self.GUARD}"))

    def test_a_guard_weakened_to_a_substring_match_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            workflow_reads.WORKFLOW, self.GUARD,
            'WORKLOADS=$(python deploy/canary/canary.py workloads)\n'
            '          if ! grep -qF -- "$WORKLOAD" <<< "$WORKLOADS"; then'))

    def test_a_guard_that_cannot_fail_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            workflow_reads.WORKFLOW, self.GUARD,
            'if ! (python deploy/canary/canary.py chart --workload="$WORKLOAD" >/dev/null 2>&1 || true); then'))

    def test_a_guard_that_lets_argparse_read_the_name_as_an_option_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            workflow_reads.WORKFLOW, self.GUARD,
            'if ! python deploy/canary/canary.py chart --workload "$WORKLOAD" >/dev/null 2>&1; then'))

    def test_a_guard_whose_errors_reach_the_log_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            workflow_reads.WORKFLOW, self.GUARD,
            'if ! python deploy/canary/canary.py chart --workload="$WORKLOAD" >/dev/null; then'))

    def test_a_guard_whose_annotation_echoes_the_input_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            workflow_reads.WORKFLOW, 'echo "::error::The dispatched workload',
            'echo "::error::$WORKLOAD, the dispatched workload'))

    def test_a_guard_that_does_not_stop_is_refused(self) -> None:
        self._assert_no_guard(self._replaced(
            workflow_reads.WORKFLOW, 'under deploy/canary/deployables."\n            exit 1\n',
            'under deploy/canary/deployables."\n            true\n'))

    def test_a_trailing_comment_on_the_guard_leaves_it_read(self) -> None:
        read, failures = self._read(workflow=self._replaced(workflow_reads.WORKFLOW, self.GUARD, f"{self.GUARD}  # a key"))

        self.assertEqual(read["workflow"], set(self.workloads))
        self.assertEqual(failures, [])

    def test_a_hash_inside_quotes_or_an_expansion_is_not_a_comment(self) -> None:
        self.assertEqual(
            workflow_reads._live('echo "a # b" \'c # d\' ${#x} $# x#y  # tail\n  # whole\nkey: v # c'),
            'echo "a # b" \'c # d\' ${#x} $# x#y\nkey: v')

    def test_a_smoke_run_that_reads_no_cases_renders_nothing(self) -> None:
        text = workflow_reads.SMOKE.read_text(encoding="utf-8").replace("smoke-cases", "workloads")

        read, failures = self._read(smoke=text)

        self.assertEqual(read["smoke"], set())
        self.assertTrue(any("takes no live cases" in f for f in failures), failures)
        self.assertTrue(any("does not render" in f for f in failures), failures)

    def test_a_case_read_named_only_in_a_trailing_comment_is_refused(self) -> None:
        read, failures = self._read(smoke=self._replaced(workflow_reads.SMOKE, self.CASES, f': >"$CASES"  # {self.CASES}'))

        self.assertEqual(read["smoke"], set())
        self.assertTrue(any("takes no live cases" in f for f in failures), failures)

    def _assert_hand_list(self, line: str) -> None:
        read, failures = self._read(smoke=workflow_reads.SMOKE.read_text(encoding="utf-8") + line + "\n")

        self.assertEqual(read["smoke"], set())
        self.assertTrue(any("lists charts by hand" in f and "catalog" in f for f in failures), failures)

    def test_a_chart_list_assigned_by_hand_is_refused(self) -> None:
        self._assert_hand_list('SERVICE_CHARTS="catalog gateway"')

    def test_a_readonly_chart_list_is_refused(self) -> None:
        self._assert_hand_list('readonly SERVICE_CHARTS="catalog gateway"')

    def test_a_chart_array_is_refused(self) -> None:
        self._assert_hand_list("SERVICE_CHARTS=(catalog gateway)")

    def test_a_chart_appended_by_hand_is_refused(self) -> None:
        self._assert_hand_list('SERVICE_CHARTS="$SERVICE_CHARTS catalog"')

    def test_a_loop_over_charts_named_by_hand_is_refused(self) -> None:
        self._assert_hand_list("for chart in catalog gateway; do :; done")

    def test_a_loop_over_charts_named_in_a_brace_list_is_refused(self) -> None:
        self._assert_hand_list("for chart in {catalog,gateway}; do :; done")

    def test_a_sibling_inputs_options_are_not_the_workload_inputs(self) -> None:
        text = """\
on:
  workflow_dispatch:
    inputs:
      workload:
        description: 'options: [wrong, values]'
        type: string
      region:
        type: choice
        options: [catalog-api]
"""
        self.assertIsNone(workflow_reads._dispatch_options(text))

    def test_the_workload_inputs_own_options_are_read_after_a_sibling(self) -> None:
        text = """\
on:
  workflow_dispatch:
    inputs:
      region:
        type: choice
        options: [north, south]
      workload:
        description: 'options: [wrong, values]'
        type: choice
        options: [catalog-api]
"""
        self.assertEqual(workflow_reads._dispatch_options(text), {"catalog-api"})

    def test_a_list_at_the_options_keys_own_indentation_is_read(self) -> None:
        text = """on:
  workflow_dispatch:
    inputs:
      workload:
        type: string
        options:
        - catalog-api
        - 'gateway'
      region:
        type: string
"""
        self.assertEqual(workflow_reads._dispatch_options(text), {"catalog-api", "gateway"})


if __name__ == "__main__":
    unittest.main()
