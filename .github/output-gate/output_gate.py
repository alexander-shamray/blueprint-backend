#!/usr/bin/env python3
"""Fail the build when it has left output directories beside the source.

Section 4.1 states the invariant as a property of the tree: `src/` and
`tests/` hold source, and nothing a build wrote. This gate checks the half
that is true of any working tree, that no `bin/` or `obj/` exists under either
root. The other half, that the trees are otherwise untouched, needs a before
to compare against, so `ci.yml` asserts it beside this gate's step where
`actions/checkout` supplies one; run here it would fail on whatever a
developer has in flight.

`Directory.Build.props` makes the invariant true, and its `Output` comment
argues how. This gate keeps the outcome true and carries none of that
reasoning (`docs/change-locality.md` section 2).

The check has three parts.

It proves a restore and a build both ran before reporting that neither wrote
here, because a checkout nobody has touched has no `obj/` under `src/` either.
Every project must be found under `artifacts/obj/` and under `artifacts/bin/`:
a restore alone creates the first and not the second, so `obj` answers whether
the restore's output landed in `artifacts/` and `bin` whether anything
compiled.

The subject is checked rather than assumed. A walk that finds no project
satisfies every assertion above, so the projects found under `src/` and
`tests/` are reconciled with the ones `Platform.slnx` lists, in both
directions.

The two are reconciled by path, because `UseArtifactsOutput` pivots a
project's output on `MSBuildProjectName`, the `.csproj` stem. Two projects
sharing a stem share one `artifacts/obj/` entry, and keying on the stem would
let a listed project stand in for an unlisted one, so a duplicate stem is
refused before anything is looked up by name.

    python .github/output-gate/output_gate.py
"""
from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ElementTree
from collections import Counter
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

# The two trees Section 4.1 names. `tools/` is deliberately absent: the
# scaffold is stdlib Python that restores nothing, so a directory MSBuild never
# enters cannot fail this gate for the reason the gate exists.
SOURCE_ROOTS = ("src", "tests")

# What MSBuild writes beside a `.csproj` when the redirect is not in force.
# Matched without regard to case, which is `.gitignore`'s spelling of the same
# set — `[Bb]in/` and `[Oo]bj/` — rather than a second opinion about it.
OUTPUT_DIRECTORY_NAMES = frozenset({"bin", "obj"})

# The two `artifacts/` subdirectories a project must appear under, and what
# each one establishes. Ordered, because a project missing from both should be
# reported against the earlier stage rather than twice.
ARTIFACT_STAGES = (("obj", "restore"), ("bin", "build"))


def solution_projects(solution: Path) -> list[Path]:
    """Every project `Platform.slnx` lists, as repository-relative paths.

    Stdlib `ElementTree` rather than `defusedxml`, on the licence gate's
    argument one directory over and for the same reason: no dependency, and the
    input is not untrusted. This parses `Platform.slnx` from the checkout the
    gate is running inside, and a repository that could plant a hostile
    solution file could plant this script instead.
    """
    root = ElementTree.parse(solution).getroot()

    projects: list[Path] = []
    for element in root.iter("Project"):
        path = element.get("Path")
        if not path:
            continue
        # `.slnx` is written with forward slashes by the SDK and with
        # backslashes by anything that has been through a Windows tool. Both
        # spell the same project, and a gate that reads only one of them
        # reports a whole solution missing from disk.
        projects.append(Path(path.replace("\\", "/")))
    return sorted(set(projects))


def walked_projects(repo_root: Path) -> list[Path]:
    """Every `.csproj` on disk under the source roots, spelled the same way."""
    projects: list[Path] = []
    for source_root in SOURCE_ROOTS:
        for csproj in (repo_root / source_root).rglob("*.csproj"):
            projects.append(csproj.relative_to(repo_root))
    return sorted(projects)


def compare_subject(walked: list[Path], listed: list[Path]) -> list[str]:
    """The two views of what this gate is looking at, reconciled.

    Both directions are findings, and they are different defects. A project the
    solution lists and the walk cannot find means the walk is reading the wrong
    tree, and every assertion made from it is worth nothing. A project the walk
    finds and the solution does not list means the solution has stopped being
    an independent inventory, so the reconciliation itself no longer
    establishes anything.

    Neither says CI skipped a build: MSBuild builds the `ProjectReference`
    closure, so an unlisted project a listed one references still compiles.
    The first says the walk did not find the project rather than that it is
    not on disk, because a project listed outside `SOURCE_ROOTS` is on disk and
    absent from this walk.
    """
    if not walked and not listed:
        roots = " and ".join(f"{name}/" for name in SOURCE_ROOTS)
        return [
            f"no projects found at all - {roots} hold no .csproj and Platform.slnx "
            f"lists none. Every check below passes over an empty set, which is a "
            f"gate reporting on nothing rather than a tree with nothing wrong"]

    findings: list[str] = []
    roots = " or ".join(f"{name}/" for name in SOURCE_ROOTS)

    for path in sorted(set(listed) - set(walked)):
        findings.append(
            f"Platform.slnx lists {path.as_posix()}, which the walk over {roots} did "
            f"not find: the file is missing, or it sits outside the roots this gate "
            f"walks. Either way the solution describes a tree this walk is not "
            f"reading, so its other findings are about part of the repository")

    for path in sorted(set(walked) - set(listed)):
        findings.append(
            f"{path.as_posix()} is on disk and absent from Platform.slnx. The "
            f"solution has stopped being an independent inventory of what this walk "
            f"should find, so reconciling the two establishes nothing - and whether "
            f"a build reaches this project at all now rests on some listed project "
            f"referencing it")

    return findings


