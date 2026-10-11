#!/usr/bin/env python3
"""Patch .claude/commands/validate-blueprint.md for review round 1 of its dispatch change.

    patch_validate_blueprint.py ROOT R1|R2 [--check]  (--check prints the size and writes nothing)
"""

import sys
from pathlib import Path

EDITS = {
    # The Report block needs Resolved and, for check 9, Verdict, and the session reads no chapter to supply them.
    "R1": [
        (
            "returns: `claim`, `sites` as `file:line`, `conflict`, `owner`. It reads no\n"
            "chapter whole, opens only a `file:line` a finding cites, and drops one that\n"
            "does not hold. One dispatch",
            "returns: `claim`, `sites` as `file:line`, `conflict`, `owner`, `resolved`\n"
            "and, for check 9, `verdict`. It reads no chapter whole, opens only a\n"
            "`file:line` a finding cites, and drops one that does not hold. One dispatch",
        ),
    ],
    "R2": [
        (
            "- Edit anything, or spawn an implementer (report code findings), or touch `.remember/`.\n",
            "- Edit anything, or spawn an implementer (report code findings), or touch\n  `.remember/`.\n",
        ),
    ],
}


def main(argv: list[str]) -> int:
    root, finding = Path(argv[1]), argv[2]
    path = root / ".claude" / "commands" / "validate-blueprint.md"
    text = path.read_bytes().decode("utf-8")
    crlf = "\r\n" in text
    if crlf:
        text = text.replace("\r\n", "\n")
    for old, new in EDITS[finding]:
        if text.count(old) != 1:
            print(f"passage found {text.count(old)} times, not once: {old[:60]!r}", file=sys.stderr)
            return 1
        text = text.replace(old, new)
    print(f"{len(text.encode('utf-8'))} bytes")
    if "--check" not in argv:
        path.write_bytes((text.replace("\n", "\r\n") if crlf else text).encode("utf-8"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
