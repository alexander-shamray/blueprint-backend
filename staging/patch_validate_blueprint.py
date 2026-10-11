#!/usr/bin/env python3
"""Patch .claude/commands/validate-blueprint.md: drop the Agent grant CI refused.

    patch_validate_blueprint.py ROOT [--check]  (--check prints the size and writes nothing)
"""

import sys
from pathlib import Path

EDITS = [("Bash(ls:*), Agent(general-purpose)\ndisallowed-tools:", "Bash(ls:*)\ndisallowed-tools:")]


def main(argv: list[str]) -> int:
    path = Path(argv[1]) / ".claude" / "commands" / "validate-blueprint.md"
    text = path.read_bytes().decode("utf-8")
    crlf = "\r\n" in text
    if crlf:
        text = text.replace("\r\n", "\n")
    for old, new in EDITS:
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
