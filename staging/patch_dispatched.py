"""Patch the --dispatched clause and its case into a checkout, one part at a time.

The session that wrote this is denied edits under .claude/, so apply.sh runs it: `test` adds the case, `code`
adds the rule. Each replacement is exact and must match once, so a moved anchor stops the run, not a wrong edit.
"""

import sys
from pathlib import Path

SHIP = ".claude/commands/ship.md"
TEST = ".claude/scripts/test_ship_dispatched.py"

CLAUSE_BEFORE = """\
  what it files goes only in the result's `filed`. (why:
  docs/commands/ship.md, *Dispatched: the one token /dispatch passes*)
"""
CLAUSE_AFTER = """\
  what it files goes only in the result's `filed`. It keeps its turn until
  the result is written: a step's status is not a reply, so it carries no
  status table and no apply line, and the next step follows in the same
  turn; and each shell read is one command its grants name, since nothing
  answers a prompt for one they do not. (why:
  docs/commands/ship.md, *Dispatched: the one token /dispatch passes*)
"""

CASE_ANCHOR = """\
        self.assertIn("what it files goes only in the result's filed", rule)

"""
CASE = """\
        self.assertIn("what it files goes only in the result's filed", rule)

    def test_a_step_status_is_not_a_reply(self):
        # A headless session exits when its turn ends, so a step report sent as a reply is the last thing it does.
        rule = between(flat(between(ship(), "## It runs to the end", "## Resume, don't restart")),
                       "Under the standalone token --dispatched <run-dir>", "(why:")
        self.assertIn("It keeps its turn until the result is written", rule)
        self.assertIn("a step's status is not a reply, so it carries no status table and no apply line", rule)
        self.assertIn("the next step follows in the same turn", rule)
        self.assertIn("each shell read is one command its grants name", rule)

"""

PARTS = {"test": (TEST, CASE_ANCHOR, CASE), "code": (SHIP, CLAUSE_BEFORE, CLAUSE_AFTER)}


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
