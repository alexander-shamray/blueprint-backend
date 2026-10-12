#!/usr/bin/env python3
"""A chapter names a reference service only as evidence. The README beside it
owns what this refuses. Stdlib only, on the licence gate's terms.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BLUEPRINT = Path("docs/backend-architecture")
SERVICES = Path("src/Services")

# ADR-096 part 5's closed list, named here and nowhere else: the ADRs and Appendices A to E. A file
# that is not on it is scanned, so a new chapter, and an Appendix F, are covered by default.
EXEMPT = frozenset({
    "adr",
    "appendix-a-adrs.md",
    "appendix-b-licences.md",
    "appendix-c-delivery-plan.md",
    "appendix-d-type-inventory.md",
    "appendix-e-reference-domain.md",
})

# ADR-096 part 2's marker: a link into Appendix E, and the phrase, both in the one block.
LINK = re.compile(r"\]\(appendix-e-reference-domain\.md#")
PHRASE = re.compile(r"in\s+the\s+reference\s+domain", re.IGNORECASE)
QUOTE = re.compile(r"^\s*(?:>\s?)+")
FENCE = re.compile(r"^\s*(?:>\s*)*(```|~~~)")


def fail(problems: list[str]) -> int:
    if not problems:
        return 0
    print(f"domain-name-gate: {len(problems)} block(s) naming a reference service without the marker:\n",
          file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    return 1


def read_services(root: Path) -> list[str]:
    """The services by their directory spelling under src/Services, never typed here."""
    directory = root / SERVICES
    if not directory.is_dir():
        return []
    return sorted(entry.name for entry in directory.iterdir() if entry.is_dir())


def scanned(root: Path) -> list[Path]:
    """Every *.md under the blueprint tree that the closed list does not name."""
    base = root / BLUEPRINT
    return sorted(
        path for path in base.rglob("*.md")
        if path.relative_to(base).parts[0] not in EXEMPT
    )


def blocks(text: str) -> list[tuple[int, str]]:
    """Each block as its first line number and its text: a paragraph, a table row, or a heading.

    A blank line outside a fence ends a paragraph, a table row is a block of its own, and a fence
    belongs to the paragraph before it.
    """
    result: list[tuple[int, list[str]]] = []
    current: list[str] | None = None
    in_fence = False
    for number, line in enumerate(text.splitlines(), start=1):
        if FENCE.match(line):
            if not in_fence and current is None and result:
                current = result[-1][1]
            in_fence = not in_fence
            if current is None:
                current = []
                result.append((number, current))
            current.append(line)
            continue
        if in_fence:
            current.append(line)
            continue
        stripped = line.strip()
        if not stripped:
            current = None
            continue
        row = stripped.lstrip("> ").startswith("|")
        heading = stripped.startswith("#")
        if row or heading or current is None:
            current = []
            result.append((number, current))
        current.append(line)
        if row or heading:
            current = None
    return [(number, "\n".join(QUOTE.sub("", line) for line in lines)) for number, lines in result]


def marked(block: str) -> bool:
    return LINK.search(block) is not None and PHRASE.search(block) is not None


def check(root: Path = ROOT) -> list[str]:
    services = read_services(root)
    # An empty set names nothing and passes every block, so it is the reader that is broken.
    if not services:
        return [f"found no service directory under {SERVICES}: the reader or the tree is broken"]
    files = scanned(root)
    if not files:
        return [f"found no file to scan under {BLUEPRINT}: the reader or the tree is broken"]
    pattern = re.compile(r"\b(" + "|".join(re.escape(name) for name in services) + r")\b")

    problems = []
    for path in files:
        text = path.read_text(encoding="utf-8")
        for number, block in blocks(text):
            names = sorted(set(pattern.findall(block)))
            if names and not marked(block):
                problems.append(f"{path.relative_to(root).as_posix()}:{number} names {', '.join(names)}")
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("check", help="no scanned block names a reference service without the marker")
    sub.add_parser("files", help="print the scanned set, one path a line")
    args = parser.parse_args()
    if args.command == "files":
        print("\n".join(path.relative_to(ROOT).as_posix() for path in scanned(ROOT)))
        return 0
    code = fail(check())
    if code == 0:
        print("domain-name-gate: every block that names a reference service carries the marker.")
    return code


if __name__ == "__main__":
    sys.exit(main())
