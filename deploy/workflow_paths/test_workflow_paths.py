"""The shared reader's cases, and the workflow files it is the reader of.

Run from this directory: `python -m unittest`.
"""
from __future__ import annotations

import contextlib
import io
import re
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import workflow_paths

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"

# The workflows of the gates that import this module or call it from smoke.sh.
GATES = ("broker-permissions.yml", "observability.yml", "realm.yml", "deploy.yml", "helm.yml")


def workflow(**triggers: list[str] | None) -> str:
    """A workflow with the named triggers; a None value is a trigger with no `paths:` at all."""
    lines = ["on:"]
    for name, paths in triggers.items():
        lines.append(f"  {name}:")
        if paths is None:
            lines.append("    branches: [main]")
            continue
        lines.append("    paths:")
        lines.extend(f"      - '{path}'" for path in paths)
    lines += ["jobs:", "  check:", "    steps:", "      - 'src/**'"]
    return "\n".join(lines) + "\n"


class TheTriggerReader(unittest.TestCase):

    def test_one_triggers_filter_is_not_read_as_the_other_s(self):
        text = workflow(pull_request=["a/**"], push=["b/**"])
        self.assertEqual(workflow_paths.trigger_paths(text, "pull_request"), ["a/**"])
        self.assertEqual(workflow_paths.trigger_paths(text, "push"), ["b/**"])

    def test_a_paths_ignore_list_is_not_read_as_a_paths_filter(self):
        text = "on:\n  pull_request:\n    paths-ignore:\n      - 'deploy/helm/**'\n"
        self.assertEqual(workflow_paths.trigger_paths(text, "pull_request"), [])

    def test_a_step_list_after_the_last_trigger_is_not_read_as_its_filter(self):
        text = workflow(push=["other/**"])
        self.assertEqual(workflow_paths.trigger_paths(text, "push"), ["other/**"])

    def test_a_sibling_key_after_the_list_ends_it(self):
        text = "on:\n  push:\n    paths:\n      - 'a/**'\n    branches:\n      - main\n"
        self.assertEqual(workflow_paths.trigger_paths(text, "push"), ["a/**"])

    def test_a_sibling_key_before_the_list_is_skipped(self):
        text = "on:\n  push:\n    branches: [main]\n    paths:\n      - 'a/**'\n"
        self.assertEqual(workflow_paths.trigger_paths(text, "push"), ["a/**"])

    def test_all_three_quoting_styles_are_read(self):
        text = "on:\n  push:\n    paths:\n      - 'a/**'\n      - \"b/**\"\n      - c/**\n"
        self.assertEqual(workflow_paths.trigger_paths(text, "push"), ["a/**", "b/**", "c/**"])

    def test_a_comment_inside_the_list_is_neither_an_entry_nor_the_end_of_it(self):
        text = "on:\n  push:\n    paths:\n      - 'a/**'  # why\n      # a note\n      - c/** # too\n"
        self.assertEqual(workflow_paths.trigger_paths(text, "push"), ["a/**", "c/**"])

    def test_a_trigger_with_a_longer_name_is_not_the_trigger(self):
        text = "on:\n  pull_request_target:\n    paths:\n      - 'a/**'\n"
        self.assertIsNone(workflow_paths.trigger_paths(text, "pull_request"))

    def test_a_missing_trigger_is_none_and_a_filterless_one_is_empty(self):
        self.assertIsNone(workflow_paths.trigger_paths(workflow(push=["a"]), "pull_request"))
        self.assertEqual(workflow_paths.trigger_paths(workflow(push=None), "push"), [])


class TheGlob(unittest.TestCase):

    def test_what_a_glob_covers(self):
        for path, entry, expected in (
                ("**", "anything/at/all", True),
                ("src/**", "src", True),
                ("src/**", "src/Services/Ordering", True),
                ("src/Services/Ordering/**", "src", False),
                ("src/Services/Ordering/**", "src/Services/Catalog", False),
                ("src/**", "srcs", False),
                ("/**", "src", False),
                ("src/*", "src", False),
                ("*.md", "README.md", False),
                ("Directory.Packages.props", "Directory.Packages.props", True),
                ("Directory.Packages.props", "Directory.Build.props", False)):
            with self.subTest(path=path, entry=entry):
                self.assertEqual(workflow_paths.covers(path, entry), expected)


class TheWorkflowCheck(unittest.TestCase):

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.file = Path(directory.name) / "gate.yml"

    def check(self, text: str, required: list[str]) -> list[str]:
        self.file.write_text(text, encoding="utf-8")
        return workflow_paths.check_workflow_covers_inputs(self.file, required, reader="gate.py")

    def test_a_filter_covering_every_input_passes(self):
        both = ["src/**", "docs/a.md"]
        self.assertEqual(self.check(workflow(pull_request=both, push=both), ["src/x", "docs/a.md"]), [])

    def test_an_uncovered_input_is_named_with_its_trigger_in_both_triggers(self):
        both = ["docs/a.md"]
        found = self.check(workflow(pull_request=both, push=both), ["docs/a.md", "src"])
        self.assertEqual(len(found), 2, found)
        self.assertIn("pull_request trigger does not cover `src`", found[0])
        self.assertIn("push trigger does not cover `src`", found[1])
        self.assertIn("gate.py", found[0])

    def test_a_narrowed_filter_does_not_cover_the_input(self):
        found = self.check(workflow(pull_request=["src/BFF/Web/**"], push=["src/**"]), ["src/BFF/Web.Bff"])
        self.assertEqual(len(found), 1, found)
        self.assertIn("pull_request trigger", found[0])

    def test_a_missing_trigger_is_named_rather_than_counted(self):
        found = self.check(workflow(push=["src/**"]), ["src"])
        self.assertEqual(len(found), 1, found)
        self.assertIn("no pull_request trigger", found[0])

    def test_a_trigger_that_lists_no_paths_fails(self):
        found = self.check(workflow(pull_request=None, push=["src/**"]), ["src"])
        self.assertEqual(len(found), 1, found)
        self.assertIn("pull_request trigger lists no paths", found[0])

    def test_an_unreadable_workflow_is_a_failure(self):
        found = workflow_paths.check_workflow_covers_inputs(
            self.file.with_name("absent.yml"), ["src"], reader="gate.py", label=".github/workflows/absent.yml")
        self.assertEqual(len(found), 1, found)
        self.assertIn(".github/workflows/absent.yml is not readable", found[0])


