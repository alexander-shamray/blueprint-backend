#!/usr/bin/env python3
"""Fail the build on a package pin whose licence has not been cleared.

Appendix B is the register of what is cleared. `Directory.Packages.props` is
what CI will actually restore. Section 4.4 asks for a check that the two agree,
and this is it: a package in the props file and not the register is how a
licence obligation gets acquired by a restore rather than by a decision.

The props file is not the only thing that can add a restore, so every
`.csproj`, `.props` and `.targets` is read as well. A `PackageReference`
carrying its own `Version` or a `VersionOverride`, and a project setting
`ManagePackageVersionsCentrally` to anything but `true`, each resolve a package
no register row was ever asked about. All of them are ordinary MSBuild, and
none of them puts a `PackageVersion` element anywhere the pin reader looks.

It reads every file as text and resolves nothing over the network, which is why
Section 15.1 can put it ahead of the build fork. Nothing here needs a restore,
and the scan that catches a licence obligation is cheapest before anything has
compiled.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

DEFAULT_PINS = REPO_ROOT / "Directory.Packages.props"
DEFAULT_REGISTER = REPO_ROOT / "docs" / "backend-architecture" / "appendix-b-licences.md"
DEFAULT_CHAPTER = REPO_ROOT / "docs" / "backend-architecture" / "04-solution-structure.md"
DEFAULT_ALLOWED = GATE_DIR / "allowed-licences.txt"

# The register has three tables. Only the first clears anything — the other two
# record what was avoided and what still needs review, and a pin matching either
# of those has no business being in the props file to begin with.
CHOSEN_HEADING = "## Chosen — free for commercial use"

# Section 4.4 names three classes of register row that will never carry a pin.
# A check that does not know them reports false positives until somebody stops
# reading its output, so each is encoded here rather than guessed at.
#
# Infrastructure products need no entry. Keycloak, RabbitMQ, SQL Server and
# Redis are named as products and carry no package identity, so they never enter
# this check at all — which is precisely what Section 4.4 means by "match on
# package identity, never on the product a package is named after".

# Rows skipped whole. Both Aspire rows are deliberately unpinned (Section 4.4,
# Section 14.2): their licences are cleared ahead of a decision not yet taken.
UNPINNED_ROWS = frozenset({"Aspire.Hosting.*", "Aspire.*"})

# Identities cleared as the unchosen half of an either/or row. Clearing a
# licence for an alternative is not a commitment to restore it.
UNPINNED_ALTERNATIVES = frozenset({"AwesomeAssertions"})

# The register writes licences the way prose does; the allow-list writes SPDX.
# One spelling map, applied in one direction. An unmapped spelling falls through
# unchanged and is then reported as a spelling this gate cannot name, which is
# the safe direction: a licence it cannot name is a licence it must not clear.
SPDX = {
    "MIT": "MIT",
    "Apache 2.0": "Apache-2.0",
    "BSD-3": "BSD-3-Clause",
    "MPL 2.0": "MPL-2.0",
}

# What the map above can produce. A part that is neither a spelling the map
# knows nor an identifier it emits is one this gate has no name for, and naming
# a licence is the whole of what it does before deciding about it.
NAMEABLE = frozenset(SPDX.values())

# Element names that add a package to what CI restores. `GlobalPackageReference`
# is the one worth naming: it shares nothing but a file with `PackageVersion`
# and injects its package into every project in the repository.
PIN_ELEMENTS = frozenset({"PackageVersion", "GlobalPackageReference"})

# Directories the project scan does not descend into. `obj` is the load-bearing
# one — a restore writes generated MSBuild files there, so a scan that read them
# would be reading the restore it exists to check.
SKIPPED_DIRECTORIES = frozenset({"obj", "bin", ".git"})

# What a restore reads and this gate therefore has to. The props and targets
# files are here because a `PackageReference` carrying a `Version` is legal in
# any of them and reaches every project at once — a wider hole than the one a
# `.csproj` opens, behind a spelling nobody looks at.
#
# `Directory.Packages.props` is deliberately NOT excluded. Its own elements are
# `PackageVersion` and `GlobalPackageReference`, which this scan does not judge,
# so including it costs nothing and a stray `PackageReference` written there is
# caught rather than being the one file the check declines to read.
PROJECT_SUFFIXES = (".csproj", ".props", ".targets")


def local_name(element: ElementTree.Element) -> str:
    """An element's name with any XML namespace stripped.

    `root.iter("PackageVersion")` matches nothing the moment an `xmlns` is
    declared on `<Project>`, because ElementTree spells a namespaced tag
    `{uri}PackageVersion`. MSBuild accepts both spellings and restores the same
    packages either way, so a gate reading only one of them is a gate an
    attribute switches off silently.
    """
    tag = element.tag
    if not isinstance(tag, str):
        return ""
    return tag.rsplit("}", 1)[-1]


def parse_msbuild(path: Path) -> ElementTree.Element:
    """The root element of an MSBuild document, with a DTD refused outright.

    The DOCTYPE check is not ceremony. Both attacks that stdlib ElementTree is
    exposed to — entity expansion and quadratic blowup — need a DTD to declare
    the entity, and an MSBuild file has no legitimate use for one. Refusing the
    declaration outright is cheaper than taking a defusedxml dependency on a
    gate whose whole argument is that it runs anywhere with nothing installed.
    """
    text = path.read_text(encoding="utf-8")
    if "<!DOCTYPE" in text or "<!ENTITY" in text:
        raise ValueError(f"{path} declares a DTD, which an MSBuild file has no reason to")

    return ElementTree.fromstring(text)


def read_pins(path: Path) -> set[str]:
    """Every package identity pinned in Directory.Packages.props.

    Both element names that pin one, rather than the obvious one.
    `GlobalPackageReference` shares nothing but a file with `PackageVersion`,
    needs no row beside it, and reaches further than any single project: it
    injects its package into all of them.
    """
    root = parse_msbuild(path)
    pins = set()

    for element in root.iter():
        if local_name(element) not in PIN_ELEMENTS:
            continue

        # `Include` is how central management names a package, and it is the
        # only identity this gate reads. An element without one is refused
        # rather than skipped: `Update` sets a version on an item defined
        # elsewhere, so passing over it would restore a package no register row
        # was asked about.
        identity = element.attrib.get("Include")

        if identity is None:
            attributes = ", ".join(sorted(element.attrib)) or "none"
            raise ValueError(
                f"{path}: <{local_name(element)}> has no Include attribute "
                f"(it carries {attributes}). This gate reads Include and nothing "
                f"else, so it can say neither whether this element pins a package "
                f"nor which one — `Remove` pins nothing and a declaration inside "
                f"an ItemDefinitionGroup carries no attributes at all, and this "
                f"refusal cannot tell either of those from a pin it cannot read.")

        pins.add(identity)

    return pins


def find_projects(root: Path) -> list[Path]:
    """Every MSBuild file the scan below will read, in path order.

    Exposed so a test can take the scan itself as its subject. A glob that
    matched nothing would satisfy every negative case by finding no fault in a
    set it never read. Every suffix in `PROJECT_SUFFIXES` is walked, not
    `.csproj` alone, because a props or targets file reaches every project.
    """
    projects: list[Path] = []
    for suffix in PROJECT_SUFFIXES:
        for path in root.rglob(f"*{suffix}"):
            if SKIPPED_DIRECTORIES.intersection(path.relative_to(root).parts[:-1]):
                continue
            projects.append(path)
    return sorted(projects)


def scan_projects(root: Path) -> list[str]:
    """Every project-level spelling that restores what the pins do not name.

    All of them are ordinary central package management rather than anything
    exotic: a `PackageReference` carrying a `Version` attribute, a
    `VersionOverride`, or a `<Version>` child element resolves a version the
    props file never declared, and a project setting
    `ManagePackageVersionsCentrally` to anything but `true` takes itself out of
    that file's reach entirely.

    Parsed rather than grepped: a multi-line `PackageReference` with child
    elements reads to a line pattern as unrelated lines.

    An empty subject is a finding, not a clean result: a glob matching nothing
    reports exactly what a repository with no fault reports, and from inside the
    gate the two are indistinguishable.
    """
    projects = find_projects(root)
    if not projects:
        # Output stays ASCII, for the reason main() gives at the other end.
        return [f"{root} holds no MSBuild project file, so the project scan read "
                f"nothing, "
                f"which is not the same result as a scan that found nothing"]

    findings: list[str] = []
    for project in projects:
        name = project.relative_to(root).as_posix()
        for element in parse_msbuild(project).iter():
            tag = local_name(element)
            if tag == "PackageReference":
                identity = element.attrib.get("Include", element.attrib.get("Update", "?"))
                for attribute in ("Version", "VersionOverride"):
                    if attribute in element.attrib:
                        findings.append(
                            f"{name}: PackageReference {identity} carries a {attribute} "
                            f"attribute, so it restores a version no pin declares")
                if any(local_name(child) == "Version" for child in element):
                    findings.append(
                        f"{name}: PackageReference {identity} carries a Version child "
                        f"element, so it restores a version no pin declares")
            elif tag == "ManagePackageVersionsCentrally":
                value = (element.text or "").strip()
                if value.lower() != "true":
                    findings.append(
                        f"{name}: sets ManagePackageVersionsCentrally to '{value}', which "
                        f"puts the project outside Directory.Packages.props and outside "
                        f"this gate")

    return findings


def read_register(path: Path) -> list[tuple[list[str], str]]:
    """The Chosen table as (identities, licence cell) pairs, in file order."""
    rows: list[tuple[list[str], str]] = []
    in_section = False

    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("## "):
            in_section = line.strip() == CHOSEN_HEADING
            continue
        if not in_section or not line.startswith("|"):
            continue

        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) < 2 or cells[0] == "Package":
            continue
        if set(cells[0]) <= {"-", ":"}:
            continue

        rows.append((re.findall(r"`([^`]+)`", cells[0]), cells[1]))

    return rows


def read_versions(text: str) -> dict[str, str]:
    """Every Include/Version pair in a props document, by identity."""
    return dict(re.findall(r'Include="([^"]+)"\s+Version="([^"]+)"', text))


def read_chapter_sample(path: Path) -> str:
    """Section 4.4's fenced transcription of Directory.Packages.props.

    Identified by content rather than by position, so inserting a chapter or
    another XML block above it does not silently start comparing the wrong one.
    """
    for block in re.findall(r"```xml\r?\n(.*?)```", path.read_text(encoding="utf-8"), re.S):
        if "ManagePackageVersionsCentrally" in block:
            return block
    return ""


def compare_sample(props_text: str, sample: str) -> list[str]:
    """Disagreements between the props file and the chapter that prints it.

    Reported against the chapter. The props file is what CI restores, so it is
    the side the rest of the system depends on; the sample is what a reader
    believes, which is the half that can be wrong without anything breaking.
    """
    if not sample:
        return ["Section 4.4 has no central package management sample to compare against"]

    actual, printed = read_versions(props_text), read_versions(sample)
    findings = []
    for identity in sorted(set(actual) | set(printed)):
        if actual.get(identity) != printed.get(identity):
            findings.append(
                f"{identity}: props pins {actual.get(identity) or 'nothing'}, "
                f"Section 4.4 prints {printed.get(identity) or 'nothing'}")
    return findings


def read_allowed(path: Path) -> set[str]:
    """The allow-list, one SPDX identifier per line.

    The stripped line is computed once and both decisions are taken on it, so
    an indented comment cannot become an entry.
    """
    allowed: set[str] = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if stripped and not stripped.startswith("#"):
            allowed.add(stripped)
    return allowed


def spdx(licence_cell: str) -> list[str]:
    """Split a register licence cell into SPDX identifiers.

    Every part it returns has to be allowed, because this function is where the
    ambiguity is: a `/` in a register cell is a disjunction to a reader and this
    gate cannot tell one from a conjunction. A spelling the map cannot name
    falls through unchanged, so `audit` can report it as unnamed rather than as
    forbidden — two different failures with two different repairs.
    """
    parts = [part.strip() for part in licence_cell.split("/")]
    return [SPDX.get(part, part) for part in parts if part]


def audit(pins: set[str], rows: list[tuple[list[str], str]], allowed: set[str]) -> list[str]:
    """Every disagreement between the pins and the register, worst first.

    Every part of a licence cell has to be allowed. The gate cannot read a `/`
    as a choice, so clearing a row on one allowed half would let a forbidden
    licence clear itself beside an allowed one; where a package is offered under
    either, the register row names the half this repository takes.

    A part the map cannot name fails with its own message. It was never read,
    so the allow-list, keyed on identifiers the map emits, is the wrong repair:
    the cause is a misspelt register cell or a real name the closed map has not
    been taught, and those are repaired in different files.
    """
    registered: dict[str, list[str]] = {}
    for identities, licence_cell in rows:
        if UNPINNED_ROWS.intersection(identities):
            continue
        for identity in identities:
            registered[identity] = spdx(licence_cell)

    unregistered: list[str] = []
    forbidden: list[str] = []
    for pin in sorted(pins):
        licences = registered.get(pin)
        if licences is None:
            unregistered.append(
                f"{pin} is pinned and absent from Appendix B: its licence has never been cleared")
            continue
        unnamed = [licence for licence in licences if licence not in NAMEABLE]
        if unnamed:
            forbidden.append(
                f"{pin} is registered as {' / '.join(licences)}, and "
                f"{' / '.join(unnamed)} is not a licence spelling this gate knows. "
                f"Two faults reach this line and they are repaired in different "
                f"files: a misspelt register cell is fixed in Appendix B, and a "
                f"real spelling nobody has taught this gate is added to SPDX in "
                f"licence_gate.py. Naming a licence is not clearing it — a newly "
                f"nameable one still needs a line in allowed-licences.txt, which "
                f"is a decision rather than a transcription")
            continue
        refused = [licence for licence in licences if licence not in allowed]
        if refused:
            forbidden.append(
                f"{pin} is registered as {' / '.join(licences)}, which puts "
                f"{' / '.join(refused)} outside the allow-list")

    stale: list[str] = []
    for identity in sorted(registered):
        if identity in pins or identity in UNPINNED_ALTERNATIVES:
            continue
        stale.append(
            f"{identity} is registered in Appendix B and pinned nowhere: a dropped pin, "
            f"or a row that outlived its dependency")

    return unregistered + forbidden + stale


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pins", type=Path, default=DEFAULT_PINS)
    parser.add_argument("--register", type=Path, default=DEFAULT_REGISTER)
    parser.add_argument("--chapter", type=Path, default=DEFAULT_CHAPTER)
    parser.add_argument("--allowed", type=Path, default=DEFAULT_ALLOWED)
    parser.add_argument("--projects", type=Path, default=REPO_ROOT)
    args = parser.parse_args(argv)

    pins = read_pins(args.pins)
    rows = read_register(args.register)
    projects = find_projects(args.projects)
    findings = audit(pins, rows, read_allowed(args.allowed))
    findings += compare_sample(
        args.pins.read_text(encoding="utf-8"), read_chapter_sample(args.chapter))
    findings += scan_projects(args.projects)

    if findings:
        print(f"Licence gate: {len(findings)} finding(s) across {len(pins)} pinned package(s) "
              f"and {len(projects)} project(s).\n")
        for finding in findings:
            print(f"  {finding}")
        print(f"\nReconcile {args.pins.name}, {args.register.name} and {args.chapter.name}"
              f" in the same change. A project naming a version of its own is reconciled"
              f" the other way: move the pin into {args.pins.name} and register it.")
        return 1

    # Output stays ASCII. A gate whose job is to report a failure must not be the
    # thing that fails, and stdout encoding on a runner is not ours to assume.
    print(f"Licence gate: {len(pins)} pinned package(s). Every one registered, "
          f"licence-cleared, and printed correctly in {args.chapter.name}. "
          f"{len(projects)} MSBuild file(s) pin nothing of their own.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
