"""`suite_select.py`: a change selects what names it, what lists its tree, or, unplaced, everything.

The tables are held against the suites they name: every selector resolves, and a class listing a tree in a spelling
`LISTING` knows is named in `TREE_READERS`, by itself or its module, and a helper's by its module or any class of it.
"""

import ast
import importlib
import importlib.util
import re
import sys
import tempfile
import types
import unittest
import uuid
from pathlib import Path
from unittest import mock

import suite_select as select_mod
from review_helpers import copy_template, fixture_template, run_bash, setUpModule  # noqa: F401

SCRIPTS = Path(__file__).resolve().parent

# How a suite spells listing a tree of the checkout: a glob or directory read off any module constant (an ALL-CAPS
# name a suite binds to a path of the checkout) or `root`, or `git ls-files` run as an argument vector. A fixture's
# own directory (`self.root`, `self.dir`, `cache`, `worktree / CACHE`) is not one.
LISTING = re.compile(
    r"(?<![.\w])(?<!/ )(?:[A-Z][A-Z0-9_]*|root)\b[^\n]*?\.(?:r?glob|iterdir)\("
    r"|__file__\)[^\n]*?\.(?:r?glob|iterdir)\("
    r"|(?:self\.)?(?:source|LAUNCHER\.parent)\.glob\("
    r"|\[\s*(?:\"git\"|GIT)\s*,\s*\"ls-files\"")


def suites():
    return {path.stem: path.read_text(encoding="utf-8") for path in sorted(SCRIPTS.glob("test_*.py"))}


def selectors():
    return {selector for _, readers in select_mod.TREE_READERS for selector in readers}


# How a class runs a script that lists the checkout for it, which `LISTING` cannot see since the text spells no
# listing: `test_sweep_slices` runs `sweep-slices.sh` over the checkout as `Sweep(self, REPO)`, or over a clone of
# its HEAD as `Sweep(self, *own_clone(self))`. A comment, a module constant and a `Sweep` over a fixture repository
# are not a run over the checkout.
SCRIPT_LISTERS = re.compile(r"\bSweep\(self, (?:REPO\b|\*own_clone\(self\))")


def listing_units(text):
    """`(class name or None, source)` for each top-level class of a suite and for the module code between them.

    A class holding no test is a helper or a mixin its test classes call, so it is placed as module-level code is.
    """
    lines = text.splitlines()
    classes = [node for node in ast.parse(text).body if isinstance(node, ast.ClassDef)]
    units = [(node.name if any(isinstance(item, ast.FunctionDef) and item.name.startswith("test")
                               for item in node.body) else None,
              "\n".join(lines[node.lineno - 1:node.end_lineno])) for node in classes]
    inside = {number for node in classes for number in range(node.lineno, node.end_lineno + 1)}
    units.append((None, "\n".join(line for number, line in enumerate(lines, 1) if number not in inside)))
    return units


def unplaced_listings(held, readers):
    """`module.Class` (or `module` for module-level code) of each listing that no row of `TREE_READERS` names.

    Module-level code is a helper its classes call, so it is placed by the module or by any class of it that is a reader.
    """
    found = []
    for module, text in held.items():
        if module == "test_suite_select" or module in readers:
            continue
        for cls, source in listing_units(text):
            if cls is None:
                if (LISTING.search(source) or SCRIPT_LISTERS.search(source)) and not any(name.startswith(f"{module}.") for name in readers):
                    found.append(module)
            elif (LISTING.search(source) or SCRIPT_LISTERS.search(source)) and f"{module}.{cls}" not in readers:
                found.append(f"{module}.{cls}")
    return sorted(set(found))


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
    """`select` over a tree of its own, with the reach and the code list handed in, and the payload's rows alone, so
    the expectations below hold in a target whose module adds rows of its own."""

    def setUp(self):
        patcher = mock.patch.object(select_mod, "TREE_READERS", select_mod.PAYLOAD_TREE_READERS)
        patcher.start()
        self.addCleanup(patcher.stop)
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
        self.assertEqual([], unplaced_listings(suites(), selectors()))

    def test_a_listing_is_placed_by_its_class_not_only_its_module(self):
        listing = ('class Reader:\n    def test_x(self):\n        sorted(COMMANDS.glob("*.md"))\n\n'
                   "class Other:\n    def test_y(self):\n        pass\n")
        held = {"test_a": listing}
        self.assertEqual(["test_a.Reader"], unplaced_listings(held, set()))
        self.assertEqual([], unplaced_listings(held, {"test_a.Reader"}))
        self.assertEqual([], unplaced_listings(held, {"test_a"}))
        # Another class of a module that is a reader by class is not a reader for it.
        self.assertEqual(["test_a.Reader"], unplaced_listings(held, {"test_a.Other"}))
        helper = {"test_a": 'FILES = sorted(ROOT.rglob("*"))\n\nclass Uses:\n    pass\n'}
        self.assertEqual(["test_a"], unplaced_listings(helper, set()))
        self.assertEqual([], unplaced_listings(helper, {"test_a.Uses"}))

    def test_a_class_running_a_script_that_lists_the_tree_is_a_listing(self):
        held = {"test_b": "class Runs:\n    def test_x(self):\n        Sweep(self, REPO).slices()\n"}
        self.assertEqual(["test_b.Runs"], unplaced_listings(held, set()))
        cloned = {"test_b": "class Runs:\n    def test_x(self):\n        Sweep(self, *own_clone(self)).slices()\n"}
        self.assertEqual(["test_b.Runs"], unplaced_listings(cloned, set()))
        named = {"test_c": 'SLICES = SCRIPTS / "sweep-slices.sh"\nclass Reads:\n    def test_x(self):\n'
                           "        Sweep(self, self.repo).slices()  # `sweep-slices.sh` narrows\n"}
        self.assertEqual([], unplaced_listings(named, set()))

    def test_the_script_lister_has_a_subject_in_the_real_suites(self):
        # The positive control: the pin would otherwise pass for want of anything to look at.
        found = {f"test_sweep_slices.{cls}" for cls, source in listing_units(suites()["test_sweep_slices"])
                 if cls and SCRIPT_LISTERS.search(source)}
        self.assertEqual({"test_sweep_slices.TheCheckout", "test_sweep_slices.TheCheckoutReadsEachFileOnce"}, found)

    def test_a_class_holding_no_test_is_placed_as_a_helper(self):
        held = {"test_d": 'class Shim:\n    files = sorted(ROOT.rglob("*"))\n\n'
                          "class Reads(Shim):\n    def test_x(self):\n        pass\n"}
        self.assertEqual(["test_d"], unplaced_listings(held, set()))
        self.assertEqual([], unplaced_listings(held, {"test_d.Reads"}))
        mixin = {"test_e": "class Shim:\n    def run(self):\n        Sweep(self, REPO).slices()\n\n"
                           "class Reads(Shim):\n    def test_x(self):\n        pass\n"}
        self.assertEqual(["test_e"], unplaced_listings(mixin, set()))

    def test_the_pattern_finds_each_listing_spelling_and_no_fixture_read(self):
        found = ['sorted(COMMANDS.glob("*.md"))', "(root / tree).rglob(", 'SKILL_DIR.rglob("*.md")',
                 'Path(__file__).parent.glob("*.py")', 'self.source.glob("run-guard.*")',
                 'self.LAUNCHER.parent.glob("x")', '["git", "ls-files"]', '[GIT, "ls-files", "-s"]',
                 'sorted(PAYLOAD.rglob("*.md"))', "(CLAUDE / \"hooks\").iterdir()", 'REPO_TREE.glob("*")']
        missed = ['(self.root / "system-temp").iterdir()', "self.dir.iterdir()", '"git ls-files -s"',
                  "os.listdir(directory)", "(worktree / CACHE).iterdir()", "self.cache.iterdir()",
                  "box.cache.iterdir()", "run_path.iterdir()"]
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


