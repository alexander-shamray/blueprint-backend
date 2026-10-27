"""mutation_survivors.py, mutation-run.sh and stryker-config.json: what /mutation-sweep runs and reads back.

Stryker itself never runs here; the cases pin the plan against this repository's tree, the reading of a report
against the shapes a broken run leaves, and the helper's refusals before it reaches `dotnet`.
"""

import json
import shutil
import tempfile
import unittest
from pathlib import Path

import mutation_survivors as ms
from review_helpers import SCRIPTS, setUpModule, run_bash

RUN = SCRIPTS / "mutation-run.sh"
DETACH = SCRIPTS / "git-worktree-detach.sh"
DROP = SCRIPTS / "git-worktree-drop.sh"
REPO = str(SCRIPTS.parent.parent)
GIT_ID = "-c user.name=t -c user.email=t@example.invalid"


def mutant(status, line=1, mutator="Equality mutation", replacement="a <= b"):
    return {"id": "0", "status": status, "mutatorName": mutator, "replacement": replacement,
            "location": {"start": {"line": line, "column": 1}, "end": {"line": line, "column": 2}}}


def stryker_report(*mutants, root="C:\\work\\secsweep-abcdef\\src\\A.Domain"):
    return {"projectRoot": root,
            "files": {root + "\\Thing.cs": {"source": "if (a < b)\nreturn;\n", "mutants": list(mutants)}}}


class ThePlan(unittest.TestCase):

    def test_each_domain_project_is_paired_with_its_test_project(self):
        planned = ms.plan(["src/S/A/A.Domain/A.Domain.csproj"],
                          ["tests/A.Domain.Tests/A.Domain.Tests.csproj", "tests/A.Api.Tests/A.Api.Tests.csproj"])
        self.assertEqual([("A.Domain", "src/S/A/A.Domain", "tests/A.Domain.Tests")], planned)

    def test_a_domain_project_without_a_test_project_refuses_the_run(self):
        # Skipping it would report the project clean when nothing ran against it.
        with self.assertRaisesRegex(ms.Refusal, "B.Domain.Tests"):
            ms.plan(["src/A.Domain/A.Domain.csproj", "src/B.Domain/B.Domain.csproj"],
                    ["tests/A.Domain.Tests/A.Domain.Tests.csproj"])

    def test_no_domain_project_refuses_the_run(self):
        with self.assertRaises(ms.Refusal):
            ms.plan([], ["tests/A.Domain.Tests/A.Domain.Tests.csproj"])

    def test_this_repository_plans_every_tracked_domain_project(self):
        # The subject is the tree the sweep mutates, so a domain project added tomorrow is planned or refused here.
        domains = ms.tracked(REPO, "src/**/*.Domain.csproj")
        self.assertTrue(domains, "the pattern matched no domain project")
        planned = ms.plan_of(REPO)
        self.assertEqual(sorted(str(Path(d).parent.as_posix()) for d in domains),
                         sorted(directory for _, directory, _ in planned))


