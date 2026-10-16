"""The one reader of a workflow's `paths:` filter, and the two checks built on it.

Every gate under `deploy/` proves two things about its own workflow: that each
trigger's filter covers every path the gate reads, so a change to an input
cannot skip the gate, and that no path the gate reads is missing from the list
it declares. This module is where that is held, once; a gate keeps its own
SOURCE_INPUTS and its own scan of what it reads, and asks here whether the
workflow and the list agree.

The reader is text rather than YAML, because the standard library has none, and
it reads one trigger's own `paths:` entries and nothing else. Reading a
trigger's whole remaining text instead lets a gate certify a filter that skips
it: `push` is the last trigger, so its "body" ran to the end of the file and
included the step that runs the gate, and deleting the entry left that
substring behind. **A gate that reads more than the thing it is judging can be
satisfied by something that is not the thing.**

`smoke.sh` reaches the same functions through the command line below, so the
shell and the Python gates cannot read a form differently. Each takes its
input on stdin, because a path the shell hands a native interpreter is not
always one it can open:

    workflow_paths.py trigger-paths <trigger>       (a workflow)
    workflow_paths.py covered <entry>               (filter entries, one a line)
    workflow_paths.py undeclared <declared>...      (reads, one a line)
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
    """The `paths:` entries of one trigger, or None when the workflow has none.

    A trigger whose `paths:` key is absent, or spelled `paths-ignore:`, yields
    an empty list: it is a trigger that filters nothing in, which is not the
    same as not having the trigger. A sibling key ends the list, so
    `branches:` after `paths:` is not read as entries.
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
    """Does one `paths:` glob cover the WHOLE of an input a gate reads?

    Only `/**` covers a directory — GitHub's `*` does not cross a separator, so
    `src/*` matches the files directly under `src` and none of the C# beneath
    them, and a filter narrowed that way would skip every source change while
    this check called it covered.

    The direction matters: a glob covers an entry when the glob's literal
    prefix is the entry or an ancestor of it, never the other way round. An
    earlier copy had it backwards and read `src/Services/Ordering/**` as
    covering `src`, approving a filter that skips every other service.
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
    """Both triggers of `workflow` carry a filter that covers every entry of `required`.

    Both, and each named: anchoring a `paths:` list to its event is what lets a
    failure name the event instead of a position. An empty filter and a missing
    trigger are failures rather than passes, because a parser that read nothing
    would otherwise agree with any list at all. `reader` names the gate, for
    the message; `label` is how the workflow is spelled in it.
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
    """Every path in `reads` is `declared`, whole or as a directory prefix, never as a substring.

    The other direction from the workflow check, and the one that stays green
    when the list is short rather than wrong: a workflow can only be asked
    about entries the list already contains, so a read nobody declared is
    invisible from that side. An empty `reads` is a failure, because a scan
    that found nothing would pass this loop against any list at all.
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
