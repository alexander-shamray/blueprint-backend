"""Cut the teardown case's comment to a citation of the argument it restated; nothing the suite runs changes."""

import sys
from pathlib import Path

OLD = """        # /branch leaves another session's branch in the main checkout when main
        # was dirty, so the forked teardown asks before it pulls, and checks main.
"""

NEW = """        # Why the branch is read first is docs/commands/ship.md's, *Step 6: the teardown*.
"""


def main(root):
    path = Path(root) / ".claude" / "scripts" / "test_ship_landing_method.py"
    text = path.read_bytes().decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    lf = text.replace("\r\n", "\n")
    assert lf.count(OLD) == 1, "comment not found exactly once"
    path.write_bytes(lf.replace(OLD, NEW).replace("\n", newline).encode("utf-8"))


if __name__ == "__main__":
    main(*sys.argv[1:])
