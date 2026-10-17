"""What the rollout reads out of the C# sources: the hosts, their consumers and sagas, and
the health routes they map."""

from __future__ import annotations

import functools
import re
from pathlib import Path

from descriptor_model import PlanError, ROOT


# The MassTransit registration methods a scan can find, singly and in bulk,
# as its assembly names them at the verified version. A service is a consumer
# by registering one, so the call is the subject. Sagas and futures are apart:
# MassTransit measures them on the saga instruments.
CONSUMER_METHODS = (
    "AddConsumer", "AddConsumers", "AddConsumersFromNamespaceContaining",
    "AddFutureRequestConsumer",
)

SAGA_METHODS = (
    "AddSaga", "AddSagas", "AddSagasFromNamespaceContaining",
    "AddSagaStateMachine", "AddSagaStateMachines",
    "AddSagaStateMachinesFromNamespaceContaining", "AddJobSagaStateMachines",
    "AddFuture", "AddFutures", "AddFuturesFromNamespaceContaining",
)


def _registration(methods: tuple[str, ...]) -> re.Pattern:
    """A call to any of `methods`, generic or not, and to nothing longer."""
    return re.compile(rf"\.(?:{'|'.join(methods)})\s*[<(]")


CONSUMER_REGISTRATION = _registration(CONSUMER_METHODS)

SAGA_REGISTRATION = _registration(SAGA_METHODS)


def has_consumers(service_name: str, root: Path = ROOT) -> bool:
    """Whether the service whose host is `service_name` registers a consumer."""
    return _registers(service_name, CONSUMER_REGISTRATION, root)


def has_sagas(service_name: str, root: Path = ROOT) -> bool:
    """Whether the service whose host is `service_name` registers a saga."""
    return _registers(service_name, SAGA_REGISTRATION, root)


@functools.cache
def _registers(service_name: str, pattern: re.Pattern, root: Path) -> bool:
    """Whether any source in the service's tree matches `pattern`.

    The service's tree is the host project's parent directory, which holds
    the host and the projects it composes.
    """
    sources = _csharp_sources(root)
    for host in (path for path in sources if path.name == f"{service_name}.csproj"):
        tree = host.parent.parent
        for path, code in sources.items():
            if path.suffix == ".cs" and tree in path.parents and pattern.search(
                    _code_only(code)):
                return True
    return False


# A literal is matched before a comment, so a `//` or `/*` inside one stays
# text: raw, verbatim, regular and character literals, then the two comments.
# A raw literal closes on a run of exactly as many quotes as opened it.
CSHARP_TOKEN = re.compile(
    r'(?P<literal>\$*(?P<run>"{3,})[\s\S]*?(?P=run)|\$?@\$?"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\\n])*"'
    r"|'(?:\\.|[^'\\\n])')"
    r"|(?P<comment>//[^\n]*|/\*[\s\S]*?\*/)"
)


@functools.cache
def _code_only(code: str) -> str:
    """C# with its comments and the contents of its literals blanked.

    Every character keeps its offset and line break, so a match gives its
    line and a literal is read back from the original at the same offset.
    """
    return CSHARP_TOKEN.sub(lambda token: re.sub(r"[^\n]", " ", token.group(0)), code)


# One ordinary literal with no escape, closing its argument. A literal the
# argument continues past, as a concatenation, is not the whole value, and an
# escape spells a value the source text does not show.
WHOLE_LITERAL = re.compile(r"\s*\"([^\"\\\n]+)\"(?=\s*[,)])")


def _literal_arguments(code: str, method: str) -> list[tuple[str | None, int]]:
    """Each live call to `method`, with its first argument where that is a
    whole literal, and the call's offset. The call is found in the masked
    text, so a comment or a string that spells one is not a call."""
    masked = _code_only(code)
    calls = []
    for call in re.finditer(rf"\b{method}\s*\(", masked):
        literal = WHOLE_LITERAL.match(code, call.end())
        calls.append((literal.group(1) if literal else None, call.start()))
    return calls


@functools.cache
def _csharp_sources(root: Path) -> dict[Path, str]:
    """Every project file and C# source under src/, outside build output.

    Cached because the scans run once per workload and per check; a project
    file maps to an empty text, since only its name is read.
    """
    found = {}
    for path in (root / "src").rglob("*"):
        if {"obj", "bin"} & set(path.relative_to(root).parts):
            continue
        if path.suffix == ".cs":
            found[path] = path.read_text(encoding="utf-8")
        elif path.suffix == ".csproj":
            found[path] = ""
    return found


def health_routes(root: Path = ROOT) -> set[str]:
    """Every literal route a host maps a health check on, read from the code."""
    return {route for route, _ in _health_calls(root) if route is not None}


def unresolved_health_routes(root: Path = ROOT) -> list[str]:
    """Every MapHealthChecks call whose route is not a literal, as file:line."""
    return [site for route, site in _health_calls(root) if route is None]


@functools.cache
def _health_calls(root: Path) -> tuple[tuple[str | None, str], ...]:
    """Each MapHealthChecks call site outside a comment, with its literal."""
    calls = []
    for path, code in sorted(_csharp_sources(root).items()):
        if path.suffix != ".cs":
            continue
        for route, offset in _literal_arguments(code, "MapHealthChecks"):
            line = code.count("\n", 0, offset) + 1
            calls.append((route, f"{path.relative_to(root).as_posix()}:{line}"))
    return tuple(calls)


def probe_exclusion(source: Path = ROOT) -> str:
    """The http_route regex that excludes every probe route the code maps.

    Derived from the routes; refused where none is found or readable, as a
    partial exclusion counts probes as traffic.
    """
    failures = _probe_routes_are_readable(source)
    if failures:
        raise PlanError("; ".join(failures))
    # PromQL anchors the regex at both ends; its string literal needs each
    # backslash re.escape adds doubled.
    return "|".join(re.escape(route).replace("\\", "\\\\") for route in sorted(health_routes(source)))


def _probe_routes_are_readable(source: Path) -> list[str]:
    """The probe-route scan found routes, and every call site's is a literal.

    The subject is checked first, because a scan that found no route would
    exclude nothing and certify it.
    """
    if not health_routes(source):
        return ["found no MapHealthChecks route under src/: the probe exclusion "
                "would be empty, so the scan is what is broken"]
    return [
        f"{site} maps a health check on a route that is not a string literal, "
        "so the probe exclusion cannot include it"
        for site in unresolved_health_routes(source)
    ]


def _host_assemblies(source: Path) -> set[str]:
    """Every project that produces a host, by assembly name.

    A host has a `Program.cs` beside its csproj, which is what
    `ApplicationName` defaults to at run time; derived, never listed.
    """
    hosts = set()
    for csproj in (source / "src").rglob("*.csproj"):
        if (csproj.parent / "Program.cs").exists():
            hosts.add(csproj.stem)
    return hosts
