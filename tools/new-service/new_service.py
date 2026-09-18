#!/usr/bin/env python3
"""Render a new service from the Catalog template (Appendix C, PR-11).

    python tools/new-service/new_service.py Yankee --port 5199

There is no template directory, and that is the design rather than an
omission. The template is `src/Services/Catalog` and `tests/Catalog.*`
themselves — the copy CI builds and `dotnet test` exercises — so there is
exactly one copy of the wiring and an improvement to Catalog reaches the next
service the next time this runs. A tokenised template beside it would be a
second copy that nothing builds and nothing reconciles, which is the drift
class this repository exists to avoid.

The price is that the scaffold names text inside files people edit. It is
paid rather than hidden: every anchor in `scaffold/` must match exactly once,
the whole render is built in memory and validated before anything is written,
and a miss raises ScaffoldError naming the file. A tool that fails open on its
own precondition reports success for work it did not do.

Since #161 it also LOADS `.github/secret-scan/secret_scan.py` and runs it over
what it has just rendered. §15.1's gate reads the working tree, so a service
carrying a credential-shaped literal with no accepted-finding entry beside it
cannot be committed at all — and the fingerprints those entries name are the
scanner's own, never a second implementation of its matching. Where that gate
is not in the tree the step writes nothing and says nothing, which is stated
here rather than discovered from a green run.

Stdlib only, like the licence gate, and for the same reasons: no restore, no
SDK, and it runs on Windows and on the Ubuntu runner without either noticing.

**Python 3.12 is the floor**, because that is what both CI jobs pin. A newer
interpreter on a developer machine is the hazard, not an older one: it accepts
APIs the floor does not, and the local suite goes green on code CI cannot run.
`Path.read_text(newline=…)` is 3.13 and was exactly that mistake once.
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

from scaffold import TEMPLATE, Names, ScaffoldError
from scaffold.render import (
    BENIGN,
    COMPOSE_INDEX,
    COPY_ROOTS,
    MIGRATION_LABELS,
    TEMPLATE_TOKEN,
    compose_unit,
    render_projects,
    render_service_compose,
    update_broker_definitions,
    update_compose,
    update_env_example,
    update_infra_only,
    update_ports_readme,
    update_solution,
)
from scaffold.verify import SCAN_REASONS, TOOL_ROOT, update_allowed_secrets

# The rest of the package's surface, named here so that importing this module
# reaches everything a caller of the scaffold reads: the entry point and the
# importable surface stay one name.
from scaffold.render import (
    COMPOSE_DIR,
    MIGRATIONS,
    OMITTED,
    SERVICE_KEY,
    TEMPLATE_MIGRATIONS,
    compose_included,
    environment_keys,
)
from scaffold.verify import SCAN_ALLOW_LIST, SCAN_GATE, load_scan_gate

# §4.1 gives these two a Worker in place of an Api, and Notifications no Domain
# project at all. This script renders the Api shape, so it refuses them by name
# rather than producing a service that contradicts the chapter — which is the
# quiet failure the documentation's "no Worker template" note did not prevent,
# because a note is not a guard. The names go when the mode arrives.
WORKER_SERVICES = frozenset({"Shipping", "Notifications"})

# The nine projects a render creates, by suffix. Named once because the
# solution writer and the identity check below must agree about them.
PROJECT_SUFFIXES = (
    "Domain",
    "Application",
    "Infrastructure",
    "Migrator",
    "Api",
    "Domain.Tests",
    "Application.Tests",
    "Api.Tests",
    "TestSupport",
)

# Every anchored pattern here is applied with `fullmatch`, never `match`.
# Python's `$` matches at the end of the string *or just before a trailing
# newline*, so `NAME.match("Zulu\n")` and `MIGRATION_ID.match("20260809120000\n")`
# both succeeded — and the newline then went into a directory name, a file
# name and a C# namespace. `fullmatch` is the whole fix, and it is worth
# stating because the patterns look exhaustive on their own.
NAME = re.compile(r"^[A-Z][A-Za-z0-9]*$")

# Windows reserves these as file and directory base names, with or without an
# extension — so neither `src/Services/Con` nor `Con.Domain.csproj` can be
# created there. They pass every other check in this file: PascalCase, no
# template token, no collision. Without this they fail *inside* `apply()`,
# which is the one place the script promises not to.
# The service name becomes a database name and a schema name, and SQL Server's
# `sysname` is nvarchar(128). Past that the projects render happily and the
# first migration is the thing that fails — late, on a machine with a database
# attached, which is the worst place for this script to be wrong.
SQL_IDENTIFIER_LIMIT = 128

# SQL Server's own. `Database=Master` points the migrator at a system database
# instead of an isolated one, and `Sys` collides with the reserved schema —
# both from a name that passes every other check and fails, if it fails at all,
# against a live server.
SQL_RESERVED = frozenset({"MASTER", "MODEL", "MSDB", "TEMPDB", "SYS"})

WINDOWS_RESERVED = frozenset(
    {"CON", "PRN", "AUX", "NUL"}
    | {f"COM{port}" for port in range(1, 10)}
    | {f"LPT{port}" for port in range(1, 10)}
)

# A migration id is the timestamp EF generates, and it reaches a path. Anything
# else is both invalid metadata and, with a `..` in it, a write outside the
# service tree — from a flag whose whole purpose is to make a test repeatable.
MIGRATION_ID = re.compile(r"^\d{14}$")

# Docker publishes 1–65535 and nothing else. §14.1 allocates 51xx by
# convention, which is a decision rather than a rule, so the guard is the
# protocol's limit and the convention stays in the documentation.
PORTS = range(1, 65536)


@dataclass
class Plan:
    """Everything the run would write, before any of it is written."""

    created: dict[str, str] = field(default_factory=dict)
    updated: dict[str, str] = field(default_factory=dict)


def plan(repo_root: Path, name: str, port: int, migration_id: str) -> Plan:
    """Everything the run would write, validated. Nothing is written here."""
    if not NAME.fullmatch(name):
        raise ScaffoldError(f"'{name}' is not a PascalCase service name")
    if len(name) > SQL_IDENTIFIER_LIMIT:
        raise ScaffoldError(
            f"'{name[:20]}…' is {len(name)} characters; the name becomes a SQL Server "
            f"database and schema, and sysname stops at {SQL_IDENTIFIER_LIMIT}. The "
            f"projects would render and the first migration would not run."
        )
    if name.upper() in SQL_RESERVED:
        raise ScaffoldError(
            f"'{name}' is a SQL Server system name: the service's database and schema "
            f"take this name, so the migrator would target the server's own."
        )
    if name.upper() in WINDOWS_RESERVED:
        raise ScaffoldError(
            f"'{name}' is a reserved device name on Windows: neither "
            f"src/Services/{name} nor {name}.Domain.csproj can be created there, and "
            f"a repository that half-renders on one platform is not portable."
        )
    # Case-insensitively, because the casings are what the rename keys on:
    # `CATALOG` passes an exact-match check, and its *lower* casing is still
    # `catalog`, so the Compose block it renders keeps the template's own
    # service keys and the file gains a duplicate pair. A case-sensitive
    # filesystem is where that lands — the collision check below hides it on
    # Windows and would not on the runner.
    if name.lower() == TEMPLATE.lower():
        raise ScaffoldError(
            f"{name} is the template under another casing; it cannot be its own copy"
        )

    if name.lower() in {service.lower() for service in WORKER_SERVICES}:
        raise ScaffoldError(
            f"§4.1 gives {name} a Worker in place of an Api, and this script renders the "
            f"Api shape. Worker mode joins with the PR that builds the first worker host; "
            f"until then a {name} scaffolded here would contradict the chapter."
        )

    # And the same test against every service already here, because the
    # template is only the first entry in that set. After Ordering exists,
    # `ORDERING` makes a distinct directory on a case-sensitive filesystem and
    # then renders `ordering-api` and `ordering-migrator` a second time — the
    # duplicate-key failure again, one service along.
    services = repo_root / "src" / "Services"
    taken = (
        {path.name.lower() for path in services.iterdir() if path.is_dir()}
        if services.is_dir()
        else set()
    )
    if name.lower() in taken:
        raise ScaffoldError(
            f"a service whose name differs from {name} only by casing already exists; "
            f"the two would share every lower-cased Compose key and connection variable"
        )
    if not MIGRATION_ID.fullmatch(migration_id):
        raise ScaffoldError(
            f"'{migration_id}' is not a 14-digit migration timestamp; it reaches a file path"
        )
    if port not in PORTS:
        raise ScaffoldError(f"port {port} is outside 1–65535 and Docker cannot publish it")
    if not (repo_root / COPY_ROOTS[0]).is_dir():
        raise ScaffoldError(f"{repo_root} does not look like the repository: no {COPY_ROOTS[0]}")

    names = Names(name)

    # Assembly identity first, because it is the more fundamental refusal:
    # "this name can never work here", ahead of "this service already exists".
    # `Common` renders Common.Domain and Common.Application beside the
    # building blocks of exactly those names, and a solution holding two
    # projects with one identity does not build. It was refused before this
    # check too — `tests/Common.Domain.Tests` exists, so the directory test
    # below fired — but by accident and with a message about the wrong thing,
    # and a name colliding on identity without colliding on disk sailed through.
    # Compared case-insensitively, because a .NET assembly simple name is.
    # `COMMON` clears every check above on a case-sensitive filesystem, and
    # `COMMON.Domain` does not intersect `Common.Domain` as a string — so the
    # first version of this check let through exactly the case it was written
    # to stop.
    generated = {f"{names.pascal}.{suffix}": suffix for suffix in PROJECT_SUFFIXES}
    existing = {
        path.stem.lower()
        for directory in ("src", "tests")
        if (repo_root / directory).is_dir()
        for path in (repo_root / directory).rglob("*.csproj")
    }
    if (clash := sorted(project for project in generated if project.lower() in existing)):
        raise ScaffoldError(
            "the solution already has " + ", ".join(clash) + ", give or take casing. Two "
            "projects with one assembly identity do not build, whatever directory each "
            "sits in and however each is spelt."
        )

    for root in COPY_ROOTS:
        target = repo_root / names.rename(root)
        if target.exists():
            raise ScaffoldError(f"{names.rename(root)} already exists; this script creates, never merges")

    # The new service's own name is masked before the search, or a legitimate
    # one that contains a template token — CatalogSearch, ProductReviews — is
    # rejected for the tokens it was asked for. What is left after masking is a
    # mention the rename did not reach, which is the only thing this check is
    # about.
    #
    # The mask is why the SLICE half of this check does not run here. Masking a
    # service called `Product` would strip every genuine `Product` leftover
    # along with its own name, and the render would report itself
    # domain-neutral while the slice survived in it. `render_projects` runs
    # that half before the rename instead, where a `Product` is unambiguous —
    # the rename maps the template's casings and never touches the slice's.
    mask = re.compile("|".join(re.escape(n) for n in (names.pascal, names.lower, names.upper)))

    # MIGRATION_LABELS, SCAN_REASONS and TOOL_ROOT are handed down from this
    # module rather than read where they are checked, so replacing one of them
    # on this module replaces it for the run.
    created = render_projects(repo_root, names, migration_id, MIGRATION_LABELS)

    # The service's Compose unit is created rather than spliced, so it joins
    # `created` here — before the straggler loop below, which is exactly the
    # check a renamed file wants and the one the spliced block never got.
    #
    # **And it is refused if it is already there**, because `apply` opens every
    # created path with `w` and this one is the only created path outside
    # `COPY_ROOTS`, which is what the collision guard above reads. A unit left
    # behind by a partial run, or written by hand, would be truncated without a
    # word — against a script whose contract is that it creates and never
    # merges. `update_compose` refuses a unit the index already includes; this
    # is the other half, for a file the index has never heard of.
    unit = compose_unit(names)
    if (repo_root / unit).exists():
        raise ScaffoldError(
            f"{unit} already exists. This script creates it and would overwrite "
            f"what is there; remove it, or scaffold under a different name."
        )
    created[unit] = render_service_compose(repo_root, names, port)
    for relative, text in created.items():
        stripped = BENIGN.sub("", mask.sub("", text))
        if (left := TEMPLATE_TOKEN.search(stripped)) is not None:
            line = stripped[: left.start()].count("\n") + 1
            raise ScaffoldError(
                f"{relative}:{line}: '{left.group(0)}' survived the rename. "
                f"The file names Catalog somewhere this script does not patch."
            )
        if TEMPLATE_TOKEN.search(mask.sub("", relative)) is not None:
            raise ScaffoldError(f"{relative}: the path itself still names the template")

    updated = {
        "Platform.slnx": update_solution(repo_root, names),
        COMPOSE_INDEX: update_compose(repo_root, names, port),
        "deploy/compose/docker-compose.infra-only.yml": update_infra_only(repo_root, names),
        "deploy/compose/.env.example": update_env_example(repo_root, names),
        "deploy/compose/README.md": update_ports_readme(repo_root, names, port),
        "deploy/compose/rabbitmq/definitions.json":
            update_broker_definitions(repo_root, names),
    }

    # Last, because it is the only update whose input is every other one. The
    # gate runs over the whole render — the generated tree AND the shared files
    # as they will stand — so it can only be asked once both maps are complete,
    # and it is still asked before anything is written: the file it returns is
    # a value like every other, and `apply` remains the one thing that touches
    # disk.
    allowed = update_allowed_secrets(
        repo_root, names, created, updated, SCAN_REASONS, TOOL_ROOT)
    if allowed is not None:
        updated.update(allowed)

    return Plan(created=created, updated=updated)


def apply(repo_root: Path, rendered: Plan) -> None:
    """Write the plan.

    **This is not atomic, and the guarantee above is about validation only.**
    Every anchor, every classification and every straggler check runs before
    the first file is created, so a run this script *refuses* writes nothing —
    that much is a property. An I/O failure partway through this loop is not
    covered: some targets will exist and some shared files will be updated.

    Staging and rolling back was considered and declined. The target is a git
    checkout, the run is a developer typing one command, and `git status`
    already shows exactly what landed with `git restore` and one `rm -rf` to
    undo it — a bespoke transaction log would be a second, untested mechanism
    for something version control does better. Narrowing the claim is the
    honest half of that decision, and a Copilot review is what caught the
    claim being wider than the code.
    """
    for relative, text in {**rendered.created, **rendered.updated}.items():
        target = repo_root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("w", encoding="utf-8", newline="") as handle:
            handle.write(text)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="new_service.py",
        description="Render a new service from the Catalog template (Appendix C, PR-11).",
    )
    parser.add_argument("name", help="the service name, PascalCase — Ordering, Inventory, Payments")
    parser.add_argument(
        "--port",
        type=int,
        required=True,
        help=(
            "the host port the API publishes. Required, never derived: a port is an "
            "allocation recorded in §14.1 and deploy/compose/README.md, and a script "
            "that guessed one would quietly disagree with a printed chapter"
        ),
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
        help="the repository root (default: inferred from this script's location)",
    )
    parser.add_argument(
        "--migration-id",
        default=None,
        help="the InitialCreate migration id (default: the current UTC timestamp)",
    )
    args = parser.parse_args(argv)

    migration_id = args.migration_id or datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    try:
        rendered = plan(args.repo_root, args.name, args.port, migration_id)
        apply(args.repo_root, rendered)
    except ScaffoldError as error:
        print(f"new_service.py: {error}", file=sys.stderr)
        return 1

    print(
        f"{args.name}: {len(rendered.created)} files created, "
        f"{len(rendered.updated)} updated, API on port {args.port}."
    )
    print("Next: dotnet restore Platform.slnx && dotnet build Platform.slnx")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
