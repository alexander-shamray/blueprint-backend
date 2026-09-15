#!/usr/bin/env python3
"""Print the domain layer's coverage from every Cobertura report of a run.

Section 12.9 calls coverage a diagnostic rather than a target, so this reports
and never gates: no threshold, and no non-zero exit on a low figure. The
quality gate is the stage-count floor in `.github/pipeline-gate/`, whose
subject is whether a suite ran at all.

It does exit non-zero on a missing or unreadable report, which is a different
claim: a coverage step that shrugs at no data prints nothing on the day the
collector stops running, and nothing reads like a clean result.

It merges every stage. Section 15.1's pipeline runs the unit and integration
stages separately (docs/testing.md), the domain assemblies are exercised on
both sides of `Category=Integration`, and section 12.9 asks for the figure over
the whole run, so a per-stage figure under-reports lines only a container test
reaches.

Two properties of the artefacts decide how the merge is written:

* `lines-valid` and `lines-covered` count the lines under
  `class/methods/method/lines`, not the ones under `class/lines`, so the merge
  keys on the first.
* `--logger trx` makes each test project write its own partial attachment
  beside the run's merged one, so the same line arrives more than once.

Hits are therefore merged with `max` over an injective key, and reading an
attachment twice cannot inflate the figure.

Stdlib only, for the preference for adding no dependency; this runs after the
build, so the licence gate's reason does not apply. That rules out
`defusedxml`, and the stdlib parser is acceptable because of the input: these
are artefacts `Microsoft.CodeCoverage` wrote on the runner, under paths this
step names, and a repository that could plant one could plant the script.

    python .github/coverage/domain_coverage.py TestResults/unit TestResults/integration
"""
import os
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

# One method line, identified the way the collector identifies it.
#
# The signature is in the key because overloads share a name, and the class is
# because a partial class's members are spread across files. With all five
# parts the key is injective, one key per line the collector's `lines-valid`
# counts.
LineKey = tuple[str, str, str, str, str]


def find_reports(roots: list[Path]) -> list[Path]:
    """Every Cobertura file under the given directories.

    A stage that produced none is a stage whose collector did not run, and it
    is named individually: "no coverage anywhere" and "the integration stage
    collected nothing" are different defects, and the second is invisible in a
    total.
    """
    reports: list[Path] = []
    for root in roots:
        found = sorted(root.rglob("*.cobertura.xml"))
        if not found:
            raise SystemExit(
                f"no *.cobertura.xml under {root}. The collector did not run for "
                "that stage, or --results-directory pointed somewhere else."
            )
        reports += found
    return reports


def merge(reports: list[Path]) -> dict[LineKey, int]:
    """Union the reports, keeping the highest hit count seen for each line.

    `max` rather than `+` is the de-duplication. The layout puts the same line
    in more than one file by construction — the run's merged attachment and
    the per-project one that fed it — so summing would count a line twice for
    having been reported twice, and the figure would grow with the number of
    test projects rather than with the tests.
    """
    hits: dict[LineKey, int] = {}
    for report in reports:
        try:
            root = ElementTree.parse(report).getroot()
        except ElementTree.ParseError as error:
            raise SystemExit(f"{report} is not readable as XML: {error}") from error

        for package in root.iter("package"):
            package_name = package.get("name", "")
            for klass in package.iter("class"):
                class_name = klass.get("name", "")
                for method in klass.iter("method"):
                    for line in method.iter("line"):
                        key: LineKey = (
                            package_name,
                            class_name,
                            method.get("name", ""),
                            method.get("signature", ""),
                            line.get("number", ""),
                        )
                        hits[key] = max(hits.get(key, 0), int(line.get("hits", "0")))
    return hits


def render(hits: dict[LineKey, int], reports: list[Path]) -> str:
    """The summary a human reads, in GitHub's markdown."""
    if not hits:
        raise SystemExit(
            f"the {len(reports)} report(s) cover no lines. The ModulePaths filter "
            "in coverage.runsettings matched no assembly, which is what a renamed "
            "or removed Domain project looks like from here."
        )

    valid = len(hits)
    covered = sum(1 for count in hits.values() if count > 0)

    per_package: dict[str, list[int]] = {}
    for (package, *_rest), count in hits.items():
        totals = per_package.setdefault(package, [0, 0])
        totals[1] += 1
        if count > 0:
            totals[0] += 1

    lines = [
        "## Domain-layer coverage",
        "",
        # ASCII in the printed body on purpose. The step summary is written as
        # UTF-8, but stdout takes the console's code page, and a developer
        # running this in a cp1252 terminal should not meet a
        # UnicodeEncodeError from a reporting step.
        f"**{covered / valid:.1%}** of {valid} lines, over every `*.Domain` assembly "
        f"in the run - the union of {len(reports)} report(s) across every stage.",
        "",
        "| Assembly | Line rate |",
        "|---|---|",
    ]
    for package in sorted(per_package):
        package_covered, package_valid = per_package[package]
        lines.append(f"| `{package}` | {package_covered / package_valid:.1%} |")

    lines += [
        "",
        "Reported, not gated (Section 12.9). The filter is `.*\\.Domain\\.dll$` in "
        "`coverage.runsettings`; see `docs/testing.md`.",
    ]
    return "\n".join(lines)


def main(argv: list[str]) -> int:
    roots = [Path(argument) for argument in argv[1:]] or [Path("TestResults")]
    reports = find_reports(roots)
    summary = render(merge(reports), reports)
    print(summary)

    if step_summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(step_summary, "a", encoding="utf-8") as handle:
            handle.write(summary + "\n")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
