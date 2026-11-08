"""This repository's `CLAUDE.md`, held to a resident ceiling as the payload holds its own resident files.

`test_command_runbooks.py` holds every split command, this repository's included, at the ceilings `local_commands.py`
declares; `CLAUDE.md` is this repository's own file, so its ceiling stays here.
"""

import unittest

from test_command_runbooks import ROOT

RESIDENT = {
    "CLAUDE.md": 9000,
}


class TheLocalResidentFilesStayUnderTheirCeilings(unittest.TestCase):

    def test_each_local_resident_file_stays_under_its_ceiling(self):
        for name, ceiling in RESIDENT.items():
            with self.subTest(file=name):
                size = len((ROOT / name).read_bytes().replace(b"\r\n", b"\n"))
                self.assertLessEqual(size, ceiling, f"{name} is {size} bytes, over {ceiling}")


if __name__ == "__main__":
    unittest.main()
