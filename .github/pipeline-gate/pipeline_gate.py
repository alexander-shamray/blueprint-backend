#!/usr/bin/env python3
"""The pipeline asserting things about itself, one subcommand per way §15.1's
staged pipeline can be quietly wrong. `filters`: every deployable under `src/`
has a path filter, because one nothing filters is one CI never rebuilds.
`images`: every Dockerfile is built by a matrix entry reading a defined
filter. `stages`: each stage's test count, not its exit code, because
`dotnet test` exits zero on an empty filter (§12.1), and every project in
`Platform.slnx` runs in exactly one stage. Stdlib only (licence gate's terms).

    py -3.12 .github/pipeline-gate/pipeline_gate.py filters
    py -3.12 .github/pipeline-gate/pipeline_gate.py stages TestResults/architecture TestResults/unit TestResults/integration
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"
SOLUTION = ROOT / "Platform.slnx"

# Floors, set well under each stage's real total on purpose: a floor at the
# total is a ratchet that goes red when somebody legitimately deletes a test
# class, and what this catches is an order-of-magnitude miss, a filter that
# selected nothing or a tenth of what it should. The structural checks in
# `check_stages` catch the small stuff and have no number to age.
STAGE_FLOORS = {
    "architecture": 15,
    "unit": 550,
    "integration": 150,
}


def fail(problems: list[str], subject: str) -> int:
    if not problems:
        return 0
    print(f"pipeline-gate: {len(problems)} problem(s) with {subject}:\n", file=sys.stderr)
    for problem in problems:
        print(f"  - {problem}", file=sys.stderr)
    return 1


# --------------------------------------------------------------------------
# filters
# --------------------------------------------------------------------------

def read_filters(workflow_text: str) -> list[str]:
    """Every path pattern inside the paths-filter step's `filters:` block.

    Text rather than YAML, for the reason `deploy/observability/check.py` gives
    one tree over: there is no stdlib YAML parser, and a gate that needs a
    `pip install` is a gate that gets skipped. The block is a YAML string
    inside a YAML document either way, so a parser would hand back the same
    text to be parsed a second time.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        match = re.match(r"^(\s*)filters:\s*\|", line)
        if match:
            break
    else:
        return []

    indent = len(match.group(1))
    patterns = []
    for line in lines[index + 1:]:
        if not line.strip():
            continue
        if len(line) - len(line.lstrip()) <= indent and not line.lstrip().startswith("#"):
            break
        found = re.match(r"\s*-\s*'([^']+)'", line)
        if found:
            patterns.append(found.group(1))
    return patterns


def covers(pattern: str, directory: str) -> bool:
    """Whether one filter pattern reaches anything under a directory.

    Prefix matching is §15.1's own model: `src/Services` is spoken for by its
    siblings' filters, which is why the section checks its children
    separately, and a rule demanding `src/Services/**` would fail the correct
    workflow. What this proves is that every deployable is reachable by some
    filter, not that any filter is complete — §15.1's "if changing a file can
    change what a service ships, that file belongs in that service's filter"
    is review's. A negation covers nothing, or an exclusion could satisfy the
    inventory.
    """
    if pattern.startswith("!"):
        return False
    return pattern == f"{directory}/**" or pattern.startswith(f"{directory}/")


def check_filters(root: Path = ROOT) -> list[str]:
    problems: list[str] = []

    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f".github/workflows/ci.yml is not readable: {error}"]

    patterns = read_filters(text)

    # An empty list passes every check below vacuously, so it is the parser
    # that is broken, not the workflow that is complete.
    if not patterns:
        return [
            "found no path patterns under `filters: |` in ci.yml. Every check "
            "here asks whether a directory appears in that list, so an empty "
            "list would pass all of them vacuously — this is the parser "
            "failing, not the inventory being complete"
        ]

    # §15.1: without `some-with-excludes` the default quantifier never
    # evaluates a negated pattern, and the `deploy` filter's exclusion of the
    # compose tree would be decoration.
    if "some-with-excludes" not in text:
        problems.append(
            "the paths-filter step does not set predicate-quantifier: "
            "'some-with-excludes'. The default quantifier never evaluates a "
            "negated pattern, so `!deploy/compose/**` would be silently "
            "ignored and a compose-only change would deploy (§15.1)"
        )

    for parent in ("src", "src/Services"):
        directory = root / parent
        if not directory.is_dir():
            problems.append(f"{parent} is not a directory; the inventory cannot be checked")
            continue

        children = sorted(child.name for child in directory.iterdir() if child.is_dir())
        if not children:
            problems.append(
                f"{parent} has no subdirectories, so its half of this check "
                "would pass vacuously"
            )
            continue

        for child in children:
            path = f"{parent}/{child}"
            if not any(covers(pattern, path) for pattern in patterns):
                problems.append(
                    f"{path} is matched by no filter in ci.yml. §15.1 requires "
                    "every immediate child of src/ and of src/Services/ to "
                    "appear in at least one, because a deployable nothing "
                    "filters is a deployable CI never rebuilds"
                )

    return problems


