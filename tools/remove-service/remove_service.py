#!/usr/bin/env python3
"""Remove a service, the mirror of tools/new-service (ADR-062, ADR-102).

README.md owns what it deletes, reverts, skips and refuses.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path

NEW_SERVICE = Path(__file__).resolve().parents[1] / "new-service"
sys.path.insert(0, str(NEW_SERVICE))

from new_service import NAME  # noqa: E402
from scaffold import TEMPLATE, Names, ScaffoldError, read, restore  # noqa: E402
from scaffold.render import (  # noqa: E402
    COMPOSE_INDEX,
    ENV_MARKER,
    REDIS_USERS,
    compose_unit,
)
from scaffold.verify import SCAN_ALLOW_LIST  # noqa: E402

# Any service's marker, not only the one being removed: ENV_MARKER skips "The BFF's",
# and a block that ran on to the next marker it recognised would take the BFF's with it.
ANY_ENV_MARKER = re.compile(r"# .+'s two §7\.1 keys")

SLNX = "Platform.slnx"
INFRA_ONLY = "deploy/compose/docker-compose.infra-only.yml"
BROKER_DEFINITIONS = "deploy/compose/rabbitmq/definitions.json"

# The scaffold writes this heading above the entries it adds to an allow-list
# file, and the heading is the only thing that says whose block it is.
RENDERED_HEADING = re.compile(r"# --- (\w+), rendered by tools/new-service -+")

# What a mention search never reads: build output, VCS state, session state and
# the sibling worktrees, each a copy of the tree rather than part of it.
SKIP_DIRS = frozenset({".git", "bin", "obj", "node_modules", "artifacts", "TestResults", ".remember"})
SKIP_PATHS = frozenset({".claude/worktrees"})
MAX_MENTION_BYTES = 2_000_000


@dataclass
class Plan:
    """Everything the run would change, before any of it is."""

    deleted: list[str] = field(default_factory=list)
    updated: dict[str, str] = field(default_factory=dict)
    absent: list[str] = field(default_factory=list)


def once(found: list, what: str, where: str, *, required: bool, plan: Plan) -> bool:
    """True when exactly one match; a miss is skipped, reported, or refused by need."""
    if len(found) > 1:
        raise ScaffoldError(f"{where}: {len(found)} {what}; this script removes one and will not guess which")
    if not found:
        if required:
            raise ScaffoldError(f"{where}: no {what}. Nothing here says the service is wired in as rendered")
        plan.absent.append(f"{where}: no {what}")
        return False
    return True


def service_roots(repo_root: Path, names: Names) -> list[str]:
    """The directories that are the service's own, repository-relative."""
    tests = repo_root / "tests"
    prefix = f"{names.pascal}."
    owned = (
        sorted(path.name for path in tests.iterdir() if path.is_dir() and path.name.startswith(prefix))
        if tests.is_dir()
        else []
    )
    return [f"src/Services/{names.pascal}", *(f"tests/{name}" for name in owned)]


def without_solution_entries(repo_root: Path, names: Names, plan: Plan) -> str:
    """The service's folder and every tests/<Name>. entry out of Platform.slnx."""
    text, newline = read(repo_root, SLNX)
    lines = text.splitlines(keepends=True)

    folder = f'  <Folder Name="/src/Services/{names.pascal}/">\n'
    starts = [i for i, line in enumerate(lines) if line == folder]
    once(starts, f"`{folder.strip()}` folder", SLNX, required=True, plan=plan)
    end = lines.index("  </Folder>\n", starts[0])
    del lines[starts[0]:end + 1]

    entry = re.compile(rf'^\s*<Project Path="tests/{re.escape(names.pascal)}\.[^"]*" />$')
    kept = [line for line in lines if not entry.match(line.rstrip("\r\n"))]
    if len(kept) == len(lines):
        plan.absent.append(f"{SLNX}: no tests/{names.pascal}. project entries")
    return restore("".join(kept), newline)


