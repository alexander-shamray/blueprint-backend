"""review_helpers.py's fixture templates: a build starts no background git maintenance, and a copy takes no lock a
background git holds.

Each case showing a lock left out is paired with a file that is copied, so it failed for its reason."""

import os
import subprocess
import unittest
from pathlib import Path

from review_helpers import (  # noqa: F401
    GIT, QUIET_GIT, copy_template, fixture_template, no_maintenance, run_bash, setUpModule)

QUIET = {"GIT_CONFIG_GLOBAL": "/dev/null", "GIT_CONFIG_SYSTEM": "/dev/null", "GIT_CONFIG_NOSYSTEM": "1"}


def build_with_locks(root):
    """A repository whose `objects/` holds a lock, and a tracked `yarn.lock` that is content."""
    work = root / "work"
    work.mkdir()
    (work / "yarn.lock").write_text("content\n", encoding="utf-8")
    (root / "bare" / "objects").mkdir(parents=True)
    (root / "bare" / "HEAD").write_text("ref: refs/heads/main\n", encoding="utf-8")
    (root / "bare" / "objects" / "maintenance.lock").write_text("", encoding="utf-8")
    (work / "objects").mkdir()
    (work / "objects" / "kept.lock").write_text("content\n", encoding="utf-8")
    built = run_bash('set -eu\ncd "$W"\ngit init -q\ngit config user.email test@example.invalid\n'
                     'git config user.name Test\ngit add yarn.lock\ngit commit -qm base\n',
                     W=work.as_posix(), **QUIET)
    if built.returncode != 0:
        raise RuntimeError(built.stderr)
    (work / ".git" / "objects" / "maintenance.lock").write_text("", encoding="utf-8")


class NoMaintenance(unittest.TestCase):

    def test_a_count_the_environment_holds_is_extended_not_replaced(self):
        variables = no_maintenance({"GIT_CONFIG_COUNT": "2"})
        self.assertEqual("3", variables["GIT_CONFIG_COUNT"])
        self.assertEqual("maintenance.auto", variables["GIT_CONFIG_KEY_2"])
        self.assertEqual("false", variables["GIT_CONFIG_VALUE_2"])
        self.assertNotIn("GIT_CONFIG_KEY_0", variables)

    def test_an_environment_with_no_count_gets_the_first_slot(self):
        variables = no_maintenance({})
        self.assertEqual("1", variables["GIT_CONFIG_COUNT"])
        self.assertEqual("maintenance.auto", variables["GIT_CONFIG_KEY_0"])

    def test_a_count_the_caller_passes_to_a_bash_build_is_extended(self):
        template = fixture_template(
            'set -eu\ncd "$R"\n(git config --get maintenance.auto; git config --get my.key) > "$R/seen"\n',
            GIT_CONFIG_COUNT="1", GIT_CONFIG_KEY_0="my.key", GIT_CONFIG_VALUE_0="mine", **QUIET)
        self.assertEqual(["false", "mine"], Path(template, "seen").read_text(encoding="utf-8").split())

    def test_a_bash_build_runs_with_maintenance_off(self):
        template = fixture_template(
            'set -eu\ncd "$R"\n(git config --get maintenance.auto || echo unset) > "$R/seen"\n', **QUIET)
        self.assertEqual("false", Path(template, "seen").read_text(encoding="utf-8").strip())

    def test_a_function_build_runs_with_maintenance_off_and_leaves_the_environment_as_it_was(self):
        before = dict(os.environ)
        seen = []

        def build(root):
            result = subprocess.run([GIT, "config", "--get", "maintenance.auto"], capture_output=True, text=True,
                                    encoding="utf-8", env={**os.environ, **QUIET}, cwd=root)
            seen.append(result.stdout.strip())

        fixture_template(build, key="review-helpers-function-build")
        self.assertEqual(["false"], seen)
        self.assertEqual(before, dict(os.environ))

    def test_a_template_config_carries_maintenance_off_for_its_copies(self):
        self.assertIn("[maintenance]\n\tauto = false\n", QUIET_GIT)


class CopyTemplate(unittest.TestCase):

    def setUp(self):
        self.root = Path(copy_template(self, fixture_template(build_with_locks, key="review-helpers-locks")))

    def test_a_lock_a_background_git_holds_is_not_copied(self):
        self.assertFalse((self.root / "work" / ".git" / "objects" / "maintenance.lock").exists())

    def test_a_lock_in_a_bare_repositorys_objects_is_not_copied(self):
        self.assertTrue((self.root / "bare" / "HEAD").is_file())
        self.assertFalse((self.root / "bare" / "objects" / "maintenance.lock").exists())

    def test_a_tracked_file_that_ends_in_lock_is_copied(self):
        self.assertEqual("content\n", (self.root / "work" / "yarn.lock").read_text(encoding="utf-8"))

    def test_a_lock_file_in_a_work_tree_directory_named_objects_is_copied(self):
        self.assertTrue((self.root / "work" / "objects" / "kept.lock").is_file())

    def test_the_copy_is_a_working_repository(self):
        result = subprocess.run([GIT, "-C", str(self.root / "work"), "log", "--format=%s"], capture_output=True,
                                text=True, encoding="utf-8", env={**os.environ, **QUIET})
        self.assertEqual("base", result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
