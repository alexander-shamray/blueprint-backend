#!/usr/bin/env python3
"""§3.2's publisher and subscriber table, held to the code that publishes and
consumes. The README beside it owns what this refuses. Stdlib only, on the
licence gate's terms.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CHAPTER = Path("docs/backend-architecture/03-bounded-contexts.md")
OWED = Path(__file__).resolve().parent / "owed.txt"

COLUMNS = ("publishes", "consumes", "accepts")
HEADER = "| Service | Owns | Publishes (events) | Consumes (events) | Accepts (commands) |"

# What the code says, each read where the registration lives (§9.3, §9.4, §9.6).
TO_CONTRACT = re.compile(r"private static (\w+) ToContract\(")
BOUND_EVENT = re.compile(r"ConfigureConsumer<IntegrationEventConsumer<(\w+)>>")
SAGA_EVENT = re.compile(r"public Event<(\w+)> \w+ \{")
BOUND_COMMAND = re.compile(r"ConfigureConsumer<CommandConsumer<(\w+),")
NAMED = re.compile(r"`(\w+)`")
ROW_NAME = re.compile(r"^\*\*([\w.]+)\*\*")
OWED_LINE = re.compile(r"^([\w.]+) (publishes|consumes|accepts) (\w+) (#\d+)$")


@dataclass
class Host:
    publishes: set[str] = field(default_factory=set)
    consumes: set[str] = field(default_factory=set)
    accepts: set[str] = field(default_factory=set)

    def column(self, name: str) -> set[str]:
        return getattr(self, name)


def fail(problems: list[str]) -> int:
    if not problems:
        return 0
    print(f"messaging-gate: {len(problems)} problem(s) with §3.2's table:\n", file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    return 1


def host_directories(root: Path) -> dict[str, Path]:
    """Every service under src/Services, and the BFF, by the name §3.2's row gives each.

    A service is a directory of its projects; the BFF's projects sit directly under src/BFF, so the
    host is that whole tree, named by the one project whose name prefixes the others (§4.1).
    """
    found = {d.name: d for d in sorted((root / "src/Services").glob("*")) if d.is_dir()}
    bff = sorted(d.name for d in (root / "src/BFF").glob("*") if d.is_dir())
    if bff:
        name = min(bff, key=len)
        if all(other.startswith(name) for other in bff):
            found[name] = root / "src/BFF"
        else:
            found.update({other: root / "src/BFF" / other for other in bff})
    return found


def read_host(directory: Path) -> Host:
    host = Host()
    for path in sorted(directory.rglob("*.cs")):
        if {"obj", "bin", "Migrations"} & set(path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        if path.name.endswith("IntegrationEventMapper.cs"):
            host.publishes |= set(TO_CONTRACT.findall(text))
        host.consumes |= set(BOUND_EVENT.findall(text))
        if "MassTransitStateMachine<" in text:
            host.consumes |= set(SAGA_EVENT.findall(text))
        host.accepts |= set(BOUND_COMMAND.findall(text))
    return host


def read_code(root: Path) -> dict[str, Host]:
    return {name: read_host(directory) for name, directory in host_directories(root).items()}


def read_table(chapter_text: str) -> dict[str, Host]:
    """§3.2's rows by service, each cell as the set of names it puts in backticks."""
    lines = chapter_text.splitlines()
    try:
        start = lines.index("## 3.2 Service responsibilities")
        header = lines.index(HEADER, start)
    except ValueError:
        return {}

    table: dict[str, Host] = {}
    for line in lines[header + 2:]:
        if not line.startswith("|"):
            break
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        name = ROW_NAME.match(cells[0])
        if name is None or len(cells) != 5:
            continue
        # A Consumes cell glosses its own events in parentheses, which carry no other name in backticks.
        table[name.group(1)] = Host(*(set(NAMED.findall(cell)) for cell in cells[2:]))
    return table


def read_owed(text: str) -> tuple[set[tuple[str, str, str]], list[str]]:
    owed, problems = set(), []
    for number, line in enumerate(text.splitlines(), start=1):
        if not line.strip() or line.startswith("#"):
            continue
        found = OWED_LINE.match(line)
        if found is None:
            problems.append(f"owed.txt:{number} is not `<host> <column> <name> #<issue>`: {line!r}")
            continue
        owed.add(found.groups()[:3])
    return owed, problems


def check(root: Path = ROOT, owed_path: Path = OWED) -> list[str]:
    try:
        table = read_table((root / CHAPTER).read_text(encoding="utf-8"))
    except OSError as error:
        return [f"{CHAPTER} is not readable: {error}"]
    code = read_code(root)

    # An empty side compares equal to nothing and passes every check below, so it is the reader that is broken.
    if not table:
        return [f"found no rows under §3.2's `{HEADER}` in {CHAPTER}: the parser or the chapter is broken"]
    if not code:
        return ["found no host under src/Services or src/BFF: the parser or the tree is broken"]

    owed, problems = read_owed(owed_path.read_text(encoding="utf-8") if owed_path.exists() else "")

    for name in sorted(set(code) - set(table)):
        problems.append(f"{name} is a host under src/ and has no row in §3.2's table")
    for name in sorted(set(table) - set(code)):
        problems.append(f"§3.2's {name} row names no host under src/Services or src/BFF")

    used: set[tuple[str, str, str]] = set()
    for name in sorted(set(table) & set(code)):
        for column in COLUMNS:
            stated, built = table[name].column(column), code[name].column(column)
            for missing in sorted(stated - built):
                if (name, column, missing) in owed:
                    used.add((name, column, missing))
                    continue
                problems.append(f"§3.2's {name} row {column} {missing} and the code does not")
            for extra in sorted(built - stated):
                problems.append(f"{name}'s code {column} {extra} and §3.2's {name} row does not say so")

    for stale in sorted(owed - used):
        problems.append(
            f"owed.txt says {stale[0]} {stale[1]} {stale[2]} is unbuilt, and it is no longer a difference: "
            "delete the line")

    # §3.2's closure paragraph, both directions, over the table itself.
    publishers: dict[str, list[str]] = {}
    for name, row in table.items():
        for event in row.publishes:
            publishers.setdefault(event, []).append(name)
    consumed = {event for row in table.values() for event in row.consumes}
    for event in sorted(consumed):
        if len(publishers.get(event, [])) != 1:
            problems.append(f"§3.2 has {event} consumed and published by {sorted(publishers.get(event, []))}, "
                            "not exactly one row")
    for event in sorted(set(publishers) - consumed):
        problems.append(f"§3.2 has {event} published by {publishers[event]} and consumed by no row")
    return problems


def report(root: Path = ROOT) -> dict[str, dict[str, list[str]]]:
    """The table the code implies, the machine-readable form a sibling's broker map compares with."""
    return {name: {column: sorted(host.column(column)) for column in COLUMNS}
            for name, host in read_code(root).items()}


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("check", help="§3.2's table matches the code and closes in both directions")
    sub.add_parser("report", help="print the table the code implies, as JSON")
    args = parser.parse_args(argv[1:])

    if args.command == "report":
        print(json.dumps(report(), indent=2))
        return 0
    if code := fail(check()):
        return code
    print("messaging-gate: §3.2's table matches what every host publishes, consumes and accepts.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