class AReport(unittest.TestCase):

    def test_undetected_mutants_are_listed_under_the_tracked_directory(self):
        counts, rows = ms.summarise("A.Domain", stryker_report(
            mutant("Killed"), mutant("Survived"), mutant("NoCoverage", line=2, mutator="Statement mutation",
                                                          replacement=";"),
            mutant("CompileError"), mutant("Ignored")), "src/S/A.Domain")
        self.assertEqual({"Killed": 1, "Survived": 1, "NoCoverage": 1, "CompileError": 1, "Ignored": 1}, counts)
        self.assertEqual([("src/S/A.Domain/Thing.cs", 1, "Survived", "Equality mutation", "a <= b", "if (a < b)"),
                          ("src/S/A.Domain/Thing.cs", 2, "NoCoverage", "Statement mutation", ";", "return;")],
                         rows)

    def test_a_clean_report_lists_nothing_and_is_not_refused(self):
        counts, rows = ms.summarise("A.Domain", stryker_report(mutant("Killed"), mutant("Timeout")), "src/A.Domain")
        self.assertEqual([], rows)
        self.assertEqual(2, counts["Killed"] + counts["Timeout"])

    def test_a_report_in_which_no_mutant_was_detected_is_refused(self):
        # The shape a runner that never reached the mutated assembly leaves: every mutant through.
        with self.assertRaisesRegex(ms.Refusal, "none detected"):
            ms.summarise("A.Domain", stryker_report(mutant("Survived"), mutant("Survived")), "src/A.Domain")

    def test_a_report_with_nothing_tested_is_refused(self):
        with self.assertRaisesRegex(ms.Refusal, "no mutant was tested"):
            ms.summarise("A.Domain", stryker_report(mutant("CompileError"), mutant("Ignored")), "src/A.Domain")
        with self.assertRaises(ms.Refusal):
            ms.summarise("A.Domain", {"projectRoot": "/x", "files": {}}, "src/A.Domain")

    def test_a_file_outside_the_reported_project_root_is_refused(self):
        report = stryker_report(mutant("Killed"), mutant("Survived"))
        report["projectRoot"] = "C:\\elsewhere"
        with self.assertRaisesRegex(ms.Refusal, "outside"):
            ms.summarise("A.Domain", report, "src/A.Domain")

    def test_a_missing_report_refuses_the_whole_run(self):
        reports = tempfile.mkdtemp(prefix="mutation-reports-")
        self.addCleanup(shutil.rmtree, reports, ignore_errors=True)
        with self.assertRaisesRegex(ms.Refusal, "cannot read"):
            ms.report(reports, REPO)

    def test_render_prints_the_counts_and_each_site(self):
        lines = ms.render("A.Domain", {"Killed": 3, "Survived": 1},
                          [("src/A.Domain/Thing.cs", 1, "Survived", "Equality mutation", "a <= b", "if (a < b)")])
        self.assertEqual(["project A.Domain killed 3 timeout 0 survived 1 nocoverage 0",
                          "  src/A.Domain/Thing.cs:1 Survived Equality mutation -> a <= b",
                          "      if (a < b)"], lines)


class TheConfiguration(unittest.TestCase):

    def test_the_runner_is_pinned_to_the_testing_platform(self):
        # Under VSTest this repository's xunit.v3 suites reported mutants killed that a hand-applied edit shows survive.
        config = json.loads((SCRIPTS / "stryker-config.json").read_text(encoding="utf-8"))["stryker-config"]
        self.assertEqual("mtp", config["test-runner"])
        self.assertEqual(["json"], config["reporters"])
        self.assertIn("*Exception.ctor", config["ignore-methods"])
        self.assertNotIn("break-at", config)

    def test_the_helper_reads_its_configuration_and_reader_beside_itself(self):
        text = RUN.read_text(encoding="utf-8")
        self.assertIn('--config-file "$here/stryker-config.json"', text)
        self.assertIn('"$here/mutation_survivors.py" plan', text)
        self.assertIn('"$here/mutation_survivors.py" report', text)


