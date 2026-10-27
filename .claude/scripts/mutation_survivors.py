"""The domain projects /mutation-sweep mutates, and the mutants their suites let through.

`plan ROOT` prints each test project directory; `report REPORTS ROOT` prints each undetected mutant Stryker
recorded. Exit 3 is a run that cannot be read as a result; a survivor is a finding and exits 0.
"""

import json
import subprocess
import sys
from pathlib import PurePosixPath

DETECTED = {"Killed", "Timeout"}
UNDETECTED = {"Survived", "NoCoverage"}


class Refusal(Exception):
    pass


def plan(domain_projects, test_projects):
    """Each `*.Domain` project's name, directory and test project directory, refusing one with no tests."""
    if not domain_projects:
        raise Refusal("no src/**/*.Domain.csproj is tracked, so there is nothing to mutate")
    tests = set(test_projects)
    planned = []
    for path in sorted(domain_projects):
        project = PurePosixPath(path)
        expected = PurePosixPath("tests", f"{project.stem}.Tests", f"{project.stem}.Tests.csproj")
        if str(expected) not in tests:
            raise Refusal(f"{path} has no {expected}, so its mutants would go unrun")
        planned.append((project.stem, str(project.parent), str(expected.parent)))
    return planned


def plan_of(root):
    return plan(tracked(root, "src/**/*.Domain.csproj"), tracked(root, "tests/*/*.csproj"))


def tracked(root, pattern):
    out = subprocess.run(["git", "-C", root, "ls-files", "--", pattern],
                         capture_output=True, text=True, check=False)
    if out.returncode != 0:
        raise Refusal(f"git ls-files failed under {root}: {out.stderr.strip()}")
    return [line for line in out.stdout.splitlines() if line]


def summarise(name, report, directory):
    """One project's counts and its undetected mutants, refusing a report no test ran against."""
    counts = {}
    rows = []
    project_root = report.get("projectRoot", "")
    for path, entry in sorted(report.get("files", {}).items()):
        source = entry.get("source", "").splitlines()
        for mutant in entry.get("mutants", []):
            status = mutant["status"]
            counts[status] = counts.get(status, 0) + 1
            if status in UNDETECTED:
                line = mutant["location"]["start"]["line"]
                text = source[line - 1].strip() if 0 < line <= len(source) else ""
                rows.append((relative(path, project_root, directory), line, status, mutant["mutatorName"],
                             mutant.get("replacement", ""), text))
    detected = sum(counts.get(s, 0) for s in DETECTED)
    undetected = sum(counts.get(s, 0) for s in UNDETECTED)
    if detected + undetected == 0:
        raise Refusal(f"{name}: no mutant was tested, so the run proves nothing about its suite")
    # A runner that never reaches the mutated assembly lets every mutant through.
    if detected == 0:
        raise Refusal(f"{name}: {undetected} mutants tested and none detected; the runner did not run the suite")
    return counts, sorted(rows)


def relative(path, project_root, directory):
    """A reported path under the repository, joined from the project's own directory.

    Stryker writes host-native paths, which under MSYS do not share a spelling with the worktree the shell
    names; the project root it reports beside them does, so the tracked directory replaces it.
    """
    posix = PurePosixPath(path.replace("\\", "/"))
    base = PurePosixPath(project_root.replace("\\", "/"))
    try:
        return str(PurePosixPath(directory) / posix.relative_to(base))
    except ValueError:
        raise Refusal(f"{path} is outside the reported project root {project_root!r}") from None


def render(name, counts, rows):
    head = " ".join(f"{s.lower()} {counts.get(s, 0)}" for s in ("Killed", "Timeout", "Survived", "NoCoverage"))
    lines = [f"project {name} {head}"]
    for path, line, status, mutator, replacement, text in rows:
        lines.append(f"  {path}:{line} {status} {mutator} -> {replacement}")
        lines.append(f"      {text}")
    return lines


def report(reports, root):
    lines = []
    for name, directory, _ in plan_of(root):
        path = PurePosixPath(reports) / name / "reports" / "mutation-report.json"
        try:
            with open(path, encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, ValueError) as error:
            raise Refusal(f"{name}: cannot read {path}: {error}") from None
        lines.extend(render(name, *summarise(name, data, directory)))
    return lines


def main(argv):
    try:
        if len(argv) == 2 and argv[0] == "plan":
            out = [tests for _, _, tests in plan_of(argv[1])]
        elif len(argv) == 3 and argv[0] == "report":
            out = report(argv[1], argv[2])
        else:
            print("usage: mutation_survivors.py plan ROOT | report REPORTS ROOT", file=sys.stderr)
            return 2
    except Refusal as refusal:
        print(f"mutation_survivors: {refusal}", file=sys.stderr)
        return 3
    print("\n".join(out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
