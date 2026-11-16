"""Every helper a command or the base settings grant exists under `.claude/scripts/`.

A grant for a gone helper approves whatever file lands there next. It reads each command's
`allowed-tools` and `permissions.allow` in `.harness/settings.base.json`, from this file's own
location; a target's own config grants are its own."""

import json
import re
import tempfile
import unittest
from pathlib import Path

from test_command_grants import frontmatter_list

SCRIPTS = Path(__file__).resolve().parent
COMMANDS = SCRIPTS.parent / "commands"
BASE = SCRIPTS.parent.parent / ".harness" / "settings.base.json"

# `Bash(bash .claude/scripts/x.sh:*)`, `Bash(bash .claude/scripts/x.sh)` and the `py -3.12` form of a Python helper,
# each also with arguments after the name, as `Bash(py -3.12 .claude/scripts/x.py tasks start:*)` carries them.
# `-m unittest discover` and the other grants that name no file under `.claude/scripts/` do not match; one in a
# subdirectory does, as `sub/x.sh`.
HELPER = re.compile(r"Bash\((?:bash|py -3\.12) \.claude/scripts/([^:)\s]+)(?:[ :].*)?\)")


def granted_helpers(tools):
    """The helper file names a list of tool grants names, in order, with repeats."""
    return [found.group(1) for found in (HELPER.fullmatch(tool) for tool in tools) if found]


def missing(tools, scripts):
    return sorted({name for name in granted_helpers(tools) if not (scripts / name).is_file()})


class EveryGrantNamesAFileThatExists(unittest.TestCase):

    def command_grants(self):
        return {path.name: frontmatter_list(path.read_text(encoding="utf-8"), "allowed-tools")
                for path in sorted(COMMANDS.glob("*.md"))}

    def test_every_helper_a_command_grants_exists(self):
        grants = self.command_grants()
        seen = 0
        for name, tools in grants.items():
            seen += len(granted_helpers(tools))
            with self.subTest(command=name):
                self.assertEqual([], missing(tools, SCRIPTS))
        # Fewer means the pattern stopped matching, and the loop above passed over nothing.
        self.assertGreater(seen, 10, "found almost no helper grants in the commands")

    def test_every_helper_the_base_settings_grant_exists(self):
        allowed = json.loads(BASE.read_text(encoding="utf-8"))["permissions"]["allow"]
        self.assertGreaterEqual(len(granted_helpers(allowed)), 3, "found almost no helper grants in the base settings")
        self.assertEqual([], missing(allowed, SCRIPTS))


class TheScanSeesWhatItLooksFor(unittest.TestCase):
    """The scan's subject is the scan: a pattern that matched nothing would leave the cases above green."""

    def test_both_interpreters_and_both_grant_shapes_are_read(self):
        tools = ["Read", "Bash(bash .claude/scripts/a.sh:*)", "Bash(bash .claude/scripts/b.sh)",
                 "Bash(py -3.12 .claude/scripts/c.py:*)", "Bash(py -3.12 -m unittest discover:*)",
                 "Bash(git status:*)", "Bash(bash .claude/scripts/sub/d.sh:*)", "Agent(branch-reviewer)"]
        self.assertEqual(["a.sh", "b.sh", "c.py", "sub/d.sh"], granted_helpers(tools))

    def test_a_grant_that_carries_arguments_is_read(self):
        tools = ["Bash(py -3.12 .claude/scripts/dispatch_board.py tasks start:*)",
                 "Bash(py -3.12 .claude/scripts/dispatch_board.py tasks init)",
                 "Bash(py -3.12 .claude/scripts/shard.py --selective)",
                 "Bash(bash .claude/scripts/e.sh <n> start:*)",
                 "Bash(bash .claude/scripts/sub/f.sh tasks:*)", "Bash(bash .claude/scripts/g.shx)"]
        self.assertEqual(["dispatch_board.py", "dispatch_board.py", "shard.py", "e.sh", "sub/f.sh", "g.shx"],
                         granted_helpers(tools))

    def test_a_grant_with_arguments_for_a_file_that_is_gone_is_found(self):
        with tempfile.TemporaryDirectory() as holder:
            self.assertEqual(["renamed.py"],
                             missing(["Bash(py -3.12 .claude/scripts/renamed.py tasks start:*)"], Path(holder)))

    def test_a_grant_for_a_file_that_is_gone_is_found(self):
        with tempfile.TemporaryDirectory() as holder:
            (Path(holder) / "here.sh").write_text("", encoding="utf-8")
            tools = ["Bash(bash .claude/scripts/here.sh:*)", "Bash(bash .claude/scripts/gone.sh:*)",
                     "Bash(py -3.12 .claude/scripts/also-gone.py)"]
            self.assertEqual(["also-gone.py", "gone.sh"], missing(tools, Path(holder)))

    def test_a_gone_helper_in_a_subdirectory_is_found(self):
        # A grant approves whatever lands at its path, a subdirectory's as much as the scripts directory's own.
        with tempfile.TemporaryDirectory() as holder:
            self.assertEqual(["sub/gone.sh"], missing(["Bash(bash .claude/scripts/sub/gone.sh:*)"], Path(holder)))

    def test_a_directory_at_the_path_is_not_a_helper(self):
        with tempfile.TemporaryDirectory() as holder:
            (Path(holder) / "dir.sh").mkdir()
            self.assertEqual(["dir.sh"], missing(["Bash(bash .claude/scripts/dir.sh:*)"], Path(holder)))


if __name__ == "__main__":
    unittest.main()
