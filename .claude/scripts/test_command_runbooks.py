"""The commands split into a runbook and its argument (docs/token-plan.md, step 6).

A runbook is resident in every turn its command runs and its argument is read only when a rule is disputed;
these cases hold the two halves to each other, and the rest of what a run keeps resident under a ceiling.
"""

import re
import unittest

from review_helpers import COMMANDS

ARGUMENTS = COMMANDS.parent.parent / "docs" / "commands"

# Bytes with CRLF counted as LF, so a Windows checkout reads what Linux CI does;
# each landed size and about a tenth: a runbook past it has regrown its argument.
CEILINGS = {
    "ship.md": 30800,
    "bug-sweep.md": 19000,
    "security-sweep.md": 14900,
    "mutation-sweep.md": 7700,
    "branch.md": 9000,
    "validate-blueprint.md": 9000,
    "review-branch.md": 8400,
    "team.md": 6300,
}

ROOT = COMMANDS.parent.parent

# The rest of what a /ship run holds resident, measured the same way: step 6's
# exit counts these beside ship.md and branch.md, and none was split.
RESIDENT = {
    ".claude/commands/pr.md": 9100,
    "CLAUDE.md": 9000,
    ".claude/commands/commit.md": 5200,
}

CITATION = re.compile(r"docs/commands/([a-z-]+\.md)`?, \*([^*]+)\*")
POSITIONAL = re.compile(r"\$\d+|\$ARGUMENTS\[")


def collapsed(path):
    return " ".join(path.read_text(encoding="utf-8").split())


class EachSplitCommandHasBothHalves(unittest.TestCase):

    def argued(self):
        names = sorted(p.name for p in ARGUMENTS.glob("*.md"))
        self.assertGreaterEqual(len(names), len(CEILINGS), "the argument glob found fewer files than were split")
        return names

    def test_every_split_command_has_a_runbook_that_cites_its_argument(self):
        for name in sorted(set(self.argued()) | set(CEILINGS)):
            with self.subTest(command=name):
                self.assertTrue((ARGUMENTS / name).is_file(), f"docs/commands/{name} is missing")
                runbook = COMMANDS / name
                self.assertTrue(runbook.is_file(), f"no runbook for docs/commands/{name}")
                self.assertIn(f"docs/commands/{name}", collapsed(runbook))

    def test_every_citation_names_a_heading_that_exists(self):
        cited = 0
        for runbook in sorted(COMMANDS.glob("*.md")):
            for target, heading in CITATION.findall(collapsed(runbook)):
                cited += 1
                with self.subTest(runbook=runbook.name, target=target, heading=heading):
                    path = ARGUMENTS / target
                    self.assertTrue(path.is_file(), f"cites a missing docs/commands/{target}")
                    headings = re.findall(r"^#{2,3} (.+?)\s*$", path.read_text(encoding="utf-8"), re.MULTILINE)
                    self.assertIn(heading, headings)
        self.assertGreater(cited, len(CEILINGS), "the citation pattern stopped matching the runbooks")

    def test_each_runbook_stays_under_its_ceiling(self):
        for name, ceiling in CEILINGS.items():
            with self.subTest(command=name):
                size = len((COMMANDS / name).read_bytes().replace(b"\r\n", b"\n"))
                self.assertLessEqual(size, ceiling, f"{name} is {size} bytes, over {ceiling}")

    def test_each_resident_file_stays_under_its_ceiling(self):
        for name, ceiling in RESIDENT.items():
            with self.subTest(file=name):
                size = len((ROOT / name).read_bytes().replace(b"\r\n", b"\n"))
                self.assertLessEqual(size, ceiling, f"{name} is {size} bytes, over {ceiling}")

    def test_each_argument_names_its_runbook(self):
        for name in self.argued():
            with self.subTest(argument=name):
                self.assertIn(f".claude/commands/{name}", collapsed(ARGUMENTS / name))

    def test_free_text_arrives_through_arguments(self):
        # A positional placeholder carries one shell-quoted word, so an unquoted title, topic or path list is cut off.
        paths = sorted(COMMANDS.glob("*.md"))
        self.assertGreater(len(paths), len(CEILINGS), "the glob stopped finding the commands")
        for path in paths:
            with self.subTest(command=path.name):
                self.assertEqual([], POSITIONAL.findall(path.read_text(encoding="utf-8")))

    def test_the_positional_pattern_sees_each_spelling(self):
        for spelling in ("$0", "$1", "$9 x", "$12", "$ARGUMENTS[0]", "$ARGUMENTS[12]"):
            with self.subTest(spelling=spelling):
                self.assertTrue(POSITIONAL.search(spelling))
        self.assertIsNone(POSITIONAL.search("`$ARGUMENTS` and $HOME"))


if __name__ == "__main__":
    unittest.main()