def without_compose_include(repo_root: Path, names: Names, plan: Plan) -> str:
    """The index's one include line for the service's unit."""
    text, newline = read(repo_root, COMPOSE_INDEX)
    include = f"  - {compose_unit(names).removeprefix('deploy/compose/')}"
    lines = text.split("\n")
    at = [i for i, line in enumerate(lines) if line == include]
    once(at, f"`{include.strip()}` line", COMPOSE_INDEX, required=True, plan=plan)
    del lines[at[0]]
    return restore("\n".join(lines), newline)


def without_infra_only_pair(repo_root: Path, names: Names, plan: Plan) -> str | None:
    """Both halves of the pair out of the excluded-profile override."""
    text, newline = read(repo_root, INFRA_ONLY)
    key = re.compile(rf"  {re.escape(names.lower)}-(migrator|api|worker):")
    lines = text.split("\n")
    kept: list[str] = []
    dropped: list[str] = []
    skipping = False
    for line in lines:
        if (match := key.fullmatch(line)) is not None:
            dropped.append(match.group(1))
            skipping = True
        elif skipping and line.startswith("    "):
            continue
        else:
            skipping = False
            kept.append(line)
    if not dropped:
        plan.absent.append(f"{INFRA_ONLY}: no {names.lower}-* pair")
        return None
    if sorted(dropped) not in (["api", "migrator"], ["migrator", "worker"]):
        raise ScaffoldError(
            f"{INFRA_ONLY}: found {sorted(dropped)} for {names.lower}; §14.1's pair rule makes it "
            f"a migrator and one host, and this script will not remove half of one")
    return restore("\n".join(kept), newline)


def without_env_block(repo_root: Path, names: Names, plan: Plan) -> str | None:
    """The service's commented pair of §7.1 keys, up to the next service's marker."""
    relative = "deploy/compose/.env.example"
    text, newline = read(repo_root, relative)
    lines = text.split("\n")
    mine = [
        i for i, line in enumerate(lines)
        if (m := ENV_MARKER.match(line)) is not None and m.group(1) == names.pascal
    ]
    if not once(mine, f"\"# {names.pascal}'s two §7.1 keys\" block", relative, required=False, plan=plan):
        return None
    start = mine[0]
    stop = next((i for i, line in enumerate(lines) if i > start and ANY_ENV_MARKER.match(line)), len(lines))
    rest = lines[:start] + lines[stop:]
    # Last in the file, the block leaves the blank line that separated it
    # from the one before: the scaffold's own append is "\n\n" + block.
    return restore("\n".join(rest).rstrip("\n") + "\n" if stop == len(lines) else "\n".join(rest), newline)


def without_broker_account(repo_root: Path, names: Names, plan: Plan) -> str | None:
    """The service's broker user and its permissions entry."""
    text, newline = read(repo_root, BROKER_DEFINITIONS)
    definitions = json.loads(text)
    user = f"{names.lower}-svc"
    accounts = [entry for entry in definitions["users"] if entry["name"] == user]
    grants = [entry for entry in definitions["permissions"] if entry["user"] == user]
    found_user = once(accounts, f"broker user {user}", BROKER_DEFINITIONS, required=False, plan=plan)
    found_grant = once(grants, f"permissions entry for {user}", BROKER_DEFINITIONS, required=False, plan=plan)
    if not (found_user or found_grant):
        return None
    definitions["users"] = [entry for entry in definitions["users"] if entry["name"] != user]
    definitions["permissions"] = [entry for entry in definitions["permissions"] if entry["user"] != user]
    return restore(json.dumps(definitions, indent=2) + "\n", newline)


def without_redis_user(repo_root: Path, names: Names, plan: Plan) -> str | None:
    """The §8.1 Redis user, if the service still has one."""
    text, newline = read(repo_root, REDIS_USERS)
    lines = text.split("\n")
    prefix = f"user {names.lower}-svc "
    at = [i for i, line in enumerate(lines) if line.startswith(prefix)]
    if not once(at, f"Redis user {names.lower}-svc", REDIS_USERS, required=False, plan=plan):
        return None
    del lines[at[0]]
    return restore("\n".join(lines), newline)


