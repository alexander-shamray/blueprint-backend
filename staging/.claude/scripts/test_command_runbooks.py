"""The commands split into a runbook and its argument (docs/token-plan.md, step 6).

A runbook is resident in every turn its command runs and its argument is read
only when a rule is disputed; these cases hold the two halves to each other.
"""

import re
import unittest

from review_helpers import COMMANDS

ARGUMENTS = COMMANDS.parent.parent / "docs" / "commands"

# Bytes as git stores them (LF), each landed size and about a tenth: a rule can
# be added without a fight, and a runbook past its ceiling has regrown.
CEILINGS = {
    "ship.md": 30500,
    "bug-sweep.md": 17500,
    "security-sweep.md": 14500,
    "branch.md": 8800,
    "validate-blueprint.md": 8800,
    "review-branch.md": 8200,
}

CITATION = re.compile(r"docs/commands/([a-z-]+\.md)`?, \*([^*]+)\*")


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

    def test_each_argument_names_its_runbook(self):
        for name in self.argued():
            with self.subTest(argument=name):
                self.assertIn(f".claude/commands/{name}", collapsed(ARGUMENTS / name))


if __name__ == "__main__":
    unittest.main()
