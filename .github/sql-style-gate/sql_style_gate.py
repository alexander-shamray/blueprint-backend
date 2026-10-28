#!/usr/bin/env python3
"""Fail the build on a SQL join the style guide's join rule refuses, in the tracked tree.

The subject is the tracked files, not a list of suffixes; the README owns the claim and its exclusions.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

# The plans are never edited to match the code that followed (CLAUDE.md), and
# this suite holds the refused forms as its cases.
EXCLUDED_PREFIXES = ("docs/superpowers/",)
EXCLUDED_FILES = (".github/sql-style-gate/test_sql_style_gate.py",)

# A join is the keyword followed by a source: a name, a bracketed name, a
# variable, a temp table, a parenthesised rowset or a C# interpolation hole.
# What follows keeps prose and identifiers out: `JOIN =` and "a JOIN —" are
# not joins.
JOIN = re.compile(r"(?<![\w.])JOIN\s+(?=[A-Za-z_\[@#({])")
KIND_BEFORE = re.compile(r"(\w+)\s+$")
NAMED_KINDS = {"INNER", "LEFT", "RIGHT", "FULL", "CROSS"}
TAKES_ON = {"INNER", "LEFT", "RIGHT", "FULL"}
ON_WORD = re.compile(r"(?<![\w.])ON(?![\w.])")
ON_LINE = re.compile(r"^\s*ON\s")
CONNECTOR = re.compile(r"(?<![\w.])(AND|OR)(?![\w.])")
FENCE = re.compile(r"^\s*(```|~~~)")


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    message: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: {self.message}"


def sql_lines(path: str, text: str) -> list[tuple[int, str]]:
    """Every line of a file, or of a Markdown file only the lines inside its fences."""
    lines = text.splitlines()
    if not path.endswith(".md"):
        return list(enumerate(lines, start=1))
    inside = False
    kept: list[tuple[int, str]] = []
    for number, line in enumerate(lines, start=1):
        if FENCE.match(line):
            inside = not inside
        elif inside:
            kept.append((number, line))
    return kept


def connector_at_depth_zero(rest: str) -> str | None:
    """The first AND or OR outside every parenthesis and quoted string of an ON line's predicate."""
    depth = 0
    quoted = False
    for index, char in enumerate(rest):
        if char == "'":
            quoted = not quoted
        elif quoted:
            continue
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
        elif depth == 0 and (found := CONNECTOR.match(rest, index)):
            return found.group(1)
    return None


def judge(path: str, text: str) -> tuple[list[Finding], int]:
    """The findings in one file, and the number of joins it held."""
    findings: list[Finding] = []
    joins = 0
    # An ON line is the join rule's only when a join opened it; a MERGE's ON
    # is the clause-keyword paragraph's, which the README leaves to review.
    awaiting_on = False
    for number, line in sql_lines(path, text):
        for match in JOIN.finditer(line):
            joins += 1
            before = KIND_BEFORE.search(line[: match.start()])
            kind = before.group(1) if before else ""
            if kind == "OUTER":
                outer = KIND_BEFORE.search(line[: before.start()])
                named = outer.group(1) if outer else "LEFT"
                findings.append(Finding(path, number, f"{named} OUTER JOIN: write {named} JOIN, with OUTER left out"))
            elif kind not in NAMED_KINDS:
                findings.append(Finding(path, number, "a bare JOIN: name its kind, INNER JOIN or LEFT JOIN"))
            elif kind in TAKES_ON and ON_WORD.search(line, match.end()):
                findings.append(Finding(path, number, f"{kind} JOIN: put its ON on its own line"))
            elif kind in TAKES_ON:
                awaiting_on = True
        if awaiting_on and ON_LINE.match(line):
            awaiting_on = False
            connector = connector_at_depth_zero(line.split("ON", 1)[1])
            if connector:
                findings.append(Finding(
                    path, number, f"an ON line packs a second predicate: give the {connector} its own line"))
    return findings, joins


def excluded(path: str) -> bool:
    return path.startswith(EXCLUDED_PREFIXES) or path in EXCLUDED_FILES


def tracked(root: Path) -> list[str]:
    # Bytes, not text=True: a cp1252 default drops what it cannot decode.
    listing = subprocess.run(["git", "ls-files", "-z"], cwd=root, capture_output=True, check=True).stdout
    return sorted(name.decode("utf-8") for name in listing.split(b"\0") if name)


def read(root: Path, path: str) -> str | None:
    """A file's text, or None for one that is binary or gone from the working tree.

    Any other read error propagates, so a listed file that cannot be opened fails the run.
    """
    try:
        data = (root / path).read_bytes()
    except FileNotFoundError:
        return None
    if b"\0" in data:
        return None
    return data.decode("utf-8", errors="replace")


@dataclass
class Scan:
    findings: list[Finding]
    read: list[str]
    joins: int


def scan(root: Path, paths: list[str]) -> Scan:
    result = Scan([], [], 0)
    for path in paths:
        if excluded(path):
            continue
        text = read(root, path)
        if text is None:
            continue
        result.read.append(path)
        findings, joins = judge(path, text)
        result.findings.extend(findings)
        result.joins += joins
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=REPO_ROOT)
    args = parser.parse_args(argv)

    result = scan(args.root, tracked(args.root))
    if not result.read:
        print("sql-style-gate: no tracked file was read, so no join was judged", file=sys.stderr)
        return 1
    for finding in result.findings:
        print(f"sql-style-gate: {finding}", file=sys.stderr)
    if result.findings:
        return 1
    print(f"sql-style-gate: {result.joins} joins in {len(result.read)} files, each naming its kind,"
          " none carrying its ON on the join's line")
    return 0


if __name__ == "__main__":
    sys.exit(main())