# --------------------------------------------------------------------------
# images
# --------------------------------------------------------------------------

def read_image_matrix(workflow_text: str) -> list[tuple[str, str]]:
    """The `images` job's matrix, as (filter, dockerfile) pairs.

    Positional rather than block-scoped: the matrix entries are the only place
    in this workflow where `filter:` and `dockerfile:` appear together, and
    pairing them by adjacency is what lets this stay a text match. A `filter:`
    with no `dockerfile:` after it is dropped, which the count check below
    turns into a failure rather than a silence.
    """
    pairs = []
    pending: str | None = None
    for line in workflow_text.splitlines():
        if found := re.match(r"\s*-?\s*filter:\s*(\S+)\s*$", line):
            pending = found.group(1)
        elif found := re.match(r"\s*dockerfile:\s*(\S+)\s*$", line):
            if pending is not None:
                pairs.append((pending, found.group(1)))
                pending = None
    return pairs


def check_images(root: Path = ROOT) -> list[str]:
    """Every image is built, and everything built is an image.

    Both directions, because a Dockerfile in no matrix is an image CI never
    builds and a matrix entry naming no Dockerfile fails on the day it is
    first selected. The third check is the one neither direction can make: a
    `filter` no filter defines makes `needs.changes.outputs[matrix.filter]`
    the empty string rather than an error, so the step is skipped and the job
    reports success having built nothing.
    """
    problems: list[str] = []

    try:
        text = WORKFLOW.read_text(encoding="utf-8")
    except OSError as error:
        return [f".github/workflows/ci.yml is not readable: {error}"]

    matrix = read_image_matrix(text)
    if not matrix:
        return [
            "found no (filter, dockerfile) pairs in the images matrix in "
            "ci.yml. Every check here compares against that list, so an empty "
            "one passes them all vacuously — the parser is what is broken"
        ]

    declared = {dockerfile for _filter, dockerfile in matrix}

    # Forward: every application Dockerfile is built by some entry.
    on_disk = {
        str(path.relative_to(root)).replace("\\", "/")
        for path in (root / "src").rglob("Dockerfile")
    }
    if not on_disk:
        problems.append(
            "found no Dockerfile under src/: the forward check would pass "
            "vacuously, so the search is what is broken"
        )
    for dockerfile in sorted(on_disk - declared):
        problems.append(
            f"{dockerfile} is built by no entry in ci.yml's images matrix. "
            "§15.1 builds every image a changed service ships, and one nothing names "
            "is one CI never builds — which surfaces on the next compose pull "
            "request rather than on the change that broke it"
        )

    # Reverse: every entry names a Dockerfile that is there.
    for dockerfile in sorted(declared - on_disk):
        problems.append(
            f"ci.yml's images matrix names {dockerfile}, which does not exist. "
            "The step is path-filtered, so this fails on the day it is next "
            "selected and not on the change that renamed it"
        )

    # And the filter each entry reads back has to be one that exists...
    by_filter = read_filters_by_name(text)
    if not by_filter:
        problems.append(
            "found no filter names in the paths-filter block: the two checks "
            "below would pass vacuously"
        )
    for name in sorted({filter_name for filter_name, _ in matrix} - set(by_filter)):
        problems.append(
            f"ci.yml's images matrix reads the filter {name!r}, which the "
            "paths-filter step does not define. A GitHub expression indexing a "
            "missing output is the empty string, so that entry's `if` is "
            "always false and the job reports success having built nothing"
        )

    # ...and the `changes` job has to export it, which defining it does not do:
    # `needs.changes.outputs.gateway` reads a job output, and deleting the one
    # `outputs:` line leaves the filter defined, the checks above green, and
    # the images job's `if` false on every gateway-only change.
    exported = read_job_outputs(text, "changes")
    if not exported:
        problems.append(
            "found no outputs on the changes job: the check below would pass "
            "vacuously"
        )
    for name in sorted({filter_name for filter_name, _ in matrix} - exported):
        if name in by_filter:
            problems.append(
                f"ci.yml's changes job defines the filter {name!r} and does not "
                "export it as a job output, so `needs.changes.outputs."
                f"{name}` is empty. The images job's condition is then false "
                "and every image under that filter is silently skipped"
            )

    # ...and it has to be the right one. An entry pairing the gateway's
    # Dockerfile with `filter: catalog` passes both directions of the inventory
    # and the name check, and rebuilds the wrong image.
    for filter_name, dockerfile in sorted(matrix):
        patterns = by_filter.get(filter_name)
        if patterns is None:
            continue
        if not any(_matches(pattern, dockerfile) for pattern in patterns):
            problems.append(
                f"ci.yml's images matrix builds {dockerfile} under the filter "
                f"{filter_name!r}, whose patterns do not match that path. The "
                "entry is well-formed and wired to the wrong service: a change "
                "to it rebuilds the wrong image, and the right one is never "
                "built at all"
            )

    return problems