def lead(lines: list[str], index: int) -> int:
    """Back over the two blank lines the scaffold writes above a heading."""
    start = index
    while start > 0 and index - start < 2 and lines[start - 1] == "":
        start -= 1
    return start


def block_end(lines: list[str], heading: int) -> int:
    """Past the block's last entry: the preamble, a blank line, then entries up to the next blank."""
    index = heading
    while index < len(lines) and lines[index] != "":
        index += 1
    index += 1
    while index < len(lines) and lines[index] != "":
        index += 1
    return min(index, len(lines))


def without_allowed_entries(repo_root: Path, names: Names) -> dict[str, str]:
    """Each allow-list file with the service's block and entries out of it.

    The scaffold's headed block goes whole; entries for the service's own trees
    go wherever they are, and a comment above one stays for the mention search.
    """
    owned = (f"src/Services/{names.pascal}/", f"tests/{names.pascal}.", compose_unit(names))
    written: dict[str, str] = {}
    directory = repo_root / SCAN_ALLOW_LIST
    for source in sorted(directory.glob("*.txt")) if directory.is_dir() else []:
        relative = f"{SCAN_ALLOW_LIST}/{source.name}"
        text, newline = read(repo_root, relative)
        lines = text.split("\n")

        headings = [i for i, line in enumerate(lines) if RENDERED_HEADING.fullmatch(line)]
        for at in reversed(headings):
            if RENDERED_HEADING.fullmatch(lines[at]).group(1) != names.pascal:
                continue
            lines = lines[:lead(lines, at)] + lines[block_end(lines, at):]
            headings = [i for i, line in enumerate(lines) if RENDERED_HEADING.fullmatch(line)]

        lines = [
            line for line in lines
            if line.startswith("#") or not line.strip() or not line.split("|")[0].strip().startswith(owned)
        ]
        edited = restore("\n".join(lines), newline)
        if edited != restore(text, newline):
            written[relative] = edited
    return written


def plan(repo_root: Path, name: str) -> Plan:
    """Everything the run would change, validated. Nothing is changed here."""
    if not NAME.fullmatch(name):
        raise ScaffoldError(f"'{name}' is not a PascalCase service name")
    if name.lower() == TEMPLATE.lower():
        raise ScaffoldError(
            f"{name} is the scaffold's template: new_service.py renders every service from it, "
            f"so removing it leaves nothing to render from")
    services = repo_root / "src" / "Services"
    if name not in ({path.name for path in services.iterdir() if path.is_dir()} if services.is_dir() else set()):
        raise ScaffoldError(f"src/Services/{name} does not exist; there is no such service to remove")

    names = Names(name)
    rendered = Plan()
    roots = service_roots(repo_root, names)
    unit = compose_unit(names)

    rendered.updated[SLNX] = without_solution_entries(repo_root, names, rendered)
    rendered.updated[COMPOSE_INDEX] = without_compose_include(repo_root, names, rendered)
    for relative, edit in (
        (INFRA_ONLY, without_infra_only_pair),
        ("deploy/compose/.env.example", without_env_block),
        (BROKER_DEFINITIONS, without_broker_account),
        (REDIS_USERS, without_redis_user),
    ):
        if (text := edit(repo_root, names, rendered)) is not None:
            rendered.updated[relative] = text
    rendered.updated.update(without_allowed_entries(repo_root, names))

    rendered.deleted = [*roots, *([unit] if (repo_root / unit).exists() else [])]
    return rendered


def apply(repo_root: Path, rendered: Plan) -> None:
    """Make the change. Validation was the plan's; an I/O failure here is not rolled back."""
    for relative, text in rendered.updated.items():
        with (repo_root / relative).open("w", encoding="utf-8", newline="") as handle:
            handle.write(text)
    for relative in rendered.deleted:
        target = repo_root / relative
        if target.is_dir():
            shutil.rmtree(target)
        else:
            target.unlink()