class TheReadsCheck(unittest.TestCase):

    def check(self, reads: set[str], declared: list[str]) -> list[str]:
        return workflow_paths.check_reads_declared(reads, declared, reader="gate.py", label="gate.yml")

    def test_a_declared_read_passes_whole_or_under_a_declared_directory(self):
        self.assertEqual(self.check({"src", "docs/a.md", "tests/x/y.cs"}, ["src", "docs/a.md", "tests"]), [])

    def test_an_undeclared_read_is_named(self):
        found = self.check({"src/x", "docs/b.md"}, ["src"])
        self.assertEqual(len(found), 1, found)
        self.assertIn("`docs/b.md`", found[0])

    def test_a_substring_is_not_a_prefix(self):
        self.assertEqual(len(self.check({"srcs/x"}, ["src"])), 1)

    def test_an_empty_scan_fails_rather_than_agreeing_with_any_list(self):
        found = self.check(set(), ["src"])
        self.assertEqual(len(found), 1, found)
        self.assertIn("scan is broken", found[0])


class TheCommandLine(unittest.TestCase):

    def run_main(self, *argv: str, stdin: str = "") -> tuple[int, str]:
        out = io.StringIO()
        with (contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()),
              mock.patch("sys.stdin", io.StringIO(stdin))):
            code = workflow_paths.main(list(argv))
        return code, out.getvalue()

    def test_trigger_paths_prints_one_entry_a_line(self):
        text = workflow(pull_request=["a/**"], push=["b/**", "c"])
        self.assertEqual(self.run_main("trigger-paths", "push", stdin=text), (0, "b/**\nc\n"))

    def test_trigger_paths_prints_nothing_for_a_filterless_trigger(self):
        self.assertEqual(self.run_main("trigger-paths", "push", stdin=workflow(push=None)), (0, ""))

    def test_covered_answers_by_its_exit(self):
        entries = "src/BFF/Web/**\nother/**\n"
        self.assertEqual(self.run_main("covered", "other/x", stdin=entries)[0], 0)
        self.assertEqual(self.run_main("covered", "src/BFF/Web.Bff", stdin=entries)[0], 1)

    def test_undeclared_lists_what_no_entry_covers(self):
        code, out = self.run_main("undeclared", "src", "docs/a.md", stdin="src/x\ndocs/b.md\n")
        self.assertEqual((code, out), (1, "docs/b.md\n"))
        self.assertEqual(self.run_main("undeclared", "src", stdin="src/x\n")[0], 0)

    def test_undeclared_with_nothing_on_stdin_fails(self):
        self.assertEqual(self.run_main("undeclared", "src")[0], 2)


class TheWorkflowFilesThemselves(unittest.TestCase):
    """The subject: the files the reader exists to read, counted without it.

    A reader that returned nothing would agree with every list, so each
    workflow's filter entries are counted here by the one property that holds
    before its `jobs:` key — a list item six spaces in — and the reader is held
    to that count.
    """

    def filtered(self) -> dict[str, str]:
        found = {}
        for file in sorted(WORKFLOWS.glob("*.yml")):
            text = file.read_text(encoding="utf-8")
            if re.search(r"^    paths:\s*$", text, re.M):
                found[file.name] = text
        return found

    def test_the_scan_finds_the_gates_that_filter_on_paths(self):
        self.assertTrue(set(GATES) <= set(self.filtered()), set(self.filtered()))

    def test_the_reader_finds_every_entry_of_every_filter(self):
        for name, text in self.filtered().items():
            with self.subTest(workflow=name):
                header = text.split("\njobs:", 1)[0]
                counted = len(re.findall(r"^      - ", header, re.M))
                read = sum(len(workflow_paths.trigger_paths(text, trigger) or []) for trigger in
                           ("pull_request", "push"))
                self.assertGreater(counted, 0)
                self.assertEqual(read, counted)

    def test_every_filtered_workflow_filters_both_triggers(self):
        for name, text in self.filtered().items():
            for trigger in workflow_paths.TRIGGERS:
                with self.subTest(workflow=name, trigger=trigger):
                    self.assertTrue(workflow_paths.trigger_paths(text, trigger))

    def test_every_workflow_whose_gate_imports_this_module_watches_it(self):
        for name in GATES:
            text = (WORKFLOWS / name).read_text(encoding="utf-8")
            for trigger in workflow_paths.TRIGGERS:
                with self.subTest(workflow=name, trigger=trigger):
                    self.assertTrue(
                        workflow_paths.covered(workflow_paths.HOME, workflow_paths.trigger_paths(text, trigger) or []))


if __name__ == "__main__":
    unittest.main()
