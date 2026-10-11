#!/usr/bin/env python3
"""Fail the build on a plan written after CLAUDE.md's planning rule that is over its size.

The subject is the directory, not a list; the README owns the claim.
"""

from __future__ import annotations

import argparse
import re
import sys
from datetime import date
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

DEFAULT_PLANS = REPO_ROOT / "docs" / "superpowers" / "plans"

# The day CLAUDE.md's planning rule landed (commit ec2d8d33). Plans dated
# before it were written without the rule and are not judged.
RULE_LANDED = date(2026, 10, 7)

# CLAUDE.md says KB; this reads it as 1024 bytes.
LIMIT_BYTES = 50 * 1024

DATED = re.compile(r"^(\d{4})-(\d{2})-(\d{2})-")


def plan_date(name: str) -> date | None:
    match = DATED.match(name)
    if match is None:
        return None
    try:
        return date(*(int(part) for part in match.groups()))
    except ValueError:
        return None


def check(plans: Path, landed: date = RULE_LANDED, limit: int = LIMIT_BYTES) -> list[str]:
    if not plans.is_dir():
        return [f"{plans} is not a directory, so no plan was read"]

    files = sorted(p for p in plans.rglob("*") if p.is_file())
    if not files:
        return [f"{plans} holds no file, so no plan was read"]

    problems: list[str] = []
    for path in files:
        relative = path.relative_to(plans)
        written = plan_date(relative.parts[0])
        size = path.stat().st_size
        if written is None:
            problems.append(
                f"{relative.as_posix()}: no YYYY-MM-DD- prefix, so it cannot be placed before or after the rule")
        elif written >= landed and size > limit:
            problems.append(f"{relative.as_posix()}: {size} bytes, over the {limit} the planning rule allows")
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plans", type=Path, default=DEFAULT_PLANS)
    args = parser.parse_args(argv)

    problems = check(args.plans)
    for problem in problems:
        print(f"plan-size-gate: {problem}", file=sys.stderr)
    if problems:
        return 1
    print(f"plan-size-gate: every plan dated {RULE_LANDED} or later is within {LIMIT_BYTES} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