def read_job_outputs(workflow_text: str, job: str) -> set[str]:
    """The names one job exports under `outputs:`.

    A filter and a job output are different things — the first is
    `dorny/paths-filter`'s, the second is the workflow's — and only the second
    is what `needs.<job>.outputs.<name>` reads. Text again, for the reason
    every parser in this file is: no dependency, and the block is unambiguous
    at its own indentation.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        if re.match(rf"^\s{{2}}{re.escape(job)}:\s*$", line):
            break
    else:
        return set()

    found: set[str] = set()
    inside = False
    for line in lines[index + 1:]:
        if re.match(r"^\s{2}\S", line):          # the next job
            break
        if re.match(r"^\s{4}outputs:\s*$", line):
            inside = True
            continue
        if inside:
            if name := re.match(r"^\s{6}([a-z][a-z0-9-]*):\s*\S", line):
                found.add(name.group(1))
            elif line.strip() and not line.startswith(" " * 6):
                inside = False
    return found


def read_filters_by_name(workflow_text: str) -> dict[str, list[str]]:
    """The paths-filter block as {filter: [its own patterns]}.

    `read_filters` flattens, which is all the `filters` check needs — it asks
    whether a directory is reachable by any filter. Pairing needs to know
    which, so this keeps them apart. A `*shared` alias line carries no quoted
    pattern and is skipped, and that costs nothing here: every Dockerfile lives
    under its own service's tree, never under `shared`.
    """
    lines = workflow_text.splitlines()
    for index, line in enumerate(lines):
        match = re.match(r"^(\s*)filters:\s*\|", line)
        if match:
            break
    else:
        return {}

    indent = len(match.group(1))
    found: dict[str, list[str]] = {}
    current: str | None = None
    for line in lines[index + 1:]:
        if not line.strip():
            continue
        if len(line) - len(line.lstrip()) <= indent and not line.lstrip().startswith("#"):
            break
        if header := re.match(r"^\s+([a-z][a-z0-9-]*):\s*(?:&\w+)?\s*$", line):
            current = header.group(1)
            found.setdefault(current, [])
        elif pattern := re.match(r"\s*-\s*'([^']+)'", line):
            if current:
                found[current].append(pattern.group(1))
    return found


def _matches(pattern: str, path: str) -> bool:
    """Whether one filter pattern selects a concrete file path."""
    if pattern.startswith("!"):
        return False
    if pattern.endswith("/**"):
        return path.startswith(pattern[:-2])
    return pattern == path


# --------------------------------------------------------------------------
# stages
# --------------------------------------------------------------------------

def read_stage(directory: Path) -> tuple[int, set[str], set[str]]:
    """One stage's total, its test assemblies and its test identities.

    `dotnet test` over a solution writes one TRX per test project, so this sums
    rather than reads a single number. A stage directory with no TRX in it is
    not zero tests — it is a logger that did not run, and the caller treats
    the two differently.
    """
    total = 0
    assemblies: set[str] = set()
    identities: set[str] = set()

    files = sorted(directory.rglob("*.trx"))
    if not files:
        raise SystemExit(
            f"no *.trx under {directory}. The stage did not run, or --logger trx "
            "and --results-directory disagree about where to write."
        )

    for path in files:
        try:
            root = ElementTree.parse(path).getroot()
        except ElementTree.ParseError as error:
            raise SystemExit(f"{path} is not readable as XML: {error}") from error

        # TRX carries a default namespace, so tags arrive as
        # `{...}Counters`. Matching on the local name keeps this working if the
        # schema URL is ever versioned.
        for element in root.iter():
            tag = element.tag.rsplit("}", 1)[-1]
            if tag == "Counters":
                total += int(element.get("total", "0"))
            elif tag == "TestMethod":
                # The assembly is part of the identity: two projects may hold a
                # class of the same name, and an identity without it would
                # report an overlap that is not there.
                code_base = element.get("codeBase", "")
                assembly = Path(code_base.replace("\\", "/")).stem if code_base else ""
                if assembly:
                    assemblies.add(assembly)
                identities.add(
                    f"{assembly}!{element.get('className', '')}.{element.get('name', '')}"
                )

    return total, assemblies, identities


def test_projects(solution: Path = SOLUTION) -> set[str]:
    """Every test project in the solution, by assembly name.

    `EndsWith("Tests")` and not `".Tests"`, exactly as `Directory.Build.props`
    scopes its CA1707 carve-out and for the same reason:
    `Platform.IntegrationTests` ends with the word and not the dotted suffix.
    A `.TestSupport` library is not a test project (§4.1) and is excluded by
    the same predicate without needing to be named.
    """
    text = solution.read_text(encoding="utf-8")
    return {
        name
        for name in re.findall(r'Path="[^"]*?([A-Za-z0-9.]+)\.csproj"', text)
        if name.endswith("Tests")
    }


def check_stages(directories: list[Path], solution: Path = SOLUTION) -> list[str]:
    problems: list[str] = []
    seen: dict[str, set[str]] = {}
    covered: set[str] = set()

    for directory in directories:
        stage = directory.name
        if stage not in STAGE_FLOORS:
            problems.append(
                f"{directory} is not one of the stages this gate knows "
                f"({', '.join(sorted(STAGE_FLOORS))}). A results directory nobody "
                "declared is a stage with no floor, which is the check not running"
            )
            continue

        total, assemblies, identities = read_stage(directory)
        seen[stage] = identities
        covered |= assemblies

        if total == 0:
            problems.append(
                f"the {stage} stage ran no tests. `dotnet test` exits zero on a "
                "filter that selects nothing (§12.1), so a green step says "
                "nothing about whether this stage exists"
            )
        elif total < STAGE_FLOORS[stage]:
            problems.append(
                f"the {stage} stage ran {total} tests, below its floor of "
                f"{STAGE_FLOORS[stage]}. Either the filter stopped selecting what "
                "it used to, or the floor in pipeline_gate.py is owed a "
                "deliberate edit"
            )

    if not seen:
        return problems + [
            "no stage was read, so every check here passed vacuously"
        ]

    # Every declared stage has to have been read, not merely every read stage
    # declared: drop the architecture invocation and its argument, let those
    # tests fall into the unit filter, and every check above passes while the
    # gate reports two stages ran.
    for stage in sorted(set(STAGE_FLOORS) - set(seen)):
        problems.append(
            f"the {stage} stage was never read: no results directory was passed "
            "for it. §15.1 runs three stages, and a gate that only checks the "
            "ones it was given cannot notice one going missing"
        )

    # Structural half 1: the stages partition the suite rather than sampling it.
    expected = test_projects(solution)
    if not expected:
        problems.append(
            "found no test projects in Platform.slnx: the check below would "
            "pass vacuously, so the parser is what is broken"
        )
    for project in sorted(expected - covered):
        problems.append(
            f"{project} ran in no stage. A test project the staged pipeline "
            "never selects is a suite that stopped running without anything "
            "going red"
        )

    # Structural half 2: and they do not overlap. ci.yml claims the filters are
    # disjoint by construction, and an overlap is the slow half of the suite
    # paid for twice.
    stages = sorted(seen)
    for index, first in enumerate(stages):
        for second in stages[index + 1:]:
            both = seen[first] & seen[second]
            if both:
                sample = ", ".join(identity.replace("!", " in ") for identity in sorted(both)[:3])
                problems.append(
                    f"{len(both)} test(s) ran in both the {first} and {second} "
                    f"stages, for example {sample}. The stage filters are "
                    "supposed to be disjoint, and an overlap is the suite paid "
                    "for twice"
                )

    return problems


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("filters", help="every deployable is matched by some path filter")
    sub.add_parser("images", help="every Dockerfile is built by some matrix entry")

    stages = sub.add_parser("stages", help="every stage ran, ran enough, and ran once")
    stages.add_argument("results", nargs="+", type=Path)

    args = parser.parse_args(argv[1:])

    if args.command == "filters":
        problems = check_filters()
        if code := fail(problems, "the path filters in ci.yml"):
            return code
        print("pipeline-gate: every deployable under src/ is matched by a filter.")
        return 0

    if args.command == "images":
        problems = check_images()
        if code := fail(problems, "the images matrix in ci.yml"):
            return code
        print("pipeline-gate: every Dockerfile under src/ is built by a matrix entry.")
        return 0

    problems = check_stages(args.results)
    if code := fail(problems, "the test stages"):
        return code
    print(f"pipeline-gate: {len(args.results)} stages ran, none empty, none overlapping.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
