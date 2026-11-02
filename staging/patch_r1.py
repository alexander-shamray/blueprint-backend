"""Patch review round 1's two .claude/ fixes into a checkout, one finding at a time.

The session that wrote this is denied edits under .claude/, so apply.sh runs it. Each replacement is exact and
must match once, so a moved anchor stops the run, not a wrong edit.
"""

import sys
from pathlib import Path

PARTS = {
    "classifier": (
        ".claude/commands/ship.md",
        """\
  turn; and each shell read is one command its grants name, since nothing
  answers a prompt for one they do not. (why:
""",
        """\
  turn; and each shell read is one command its grants name, since one they
  do not is the auto classifier's to refuse, and a refusal hands the change
  back. (why:
""",
    ),
    "docstring": (
        ".claude/scripts/test_ship_dispatched.py",
        '"""The --dispatched token changes only what the dispatch spec\'s section 6.2 lists.\n',
        '"""The --dispatched token changes only section 6.2\'s list and the consequences docs/commands/ship.md spells out.\n',
    ),
}


def patch(root, part):
    name, before, after = PARTS[part]
    path = Path(root) / name
    raw = path.read_bytes().decode("utf-8")
    # The checkout may hold the file with CRLF; match on LF and write back what was there.
    eol = "\r\n" if "\r\n" in raw else "\n"
    text = raw.replace("\r\n", "\n")
    if text.count(before) != 1:
        sys.exit(f"{name}: the anchor for {part} matches {text.count(before)} times, not once")
    path.write_bytes(text.replace(before, after).replace("\n", eol).encode("utf-8"))


if __name__ == "__main__":
    patch(sys.argv[1], sys.argv[2])
