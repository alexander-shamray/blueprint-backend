"""The scaffold PR's proof: re-render a scaffold commit and compare it.

The rule is `tools/new-service/README.md`'s *A scaffold PR is the scaffold's
output*; this module is the `--verify` that checks it.
"""

from __future__ import annotations

import io
import os
import re
import subprocess
import sys
import tarfile
import tempfile
from dataclasses import dataclass
from pathlib import Path

from scaffold import ScaffoldError

KNOWN_DIFFERENCES = Path(__file__).resolve().parents[1] / "known-differences.txt"

SERVICES = "src/Services/"
INITIAL_CREATE = re.compile(r"(\d{14})_InitialCreate\.cs")
PUBLISHED = re.compile(r'ports: \[ "127\.0\.0\.1:(\d+):8080" \]')


@dataclass(frozen=True)
class Scaffolded:
    """A scaffold commit, and the arguments its render took."""

    commit: str
    parent: str
    name: str
    worker: bool
    port: int | None
    migration_id: str
    pure_consumer: bool = False

    @property
    def argv(self) -> list[str]:
        if self.pure_consumer:
            flags = ["--pure-consumer"]
        else:
            flags = ["--worker"] if self.worker else ["--port", str(self.port)]
        return [self.name, *flags, "--migration-id", self.migration_id]


def git(repo_root: Path, *args: str, stdin: bytes | None = None) -> bytes:
    result = subprocess.run(
        ["git", "-C", str(repo_root), *args], input=stdin, capture_output=True, check=False)
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        raise ScaffoldError(f"git {args[0]} failed: {detail}")
    return result.stdout


def resolve(repo_root: Path, commit: str) -> str:
    if commit.startswith("-"):
        raise ScaffoldError(f"'{commit}' is not a commit name")
    try:
        full = git(repo_root, "rev-parse", "--verify", "--quiet", f"{commit}^{{commit}}")
    except ScaffoldError:
        raise ScaffoldError(
            f"{commit} is not a commit in this checkout, whose history a shallow clone lacks") from None
    full_sha = full.decode("ascii").strip()
    # The render runs the scaffold the parent carried, so only HEAD's own history.
    ancestry = subprocess.run(
        ["git", "-C", str(repo_root), "merge-base", "--is-ancestor", full_sha, "HEAD"],
        capture_output=True, check=False)
    if ancestry.returncode != 0:
        raise ScaffoldError(f"{commit} is not in HEAD's history, and verifying it would run its parent's scaffold")
    return full_sha


def paths(output: bytes) -> list[str]:
    return [path for path in output.decode("utf-8").split("\0") if path]


def arguments(repo_root: Path, commit: str) -> Scaffolded:
    """Read the name, the host, the port and the migration id from what the commit added."""
    full = resolve(repo_root, commit)
    try:
        parent = git(repo_root, "rev-parse", "--verify", "--quiet", f"{full}^1").decode("ascii").strip()
    except ScaffoldError:
        if git(repo_root, "rev-parse", "--is-shallow-repository").strip() == b"true":
            raise ScaffoldError(
                f"{commit}'s parent is not in this checkout, whose history a shallow clone lacks") from None
        raise ScaffoldError(f"{commit} has no parent, so no scaffold rendered it") from None
    added = paths(git(repo_root, "diff-tree", "-r", "-z", "--name-only", "--no-commit-id",
                      "--diff-filter=A", parent, full))
    existing = set(paths(git(repo_root, "ls-tree", "-z", "--name-only", parent, SERVICES)))
    services = sorted({
        path.split("/")[2] for path in added
        if path.startswith(SERVICES) and f"{SERVICES}{path.split('/')[2]}" not in existing
    })
    if len(services) != 1:
        found = ", ".join(services) if services else "none"
        raise ScaffoldError(
            f"{commit} is not a scaffold commit: it must add exactly one service under "
            f"{SERVICES}, and it adds {found}")
    name = services[0]
    root = f"{SERVICES}{name}/"
    worker = any(path.startswith(f"{root}{name}.Worker/") for path in added)
    # A worker with no Domain project is §4.1's pure consumer, the one shape that omits it.
    pure_consumer = worker and not any(path.startswith(f"{root}{name}.Domain/") for path in added)
    migrations = f"{root}{name}.Infrastructure/Persistence/Migrations/"
    ids = [match.group(1) for path in added if path.startswith(migrations)
           if (match := INITIAL_CREATE.fullmatch(path.removeprefix(migrations)))]
    if len(ids) != 1:
        raise ScaffoldError(f"{commit} adds {name} without one InitialCreate migration to read its id from")
    port = None
    if not worker:
        unit = f"deploy/compose/services/{name.lower()}.yml"
        content = committed(repo_root, full, [unit])[unit]
        if content is None or (published := PUBLISHED.search(content.decode("utf-8"))) is None:
            raise ScaffoldError(f"{commit} carries no {unit} publishing a port to read {name}'s from")
        port = int(published.group(1))
    return Scaffolded(full, parent, name, worker, port, ids[0], pure_consumer)