def mentions(
    repo_root: Path, names: Names, rendered: Plan, unread: list[str], unsearched: dict[str, int],
) -> dict[str, list[int]]:
    """Files still naming the service once the plan is applied, with their line numbers.

    A file that cannot be read is added to `unread`: it is not a mention, and the
    deletion has already happened by the time this runs. A file over
    MAX_MENTION_BYTES or not UTF-8 is counted in `unsearched` under "too large" or
    "not UTF-8", so an empty result is never read as a complete one.
    """
    needle = re.compile("|".join(re.escape(token) for token in (names.pascal, names.lower, names.upper)))
    doomed = tuple(f"{path}/" for path in rendered.deleted)
    found: dict[str, list[int]] = {}
    for current, directories, files in os.walk(repo_root):
        here = Path(current).relative_to(repo_root).as_posix()
        directories[:] = sorted(
            d for d in directories
            if d not in SKIP_DIRS and (d if here == "." else f"{here}/{d}") not in SKIP_PATHS)
        for filename in sorted(files):
            relative = filename if here == "." else f"{here}/{filename}"
            if relative in rendered.deleted or relative.startswith(doomed):
                continue
            if relative in rendered.updated:
                body = rendered.updated[relative]
            else:
                path = repo_root / relative
                try:
                    if path.stat().st_size > MAX_MENTION_BYTES:
                        unsearched["too large"] = unsearched.get("too large", 0) + 1
                        continue
                    body = path.read_bytes().decode("utf-8")
                except UnicodeDecodeError:
                    unsearched["not UTF-8"] = unsearched.get("not UTF-8", 0) + 1
                    continue
                except OSError:
                    unread.append(relative)
                    continue
            hits = [n for n, line in enumerate(body.splitlines(), start=1) if needle.search(line)]
            if hits:
                found[relative] = hits
    return found


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="remove_service.py",
        description="Remove a service: the mirror of tools/new-service (ADR-062).",
    )
    parser.add_argument("name", help="the service, PascalCase, as it is under src/Services")
    parser.add_argument(
        "--dry-run", action="store_true",
        help="print what would be deleted, edited and still mentioned, and change nothing")
    parser.add_argument(
        "--repo-root", type=Path, default=Path(__file__).resolve().parents[2],
        help="the repository root (default: inferred from this script's location)")
    args = parser.parse_args(argv)

    try:
        rendered = plan(args.repo_root, args.name)
        if not args.dry_run:
            apply(args.repo_root, rendered)
    except ScaffoldError as error:
        print(f"remove_service.py: {error}", file=sys.stderr)
        return 1

    verb = "would delete" if args.dry_run else "deleted"
    print(f"{args.name}: {verb} {len(rendered.deleted)} paths and edit {len(rendered.updated)} shared files.")
    for relative in rendered.deleted:
        print(f"  {relative}")
    for note in rendered.absent:
        print(f"  skipped: {note}")

    # Read over the plan's own texts, so a dry run names what the real run leaves.
    unread: list[str] = []
    unsearched: dict[str, int] = {}
    left = mentions(args.repo_root, Names(args.name), rendered, unread, unsearched)
    for relative in unread:
        print(f"  could not read {relative}; it may still name {args.name}")
    if left:
        print(f"Still naming {args.name} in {len(left)} files, yours to decide:")
        for relative, lines in sorted(left.items()):
            shown = ", ".join(str(n) for n in lines[:5]) + (", ..." if len(lines) > 5 else "")
            print(f"  {relative}: {len(lines)} line(s), at {shown}")
    if unsearched:
        counts = ", ".join(f"{count} {reason}" for reason, count in sorted(unsearched.items()))
        print(f"Not searched, so the list is not complete: {counts} "
              f"(a file is skipped over {MAX_MENTION_BYTES} bytes or when it is not UTF-8).")
    if not left and not unread:
        print(f"Nothing else names {args.name}" + (" in the files it searched." if unsearched else "."))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
