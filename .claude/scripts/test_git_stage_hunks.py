"""git-stage-hunks.sh: the hunks named, and only they, staged from one tracked file, the working tree untouched.

Real repositories, the index and the working tree read as bytes, so a CRLF line that lost its `\\r` fails. Each
refusal is paired with a staging that succeeds on the same fixture, so it failed for its reason."""

import os
import subprocess
import unittest
from pathlib import Path

from review_helpers import BASH, GIT, SCRIPTS, copy_template, fixture_template, run_bash, setUpModule  # noqa: F401

HELPER = SCRIPTS / "git-stage-hunks.sh"
QUIET = {"GIT_CONFIG_GLOBAL": "/dev/null", "GIT_CONFIG_SYSTEM": "/dev/null", "GIT_CONFIG_NOSYSTEM": "1"}


def lines(count, end=b"\n"):
    return [b"line %02d" % n + end for n in range(1, count + 1)]


def changed(original, at, end=b"\n"):
    """`original` with each 1-based line in `at` rewritten: each a hunk of its own while they lie seven apart."""
    return b"".join(b"edit %02d" % n + end if n in at else line for n, line in enumerate(original, 1))


# What each file holds when committed, and the lines the working tree rewrites, one hunk each.
FILES = {
    "two.txt": (lines(20), {2, 18}, b"\n"),
    "three.txt": (lines(30), {2, 15, 28}, b"\n"),
    "crlf.txt": (lines(20, b"\r\n"), {2, 18}, b"\r\n"),
    "sub/deep.txt": (lines(20), {2, 18}, b"\n"),
}


def build(root):
    work = root / "work"
    for name, (original, _, _) in FILES.items():
        (work / name).parent.mkdir(parents=True, exist_ok=True)
        (work / name).write_bytes(b"".join(original))
    (work / "bin.dat").write_bytes(b"\x00\x01binary\x00")
    (root / "other").mkdir()
    (root / "other" / "outside.txt").write_bytes(b"not in any checkout\n")
    built = run_bash(
        'set -eu\ncd "$W"\ngit init -q --initial-branch=main\ngit config user.email test@example.invalid\n'
        'git config user.name Test\ngit config core.autocrlf false\n'
        'git add two.txt three.txt crlf.txt sub/deep.txt bin.dat\ngit commit -qm base\n',
        W=work.as_posix(), **QUIET)
    if built.returncode != 0:
        raise RuntimeError(built.stderr)


class Fixture(unittest.TestCase):

    def setUp(self):
        self.root = copy_template(self, fixture_template(build, key="git-stage-hunks"))
        self.work = Path(self.root, "work")
        for name, (original, at, end) in FILES.items():
            (self.work / name).write_bytes(changed(original, at, end))
        (self.work / "bin.dat").write_bytes(b"\x00\x02binary changed\x00")
        (self.work / "new.txt").write_bytes(b"untracked\n")

    def helper(self, *args, cwd=""):
        script = 'cd "$W"\nbash "$H"' + "".join(f' "$A{n}"' for n in range(len(args)))
        env = {f"A{n}": arg for n, arg in enumerate(args)}
        return run_bash(script, W=(self.work / cwd).as_posix(), H=HELPER.as_posix(), **env, **QUIET)

    def git(self, *args):
        result = subprocess.run([GIT, "-C", str(self.work), *args], capture_output=True,
                                env={**os.environ, **QUIET})
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout

    def index(self, name):
        return self.git("show", f":{name}")

    def staged_nothing(self):
        self.assertEqual(b"", self.git("diff", "--cached", "--name-only"))

    def untouched(self, name):
        original, at, end = FILES[name]
        self.assertEqual(changed(original, at, end), (self.work / name).read_bytes())


class Listing(Fixture):

    def test_it_numbers_each_hunk_with_its_header_and_changes(self):
        result = self.helper("--list", "three.txt")
        self.assertEqual(0, result.returncode, result.stderr)
        numbered = [line for line in result.stdout.splitlines() if not line.startswith(" ")]
        self.assertEqual(["1: @@ -1,5 +1,5 @@", "2: @@ -12,7 +12,7 @@ line 11", "3: @@ -25,6 +25,6 @@ line 24"],
                         numbered)
        self.assertIn("    -line 15\n    +edit 15\n    (+1 -1)", result.stdout)
        self.staged_nothing()

    def test_a_crlf_file_lists_without_its_carriage_returns(self):
        # Bytes: `run_bash` decodes in text mode, whose universal newlines turn every `\r` into a newline first.
        result = subprocess.run([BASH, "-c", 'cd "$W"\nbash "$H" --list crlf.txt'], capture_output=True,
                                env={**os.environ, "W": self.work.as_posix(), "H": HELPER.as_posix(), **QUIET})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn(b"\r", result.stdout)
        self.assertIn(b"    +edit 18\n", result.stdout)

    def test_a_clean_file_has_none(self):
        self.git("checkout", "--", "two.txt")
        result = self.helper("--list", "two.txt")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("no unstaged hunks in two.txt\n", result.stdout)


