"""The descriptor model: canary.json, the descriptors under deployables/ and their queries."""

from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import canary
import descriptor_model
import repo_gate
import workflow_reads


class DescriptorTests(unittest.TestCase):
    """A deployable's descriptor: one file, named for its release."""

    def _plan(self, plan: dict, descriptors: dict[str, dict]) -> dict:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "canary.json"
            path.write_text(json.dumps(plan), encoding="utf-8")
            folder = Path(tmp) / "deployables"
            folder.mkdir()
            for name, descriptor in descriptors.items():
                (folder / f"{name}.json").write_text(json.dumps(descriptor), encoding="utf-8")
            return descriptor_model.load_plan(path, folder)

    def test_each_descriptor_is_the_workload_its_file_names(self) -> None:
        plan = self._plan({"steps": []}, {"a-api": {"chart": "a"}, "b": {"chart": "b"}})

        self.assertEqual(plan["workloads"], {"a-api": {"chart": "a"}, "b": {"chart": "b"}})

    def test_a_plan_that_still_carries_workloads_is_refused(self) -> None:
        with self.assertRaisesRegex(descriptor_model.PlanError, "its own file"):
            self._plan({"workloads": {"a": {}}}, {})

    def test_smoke_cases_prints_what_smoke_sh_reads(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = canary.main(["canary", "smoke-cases"])

        self.assertEqual(code, 0)
        self.assertEqual(out.getvalue().splitlines(), workflow_reads.smoke_cases(descriptor_model.load_plan()["workloads"]))
        self.assertIn("payments overlay paymentProvider.baseUrl=https://psp.example.invalid/",
                      out.getvalue().splitlines())
        self.assertIn("notifications host Notifications.Worker", out.getvalue().splitlines())

    def test_a_host_name_smoke_sh_cannot_find_settings_by_is_refused(self) -> None:
        """smoke.sh finds a chart's listeners beside its host's project, so the name must be one word it can
        hand to find; a service with two hosts under one source is why the source alone is not enough."""
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["payments-api"]["serviceName"] = "Payments Api"

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(
            any("payments-api.serviceName" in f and "one project name" in f for f in failures), failures)

    def test_a_case_smoke_sh_would_split_into_two_words_is_refused(self) -> None:
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["payments-api"]["smoke"]["overlay"] = ["paymentProvider.baseUrl=https://a b/"]

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(any("payments-api.smoke.overlay" in f for f in failures), failures)

    def test_an_address_and_a_one_item_list_are_one_word_each(self) -> None:
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["payments-api"]["smoke"]["overlay"] = [
            "paymentProvider.baseUrl=https://psp.example.invalid/",
            "mail.from=no-reply@commerce.example.invalid",
            "jurisdiction.languages={en}",
        ]

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertFalse(any("payments-api.smoke.overlay" in f for f in failures), failures)

    def test_a_comma_is_refused_even_inside_a_list(self) -> None:
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["payments-api"]["smoke"]["overlay"] = ["jurisdiction.languages={en,kk}"]

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(any("payments-api.smoke.overlay" in f for f in failures), failures)

    def test_a_source_that_holds_another_host_is_refused(self) -> None:
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["catalog-api"]["source"] = "src/Services/Ordering"

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(any("catalog-api.source" in f and "Catalog.Api.csproj" in f for f in failures), failures)

    def test_two_descriptors_naming_one_chart_are_refused(self) -> None:
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["ordering-api"]["chart"] = "catalog"

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(any("two descriptors name the chart 'catalog'" in f for f in failures), failures)

    def test_a_descriptor_that_does_not_say_whether_it_has_a_database_is_refused(self) -> None:
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        del workloads["catalog-api"]["smoke"]["database"]

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(any("catalog-api.smoke.database" in f for f in failures), failures)

    def test_a_chart_that_migrates_a_database_it_lacks_is_refused(self) -> None:
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["catalog-api"]["smoke"]["database"] = False

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(any("migrates a database it does not have" in f for f in failures), failures)

    def test_a_database_with_no_job_must_name_the_release_that_migrates_it(self) -> None:
        """ADR-118: without the name, nothing orders the rollout behind the schema."""
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        del workloads["notifications-api"]["migratedBy"]

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(
            any("notifications-api" in f and "names no migratedBy" in f for f in failures), failures)

    def test_a_chart_that_migrates_itself_names_no_other(self) -> None:
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["catalog-api"]["migratedBy"] = "notifications"

        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertTrue(
            any("catalog-api.migratedBy" in f and "no schema" in f for f in failures), failures)

    def test_migrated_by_must_name_a_workload_whose_chart_migrates(self) -> None:
        for named in ("gateway", "notifications-api", "no-such-workload"):
            with self.subTest(named=named):
                workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
                workloads["notifications-api"]["migratedBy"] = named

                failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

                self.assertTrue(
                    any("notifications-api.migratedBy" in f and "runs a migration Job" in f
                        for f in failures), failures)

    def test_the_shipped_plan_names_the_worker_as_the_api_host_s_migrator(self) -> None:
        cases = workflow_reads.smoke_cases(descriptor_model.load_plan()["workloads"])

        self.assertIn("notifications-api migratedBy notifications", cases)
        self.assertIn("notifications-api database yes", cases)
        self.assertIn("notifications-api migrator no", cases)

    def test_migrated_by_prints_the_release_or_nothing(self) -> None:
        for workload, printed in (("notifications-api", "notifications\n"), ("catalog-api", "\n")):
            with self.subTest(workload=workload):
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    code = canary.main(["canary", "migrated-by", "--workload", workload])

                self.assertEqual((0, printed), (code, out.getvalue()))

    def test_a_migrated_by_that_is_not_a_name_is_refused_rather_than_raised(self) -> None:
        """Check 4 refuses it, smoke-cases prints no line for it, and the rollout's question fails closed."""
        workloads = json.loads(json.dumps(descriptor_model.load_plan()["workloads"]))
        workloads["notifications-api"]["migratedBy"] = ["notifications"]

        cases = workflow_reads.smoke_cases(workloads)
        failures = repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

        self.assertFalse(any(" migratedBy " in case for case in cases), cases)
        self.assertTrue(any("notifications-api.migratedBy" in f for f in failures), failures)
        with self.assertRaisesRegex(descriptor_model.PlanError, "names no workload"):
            descriptor_model.migrated_by("notifications-api", {"workloads": workloads})

    def test_migrated_by_refuses_a_workload_the_plan_does_not_describe(self) -> None:
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = canary.main(["canary", "migrated-by", "--workload", "no-such-workload"])

        self.assertEqual(1, code)
        self.assertIn("no workload 'no-such-workload'", err.getvalue())

    def test_migrates_prints_each_host_whose_schema_the_release_migrates(self) -> None:
        for workload, printed in (("notifications", "notifications-api\n"), ("catalog-api", "")):
            with self.subTest(workload=workload):
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    code = canary.main(["canary", "migrates", "--workload", workload])

                self.assertEqual((0, printed), (code, out.getvalue()))

    def test_migrates_refuses_a_workload_the_plan_does_not_describe(self) -> None:
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = canary.main(["canary", "migrates", "--workload", "no-such-workload"])

        self.assertEqual(1, code)
        self.assertIn("no workload 'no-such-workload'", err.getvalue())


class CommentTests(unittest.TestCase):
    def test_comment_keys_are_not_data(self) -> None:
        """JSON has no comments and every number in the plan is a decision
        somebody has to be able to re-take. Filtered in one place, because
        forgetting it once turns a comment into a workload with no service
        name."""
        self.assertEqual(
            descriptor_model.entries({"$comment": ["why"], "gateway": {}}),
            {"gateway": {}},
        )

    def test_the_shipped_plan_actually_uses_them(self) -> None:
        """If the comments were ever stripped out, the filter above would stop
        being exercised by anything real."""
        raw = json.loads(Path(descriptor_model.PLAN_PATH).read_text(encoding="utf-8"))

        self.assertIn("$comment", raw)


class WorkloadImageTests(unittest.TestCase):
    """The rollout verifies the images the chart pulls, so the chart is the list."""

    def test_a_service_pulls_its_host_and_its_migrator(self) -> None:
        plan = descriptor_model.load_plan()

        self.assertEqual(
            {"api": "catalog-api", "migrator": "catalog-migrator"},
            descriptor_model.workload_images("catalog-api", plan))
        self.assertEqual(
            {"api": "shipping-worker", "migrator": "shipping-migrator"},
            descriptor_model.workload_images("shipping", plan))

    def test_the_bff_s_migrator_is_not_named_for_its_host(self) -> None:
        self.assertEqual(
            {"api": "web-bff", "migrator": "bff-migrator"},
            descriptor_model.workload_images("web-bff", descriptor_model.load_plan()))

    def test_the_gateway_has_no_migrator(self) -> None:
        self.assertEqual({"api": "gateway"}, descriptor_model.workload_images("gateway", descriptor_model.load_plan()))

    def test_a_migrator_is_exactly_where_the_descriptor_says_there_is_one(self) -> None:
        """Two owners of one fact: the descriptor feeds smoke.sh and the chart
        feeds the rollout, and a workload one of them gives a migrator and the
        other does not is an image verified for nobody or pulled unverified."""
        plan = descriptor_model.load_plan()
        for name, descriptor in descriptor_model.entries(plan["workloads"]).items():
            with self.subTest(workload=name):
                self.assertEqual(
                    descriptor["smoke"]["migrator"],
                    "migrator" in descriptor_model.workload_images(name, plan))

    def test_an_unknown_workload_is_refused(self) -> None:
        with self.assertRaises(descriptor_model.PlanError):
            descriptor_model.workload_images("nonesuch", descriptor_model.load_plan())

    def test_a_chart_with_no_api_image_is_refused_rather_than_read_as_empty(self) -> None:
        root = Path(tempfile.mkdtemp())
        (root / "deploy" / "helm" / "x").mkdir(parents=True)
        (root / "deploy" / "helm" / "x" / "values.yaml").write_text(
            "image:\n  # api: commented-out\n  tag: \"\"\n", encoding="utf-8")

        with self.assertRaises(descriptor_model.PlanError):
            descriptor_model.workload_images("x", {"workloads": {"x": {"chart": "x"}}}, root)

    def test_a_key_outside_the_image_block_is_not_an_image(self) -> None:
        root = Path(tempfile.mkdtemp())
        (root / "deploy" / "helm" / "x").mkdir(parents=True)
        (root / "deploy" / "helm" / "x" / "values.yaml").write_text(
            "image:\n  api: x-api\nworker:\n  migrator: not-an-image\n", encoding="utf-8")

        self.assertEqual(
            {"api": "x-api"},
            descriptor_model.workload_images("x", {"workloads": {"x": {"chart": "x"}}}, root))


if __name__ == "__main__":
    unittest.main()
