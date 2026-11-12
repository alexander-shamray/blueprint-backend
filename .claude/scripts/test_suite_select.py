"""`suite_select.py`: a change selects what names it, what lists its tree, or, unplaced, everything.

The tables are held against the suites they name: every selector resolves, and a suite listing a tree in a spelling
`LISTING` knows is named in `TREE_READERS`, by module, found by reading the suites rather than a list kept here.
"""

import re
import tempfile
import unittest
from pathlib import Path

import suite_select as select_mod
from review_helpers import copy_template, fixture_template, run_bash, setUpModule  # noqa: F401

SCRIPTS = Path(__file__).resolve().parent

# How a suite spells listing a tree of the checkout: a glob or directory read off one of the roots the suites name,
# or `git ls-files` run as an argument vector. A fixture's own directory (`self.root`, `self.dir`) is not one.
LISTING = re.compile(
    r"(?<![.\w])(?:COMMANDS|AGENTS|ARGUMENTS|SCRIPTS|HOOKS|SKILL\w*|ROOT|root)\b[^\n]*?\.(?:r?glob|iterdir)\("
    r"|__file__\)[^\n]*?\.(?:r?glob|iterdir)\("
    r"|(?:self\.)?(?:source|LAUNCHER\.parent)\.glob\("
    r"|\[\s*(?:\"git\"|GIT)\s*,\s*\"ls-files\"")


def suites():
    return {path.stem: path.read_text(encoding="utf-8") for path in sorted(SCRIPTS.glob("test_*.py"))}


def selectors():
    return {selector for _, readers in select_mod.TREE_READERS for selector in readers}


# What every path selects: the suites that read the whole tracked tree.
ANYWHERE = {"test_harness_denies.HarnessControlSurfaceIsDenied", "test_harness_denies.TheRepositoryTracksNoSymbolicLink",
            "test_harness_legs.WhatTheDecisionLooksAtTests", "test_sweep_slices.TheCheckout",
            "test_sweep_slices.TheCheckoutReadsEachFileOnce"}


def everywhere(_path):
    return True


def nowhere(_path):
    return False


class PatternsMatchSegmentBySegment(unittest.TestCase):

    def test_a_star_stays_inside_one_segment(self):
        self.assertTrue(select_mod.matches(".claude/commands/*.md", ".claude/commands/ship.md"))
        self.assertFalse(select_mod.matches(".claude/commands/*.md", ".claude/commands/x/ship.md"))
        self.assertTrue(select_mod.matches("*", "README.md"))
        self.assertFalse(select_mod.matches("*", "docs/README.md"))

    def test_a_double_star_spans_any_number_of_segments_none_included(self):
        self.assertTrue(select_mod.matches("**/.gitignore", ".gitignore"))
        self.assertTrue(select_mod.matches("**/.gitignore", "a/b/.gitignore"))
        self.assertTrue(select_mod.matches(".claude/skills/**", ".claude/skills/codebase-index/scripts/cbx"))
        self.assertFalse(select_mod.matches(".claude/skills/**", ".claude/hooks/x.sh"))
        self.assertTrue(select_mod.matches("**", "any/path/at/all"))


class WhatAChangeSelects(unittest.TestCase):
    """`select` over a tree of its own, with the reach and the code list handed in."""

    def setUp(self):
        holder = tempfile.TemporaryDirectory()
        self.addCleanup(holder.cleanup)
        self.root = Path(holder.name)
        self.code = []
        for path, text in {
            ".claude/scripts/leaf.sh": "#!/usr/bin/env bash\necho leaf\n",
            ".claude/scripts/caller.sh": "#!/usr/bin/env bash\nbash .claude/scripts/leaf.sh\n",
            ".claude/scripts/lib_mod.py": "VALUE = 1\n",
            ".claude/scripts/user": "#!/usr/bin/env python3\nimport lib_mod\n",
        }.items():
            target = self.root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text, encoding="utf-8")
            self.code.append(path)
        self.modules = {
            "test_leaf": 'HELPER = SCRIPTS / "leaf.sh"\n',
            "test_caller": 'HELPER = SCRIPTS / "caller.sh"\n',
            "test_user": 'USER = SCRIPTS / "user"\n',
            "test_other": "nothing here\n",
        }

    def select(self, paths, reach=everywhere):
        return select_mod.select(paths, self.modules, self.root, reach, self.code)

    def test_a_helper_selects_the_suites_that_name_it_and_those_of_its_callers(self):
        chosen, why = self.select([".claude/scripts/leaf.sh"])
        self.assertIsNone(why)
        self.assertEqual({"test_leaf", "test_caller", "test_here_string_band",
                          "test_git_rebase_onto_main.TheArgumentGuardsEachHaveTheirOwnCase",
                          "test_pr_helpers.NoHelperNeedsBash4"} | ANYWHERE, chosen)

    def test_a_python_module_is_named_by_its_import(self):
        chosen, _ = self.select([".claude/scripts/lib_mod.py"])
        self.assertIn("test_user", chosen)
        self.assertNotIn("test_leaf", chosen)

    def test_a_changed_suite_selects_itself_the_readers_of_its_tree_and_the_table_check(self):
        # The table check among them, since a listing added to a suite is what it exists to catch.
        chosen, why = self.select([".claude/scripts/test_other.py"])
        self.assertIsNone(why)
        self.assertEqual({"test_other", "test_python_hygiene", "test_ship_sync.TheSyncRow",
                          "test_pr_helpers.NoHelperNeedsBash4", "test_suite_select.TheTablesNameWhatTheSuitesHold"}
                         | ANYWHERE, chosen)

    def test_a_path_outside_the_reach_selects_only_what_reads_every_path(self):
        self.assertEqual((ANYWHERE, None), self.select(["src/Product/Thing.cs"], reach=nowhere))

    def test_a_path_inside_the_reach_that_nothing_names_selects_the_whole_suite(self):
        chosen, why = self.select([".claude/scripts/leaf.sh", ".harness/unnamed.json"])
        self.assertIsNone(chosen)
        self.assertIn(".harness/unnamed.json", why)

    def test_a_listed_tree_selects_its_readers_though_no_suite_names_the_file(self):
        chosen, why = self.select([".claude/commands/brand-new.md"])
        self.assertIsNone(why)
        self.assertIn("test_command_grants", chosen)
        self.assertIn("test_harness_denies.HarnessControlSurfaceIsDenied", chosen)

    def test_a_shared_module_selects_the_whole_suite(self):
        for path in sorted(select_mod.WHOLE):
            with self.subTest(path=path):
                chosen, why = self.select([path])
                self.assertIsNone(chosen)
                self.assertIn(path, why)

    def test_an_unread_or_empty_change_selects_the_whole_suite(self):
        self.assertIsNone(self.select(None)[0])
        self.assertIsNone(self.select([])[0])

    def test_an_unread_reach_selects_the_whole_suite(self):
        chosen, why = select_mod.select([".claude/scripts/leaf.sh"], self.modules, self.root, None, self.code)
        self.assertIsNone(chosen)
        self.assertIn("reach", why)


