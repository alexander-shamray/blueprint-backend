"""What deploy.yml's rollout steps must do with the facts the descriptors give them."""

from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import canary
import descriptor_model
import repo_gate
import rollout_gate
import workflow_reads


class RolloutOrderTests(unittest.TestCase):
    """Check 13: a host another release migrates rolls in lockstep with it (ADR-118, ADR-122)."""

    def _workflow(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "deploy.yml"
        path.write_text(text, encoding="utf-8")
        return path

    @property
    def shipped(self) -> str:
        return workflow_reads.WORKFLOW.read_text(encoding="utf-8")

    @property
    def workloads(self) -> dict:
        return descriptor_model.entries(descriptor_model.load_plan()["workloads"])

    def test_the_shipped_workflow_orders_the_rollout(self) -> None:
        self.assertEqual([], rollout_gate._rollout_follows_the_migrator(self.workloads))

    def test_a_workflow_that_never_asks_is_refused(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "canary.py migrated-by" not in line)

        failures = rollout_gate._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("runs no `canary.py migrated-by`", failures[0])

    def test_an_answer_that_refuses_nothing_is_refused(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "merge-base --is-ancestor" not in line
            or "MIGRATED_REVISION" not in line)

        failures = rollout_gate._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("refuses nothing", failures[0])

    def test_a_host_held_from_above_only_is_refused(self) -> None:
        """ADR-122: a host may not roll back behind a schema the migrating release has moved."""
        without = "\n".join(
            line for line in self.shipped.splitlines()
            if 'merge-base --is-ancestor "$MIGRATED_REVISION" "$REVISION"' not in line)

        failures = rollout_gate._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("older than the schema", failures[0])

    def test_a_migrating_release_that_never_asks_what_it_migrates_is_refused(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "canary.py migrates " not in line)

        failures = rollout_gate._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("runs no `canary.py migrates`", failures[0])

    def test_a_migrating_release_held_to_no_host_is_refused(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "merge-base --is-ancestor" not in line
            or "HOST_REVISION" not in line)

        failures = rollout_gate._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("never holds", failures[0])
        self.assertIn("$HOST_REVISION", failures[0])

    def test_a_migrating_release_held_from_below_only_is_refused(self) -> None:
        """ADR-122: a worker rolled back leaves the host ahead of it, and the next migration need not support that."""
        without = "\n".join(
            line for line in self.shipped.splitlines()
            if 'merge-base --is-ancestor "$HOST_REVISION" "$STABLE_REVISION"' not in line)

        failures = rollout_gate._rollout_follows_the_migrator(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("newer than", failures[0])

    def test_a_commented_out_question_is_no_question(self) -> None:
        commented = self.shipped.replace(
            "MIGRATED_BY=$(python deploy/canary/canary.py migrated-by",
            "# MIGRATED_BY=$(python deploy/canary/canary.py migrated-by")

        failures = rollout_gate._rollout_follows_the_migrator(self.workloads, self._workflow(commented))

        self.assertTrue(any("runs no `canary.py migrated-by`" in f for f in failures), failures)

    def test_with_no_host_migrated_by_another_there_is_nothing_to_order(self) -> None:
        workloads = {name: entry for name, entry in self.workloads.items() if "migratedBy" not in entry}

        self.assertEqual([], rollout_gate._rollout_follows_the_migrator(workloads, self._workflow("")))


class SharedValuesTests(unittest.TestCase):
    """ADR-125: a value two releases are each given is held equal before either rolls; check 14 holds it."""

    @property
    def workloads(self) -> dict:
        return json.loads(json.dumps(descriptor_model.entries(descriptor_model.load_plan()["workloads"])))

    def _checked(self, workloads: dict) -> list[str]:
        return repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

    def _workflow(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "deploy.yml"
        path.write_text(text, encoding="utf-8")
        return path

    @property
    def shipped(self) -> str:
        return workflow_reads.WORKFLOW.read_text(encoding="utf-8")

    def test_the_shipped_pairs_are_read_from_both_ends(self) -> None:
        plan = descriptor_model.load_plan()

        self.assertEqual(
            [("notifications-api", "jurisdiction.required"), ("web-bff", "push.apps")],
            descriptor_model.shared_values("notifications", plan))
        self.assertEqual([("notifications", "jurisdiction.required")], descriptor_model.shared_values("notifications-api", plan))
        self.assertEqual([("notifications", "push.apps")], descriptor_model.shared_values("web-bff", plan))
        self.assertEqual([], descriptor_model.shared_values("catalog-api", plan))

    def test_the_cli_prints_one_pair_a_line_and_refuses_an_unknown_workload(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = canary.main(["canary", "shared-values", "--workload", "web-bff"])
        self.assertEqual((0, "notifications push.apps\n"), (code, out.getvalue()))

        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, canary.main(["canary", "shared-values", "--workload", "nobody"]))

    def test_the_shipped_descriptors_pass_check_4(self) -> None:
        self.assertEqual([], [f for f in self._checked(self.workloads) if "sharesValues" in f])

    def test_a_declaration_check_4_refuses(self) -> None:
        cases = {
            "names no peer and no key": {},
            "which is not another workload": {"nobody": ["push.apps"]},
            "which names no key": {"web-bff": []},
            "which is not a dotted values path": {"web-bff": ["push..apps"]},
        }
        for message, declared in cases.items():
            with self.subTest(message):
                workloads = self.workloads
                workloads["notifications"]["sharesValues"] = declared

                self.assertTrue(any(message in f for f in self._checked(workloads)), self._checked(workloads))

    def test_a_malformed_declaration_is_refused_and_not_a_crash(self) -> None:
        for declared in [["web-bff"], {"web-bff": 1}]:
            with self.subTest(declared=declared):
                plan = descriptor_model.load_plan()
                plan["workloads"]["notifications"]["sharesValues"] = declared

                failures = repo_gate.check(plan)

                self.assertTrue(any("sharesValues" in failure for failure in failures), failures)

    def test_a_pair_declared_from_both_ends_is_refused(self) -> None:
        workloads = self.workloads
        workloads["web-bff"]["sharesValues"] = {"notifications": ["push.apps"]}

        self.assertTrue(any("one declaration is the pair's owner" in f for f in self._checked(workloads)))

    def test_equal_values_agree_whatever_their_order(self) -> None:
        mine = {"push": {"apps": [{"appId": "a", "platform": "fcm"}, {"appId": "b", "platform": "apns"}]}}
        theirs = {"push": {"apps": [{"platform": "apns", "appId": "b"}, {"appId": "a", "platform": "fcm"}]}}

        self.assertEqual([], descriptor_model.values_agree("push.apps", mine, theirs, "web-bff"))

    def test_a_member_named_twice_is_the_same_member(self) -> None:
        mine, theirs = {"push": {"apps": ["a", "a", "b"]}}, {"push": {"apps": ["b", "a"]}}

        self.assertEqual([], descriptor_model.values_agree("push.apps", mine, theirs, "web-bff"))

    def test_a_member_only_one_release_names_is_a_difference_that_says_which(self) -> None:
        mine = {"jurisdiction": {"required": ["order-placed/email", "order-confirmed/email"]}}
        theirs = {"jurisdiction": {"required": ["order-placed/email"]}}

        differences = descriptor_model.values_agree("jurisdiction.required", mine, theirs, "notifications-api")

        self.assertEqual(1, len(differences), differences)
        self.assertIn('only this release names "order-confirmed/email"', differences[0])
        self.assertIn("only notifications-api names nothing", differences[0])

    def test_absent_null_and_empty_are_three_answers(self) -> None:
        """A chart refuses null as unstated and takes [] as none (Appendix E), so neither stands in for the other."""
        absent, null, empty = {}, {"jurisdiction": {"required": None}}, {"jurisdiction": {"required": []}}

        for a, b in [(absent, null), (null, empty), (absent, empty)]:
            with self.subTest(a=a, b=b):
                self.assertTrue(descriptor_model.values_agree("jurisdiction.required", a, b, "peer"))
        self.assertEqual([], descriptor_model.values_agree("jurisdiction.required", null, null, "peer"))

    def test_a_path_neither_release_holds_is_refused_and_not_agreed(self) -> None:
        """A misspelt key would otherwise pass every rollout, since `--all` carries every default."""
        differences = descriptor_model.values_agree("push.app", {"push": {"apps": []}}, {"push": {"apps": []}}, "web-bff")

        self.assertEqual(1, len(differences), differences)
        self.assertIn("neither this release's values nor web-bff's hold", differences[0])

    def test_the_cli_exits_by_the_answer(self) -> None:
        folder = Path(tempfile.mkdtemp())
        same, other = folder / "same.json", folder / "other.json"
        same.write_text(json.dumps({"push": {"apps": []}}), encoding="utf-8")
        other.write_text(json.dumps({"push": {"apps": [{"appId": "a", "platform": "fcm"}]}}), encoding="utf-8")
        argv = ["canary", "values-agree", "--key", "push.apps", "--peer-name", "web-bff"]

        broken = folder / "broken.json"
        broken.write_text("{", encoding="utf-8")

        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(0, canary.main([*argv, "--values", str(same), "--peer", str(same)]))
            self.assertEqual(1, canary.main([*argv, "--values", str(same), "--peer", str(other)]))
            self.assertEqual(2, canary.main([*argv, "--values", str(same), "--peer", str(broken)]))
            self.assertEqual(2, canary.main(
                ["canary", "values-agree", "--key", "push.app", "--peer-name", "web-bff",
                 "--values", str(same), "--peer", str(same)]))
            self.assertEqual(2, canary.main(
                ["canary", "values-agree", "--key", "push..apps", "--peer-name", "web-bff",
                 "--values", str(same), "--peer", str(same)]))
        self.assertIn("push.apps differs from web-bff's", err.getvalue())

    def test_the_shipped_workflow_compares_the_pairs(self) -> None:
        self.assertEqual([], rollout_gate._rollout_holds_shared_values(self.workloads))

    def test_a_workflow_that_never_asks_is_refused(self) -> None:
        without = "\n".join(line for line in self.shipped.splitlines() if "canary.py shared-values" not in line)

        failures = rollout_gate._rollout_holds_shared_values(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("runs no `canary.py shared-values`", failures[0])

    def test_an_answer_that_compares_nothing_is_refused(self) -> None:
        without = "\n".join(line for line in self.shipped.splitlines() if "canary.py values-agree" not in line)

        failures = rollout_gate._rollout_holds_shared_values(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("refuses nothing", failures[0])

    def test_a_key_the_rollout_sets_itself_is_refused(self) -> None:
        """Read from the workflow's own `--set` flags, so a newly pinned key is covered without a list to keep."""
        self.assertIn('identity.authority="$CHECKED_AUTHORITY"', self.shipped)
        for key in ["identity.authority", "image", "image.tag"]:
            with self.subTest(key=key):
                workloads = self.workloads
                workloads["notifications"]["sharesValues"]["web-bff"].append(key)

                failures = rollout_gate._rollout_holds_shared_values(workloads, self._workflow(self.shipped))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(f"names {key}, which", failures[0])
                self.assertIn("own command line", failures[0])

    def test_a_key_pinned_quoted_literal_or_after_a_comma_is_refused(self) -> None:
        for flag, key in [
            ("--set-string 'jurisdiction.required={a,b}'", "jurisdiction.required"),
            ("--set image.pullPolicy=Always,push.apps={a}", "push.apps"),
            ('--set-literal "push.apps=a"', "push.apps"),
            ("--set-json=push.apps='[1, 2]'", "push.apps"),
            ("--set push.apps[0]=a", "push.apps"),
            ("--set-string jurisdiction.required[1].topic=x", "jurisdiction.required"),
        ]:
            with self.subTest(flag=flag):
                pinned = self.shipped + f"\n          helm upgrade x deploy/helm/x {flag}\n"

                failures = rollout_gate._rollout_holds_shared_values(self.workloads, self._workflow(pinned))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(f"names {key}, which", failures[0])

    def test_no_spelling_of_a_set_flag_hides_a_key_it_pins(self) -> None:
        """A superset: a list member may read as a key, which refuses; a pinned key never reads as none."""
        for argument in [
            "a=x{y,push.apps=z", "a=x{y,push.apps=z}", "a=}x,push.apps=z,c={", "a=b={x,push.apps=y}",
            "'x=a" + chr(92) + ",push.apps=c'", "x={a,b},push.apps=1", '"push.apps"=x', 'push."apps"=x',
        ]:
            with self.subTest(argument=argument):
                self.assertIn("push.apps", rollout_gate._pinned_keys([f"helm upgrade r c --set {argument}"]))
        continued = ["helm upgrade r c --set " + chr(92), "  push.apps=x --wait"]
        self.assertIn("push.apps", rollout_gate._pinned_keys(continued))
        folded = ["helm upgrade r c --set", "  push.apps=x", "  --wait"]
        self.assertIn("push.apps", rollout_gate._pinned_keys(folded))
        self.assertIn("push.apps", rollout_gate._pinned_keys(["helm upgrade r c --set a=x--y,push.apps=z"]))
        self.assertEqual(set(), rollout_gate._pinned_keys(["helm upgrade r c -f values.yaml"]))
        self.assertEqual({"image.tag"}, rollout_gate._pinned_keys(['helm upgrade r c --set-string image.tag="$TAG"']))

    def test_a_comparison_of_the_running_values_is_refused(self) -> None:
        """The running values carry the old chart's defaults, so a changed default would pass unseen."""
        running = self.shipped.replace('--values "$RUNNER_TEMP/candidate.json"', '--values "$RUNNER_TEMP/stable.json"')

        failures = rollout_gate._rollout_holds_shared_values(self.workloads, self._workflow(running))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("will be installed with", failures[0])

    def test_overrides_read_with_their_defaults_are_refused(self) -> None:
        coalesced = self.shipped.replace(
            '-n "$NAMESPACE" -o yaml > "$RUNNER_TEMP/overrides.yaml"',
            '-n "$NAMESPACE" --all -o yaml > "$RUNNER_TEMP/overrides.yaml"')

        failures = rollout_gate._rollout_holds_shared_values(self.workloads, self._workflow(coalesced))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("own overrides", failures[0])

    def test_candidate_values_not_drawn_from_the_checked_out_chart_are_refused(self) -> None:
        for mutation in [
            ('cp "deploy/helm/$CHART/values.yaml"', 'touch "deploy/helm/$CHART/values.yaml"'),
            ('-f "$RUNNER_TEMP/overrides.yaml" ' + chr(92), chr(92)),
        ]:
            with self.subTest(mutation=mutation[0]):
                self.assertIn(mutation[0], self.shipped)
                failures = rollout_gate._rollout_holds_shared_values(
                    self.workloads, self._workflow(self.shipped.replace(*mutation)))

                self.assertEqual(1, len(failures), failures)
                self.assertIn("checked-out chart's", failures[0])

    def test_a_commented_out_question_is_no_question(self) -> None:
        commented = self.shipped.replace(
            "python deploy/canary/canary.py shared-values", "# python deploy/canary/canary.py shared-values")

        failures = rollout_gate._rollout_holds_shared_values(self.workloads, self._workflow(commented))

        self.assertEqual(1, len(failures), failures)

    def test_with_no_pair_declared_there_is_nothing_to_compare(self) -> None:
        workloads = self.workloads
        del workloads["notifications"]["sharesValues"]

        self.assertEqual([], rollout_gate._rollout_holds_shared_values(workloads, self._workflow("")))


class EmptyUntilTests(unittest.TestCase):
    """ADR-126: a value earlier revisions misread stays empty while either track serves one; check 15 holds it."""

    PUSH_DELIVERIES = "19ff224ff02274f4c9b33b2685f197f3005ca7f1"

    @property
    def workloads(self) -> dict:
        return json.loads(json.dumps(descriptor_model.entries(descriptor_model.load_plan()["workloads"])))

    def _checked(self, workloads: dict) -> list[str]:
        return repo_gate.check({**descriptor_model.load_plan(), "workloads": workloads})

    def _workflow(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "deploy.yml"
        path.write_text(text, encoding="utf-8")
        return path

    @property
    def shipped(self) -> str:
        return workflow_reads.WORKFLOW.read_text(encoding="utf-8")

    def test_the_shipped_key_waits_on_the_last_commit_of_push_deliveries(self) -> None:
        plan = descriptor_model.load_plan()

        self.assertEqual([("push.apps", self.PUSH_DELIVERIES)], descriptor_model.empty_until("notifications", plan))
        self.assertEqual([], descriptor_model.empty_until("web-bff", plan))

    def test_the_cli_prints_one_key_a_line_and_refuses_an_unknown_workload(self) -> None:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = canary.main(["canary", "empty-until", "--workload", "notifications"])
        self.assertEqual((0, f"push.apps {self.PUSH_DELIVERIES}\n"), (code, out.getvalue()))

        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, canary.main(["canary", "empty-until", "--workload", "nobody"]))

    def test_the_shipped_descriptors_pass_check_4(self) -> None:
        self.assertEqual([], [f for f in self._checked(self.workloads) if "emptyUntil" in f])

    def test_a_declaration_check_4_refuses(self) -> None:
        cases = {
            "which names no key": {},
            "which is not a dotted values path": {"push..apps": self.PUSH_DELIVERIES},
            "which is not a whole commit object name": {"push.apps": self.PUSH_DELIVERIES[:12]},
        }
        for message, declared in cases.items():
            with self.subTest(message):
                workloads = self.workloads
                workloads["notifications"]["emptyUntil"] = declared

                self.assertTrue(any(message in f for f in self._checked(workloads)), self._checked(workloads))

    def test_a_malformed_declaration_is_refused_and_not_a_crash(self) -> None:
        for declared in [["push.apps"], {"push.apps": 1}]:
            with self.subTest(declared=declared):
                plan = descriptor_model.load_plan()
                plan["workloads"]["notifications"]["emptyUntil"] = declared

                self.assertTrue(any("emptyUntil" in failure for failure in repo_gate.check(plan)))

    def test_empty_names_nothing_and_a_member_is_named(self) -> None:
        for empty in [[], None, "", {}]:
            with self.subTest(empty=empty):
                self.assertEqual([], descriptor_model.values_named("push.apps", {"push": {"apps": empty}}))
        named = descriptor_model.values_named("push.apps", {"push": {"apps": [{"appId": "a", "platform": "fcm"}]}})

        self.assertEqual(['{"appId": "a", "platform": "fcm"}'], named)

    def test_a_path_the_values_do_not_hold_is_refused_and_not_empty(self) -> None:
        """The values carry every chart default, so a misspelt key would otherwise let every rollout through."""
        with self.assertRaisesRegex(descriptor_model.PlanError, "push.app is a path the values do not hold"):
            descriptor_model.values_named("push.app", {"push": {"apps": []}})

    def test_the_cli_exits_by_the_answer(self) -> None:
        folder = Path(tempfile.mkdtemp())
        empty, named, broken = folder / "empty.json", folder / "named.json", folder / "broken.json"
        empty.write_text(json.dumps({"push": {"apps": []}}), encoding="utf-8")
        named.write_text(json.dumps({"push": {"apps": [{"appId": "a", "platform": "fcm"}]}}), encoding="utf-8")
        broken.write_text("{", encoding="utf-8")
        argv = ["canary", "values-empty", "--key"]

        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(0, canary.main([*argv, "push.apps", "--values", str(empty)]))
            self.assertEqual(1, canary.main([*argv, "push.apps", "--values", str(named)]))
            self.assertEqual(2, canary.main([*argv, "push.apps", "--values", str(broken)]))
            self.assertEqual(2, canary.main([*argv, "push.app", "--values", str(empty)]))
            self.assertEqual(2, canary.main([*argv, "push..apps", "--values", str(empty)]))
        self.assertIn('push.apps names {"appId": "a", "platform": "fcm"} (ADR-126)', err.getvalue())

    def test_the_shipped_workflow_holds_the_keys_empty(self) -> None:
        self.assertEqual([], rollout_gate._rollout_keeps_values_empty(self.workloads))

    def test_check_runs_check_15(self) -> None:
        with mock.patch.object(repo_gate, "_rollout_keeps_values_empty", return_value=["check 15 refused"]):
            self.assertIn("check 15 refused", repo_gate.check(descriptor_model.load_plan()))

    def test_a_workflow_that_never_asks_is_refused(self) -> None:
        without = "\n".join(line for line in self.shipped.splitlines() if "canary.py empty-until" not in line)

        failures = rollout_gate._rollout_keeps_values_empty(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("runs no `canary.py empty-until`", failures[0])

    def test_an_answer_that_holds_nothing_empty_is_refused(self) -> None:
        without = "\n".join(line for line in self.shipped.splitlines() if "canary.py values-empty" not in line)

        failures = rollout_gate._rollout_keeps_values_empty(self.workloads, self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("refuses nothing", failures[0])

    def test_holding_the_running_values_empty_is_refused(self) -> None:
        """The running values are the stable track's, not the ones the candidate is installed with."""
        running = self.shipped.replace(
            'values-empty --key "$KEY" ' + chr(92) + '\n              --values "$RUNNER_TEMP/candidate.json"',
            'values-empty --key "$KEY" ' + chr(92) + '\n              --values "$RUNNER_TEMP/stable.json"')
        self.assertNotEqual(self.shipped, running)

        failures = rollout_gate._rollout_keeps_values_empty(self.workloads, self._workflow(running))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("will be installed with", failures[0])

    def test_candidate_values_are_held_to_their_source_without_check_14(self) -> None:
        """Check 14 is silent with no `sharesValues` declared, so check 15 cannot lean on it."""
        workloads = self.workloads
        del workloads["notifications"]["sharesValues"]
        self.assertEqual([], rollout_gate._rollout_holds_shared_values(workloads, self._workflow("")))
        for mutation, message in [
            (('-n "$NAMESPACE" -o yaml > "$RUNNER_TEMP/overrides.yaml"',
              '-n "$NAMESPACE" --all -o yaml > "$RUNNER_TEMP/overrides.yaml"'), "own overrides"),
            (('cp "deploy/helm/$CHART/values.yaml"', 'touch "deploy/helm/$CHART/values.yaml"'), "checked-out chart's"),
        ]:
            with self.subTest(mutation=mutation[0]):
                self.assertIn(mutation[0], self.shipped)
                failures = rollout_gate._rollout_keeps_values_empty(
                    workloads, self._workflow(self.shipped.replace(*mutation)))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(message, failures[0])
                self.assertIn("(ADR-126)", failures[0])

    def test_a_step_moved_above_the_candidate_values_is_refused(self) -> None:
        lines = self.shipped.splitlines()
        asked = next(i for i, line in enumerate(lines) if "canary.py values-empty" in line)
        moved = lines[asked:asked + 2] + lines[:asked] + lines[asked + 2:]

        failures = rollout_gate._rollout_keeps_values_empty(self.workloads, self._workflow("\n".join(moved)))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("once written", failures[0])

    def test_a_track_never_compared_with_the_revision_is_refused(self) -> None:
        """An older candidate misreads the stable track's rows, so each track is asked, not only the stable one."""
        for track in ["STABLE_REVISION", "REVISION"]:
            with self.subTest(track=track):
                asked = f'git merge-base --is-ancestor "$SINCE" "${track}"'
                self.assertIn(asked, self.shipped)
                without = self.shipped.replace(asked, "true")

                failures = rollout_gate._rollout_keeps_values_empty(self.workloads, self._workflow(without))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(f'"${track}"', failures[0])

    def test_a_key_skipped_on_either_track_alone_is_refused(self) -> None:
        conjunction = '"$STABLE_REVISION" && git merge-base'
        for mutation in [
            (conjunction, '"$STABLE_REVISION" || git merge-base'),
            ("            if git merge-base --is-ancestor \"$SINCE\" \"$STABLE_REVISION\"",
             "            if true || git merge-base --is-ancestor \"$SINCE\" \"$STABLE_REVISION\""),
        ]:
            with self.subTest(mutation=mutation[1]):
                self.assertIn(mutation[0], self.shipped)
                failures = rollout_gate._rollout_keeps_values_empty(
                    self.workloads, self._workflow(self.shipped.replace(*mutation)))

                self.assertEqual(1, len(failures), failures)
                self.assertIn("without both tracks", failures[0])

    def test_a_workflow_that_rolls_back_across_the_revision_is_refused(self) -> None:
        """ADR-127: the rows a stable track left are not in the values, so the refusal reads revisions alone."""
        back = '            if git merge-base --is-ancestor "$SINCE" "$STABLE_REVISION"; then\n'
        self.assertIn(back, self.shipped)
        start = self.shipped.index(back)
        refusal = self.shipped[start:self.shipped.index("            fi\n", start) + len("            fi\n")]
        done = '          done < "$RUNNER_TEMP/empty-until.txt"\n'
        skip = '            if git merge-base --is-ancestor "$SINCE" "$STABLE_REVISION" && '
        self.assertIn(done, self.shipped)
        self.assertEqual(1, self.shipped.count(skip))
        without = self.shipped.replace(refusal, "")
        refuses = "              exit 1\n"
        self.assertEqual(1, refusal.count(refuses))
        for name, mutated in {
            "removed": without,
            "exit 1 made true": self.shipped.replace(refusal, refusal.replace("exit 1", "true")),
            "exit 1 commented out": self.shipped.replace(refusal, refusal.replace("exit 1", "# exit 1")),
            # Past the loop, an empty $SINCE is no commit's ancestor, so the refusal never fires.
            "past the loop": without.replace(done, done + refusal),
            # Above the skip, it refuses every rollout once the stable track carries the revision.
            "above the skip": without.replace(skip, refusal + skip),
            # The branch that runs when the stable track carries the revision must be the one that exits.
            "exit 1 under else": self.shipped.replace(
                refusal, refusal.replace(refuses, "              true\n            else\n" + refuses)),
            # Bash reads no indent, so an `else` at the body's depth still starts a branch.
            "exit 1 under an else at the body's depth": self.shipped.replace(
                refusal, refusal.replace(refuses, "              true\n              else\n" + refuses)),
            "exit 1 under an else with a trailing comment": self.shipped.replace(
                refusal, refusal.replace(refuses, "              true\n              else  # note\n" + refuses)),
            "exit 1 in a nested block": self.shipped.replace(
                refusal, refusal.replace(refuses, "              if false; then\n  " + refuses + "              fi\n")),
        }.items():
            with self.subTest(name):
                self.assertNotEqual(self.shipped, mutated)
                failures = rollout_gate._rollout_keeps_values_empty(self.workloads, self._workflow(mutated))

                self.assertEqual(1, len(failures), failures)
                self.assertIn("(ADR-127)", failures[0])

    def test_a_key_the_rollout_sets_itself_is_refused(self) -> None:
        workloads = self.workloads
        workloads["notifications"]["emptyUntil"]["image.tag"] = self.PUSH_DELIVERIES

        failures = rollout_gate._rollout_keeps_values_empty(workloads, self._workflow(self.shipped))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("names image.tag, which", failures[0])

    def test_a_commented_out_question_is_no_question(self) -> None:
        commented = self.shipped.replace(
            "python deploy/canary/canary.py empty-until", "# python deploy/canary/canary.py empty-until")

        failures = rollout_gate._rollout_keeps_values_empty(self.workloads, self._workflow(commented))

        self.assertEqual(1, len(failures), failures)

    def test_with_no_key_declared_there_is_nothing_to_hold(self) -> None:
        workloads = self.workloads
        del workloads["notifications"]["emptyUntil"]

        self.assertEqual([], rollout_gate._rollout_keeps_values_empty(workloads, self._workflow("")))


class RolloutBindingTests(unittest.TestCase):
    """Check 12: the workflow hands the image's tree to what reads it."""

    def _workflow(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "deploy.yml"
        path.write_text(text, encoding="utf-8")
        return path

    @property
    def shipped(self) -> str:
        return workflow_reads.WORKFLOW.read_text(encoding="utf-8")

    def test_the_shipped_workflow_is_bound(self) -> None:
        self.assertEqual([], rollout_gate._rollout_reads_the_image_source())

    def test_a_dropped_reading_source_fails_on_its_own(self) -> None:
        """The mutation ADR-050 is defended against: the commands still run
        and every other check still passes, so nothing but this one notices
        that the readings went back to the checkout's routes. The reading's
        flag is dropped alone, so this failure is its own rather than the
        gate's standing in for it."""
        without = "\n".join(
            line for line in self.shipped.splitlines()
            if line.strip() != '--source "$IMAGE_SOURCE" \\'
        )

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("read_prometheus.py", failures[0])

    def test_a_dropped_gate_source_fails_on_its_own(self) -> None:
        """The other half, and the reason each is mutated separately: a run
        that removed both would report one diagnostic and leave the branch
        that produced the other unproved."""
        without = self.shipped.replace(
            '--source "$IMAGE_SOURCE" --installed-source', "--installed-source")

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("canary.py check", failures[0])

    def test_a_dropped_export_fails(self) -> None:
        without = "\n".join(
            line for line in self.shipped.splitlines() if "IMAGE_SOURCE=" not in line
        )

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(without))

        self.assertTrue(any("GITHUB_ENV" in f for f in failures), failures)

    def test_a_reading_that_is_not_run_at_all_fails(self) -> None:
        """The gate's own subject, on checks 6's terms: a workflow this could
        find nothing in would report a bound rollout it never looked at."""
        without = "\n".join(
            line for line in self.shipped.splitlines() if "read_prometheus.py" not in line
        )

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(without))

        self.assertTrue(any("nowhere" in f for f in failures), failures)

    def test_a_commented_out_binding_binds_nothing(self) -> None:
        """This file argues at length about the commands it runs, so a check
        reading its prose would pass on the argument for the thing."""
        commented = "\n".join(
            "# " + line if "read_prometheus.py" in line or "IMAGE_SOURCE=" in line else line
            for line in self.shipped.splitlines()
        )

        self.assertTrue(rollout_gate._rollout_reads_the_image_source(self._workflow(commented)))

    def test_a_near_miss_variable_is_not_the_binding(self) -> None:
        """The near miss, and the exports are left correct on purpose: with
        them renamed too the export check reports the fault and this one
        never has to. `--source "$OLD_IMAGE_SOURCE"` passes a different tree
        while containing the flag and the name, so a substring test reads it
        as bound. The whole argument is matched instead."""
        near = self.shipped.replace('"$IMAGE_SOURCE"', '"$OLD_IMAGE_SOURCE"')

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(near))

        self.assertTrue(
            any("read_prometheus.py" in f for f in failures), failures)

    def test_a_dropped_installed_source_fails_on_its_own(self) -> None:
        """The stable track's tree is a second argument and goes missing the
        same way the first one can."""
        without = "\n".join(
            line for line in self.shipped.splitlines()
            if line.strip() != '--installed-source "$INSTALLED_SOURCE" \\'
        )

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(without))

        self.assertEqual(1, len(failures), failures)
        self.assertIn("--installed-source", failures[0])

    def test_an_export_that_no_archive_wrote_fails(self) -> None:
        """The near miss with the right name on it. Exporting the checkout
        satisfies every name and argument check while the rollout reads
        exactly the tree the binding exists to stop it reading."""
        swapped = self.shipped.replace(
            'IMAGE_SOURCE=$RUNNER_TEMP/image-source',
            'IMAGE_SOURCE=$GITHUB_WORKSPACE')

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(swapped))

        self.assertTrue(any("no `git archive` writes" in f for f in failures),
                        failures)

    def test_a_tree_archived_from_another_revision_fails(self) -> None:
        """The other half: the path is the archive's and the archive is of
        something else, so the export is correct about the wrong tree.

        The comparison is positive: an unrelated variable is refused exactly
        as `HEAD` is, which a denylist of checkout spellings could not do."""
        for revision in ("HEAD", "$GITHUB_SHA", "$OTHER_REVISION", "v1.2.3"):
            with self.subTest(revision=revision):
                swapped = self.shipped.replace(
                    'git archive "$REVISION" src', f"git archive {revision} src")

                failures = rollout_gate._rollout_reads_the_image_source(
                    self._workflow(swapped))

                self.assertTrue(
                    any(f"rather than {rollout_gate.RESOLVED_REVISION}" in f
                        for f in failures), failures)

    def test_the_right_name_with_the_wrong_value_fails(self) -> None:
        """One level deeper, and the last place the binding can be true in
        name only: the archive is of the expected variable, and the step
        sets that variable from the checkout rather than from the tag."""
        swapped = self.shipped.replace(
            'REVISION=$(python deploy/canary/canary.py revision --value "$TAG")',
            "REVISION=$GITHUB_SHA", 1)

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(swapped))

        self.assertTrue(
            any("does not set from `canary.py revision`" in f for f in failures),
            failures)

    def test_a_gate_run_missing_either_companion_fails(self) -> None:
        """A tree handed over without the workload it answers for is a
        refusal inside check(), and one handed over without the release it
        replaces leaves the baseline half unjudged. Both are the same run,
        not merely the same file."""
        # Only from the canary.py check run: the read_prometheus.py runs carry
        # the installed flag too, and their own failure must not stand in.
        for original, dropped, cost in (
            (' --installed-source "$INSTALLED_SOURCE" ' + chr(92) + "\n            --workload",
             " " + chr(92) + "\n            --workload", "the release it replaces"),
            ('"$INSTALLED_SOURCE" ' + chr(92) + '\n            --workload "$WORKLOAD"\n',
             '"$INSTALLED_SOURCE"\n', "the workload it answers for"),
        ):
            with self.subTest(cost=cost):
                self.assertEqual(self.shipped.count(original), 1, original)
                without = self.shipped.replace(original, dropped)

                failures = rollout_gate._rollout_reads_the_image_source(
                    self._workflow(without))

                self.assertEqual(1, len(failures), failures)
                self.assertIn(cost, failures[0])

    def test_a_revision_resolved_from_the_wrong_value_fails(self) -> None:
        """One level below the archive again: `canary.py revision` is
        invoked, and handed something that is not the tag."""
        swapped = self.shipped.replace(
            'revision --value "$TAG"', 'revision --value "$GITHUB_SHA"', 1)

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(swapped))

        self.assertTrue(
            any("does not set from `canary.py revision`" in f for f in failures),
            failures)

    def test_a_tag_from_anywhere_but_the_release_fails(self) -> None:
        """And below that: the stable track's tag is the running
        release's or it is a name somebody put there."""
        swapped = self.shipped.replace(
            'TAG=$(python deploy/canary/canary.py installed-tag',
            'TAG=$(echo installed-tag', 1)

        failures = rollout_gate._rollout_reads_the_image_source(self._workflow(swapped))

        self.assertTrue(
            any("other than `canary.py installed-tag`" in f for f in failures),
            failures)

    def test_a_continuation_is_read_as_one_invocation(self) -> None:
        """The flag is never on the line the command is named on."""
        lines = ["  run: |", "    python read_prometheus.py \\", '      --source "$IMAGE_SOURCE"']

        self.assertEqual(
            ['python read_prometheus.py --source "$IMAGE_SOURCE"'],
            rollout_gate._invocations(lines, "read_prometheus.py"),
        )


if __name__ == "__main__":
    unittest.main()
