"""A workflow's `paths:` filter, read once, and the two checks on it (ADR-132).

Text, one trigger's own entries only. smoke.sh calls `trigger-paths`,
`covered` and `undeclared` on stdin.
"""
from __future__ import annotations

import sys
from pathlib import Path

# Where this module lives, for the SOURCE_INPUTS of every gate that imports it.
HOME = "deploy/workflow_paths"

# The two triggers a path-filtered gate must run on: a merged change that
# skips the gate on `main` is the same defect one branch later.
TRIGGERS = ("pull_request", "push")


def trigger_paths(text: str, trigger: str) -> list[str] | None:
    """The `paths:` entries of one trigger, or None when the workflow lacks it.

    A trigger with no `paths:` key, or `paths-ignore:`, yields an empty list,
    and a sibling key ends the list.
    """
    lines = text.splitlines()

    for index, line in enumerate(lines):
        if line.rstrip() != f"  {trigger}:":
            continue

        paths: list[str] = []
        in_paths = False
        for following in lines[index + 1:]:
            stripped = following.strip()
            if not stripped or stripped.startswith("#"):
                continue
            indent = len(following) - len(following.lstrip())
            if indent <= 2:                      # the next trigger, or `jobs:`
                break
            if stripped.partition(" #")[0].rstrip() == "paths:":
                in_paths = True
                continue
            if in_paths and stripped.startswith("- "):
                paths.append(_entry(stripped[2:]))
                continue
            if in_paths:                         # a sibling key ends the list
                in_paths = False

        return paths

    return None


def _entry(value: str) -> str:
    """One list item less its trailing comment and its quotes, in any of the three styles."""
    value = value.strip()
    if value[:1] in "'\"":
        end = value.find(value[0], 1)
        return value[1:end] if end > 0 else value[1:]
    return value.split(" #", 1)[0].strip()


def covers(path: str, entry: str) -> bool:
    """Does one `paths:` glob cover the whole of an input a gate reads?

    Only `/**` covers a directory (`*` stops at a separator), and only from
    the entry or an ancestor: `src/Services/Ordering/**` does not cover `src`.
    """
    if path == "**":
        return True
    if path.endswith("/**"):
        prefix = path[: -len("/**")].rstrip("/")
        return bool(prefix) and (entry == prefix or entry.startswith(prefix + "/"))
    if "*" in path:
        return False
    return entry == path


def covered(entry: str, patterns: list[str]) -> bool:
    return any(covers(pattern, entry) for pattern in patterns)


def check_workflow_covers_inputs(
        workflow: Path, required: list[str], *, reader: str, label: str | None = None) -> list[str]:
    """Both triggers of `workflow` carry a filter covering every `required` entry.

    A missing trigger or empty filter fails: a parser that read nothing would
    agree with any list. `reader` names the gate in a message.
    """
    label = label or workflow.name
    try:
        text = workflow.read_text(encoding="utf-8")
    except OSError as error:
        return [f"{label} is not readable: {error}"]

    problems: list[str] = []
    for trigger in TRIGGERS:
        paths = trigger_paths(text, trigger)
        if paths is None:
            problems.append(f"{label} has no {trigger} trigger — the gate must run on both")
            continue
        if not paths:
            problems.append(f"{label}'s {trigger} trigger lists no paths — the parser, or the file")
            continue
        for entry in required:
            if not covered(entry, paths):
                problems.append(
                    f"{label}'s {trigger} trigger does not cover `{entry}`, which {reader} "
                    f"reads. A change to it would skip this gate")
    return problems


def check_reads_declared(
        reads: set[str], declared: list[str], *, reader: str, label: str) -> list[str]:
    """Every read is declared, whole or as a directory prefix, never a substring.

    The workflow check cannot see a read the list omits; an empty `reads` fails.
    """
    if not reads:
        return [f"{reader}: found no reads in its own source — the scan is broken, not the list"]

    return [
        f"{reader} reads `{read}` and SOURCE_INPUTS does not declare it, so {label}'s "
        f"triggers do not watch it: {declared}"
        for read in sorted(reads)
        if not any(read == entry or read.startswith(f"{entry}/") for entry in declared)]


def main(argv: list[str]) -> int:
    command, rest = (argv[0], argv[1:]) if argv else ("", [])

    if command == "trigger-paths" and len(rest) == 1:
        paths = trigger_paths(sys.stdin.read(), rest[0])
        if paths:
            print("\n".join(paths))
        return 0

    if command == "covered" and len(rest) == 1:
        return 0 if covered(rest[0], sys.stdin.read().splitlines()) else 1

    if command == "undeclared":
        reads = {line.strip() for line in sys.stdin if line.strip()}
        if not reads:
            print("found no reads on stdin: the scan is broken, not the list", file=sys.stderr)
            return 2
        missing = [read for read in sorted(reads)
                   if not any(read == entry or read.startswith(f"{entry}/") for entry in rest)]
        print("\n".join(missing))
        return 1 if missing else 0

    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