class AnIdIsChosenByItsModuleOrClass(unittest.TestCase):

    def test_module_class_and_a_failed_import(self):
        chosen = {"test_a", "test_b.Two"}
        self.assertTrue(select_mod.chosen("test_a.One.test_x", chosen))
        self.assertTrue(select_mod.chosen("test_b.Two.test_x", chosen))
        self.assertFalse(select_mod.chosen("test_b.Three.test_x", chosen))
        self.assertFalse(select_mod.chosen("test_c.One.test_x", chosen))
        # A module that failed to import is a loader placeholder, and the red it carries runs in every selection.
        self.assertTrue(select_mod.chosen("unittest.loader._FailedTest.test_c", chosen))


class TheTablesNameWhatTheSuitesHold(unittest.TestCase):

    def test_every_selector_resolves_to_a_suite_and_class_that_exist(self):
        held = suites()
        for selector in sorted(selectors()):
            module, _, cls = selector.partition(".")
            with self.subTest(selector=selector):
                self.assertIn(module, held)
                if cls:
                    self.assertRegex(held[module], rf"(?m)^class {re.escape(cls)}\b")

    def test_every_suite_that_lists_a_tree_is_a_tree_reader(self):
        listing = sorted(name for name, text in suites().items()
                         if name != "test_suite_select" and LISTING.search(text))
        self.assertGreater(len(listing), 10, "the pattern stopped finding listings")
        readers = {selector.partition(".")[0] for selector in selectors()}
        self.assertEqual([], [name for name in listing if name not in readers])

    def test_the_pattern_finds_each_listing_spelling_and_no_fixture_read(self):
        found = ['sorted(COMMANDS.glob("*.md"))', "(root / tree).rglob(", 'SKILL_DIR.rglob("*.md")',
                 'Path(__file__).parent.glob("*.py")', 'self.source.glob("run-guard.*")',
                 'self.LAUNCHER.parent.glob("x")', '["git", "ls-files"]', '[GIT, "ls-files", "-s"]']
        missed = ['(self.root / "system-temp").iterdir()', "self.dir.iterdir()", '"git ls-files -s"',
                  "os.listdir(directory)"]
        for text in found:
            with self.subTest(text=text):
                self.assertRegex(text, LISTING)
        for text in missed:
            with self.subTest(text=text):
                self.assertNotRegex(text, LISTING)

    def test_the_whole_suite_modules_exist(self):
        root = select_mod.ROOT
        for path in sorted(select_mod.WHOLE - {f".claude/scripts/{select_mod.LOCAL_MODULE}.py"}):
            with self.subTest(path=path):
                self.assertTrue((root / path).is_file(), path)


# A base branch and a feature branch one commit ahead of it, which `changed` reads against the base.
BUILD = '''
git init -q --initial-branch=base "$R/work"
cd "$R/work"
git config user.email test@example.invalid
git config user.name Test
echo a > kept.txt && echo b > edited.txt && git add -A && git commit -qm base
git checkout -qb feat/x
echo c > committed.txt && git add -A && git commit -qm work
'''


class TheChangeIsReadAgainstTheBase(unittest.TestCase):

    def test_commits_edits_and_untracked_files_are_all_the_change(self):
        work = Path(copy_template(self, fixture_template(BUILD, "R", GIT_CONFIG_GLOBAL="/dev/null",
                                                         GIT_CONFIG_NOSYSTEM="1"))) / "work"
        (work / "edited.txt").write_text("changed\n", encoding="utf-8")
        (work / "new.txt").write_text("new\n", encoding="utf-8")
        self.assertEqual(["committed.txt", "edited.txt", "new.txt"], select_mod.changed(work, "base"))

    def test_a_base_that_does_not_resolve_is_unread(self):
        work = Path(copy_template(self, fixture_template(BUILD, "R", GIT_CONFIG_GLOBAL="/dev/null",
                                                         GIT_CONFIG_NOSYSTEM="1"))) / "work"
        self.assertIsNone(select_mod.changed(work, "no-such-base"))


if __name__ == "__main__":
    unittest.main()