def find_duplicate_names(walked: list[Path]) -> list[str]:
    """Two projects cannot share a stem, because their output would collide.

    This is a defect in the repository before it is a problem for the gate:
    `UseArtifactsOutput` pivots on `MSBuildProjectName`, so a shared stem means
    a shared `artifacts/obj/` and `artifacts/bin/` entry. It is checked here
    rather than left to the SDK because everything below this line looks a
    project up by name, and a name that means two projects makes those lookups
    answer for whichever one it happens to find.

    Stems are compared with `casefold()`, because the collision is the
    filesystem's: two stems differing only in case are two directories on the
    Linux runner and one on Windows and a default macOS install, so a
    case-sensitive check would pass in CI on the pair that collides where the
    repository is developed.
    """
    counted = Counter(path.stem.casefold() for path in walked)

    findings: list[str] = []
    for folded in sorted(name for name, count in counted.items() if count > 1):
        colliding = [path for path in walked if path.stem.casefold() == folded]
        spellings = sorted({path.stem for path in colliding})
        named = " / ".join(spellings) if len(spellings) > 1 else spellings[0]
        sharing = ", ".join(path.as_posix() for path in colliding)
        findings.append(
            f"{named} is the MSBuild project name of more than one project: {sharing}. "
            f"They share one artifacts/obj/ and artifacts/bin/ entry on any filesystem "
            f"that ignores case, so neither this gate nor the SDK can tell their output "
            f"apart")
    return findings


def find_missing_output(artifacts: Path, walked: list[Path]) -> list[str]:
    """Every walked project appears under `artifacts/obj/` and `artifacts/bin/`.

    This is the assertion that makes a green result mean something. Without it
    the gate passes on a fresh checkout, on a failed build, and on a build of
    some other solution — three states in which `src/` holds no output for the
    uninteresting reason.

    The two stages are asked separately because a tree can be in between them.
    `dotnet restore` writes every `artifacts/obj/<Project>/` and not one
    `artifacts/bin/` entry, so a gate asking only about `obj` reports a fully
    built solution to anyone who has run restore and nothing else.
    """
    names = sorted(path.stem for path in walked)

    for directory, stage in ARTIFACT_STAGES:
        missing = [name for name in names if not (artifacts / directory / name).is_dir()]
        if not missing:
            continue

        if len(missing) == len(names):
            return [
                f"artifacts/{directory}/ has an entry for no project at all: no "
                f"{stage} has run here. Run `dotnet restore Platform.slnx` and "
                f"`dotnet build Platform.slnx` first - this gate reads what they "
                f"leave behind, and a tree nobody built holds output nowhere"]

        return [
            f"artifacts/{directory}/{name}/ does not exist, though the project does. "
            f"The {stage} skipped it, or its output went somewhere this gate is not "
            f"looking" for name in missing]

    return []


def find_residue(repo_root: Path) -> list[str]:
    """Any `bin/` or `obj/` under the source roots, outermost first.

    The walk prunes what it reports, so a stale `obj/` full of a previous
    layout's subdirectories is one finding rather than a screenful. The
    outermost directory is also the one to delete, which makes the finding and
    the fix the same sentence.
    """
    findings: list[str] = []
    for source_root in SOURCE_ROOTS:
        for directory, subdirectories, _files in os.walk(repo_root / source_root):
            residue = sorted(
                name for name in subdirectories if name.lower() in OUTPUT_DIRECTORY_NAMES)
            for name in residue:
                relative = (Path(directory) / name).relative_to(repo_root)
                findings.append(
                    f"{relative.as_posix()}/ is build output beside the source it was "
                    f"built from. Delete it: git-ignored output is never removed by a "
                    f"checkout, so a directory written before the redirect landed "
                    f"survives every branch")
            subdirectories[:] = [name for name in subdirectories if name not in residue]
    return findings


def audit(repo_root: Path) -> list[str]:
    """Every finding, in the order a reader can act on them.

    The subject comes first on purpose: a mismatch there changes what the other
    checks were even about, so reporting them alongside would be three findings
    where there is one. A duplicate name comes second for the narrower version
    of the same reason — every lookup after it is by name.
    """
    walked = walked_projects(repo_root)
    listed = solution_projects(repo_root / "Platform.slnx")

    findings = compare_subject(walked, listed)
    if findings:
        return findings

    findings = find_duplicate_names(walked)
    if findings:
        return findings

    return find_missing_output(repo_root / "artifacts", walked) + find_residue(repo_root)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=REPO_ROOT)
    args = parser.parse_args(argv)

    repo_root = args.repo.resolve()
    findings = audit(repo_root)

    if findings:
        print(f"Output gate: {len(findings)} finding(s).\n")
        for finding in findings:
            print(f"  {finding}")
        print("\nSection 4.1: src/ and tests/ hold source, and nothing a build wrote. "
              "Directory.Build.props owns where output goes; see its Output comment.")
        return 1

    projects = walked_projects(repo_root)
    roots = " and ".join(f"{name}/" for name in SOURCE_ROOTS)
    # Output stays ASCII. A gate whose job is to report a failure must not be the
    # thing that fails, and stdout encoding on a runner is not ours to assume.
    print(f"Output gate: {len(projects)} project(s) under {roots}. Every one restored "
          f"and built into artifacts/, and neither tree holds a bin/ or obj/ of its own.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