class Staging(Fixture):

    def test_the_second_of_two_is_staged_alone(self):
        result = self.helper("two.txt", "2")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(changed(lines(20), {18}), self.index("two.txt"))
        self.untouched("two.txt")
        self.assertIn("two.txt | 2 +-", result.stdout)
        # What is left unstaged is the first hunk, now numbered 1 of 1.
        self.assertTrue(self.helper("--list", "two.txt").stdout.startswith("1: @@ -1,5 +1,5 @@\n"))

    def test_the_first_and_third_of_three_skip_the_second(self):
        result = self.helper("three.txt", "3,1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(changed(lines(30), {2, 28}), self.index("three.txt"))
        self.untouched("three.txt")

    def test_a_number_given_twice_stages_its_hunk_once(self):
        result = self.helper("three.txt", "2,2")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(changed(lines(30), {15}), self.index("three.txt"))

    def test_a_crlf_file_keeps_its_carriage_returns_in_the_index(self):
        result = self.helper("crlf.txt", "1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(changed(lines(20, b"\r\n"), {2}, b"\r\n"), self.index("crlf.txt"))
        self.untouched("crlf.txt")

    def test_a_path_relative_to_a_subdirectory_is_the_one_staged(self):
        result = self.helper("deep.txt", "1", cwd="sub")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(changed(lines(20), {2}), self.index("sub/deep.txt"))
        self.untouched("sub/deep.txt")

    def test_a_config_that_reshapes_the_diff_changes_nothing(self):
        # Each would hand `git apply` a patch it cannot read, or refuse the trailing blanks below; the
        # committed blank line 03 is the context `diff.suppressBlankEmpty` would print with no space.
        base = lines(20)
        base[2] = b"\n"
        (self.work / "two.txt").write_bytes(b"".join(base))
        self.git("commit", "-qam", "a blank line 03")
        for key, value in (("diff.noprefix", "true"), ("color.diff", "always"), ("color.ui", "always"),
                           ("diff.suppressBlankEmpty", "true"), ("apply.whitespace", "error"),
                           ("diff.context", "0"), ("diff.relative", "true")):
            self.git("config", key, value)
        wanted = b"".join(base).replace(b"line 02\n", b"edit 02   \n")
        (self.work / "two.txt").write_bytes(wanted.replace(b"line 18\n", b"edit 18\n"))
        result = self.helper("two.txt", "1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(wanted, self.index("two.txt"))


class Refusals(Fixture):

    def refused(self, code, *args, says):
        result = self.helper(*args)
        self.assertEqual(code, result.returncode, result.stderr)
        self.assertIn(says, result.stderr)
        self.staged_nothing()

    def test_a_number_past_the_last_hunk(self):
        self.refused(4, "two.txt", "1,3", says="past the last of two.txt's 2")
        self.assertEqual(0, self.helper("two.txt", "1,2").returncode)

    def test_a_number_that_is_not_one_counted_from_one(self):
        # Twenty digits overflowed `-gt`, which read the error as in range and staged hunk 1 alone.
        for numbers in ("0", "1,", "a", "1 2", "-1", "", "1,99999999999999999999", "1234567890"):
            with self.subTest(numbers=numbers):
                self.refused(2, "two.txt", numbers, says="counted from 1")

    def test_an_untracked_file(self):
        self.refused(3, "new.txt", "1", says="not one tracked file")

    def test_a_file_outside_the_checkout(self):
        self.refused(3, "../other/outside.txt", "1", says="not one tracked file")

    def test_a_directory(self):
        self.refused(3, "sub", "1", says="not one tracked file")

    def test_a_binary_file(self):
        self.refused(3, "bin.dat", "1", says="binary")
        self.refused(3, "--list", "bin.dat", says="binary")

    def test_a_deleted_file(self):
        (self.work / "two.txt").unlink()
        self.refused(3, "two.txt", "1", says="added, deleted or changed in type")

    def test_a_path_read_as_a_flag(self):
        self.refused(2, "-p", "1", says="start with '-'")
        self.refused(2, "--list", "--cached", says="start with '-'")

    def test_the_wrong_argument_count(self):
        self.refused(2, "two.txt", says="usage")
        self.refused(2, "two.txt", "1", "2", says="usage")

    def test_a_hunk_git_apply_refuses_stages_nothing(self):
        # The index held by another process is where `git apply --cached` refuses after the cut succeeded.
        lock = self.work / ".git" / "index.lock"
        lock.write_bytes(b"")
        self.refused(5, "three.txt", "1,2", says="nothing was staged")
        self.untouched("three.txt")
        lock.unlink()
        self.assertEqual(0, self.helper("three.txt", "1,2").returncode)


if __name__ == "__main__":
    unittest.main()