def render(repo_root: Path, scaffolded: Scaffolded, directory: Path) -> dict[str, bytes]:
    """Every file the parent's scaffold writes, rendered into the parent's own tree."""
    archive = git(repo_root, "archive", "--format=tar", scaffolded.parent)
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        tar.extractall(directory, filter="data")
        # Extraction stamps the parent's commit time, so a file the render writes is one whose time moved.
        stamped = {member.name: member.mtime for member in tar.getmembers() if member.isfile()}
    script = directory / "tools" / "new-service" / "new_service.py"
    if not script.is_file():
        raise ScaffoldError(f"{scaffolded.parent[:8]} carries no scaffold to render {scaffolded.name} with")
    result = subprocess.run(
        [sys.executable, "-B", str(script), *scaffolded.argv, "--repo-root", str(directory)],
        capture_output=True, check=False, env={**os.environ, "PYTHONUTF8": "1"})
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        raise ScaffoldError(f"the scaffold at {scaffolded.parent[:8]} refused the render: {detail}")
    written = {}
    for path in directory.rglob("*"):
        relative = path.relative_to(directory).as_posix()
        if path.is_file() and stamped.get(relative) != int(path.stat().st_mtime):
            written[relative] = path.read_bytes()
    return written


def committed(repo_root: Path, commit: str, relatives: list[str]) -> dict[str, bytes | None]:
    """Each path's content at the commit, or None where the commit holds none."""
    requests = "".join(f"{commit}:{relative}\n" for relative in relatives).encode("utf-8")
    output = io.BytesIO(git(repo_root, "cat-file", "--batch", stdin=requests))
    contents: dict[str, bytes | None] = {}
    for relative in relatives:
        header = output.readline().split()
        if header[-1] == b"missing":
            contents[relative] = None
            continue
        contents[relative] = output.read(int(header[2]))
        output.read(1)
    return contents


def normalised(content: bytes) -> bytes:
    # The object store holds LF (.gitattributes); a checkout's CRLF is not the rule's subject.
    return content.replace(b"\r\n", b"\n")


def differences(repo_root: Path, scaffolded: Scaffolded) -> dict[str, str]:
    """Every path where the commit is not the render, with what differs."""
    with tempfile.TemporaryDirectory(prefix="scaffold-verify-") as directory:
        rendered = render(repo_root, scaffolded, Path(directory))
    changed = paths(git(repo_root, "diff-tree", "-r", "-z", "--name-only", "--no-commit-id",
                        scaffolded.parent, scaffolded.commit))
    relatives = sorted(set(rendered) | set(changed))
    theirs = committed(repo_root, scaffolded.commit, relatives)
    found = {}
    for relative in relatives:
        if relative not in rendered:
            found[relative] = "the commit changes it and the render does not"
        elif theirs[relative] is None:
            found[relative] = "the render writes it and the commit does not carry it"
        elif normalised(theirs[relative]) != normalised(rendered[relative]):
            found[relative] = "the commit's content is not the render's"
    return found


def known_differences(commit: str) -> dict[str, str]:
    """The listed paths and reasons for this commit, from lines of `<commit> <path> <reason>`."""
    listed = {}
    text = KNOWN_DIFFERENCES.read_text(encoding="utf-8")
    for number, line in enumerate(text.splitlines(), start=1):
        if not line.strip() or line.startswith("#"):
            continue
        fields = line.split(maxsplit=2)
        if len(fields) != 3 or len(fields[0]) < 7:
            raise ScaffoldError(f"known-differences.txt:{number}: expected '<commit> <path> <reason>'")
        if commit.startswith(fields[0]):
            listed[fields[1]] = fields[2]
    return listed


def verify(repo_root: Path, commit: str) -> tuple[list[str], list[str]]:
    """The report's lines, and the failures among them; no failure means the rule holds."""
    scaffolded = arguments(repo_root, commit)
    found = differences(repo_root, scaffolded)
    listed = known_differences(scaffolded.commit)
    short = scaffolded.commit[:8]
    report = [f"{short}: new_service.py {' '.join(scaffolded.argv)}, "
              f"rendered by the scaffold at {scaffolded.parent[:8]}"]
    failures = []
    for relative, what in sorted(found.items()):
        if relative in listed:
            report.append(f"  known: {relative}: {listed[relative]}")
        else:
            failures.append(f"{short}: {relative}: {what}, and it is not a known difference")
    for relative in sorted(set(listed) - set(found)):
        failures.append(f"{short}: {relative} is a known difference the render reproduces")
    return report, failures