class TheHelper(unittest.TestCase):
    """mutation-run.sh against a small repository, with a `dotnet` that records any call and refuses it."""

    def setUp(self):
        self.repo = tempfile.mkdtemp(prefix="mutationrun-")
        self.addCleanup(shutil.rmtree, self.repo, ignore_errors=True)
        made = run_bash('cd "$R" && git init -q --initial-branch=main . && echo x > a.txt && git add a.txt'
                        f' && git {GIT_ID} commit -q -m init', R=self.repo)
        self.assertEqual(0, made.returncode, made.stderr)
        self.bin = tempfile.mkdtemp(prefix="mutationrun-bin-")
        self.addCleanup(shutil.rmtree, self.bin, ignore_errors=True)
        stub = Path(self.bin, "dotnet")
        stub.write_text('#!/usr/bin/env bash\necho "$*" >> "$(dirname "$0")/calls"\nexit 99\n')
        stub.chmod(0o755)
        sha = run_bash('cd "$R" && git rev-parse HEAD', R=self.repo).stdout.strip()
        detached = run_bash('cd "$R" && bash "$D" "$SHA" 2>/dev/null', R=self.repo, D=str(DETACH), SHA=sha)
        self.assertEqual(0, detached.returncode, detached.stderr)
        self.worktree = detached.stdout.strip()
        self.addCleanup(lambda: run_bash('cd "$R" && bash "$D" "$W" >/dev/null 2>&1; rm -rf "$W.mutation"',
                                         R=self.repo, D=str(DROP), W=self.worktree))

    def run_helper(self, *args):
        # Arguments travel in the environment, which MSYS does not re-parse (`review_helpers.run_bash`).
        numbered = {f"A{i}": a for i, a in enumerate(args, 1)}
        return run_bash('cd "$R" && set --; for ((i = 1; i <= N; i++)); do v="A$i"; set -- "$@" "${!v}"; done;'
                        ' PATH="$B:$PATH" bash "$RUN" "$@"',
                        R=self.repo, B=self.bin, RUN=str(RUN), N=str(len(args)), **numbered)

    def dotnet_calls(self):
        calls = Path(self.bin, "calls")
        return calls.read_text().splitlines() if calls.exists() else []

    def test_it_takes_exactly_one_argument(self):
        self.assertEqual(2, self.run_helper().returncode)
        self.assertEqual(2, self.run_helper(self.worktree, "--since").returncode)
        self.assertEqual([], self.dotnet_calls())

    def test_a_path_that_is_not_a_sweep_worktree_is_refused_before_dotnet(self):
        for path in (self.repo, "--output=/etc", "/nonexistent/secsweep-abcdef"):
            with self.subTest(path=path):
                self.assertEqual(2, self.run_helper(path).returncode)
        self.assertEqual([], self.dotnet_calls())

    def test_a_tree_with_no_domain_project_refuses_before_dotnet(self):
        result = self.run_helper(self.worktree)
        self.assertEqual(3, result.returncode, result.stderr)
        self.assertIn("nothing to mutate", result.stderr)
        self.assertEqual([], self.dotnet_calls())

    def test_a_second_run_into_the_same_reports_is_refused(self):
        made = run_bash('mkdir "$W.mutation"', W=self.worktree)
        self.assertEqual(0, made.returncode, made.stderr)
        result = self.run_helper(self.worktree)
        self.assertEqual(3, result.returncode)
        self.assertIn("already run", result.stderr)

    def test_each_planned_test_project_is_entered_by_its_own_directory(self):
        # On Windows `py` ends each planned line in CRLF and bash strips only the last, so it takes two projects
        # for a kept \r to name a directory that is not there.
        made = run_bash('cd "$W" && for p in A B; do mkdir -p "src/$p.Domain" "tests/$p.Domain.Tests"'
                        ' && echo "<Project />" > "src/$p.Domain/$p.Domain.csproj"'
                        ' && echo "<Project />" > "tests/$p.Domain.Tests/$p.Domain.Tests.csproj"; done'
                        f' && git add src tests && git {GIT_ID} commit -q -m projects', W=self.worktree)
        self.assertEqual(0, made.returncode, made.stderr)
        Path(self.bin, "dotnet").write_bytes(
            b'#!/usr/bin/env bash\necho "$(basename "$PWD") $*" >> "$(dirname "$0")/calls"\n')
        result = self.run_helper(self.worktree)
        self.assertNotIn("Stryker failed", result.stderr)
        stryker = [call for call in self.dotnet_calls() if " tool run dotnet-stryker " in call]
        self.assertEqual(["A.Domain.Tests", "B.Domain.Tests"], [call.split(" ", 1)[0] for call in stryker],
                         result.stderr)

    def test_the_teardown_takes_the_reports_with_the_worktree(self):
        made = run_bash('mkdir "$W.mutation" && echo r > "$W.mutation/report"', W=self.worktree)
        self.assertEqual(0, made.returncode, made.stderr)
        dropped = run_bash('cd "$R" && bash "$D" "$W"', R=self.repo, D=str(DROP), W=self.worktree)
        self.assertEqual(0, dropped.returncode, dropped.stderr)
        self.assertNotEqual(0, run_bash('test -e "$W.mutation"', W=self.worktree).returncode)


if __name__ == "__main__":
    unittest.main()
