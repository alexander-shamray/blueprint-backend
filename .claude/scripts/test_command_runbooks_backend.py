"""This repository's own split commands and its `CLAUDE.md`, held to the payload's runbook ceilings.

`test_command_runbooks.py` holds the payload's runbooks; these cases hold the local ones, whose ceilings are measured
here. The payload's argument glob reads `docs/commands/`, so each local argument is checked for its runbook there.
"""

import unittest

from review_helpers import COMMANDS
from test_command_runbooks import ARGUMENTS, ROOT, collapsed

# Bytes with CRLF counted as LF, each landed size and about a tenth, as the payload's are.
CEILINGS = {
    "mutation-sweep.md": 7700,
    "validate-blueprint.md": 9900,
    "review-branch.md": 8400,
}

RESIDENT = {
    "CLAUDE.md": 9000,
}


class TheLocalSplitCommandsHaveBothHalves(unittest.TestCase):

    def test_each_local_runbook_cites_its_argument(self):
        for name in sorted(CEILINGS):
            with self.subTest(command=name):
                self.assertTrue((ARGUMENTS / name).is_file(), f"docs/commands/{name} is missing")
                runbook = COMMANDS / name
                self.assertTrue(runbook.is_file(), f"no runbook for docs/commands/{name}")
                self.assertIn(f"docs/commands/{name}", collapsed(runbook))

    def test_each_local_runbook_stays_under_its_ceiling(self):
        for name, ceiling in CEILINGS.items():
            with self.subTest(command=name):
                size = len((COMMANDS / name).read_bytes().replace(b"\r\n", b"\n"))
                self.assertLessEqual(size, ceiling, f"{name} is {size} bytes, over {ceiling}")

    def test_each_local_resident_file_stays_under_its_ceiling(self):
        for name, ceiling in RESIDENT.items():
            with self.subTest(file=name):
                size = len((ROOT / name).read_bytes().replace(b"\r\n", b"\n"))
                self.assertLessEqual(size, ceiling, f"{name} is {size} bytes, over {ceiling}")


if __name__ == "__main__":
    unittest.main()