class ATargetNamesItsOwnListings(unittest.TestCase):
    """A suite only a target has is named in `TREE_READERS` in its `local_commands.py`: a managed row naming it would
    not resolve in any other target. The pins above read the joined table, so they hold the target's rows too."""

    def module(self, source):
        # A uniquely named module in a directory of its own, so no case sees another's or the target's.
        name = f"local_commands_{uuid.uuid4().hex}"
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        Path(directory.name, f"{name}.py").write_text(source, encoding="utf-8")
        sys.path.insert(0, directory.name)
        self.addCleanup(sys.path.remove, directory.name)
        self.addCleanup(sys.modules.pop, name, None)
        importlib.invalidate_caches()
        return name

    def test_the_table_is_the_payloads_rows_then_the_targets(self):
        # Most targets declare no rows, so comparing the loaded table would pass with the join gone: the selector is
        # loaded afresh over a declaration module and its table read.
        fixture = types.SimpleNamespace(TREE_READERS=(("**", ("test_x.Y",)),))
        spec = importlib.util.spec_from_file_location("suite_select_fresh", SCRIPTS / "suite_select.py")
        fresh = importlib.util.module_from_spec(spec)
        with mock.patch("importlib.import_module", return_value=fixture):
            spec.loader.exec_module(fresh)
        self.assertEqual(select_mod.PAYLOAD_TREE_READERS + (("**", ("test_x.Y",)),), fresh.TREE_READERS)

    def test_a_target_that_declares_nothing_adds_no_row(self):
        self.assertEqual((), select_mod.local_tree_readers(f"local_commands_{uuid.uuid4().hex}"))
        self.assertEqual((), select_mod.local_tree_readers(self.module('CEILINGS = {"own.md": 1}\n')))

    def test_what_a_target_declares_is_read_and_selects(self):
        rows = select_mod.local_tree_readers(self.module(
            'TREE_READERS = (("**", ("test_own.Lists",)), (".claude/commands/*.md", ("test_own_module",)))\n'))
        self.assertEqual((("**", ("test_own.Lists",)), (".claude/commands/*.md", ("test_own_module",))), rows)
        with mock.patch.object(select_mod, "TREE_READERS", select_mod.PAYLOAD_TREE_READERS + rows):
            chosen, why = select_mod.select([".claude/commands/brand-new.md"], {}, Path("."), everywhere, [])
        self.assertIsNone(why)
        self.assertLessEqual({"test_own.Lists", "test_own_module"}, chosen)

    def test_a_declaration_whose_own_import_fails_is_not_an_absent_one(self):
        # Reading it as absent would drop every local row from the selection and pass in silence.
        with self.assertRaises(ModuleNotFoundError):
            select_mod.local_tree_readers(self.module("import a_module_no_target_has\n"))

    def test_a_misshapen_declaration_is_refused_rather_than_read(self):
        # A bare string selector would iterate as its characters and select nothing a reader could see.
        for source in ('TREE_READERS = (("**", "test_own"),)\n',
                       'TREE_READERS = [("**", ("test_own",))]\n',
                       'TREE_READERS = (("**", ()),)\n',
                       'TREE_READERS = (("", ("test_own",)),)\n',
                       'TREE_READERS = (("**", ("own",)),)\n',
                       'TREE_READERS = (("**", ("test_own",), "extra"),)\n',
                       'TREE_READERS = {"**": ("test_own",)}\n'):
            with self.subTest(source=source):
                with self.assertRaises(TypeError):
                    select_mod.local_tree_readers(self.module(source))


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
