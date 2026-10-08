#!/usr/bin/env python3
"""Gate for the broker's per-service permissions (#44).

`definitions.json` is JSON, so it can hold no argument and no reference to the
code it has to keep up with. That code moves: a receive endpoint added in a
service's `Messaging.DependencyInjection`, a peer queue added to its
`Endpoints`, a sixth bounded context added to `Common.Contracts` — each changes
what a service must be allowed to touch, and none of them is anywhere near this
file.

**A too-narrow permission does not fail the way a typo fails.** The broker
refuses the operation and MassTransit retries the topology, so the service
stays healthy and silent while a message goes nowhere — the failure mode
`Dockerfile` already records for the delayed exchange, arriving through
authorisation instead of through a missing plugin. That is why this is a gate
and not a comment.

Two halves, and the second is the one #44 is actually about:

  * every resource a service's own code needs, that service may use; and
  * every resource that is somebody else's, it may NOT write.

**Both halves are derived per service, from the code, and nothing here is a
list of service names.** §4.5's scaffold renames the template inside whatever
it renders and adds a broker account for it, so a hard-coded pair would have
been right for exactly as long as this platform had two services and would then
have gone quiet rather than red. That is the same reason §4.2's cross-service
architecture gate is keyed on a predicate rather than a name.

Stdlib only, on the licence gate's terms: no restore, no dependencies, no
broker. It reads text.

    py -3.12 deploy/compose/rabbitmq/check_permissions.py
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
HERE = ROOT / "deploy" / "compose" / "rabbitmq"
DEFINITIONS = HERE / "definitions.json"
WORKFLOW_PATH = ".github/workflows/broker-permissions.yml"
WORKFLOW = ROOT / WORKFLOW_PATH

SERVICES = ROOT / "src" / "Services"
# The hosts' tree (§4.1). A host has no Infrastructure project (§4.1), so a
# consuming host keeps its Messaging directory at its own project's root.
HOSTS = ROOT / "src" / "BFF"
CONTRACTS = ROOT / "src" / "BuildingBlocks" / "Common.Contracts"
DOCKERFILE = HERE / "Dockerfile"
TESTS = ROOT / "tests"

# Found by what a file constructs, not by where it sits or what it is called.
# A name in a constant covers the files it names and no others, and a gate
# that stops covering the newest surface without a word is the failure
# CLAUDE.md calls this repository's most-repeated. A fixture under any name,
# anywhere under tests/, starts a broker by constructing one.
BROKER_CONTAINER = re.compile(r"\bnew\s+RabbitMqBuilder\s*\(")

# EVERY PATH OUTSIDE deploy/compose/rabbitmq THAT THIS SCRIPT READS, declared
# once, on deploy/helm/smoke.sh's terms and check.py's. The workflow's filters
# must cover each, or a change to one is a green pull request that skips the
# gate watching it — and docs/lessons.md records that a fourth copy of this pattern
# arrives owing a test whose subject is the READS rather than the workflow,
# because a list can only be compared for entries it already contains. That is
# check 7 here.
SOURCE_INPUTS = [
    "src/Services",
    "src/BFF",
    "src/BuildingBlocks/Common.Contracts",
    "tests",
]

# A service's broker account is its name plus this suffix, and the contract
# namespace it OWNS follows from the same name: `catalog-svc` owns
# `Common.Contracts.Catalog.V1:`. Derived rather than listed, for the reason
# the docstring gives. The floors in main() assert the derivation resolves, so
# a misnamed account fails loudly instead of matching nothing.
USER_SUFFIX = "-svc"

# MassTransit's own exchanges, which every service declares and writes. Fault
# reporting is the error path §13.6 pages on, so a service that cannot publish
# a fault fails INTO the silence that alert exists to break.
#
# Found by running an exploit probe rather than by reading: normal operation
# never faults, so `MassTransit:ReceiveFault` did not appear in a topology
# capture taken from a healthy stack. **A runtime capture shows what RAN, not
# what CAN run** — the same reason `payments-commands` is derived from
# `Endpoints.cs` below and not from a broker, having never been reached by a
# saga with no Inventory service to answer it.
FRAMEWORK_PREFIX = "MassTransit:"

# RabbitMqMessageNameFormatter's names for the fault hierarchy: `ReceiveFault`
# and `Fault<T>` both extend the root `Fault`, and a generic's argument sits
# between `--` separators with its own namespace.
FAULT_ROOT = f"{FRAMEWORK_PREFIX}Fault"


def fault_of(prefix: str) -> str:
    """The fault exchange for a message in one namespace, `MassTransit:Fault--<ns>:<type>--`."""
    return f"{FAULT_ROOT}--{prefix}Anything--"

# The polymorphic publish exchange. MassTransit binds each concrete contract
# exchange to one exchange per interface the message implements, and the
# sender declares that binding: `exchange.bind` takes write on the
# destination, which is this exchange, and read on the source, which is the
# concrete contract. So a publisher declares and writes it and a consumer
# needs no read here — a grant that enumerates contract prefixes rather than
# naming `Common\.Contracts` whole is not thereby too narrow, because the
# read a consumer does need is on its peer's concrete contract exchange.
INTERFACE_EXCHANGE = "Common.Contracts:IIntegrationEvent"

# The one account here that is not a service's, and the one tag any account may
# hold: ADR-072 decides both, main holds the tag and check_operator the grant.
OPERATOR = "dead-letter-operator"
OPERATOR_TAGS = ["management"]

# The keys this gate judges, and the ones that only describe the export. The
# broker imports every other key at boot, so each must stay empty or be judged.
JUDGED = {"users", "vhosts", "permissions"}
METADATA = {"rabbit_version", "rabbitmq_version", "product_name", "product_version"}
VHOST = "/"

NAMESPACE = re.compile(r"^namespace\s+([A-Za-z0-9_.]+);", re.M)
TYPE_DECLARATION = re.compile(
    r"^\s*(?:\[(?:[^\[\]\n]|\[[^\[\]\n]*\])*\]\s*)*"
    r"(?:(?:public|internal|private|protected|sealed|static|partial|abstract|readonly|file|new|unsafe|ref)\s+)*"
    r"(?:record\s+(?:struct|class)|record|class|interface|struct|enum)\s+([A-Za-z_]\w*)", re.M)

failures: list[str] = []


def fail(message: str) -> None:
    failures.append(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def matches(pattern: str, resource: str) -> bool:
    """RabbitMQ applies a permission as an unanchored Erlang regex."""
    # An empty pattern is RabbitMQ's spelling of no permission, not a regex matching everything.
    return pattern != "" and re.search(pattern, resource) is not None


def owned_contract(user: str) -> str:
    """`catalog-svc` -> `Common.Contracts.Catalog.V1:`."""
    return f"Common.Contracts.{user[: -len(USER_SUFFIX)].capitalize()}.V1:"


def derived_names(queue: str) -> set[str]:
    """The resources MassTransit derives from a receive endpoint's name.

    The endpoint is a queue and a fanout exchange sharing the name; the delayed
    scheduler adds `<queue>_delay` (ADR-021 — measured, and named after the
    ENDPOINT rather than after the message, which is what makes a per-service
    prefix sufficient); and a faulted or unroutable message goes to
    `<queue>_error` and `<queue>_skipped`, which §13.6 alerts on.
    """
    return {queue, f"{queue}_delay", f"{queue}_error", f"{queue}_skipped"}


def messaging_dirs() -> dict[str, Path]:
    """Every service's and host's Messaging directory, keyed by its tree's name, so the BFF's is bff-svc.

    Globbed, so a service §4.5's scaffold renders is read the day it lands. A
    second directory on one key is refused, or it would replace the first unchecked.
    """
    found: dict[str, Path] = {}
    paths = [*sorted(SERVICES.glob("*/*.Infrastructure/Messaging")), *sorted(HOSTS.glob("*/Messaging"))]
    for path in paths:
        if not path.is_dir():
            continue
        key = path.parents[1].name
        if key in found:
            fail(f"{key}: two Messaging directories key to one account, "
                 f"{found[key].relative_to(found[key].parents[2]).as_posix()} and "
                 f"{path.relative_to(path.parents[2]).as_posix()} — key a host by its project")
            continue
        found[key] = path
    return found


def sends_and_consumes(directory: Path) -> tuple[set[str], set[str]]:
    """What a service SENDS to, and what it RUNS, from its own source.

    `queue:NAME` addresses are peer command endpoints the service drives;
    `…Queue = "NAME"` constants are the receive endpoints it hosts. Both are
    read as text, because this gate runs before anything is restored.
    """
    sends: set[str] = set()
    consumes: set[str] = set()
    for path in sorted(directory.rglob("*.cs")):
        text = read(path)
        sends |= set(re.findall(r'new\("queue:([A-Za-z0-9._-]+)"\)', text))
        consumes |= set(re.findall(r'Queue = "([A-Za-z0-9._-]+)"', text))
    return sends, consumes


def private_namespace(directory: Path) -> str | None:
    """The exchange prefix for a service's own internal messages.

    §9.6's scheduled timeouts are declared in the service's `Messaging`
    namespace rather than in `Common.Contracts`, so they carry a prefix like
    `Ordering.Infrastructure.Messaging:` — the five `*Expired` messages the
    saga sends itself. They are the service's PRIVATE vocabulary: nothing else
    publishes them and nothing else may, or a peer could forge a saga timeout.

    Read from the namespace the source declares rather than assembled from the
    directory name, so a moved or renamed namespace changes this with it.
    """
    for path in sorted(directory.glob("*.cs")):
        found = NAMESPACE.findall(read(path))
        if found:
            return f"{found[0]}:"
    return None


def referenced_contexts(directory: Path, names: dict[str, set[str]]) -> set[str]:
    """Every `Common.Contracts.<Context>.V<n>:` prefix a service's Messaging code names, or names a type of.

    These are the contexts whose exchanges it declares and binds. A type one
    context alone declares counts, so a namespace a global using imports is not missed.
    """
    owners: dict[str, set[str]] = {}
    for prefix, declared in names.items():
        if prefix.startswith("Common.Contracts."):
            for type_name in declared:
                owners.setdefault(type_name, set()).add(prefix)
    found: set[str] = set()
    for path in sorted(directory.rglob("*.cs")):
        code = code_only(read(path), keep_strings=False)
        found |= {f"{match}:" for match in re.findall(r"\bCommon\.Contracts\.[A-Za-z0-9_]+\.V\d+\b", code)}
        for identifier in set(re.findall(r"\b[A-Za-z_]\w*\b", code)):
            if len(owners.get(identifier, ())) == 1:
                found |= owners[identifier]
    return found


def declared_names(paths) -> dict[str, set[str]]:
    """Every type each file declares, keyed by its namespace's exchange prefix.

    MassTransit names a message's exchange `<namespace>:<type>`, so these are
    the real names a foreign write is probed with, beside one invented name.
    """
    names: dict[str, set[str]] = {}
    for path in paths:
        text = read(path)
        for namespace in NAMESPACE.findall(text):
            names.setdefault(f"{namespace}:", set()).update(type_names(text))
    return names


def type_names(text: str) -> set[str]:
    """Each declared type by its exchange name: RabbitMqMessageNameFormatter joins a nested one `Outer-Inner`."""
    code = code_only(text, keep_strings=False)
    found: set[str] = set()
    enclosing: list[tuple[str, int]] = []
    depth = 0
    pending = None
    for match in re.finditer(rf"{TYPE_DECLARATION.pattern}|[{{}};]", code, re.M):
        token = match.group(0)
        if token not in ("{", "}", ";"):
            declared = TYPE_DECLARATION.match(token).group(1)
            pending = "-".join([*(name for name, _ in enclosing[-1:]), declared])
            found.add(pending)
        elif token == "{":
            depth += 1
            if pending:
                enclosing.append((pending, depth))
                pending = None
        elif token == "}":
            if enclosing and enclosing[-1][1] == depth:
                enclosing.pop()
            depth -= 1
        else:
            pending = None
    return found


def probes(prefix: str, names: dict[str, set[str]]) -> list[str]:
    """The resources a write on one namespace is tested against."""
    return [f"{prefix}Anything", *(f"{prefix}{name}" for name in sorted(names.get(prefix, ())))]


def first_covered(pattern: str, resources) -> str | None:
    return next((resource for resource in resources if matches(pattern, resource)), None)


def publishes(service: str) -> bool:
    """Whether a service can publish at all: §4.1 gives it a Domain project.

    One with none raises no event for §9.3's mapper to translate (§3.2), so its
    account is owed no write on any `Common.Contracts` exchange.
    """
    return any((SERVICES / service).glob(f"{service}.Domain/*.csproj"))


def contract_prefixes() -> set[str]:
    """Every `Common.Contracts.<Context>.V1:` exchange prefix, from namespaces."""
    prefixes = set()
    for path in CONTRACTS.rglob("*.cs"):
        for namespace in NAMESPACE.findall(read(path)):
            if namespace.count(".") >= 2:
                prefixes.add(f"{namespace}:")
    return prefixes


def check_definitions_shape(definitions: dict) -> None:
    """The file holds nothing the broker imports and this gate does not judge.

    RabbitMQ keeps one grant per user and vhost, and the checks below key a
    grant by its user, so a second vhost or a second entry would be unread.
    """
    for key, value in sorted(definitions.items()):
        if key not in JUDGED | METADATA and value:
            fail(f"definitions.json: `{key}` is not empty, and this gate judges nothing in it. "
                 f"A binding, policy or shovel imported at boot can route one service's "
                 f"writes into a peer's queue with every permission below unchanged")
    vhosts = [vhost.get("name") for vhost in definitions.get("vhosts") or []]
    if vhosts != [VHOST]:
        fail(f"definitions.json: vhosts are {vhosts}, not exactly `{VHOST}`. The Dockerfile "
             f"keeps one vhost deliberately, and a grant on another is one this gate keys wrongly")
    seen: set[str] = set()
    for entry in definitions.get("permissions") or []:
        if entry.get("vhost") != VHOST:
            fail(f"{entry.get('user')}: holds a grant on vhost `{entry.get('vhost')}`, not `{VHOST}`")
        if entry.get("user") in seen:
            fail(f"{entry.get('user')}: holds two permission entries, and only one is judged")
        seen.add(entry.get("user"))
    named: set[str] = set()
    for user in definitions.get("users") or []:
        if user.get("name") in named:
            fail(f"{user.get('name')}: is declared twice, and only one declaration's tags are judged")
        named.add(user.get("name"))


def main() -> int:
    for path in (DEFINITIONS, WORKFLOW, SERVICES, HOSTS, CONTRACTS, DOCKERFILE, TESTS):
        if not path.exists():
            fail(f"missing: {path.relative_to(ROOT).as_posix()}")
    if failures:
        return report()

    definitions = json.loads(read(DEFINITIONS))
    check_definitions_shape(definitions)
    if failures:
        return report()
    permissions = {entry["user"]: entry for entry in definitions["permissions"]}
    users = {user["name"] for user in definitions["users"]}
    tags = {user["name"]: user.get("tags") or [] for user in definitions["users"]}

    directories = messaging_dirs()
    prefixes = contract_prefixes()
    code = {name: sends_and_consumes(path) for name, path in directories.items()}
    private = {name: private_namespace(path) for name, path in directories.items()}
    names = declared_names([*CONTRACTS.rglob("*.cs"), *(
        path for directory in directories.values() for path in directory.rglob("*.cs"))])
    referenced = {name: referenced_contexts(path, names) & prefixes for name, path in directories.items()}

    # THE GATE'S OWN SUBJECT, before anything relies on it. A scan that found
    # nothing would agree with any permission set at all, which is this
    # repository's most-repeated failure pointed at its newest surface.
    if not directories:
        fail("src/Services: found no */*.Infrastructure/Messaging directory — the glob")
    if not prefixes:
        fail("Common.Contracts: found no versioned namespaces — the pattern")
    if not users:
        fail("definitions.json declares no users")
    if not any(sends for sends, _ in code.values()):
        fail("no service declares a `queue:` address — the pattern, not the source")
    if not any(consumes for _, consumes in code.values()):
        fail("no service declares a receive endpoint — the pattern, not the source")
    if not any(private.values()):
        fail("no service's Messaging namespace could be read — the pattern, not the "
             "source. Every private-vocabulary check below would pass vacuously")
    if not any(referenced.values()):
        fail("no service's Messaging code names a Common.Contracts context — the pattern, not the "
             "source. Every configure and read bound below would refuse the contexts consumed")
    for prefix in sorted(prefixes | set(filter(None, private.values()))):
        if not names.get(prefix):
            fail(f"`{prefix}` declares no type the pattern can read — the pattern, not the "
                 f"source. A write naming one of its messages would be probed with none")
    if failures:
        return report()

    services = sorted(user for user in users if user.endswith(USER_SUFFIX))
    if not services:
        fail(f"definitions.json declares no `*{USER_SUFFIX}` accounts — the naming "
             f"convention this gate derives ownership from, not the file")
    for user in services:
        if user not in permissions:
            fail(f"definitions.json declares user {user} with no permissions")
    for user in permissions:
        if user not in users:
            fail(f"definitions.json grants permissions to {user}, which is not a user")
    if failures:
        return report()

    # `guest` is not a user here. RabbitMQ seeds it only when it boots with an
    # empty database and skips that when definitions are imported, so its
    # absence from this file is what removes it — an entry would put back the
    # single shared principal #44 is about.
    if "guest" in users:
        fail("definitions.json declares `guest`. That account is what #44 is about — "
             "one principal, tagged administrator, reachable from any container")
    for user, held in sorted(tags.items()):
        if held and not (user == OPERATOR and held == OPERATOR_TAGS):
            fail(f"{user}: carries tags {held}. A service account needs none, and "
                 f"`administrator` is what made `guest` worth stealing")

    for user in sorted(users - set(services) - {OPERATOR, "guest"}):
        fail(f"{user}: is neither a `*{USER_SUFFIX}` service account nor {OPERATOR}, "
             f"so nothing here derives what it may touch")

    # Every service with a broker account has source, and every service with
    # source has a broker account. Both directions, because a service the
    # scaffold rendered and nobody granted cannot connect at all, and an
    # account for a service that no longer exists is a live credential nothing
    # uses.
    named = {name.lower() for name in directories}
    for user in services:
        if user[: -len(USER_SUFFIX)] not in named:
            fail(f"{user}: has broker permissions and no messaging source under "
                 f"src/Services or src/BFF. Delete the account or restore the source")
    for name in sorted(directories):
        if f"{name.lower()}{USER_SUFFIX}" not in users:
            fail(f"{name}: has messaging source and no broker account in "
                 f"definitions.json, so it cannot authenticate at all (#44)")
    if failures:
        return report()

    for user in services:
        entry = permissions[user]
        name = user[: -len(USER_SUFFIX)]
        sends, consumes = code[next(k for k in directories if k.lower() == name)]

        # 1. It may drive every queue its own code addresses. `write` is the
        #    publish; `read` is needed too, because MassTransit declares and
        #    BINDS the destination and `queue.bind` takes read on the exchange
        #    — measured, as a refusal on `inventory-commands` with write
        #    already granted.
        for queue in sorted(sends):
            for verb in ("configure", "write", "read"):
                if not matches(entry[verb], queue):
                    fail(f"{user}: {verb} does not cover `{queue}`, which its own "
                         f"Endpoints addresses. The send is refused and MassTransit "
                         f"retries the topology for ever, service healthy and silent")

        # 2. It may run every receive endpoint it declares, and the three names
        #    MassTransit derives from each.
        for queue in sorted(consumes):
            for derived in sorted(derived_names(queue)):
                for verb in ("configure", "write", "read"):
                    if not matches(entry[verb], derived):
                        fail(f"{user}: {verb} does not cover `{derived}`, derived from "
                             f"its receive endpoint `{queue}`")

        # 3. It declares and writes the framework's fault exchanges and, if it
        #    publishes, the polymorphic interface exchange.
        service = next(k for k in directories if k.lower() == name)
        owed = (f"{FRAMEWORK_PREFIX}ReceiveFault", *((INTERFACE_EXCHANGE,) if publishes(service) else ()))
        for resource in owed:
            for verb in ("configure", "write"):
                if not matches(entry[verb], resource):
                    fail(f"{user}: {verb} does not cover `{resource}`")

        # 3b. A service that publishes nothing writes no contract exchange at
        #     all, the interface one and its own context's included (ADR-036).
        if not publishes(service):
            covered = (first_covered(entry["write"], [INTERFACE_EXCHANGE]),
                       first_covered(entry["write"], probes(owned_contract(user), names)))
            for resource in filter(None, covered):
                fail(f"{user}: write COVERS `{resource}`, and the service has no Domain "
                     f"project to publish from (§4.1, §3.2)")
            if matches(entry["configure"], INTERFACE_EXCHANGE):
                fail(f"{user}: configure COVERS `{INTERFACE_EXCHANGE}`, which only a publisher "
                     f"declares, and the service has no Domain project to publish from (§4.1)")

        # 3c. Nobody reads the interface exchange: it is only ever a binding's
        #     destination, and a queue bound to it receives every context's events.
        if matches(entry["read"], INTERFACE_EXCHANGE):
            fail(f"{user}: read COVERS `{INTERFACE_EXCHANGE}`. A queue of its own bound there "
                 f"receives every integration event of every context")

        # 3d. A fault's publish binds `ReceiveFault` and each `Fault--<type>--` to the
        #     root `Fault` interface, so read is owed on those sources and never on the root.
        consumed = referenced[service] | ({owned_contract(user)} & prefixes) | ({private[service]} - {None})
        sources = [f"{FRAMEWORK_PREFIX}ReceiveFault", *(fault_of(prefix) for prefix in sorted(consumed))]
        for resource in sources:
            if not matches(entry["read"], resource):
                fail(f"{user}: read does not cover `{resource}`, the source of the binding a "
                     f"fault's publish declares, so the fault is refused (§13.6)")
        if matches(entry["read"], FAULT_ROOT):
            fail(f"{user}: read COVERS `{FAULT_ROOT}`, which no fault's publish binds from. A "
                 f"queue of its own bound there receives every service's faults")
        if matches(entry["read"], f"{FRAMEWORK_PREFIX}Anything"):
            fail(f"{user}: read COVERS `{FRAMEWORK_PREFIX}Anything`, beyond the fault exchanges "
                 f"a fault's publish binds from")

        # 4. It may publish its OWN context's contracts — WHERE IT HAS ANY.
        #
        # A service §4.5's scaffold renders has a broker account and no
        # contracts: `Common.Contracts` gains a record in the PR whose code
        # publishes or consumes it, never in the one that renders the service.
        # So a missing namespace is the ordinary early state rather than a
        # defect, and the grant it makes unusable is a write on an exchange
        # nothing declares.
        #
        # The vacuity this skip could hide is caught once, globally, below.
        owned = owned_contract(user)
        if owned in prefixes:
            # Read too: the publisher binds each contract to the interface exchange, which takes read on the source.
            for verb in ("configure", "write", "read"):
                if not matches(entry[verb], f"{owned}Anything"):
                    fail(f"{user}: {verb} does not cover its own contracts `{owned}`")

        # 4b. It may declare and bind every context its Messaging code names.
        for prefix in sorted(referenced[service]):
            for verb in ("configure", "read"):
                missing = next((r for r in probes(prefix, names) if not matches(entry[verb], r)), None)
                if missing:
                    fail(f"{user}: {verb} does not cover `{missing}`, in `{prefix}`, which its "
                         f"Messaging code names. Its consumer cannot declare or bind the exchange, "
                         f"and MassTransit retries the topology for ever, service healthy and silent")

    # The derivation's own subject, asserted once rather than per service. If
    # check 4 skipped EVERY service the `*-svc` -> `Common.Contracts.<Name>.V1:`
    # rule would be broken and nobody would hear about it; a single service
    # legitimately skipping it cannot hide that.
    if not any(owned_contract(user) in prefixes for user in services):
        fail(f"no service's account resolves to a namespace under Common.Contracts. "
             f"The `*{USER_SUFFIX}` naming convention this gate derives ownership "
             f"from is broken, and check 4 has been passing vacuously for all of "
             f"{services}")

    if failures:
        return report()

    # 5. #44'S PROPERTY, and the reason this is a gate rather than a comment.
    #    A service may not write another service's command endpoint, nor
    #    another context's contracts. What makes a peer's queue legitimate is
    #    the service's OWN source addressing it — the saga orchestrates
    #    Inventory and Payments, and that is visible in `Endpoints.cs` rather
    #    than asserted here.
    every_endpoint: set[str] = set()
    for sends, consumes in code.values():
        every_endpoint |= sends | consumes

    for user in services:
        entry = permissions[user]
        name = user[: -len(USER_SUFFIX)]
        sends, _ = code[next(k for k in directories if k.lower() == name)]
        # A delay exchange republishes into its queue, so a foreign endpoint's derived names are its own.
        # A peer queue the source sends to is owed by check 1, and none of the names derived from it.
        for queue in sorted(every_endpoint):
            if queue.startswith(f"{name}-"):
                continue
            foreign = sorted(derived_names(queue) - sends)
            for verb in ("configure", "write", "read"):
                resource = first_covered(entry[verb], foreign)
                if resource:
                    fail(f"{user}: {verb} COVERS `{resource}`, which is neither its own nor "
                         f"addressed by its source. Configure deletes it, write puts another "
                         f"service's business command on it, and read consumes it (#44)")

        service = next(k for k in directories if k.lower() == name)
        for prefix in sorted(prefixes - {owned_contract(user)}):
            if prefix == f"{INTERFACE_EXCHANGE.split(':')[0]}:":
                continue
            resource = first_covered(entry["write"], probes(prefix, names))
            if resource:
                fail(f"{user}: write COVERS `{resource}`, another context's contracts "
                     f"`{prefix}`. A service that can publish a peer's events can forge them")
            if prefix in referenced[service]:
                continue
            for verb in ("configure", "read"):
                resource = first_covered(entry[verb], probes(prefix, names))
                if resource:
                    fail(f"{user}: {verb} COVERS `{resource}`, in `{prefix}`, which its Messaging "
                         f"code never names. Configure deletes that exchange, and read binds a "
                         f"queue of its own to it and receives the context's events")

        # AND NOBODY ELSE'S PRIVATE VOCABULARY. `Common.Contracts` is the
        # published half; a service also owns messages nothing outside it ever
        # sends — §9.6's `*Expired` saga timeouts live in Ordering's own
        # Messaging namespace. Without this, granting Catalog
        # `Ordering.Infrastructure.Messaging:` passed every check above while
        # letting it forge a saga timeout, which is #44's own class of defect
        # one namespace over. Found by Copilot on PR #160 and reproduced before
        # it was believed.
        mine = private.get(next(k for k in directories if k.lower() == name))
        for owner, prefix in sorted(private.items()):
            if not prefix or prefix == mine:
                continue
            for verb in ("configure", "write", "read"):
                resource = first_covered(entry[verb], probes(prefix, names))
                if resource:
                    fail(f"{user}: {verb} COVERS `{resource}`, in {owner}'s private "
                         f"messaging vocabulary. Nothing outside that service declares, "
                         f"publishes or consumes those messages, and a peer that can is a "
                         f"peer that can forge or swallow a scheduled timeout (§9.6)")

    check_operator(definitions, permissions, code, prefixes, private, names)

    # 6. The two ways the broker's configuration reaches a container agree.
    check_fixture_matches_dockerfile()

    check_source_inputs_covers_reads()
    check_workflow_covers_inputs()
    return report()


def check_operator(definitions: dict, permissions: dict, code: dict, prefixes: set[str], private: dict,
                   names: dict[str, set[str]]) -> None:
    """The dead-letter operator's grant: dead letters and the endpoints they replay to, and nothing else."""
    account = next((user for user in definitions["users"] if user["name"] == OPERATOR), None)
    if account is None or OPERATOR not in permissions:
        fail(f"definitions.json declares no {OPERATOR} with permissions, the account "
             f"tools/dead-letters reaches the broker as")
        return
    if account.get("password_hash"):
        fail(f"{OPERATOR}: carries a password hash. It ships with none, so nothing logs in "
             f"as it until an operator sets one (tools/dead-letters/README.md)")

    entry = permissions[OPERATOR]
    endpoints = sorted(set().union(*(consumes for _, consumes in code.values())))
    # Somebody else's vocabulary, and the default exchange, whose write reaches every queue by name.
    foreign = [[f"{FRAMEWORK_PREFIX}ReceiveFault"], [INTERFACE_EXCHANGE], ["amq.default"],
               *(probes(prefix, names) for prefix in sorted(prefixes)),
               *(probes(prefix, names) for prefix in sorted(filter(None, private.values())))]
    for queue in endpoints:
        dead = [f"{queue}_error", f"{queue}_skipped"]
        for resource in dead:
            if not matches(entry["read"], resource):
                fail(f"{OPERATOR}: read does not cover `{resource}`, so the tool cannot inspect it")
        for resource in (queue, *dead):
            if not matches(entry["write"], resource):
                fail(f"{OPERATOR}: write does not cover `{resource}`, where a replay or a "
                     f"returned message lands")
        for resource in (queue, f"{queue}_delay"):
            if matches(entry["read"], resource):
                fail(f"{OPERATOR}: read COVERS `{resource}`, a live queue. The tool reads dead "
                     f"letters, and a read here is a consume of the endpoint's own work")
        foreign.append([f"{queue}_delay"])
        for resource in derived_names(queue):
            if matches(entry["configure"], resource):
                fail(f"{OPERATOR}: configure COVERS `{resource}`. The tool declares nothing, "
                     f"so it may delete nothing")
    for verb in ("configure", "write", "read"):
        for group in foreign:
            resource = first_covered(entry[verb], group)
            if resource:
                fail(f"{OPERATOR}: {verb} COVERS `{resource}`, which no dead-letter move needs")


# A builder alone is not the broker's: a fixture that builds a stub service's
# image while pulling a stock broker would otherwise be excused from mapping
# the configuration its own broker never loads. The chain has to name §14.1's
# context, and it is read within one statement, so a builder elsewhere in the
# file cannot stand in for this one.
BUILDER = re.compile(r"\bnew\s+ImageFromDockerfileBuilder\s*\(")
BROKER_CONTEXT = re.compile(
    r"\bWithDockerfileDirectory\s*\(\s*BrokerContextPath\s*\(\s*\)\s*\)")

# A tagged broker image is the stock route, whatever else the file builds: one
# fixture may hold both routes, and its builder cannot excuse the stock image
# from mapping the configuration. The tag is a literal or the Dockerfile's base,
# read through ComposeImage.BaseOf.
STOCK_IMAGE = re.compile(
    r'\bWithImage\s*\(\s*(?:"rabbitmq:|ComposeImage\s*\.\s*BaseOf\s*\(\s*"rabbitmq"\s*\))')


def _string_end(text: str, start: int) -> int:
    """The index just past the literal opening at `start`."""
    limit = len(text)
    i = start
    verbatim = False
    while i < limit and text[i] in "@$":
        verbatim = verbatim or text[i] == "@"
        i += 1
    opening = 0
    while i < limit and text[i] == '"':
        opening += 1
        i += 1
    if opening >= 3:
        # A raw literal ends on a run of quotes at least as long as its own.
        run = 0
        while i < limit:
            run = run + 1 if text[i] == '"' else 0
            i += 1
            if run >= opening:
                return i
        return limit
    if opening != 1:
        return i
    while i < limit:
        if text[i] == "\\" and not verbatim:
            i += 2
        elif text[i] != '"':
            i += 1
        elif verbatim and i + 1 < limit and text[i + 1] == '"':
            i += 2
        else:
            return i + 1
    return limit


def _char_end(text: str, start: int) -> int:
    """The index just past the char literal opening at `start`."""
    limit = len(text)
    i = start + 1
    while i < limit:
        if text[i] == "\\":
            i += 2
        elif text[i] == "'":
            return i + 1
        else:
            i += 1
    return limit


def _opens_string(text: str, start: int) -> bool:
    """Whether the `@` or `$` at `start` prefixes a literal."""
    i = start
    while i < len(text) and text[i] in "@$":
        i += 1
    return i < len(text) and text[i] == '"'


def code_only(text: str, *, keep_strings: bool = True) -> str:
    """The fixture with its comments dropped, and its literals on request.

    The mapping regex reads the container paths out of a literal and needs
    them kept; a search for a call wants them masked, because a literal
    spelling a call out is not one. The scan runs left to right because a
    pattern cannot tell a char literal from the start of a string: a lone
    quote inside one opens a string that runs to the next quote in the file,
    and inverts which half of it counts as code.
    """
    out = []
    limit = len(text)
    i = held = 0
    while i < limit:
        here = text[i]
        if here == "/" and i + 1 < limit and text[i + 1] in "/*":
            if text[i + 1] == "/":
                end = text.find("\n", i)
                end = limit if end < 0 else end
            else:
                end = text.find("*/", i + 2)
                end = limit if end < 0 else end + 2
            out.append(text[held:i])
            out.append(" ")
            i = held = end
        elif here == "'":
            end = _char_end(text, i)
            out.append(text[held:i])
            # Masked either way: nothing reads a char literal's content, and a
            # quote inside one is what breaks a pattern that tries.
            out.append("' '")
            i = held = end
        elif here == '"' or (here in "@$" and _opens_string(text, i)):
            end = _string_end(text, i)
            out.append(text[held:i])
            out.append(text[i:end] if keep_strings else '""')
            i = held = end
        else:
            i += 1
    out.append(text[held:])
    return "".join(out)


def builds_the_broker(code: str) -> bool:
    """Whether one statement builds §14.1's image from its own context."""
    for match in BUILDER.finditer(code):
        end = code.find(";", match.end())
        statement = code[match.end():end if end >= 0 else len(code)]
        if BROKER_CONTEXT.search(statement):
            return True
    return False


def broker_fixtures() -> list[Path]:
    """Every test file that starts a broker, found by what it constructs."""
    return sorted(
        path for path in TESTS.rglob("*.cs")
        if BROKER_CONTAINER.search(code_only(read(path), keep_strings=False)))


def check_fixture_matches_dockerfile() -> None:
    """The configuration files reach the broker two ways; they must agree.

    §14.1's image COPYs the definitions and the configuration into place, and
    a fixture that maps them onto the stock image instead carries a second
    copy of those paths (ADR-036). Drift between the two does not fail
    loudly: the broker boots with none of the definitions, and the suite
    passes against a default account holding every permission — green, and
    testing nothing. A fixture that neither maps nor builds is that same
    broker, so it fails here rather than passing for want of a mapping.
    """
    dockerfile = read(DOCKERFILE)

    declared = len(re.findall(r"^COPY\b", dockerfile, re.M))
    copies = dict(re.findall(r"^COPY\s+(\S+)\s+(\S+)\s*$", dockerfile, re.M))
    if not copies:
        fail("Dockerfile: no COPY lines found — the pattern, not the file")
        return
    if len(copies) != declared:
        fail(f"Dockerfile: {declared} COPY line(s) and {len(copies)} read — the "
             f"pattern, not the file. A line it cannot parse is a file nothing "
             f"asks a fixture to map")
        return

    fixtures = broker_fixtures()
    if not fixtures:
        fail(f"tests/: no file constructs a broker — the search, not the tree. "
             f"Check 6 has no subject and would otherwise report a pass over nothing")
        return

    mapping = {}
    for path in fixtures:
        name = path.relative_to(ROOT).as_posix()
        raw = read(path)
        text = code_only(raw)
        mapped = dict(re.findall(
            r'WithResourceMapping\(\s*new FileInfo\(Path\.Combine\(BrokerContextPath\(\),\s*"([^"]+)"\)\),\s*"([^"]+)"',
            text))
        if mapped:
            mapping[name] = mapped
        elif not builds_the_broker(code_only(raw, keep_strings=False)):
            fail(f"{name}: maps none of the broker's configuration and builds no "
                 f"image from its context either, so its broker starts with none of "
                 f"the definitions — no vhost, no per-service account, nothing to "
                 f"enforce")
        elif STOCK_IMAGE.search(text):
            fail(f"{name}: starts the stock broker image and maps none of the "
                 f"broker's configuration. The image it builds elsewhere does not "
                 f"reach that route, so its broker starts with none of the definitions")

    if not mapping:
        fail(f"no fixture under tests/ maps the broker's configuration. Either every "
             f"one of them now builds the image — in which case ADR-036's stock-image "
             f"route is gone — or this pattern went stale")
        return

    for name, mapped in sorted(mapping.items()):
        for source, target in sorted(copies.items()):
            if source not in mapped:
                fail(f"Dockerfile COPYs `{source}` into the broker image and {name} "
                     f"does not map it. That fixture runs the STOCK image, so a file "
                     f"only the Dockerfile carries is a file its broker does not have")
                continue
            # WithResourceMapping names the directory and keeps the file's own
            # name, so the path it lands at is rebuilt rather than trimmed off
            # the Dockerfile's: a COPY that renames is drift the directory alone
            # cannot show.
            want = mapped[source].rstrip("/") + "/" + source.rsplit("/", 1)[-1]
            if want != target:
                fail(f"`{source}`: the Dockerfile puts it at `{target}` and {name} "
                     f"puts it at `{want}`. One of the two brokers is not reading it")

        for source in sorted(set(mapped) - set(copies)):
            fail(f"{name} maps `{source}` and the Dockerfile does not COPY it, so the "
                 f"Compose broker and that test broker disagree about what they hold")


def check_source_inputs_covers_reads() -> None:
    """SOURCE_INPUTS against the reads it claims to enumerate, not the workflow.

    `deploy/canary/canary.py` declared two paths and opened three with its
    trigger assertion green throughout: a list can only be checked for entries
    it already contains, so a read nobody declared is invisible from both
    sides. The subject here is this file's own source.
    """
    source = read(Path(__file__))

    reads = set()
    for match in re.findall(r'ROOT(?:\s*/\s*"[A-Za-z0-9._-]+")+', source):
        segments = re.findall(r'"([A-Za-z0-9._-]+)"', match)
        if segments:
            reads.add("/".join(segments))

    if not reads:
        fail("check_permissions.py: found no ROOT-relative reads in its own source — "
             "the scan is broken, not the list")
        return

    declared = SOURCE_INPUTS + ["deploy/compose/rabbitmq", WORKFLOW_PATH]
    for entry in sorted(reads):
        if not any(entry == path or entry.startswith(f"{path}/") for path in declared):
            fail(f"check_permissions.py opens `{entry}` and SOURCE_INPUTS does not "
                 f"declare it, so broker-permissions.yml's filters do not watch it: "
                 f"{SOURCE_INPUTS}")


def trigger_paths(text: str, trigger: str) -> list[str] | None:
    match = re.search(rf"^  {trigger}:\n(.*?)(?=^  \w|\Z)", text, re.S | re.M)
    if not match:
        return None
    return re.findall(r"^\s*-\s*'([^']+)'", match.group(1), re.M)


def covers(path: str, entry: str) -> bool:
    """Does one `paths:` glob cover the WHOLE of an input this gate reads?

    Only `/**` covers a directory — GitHub's `*` does not cross a separator —
    and the direction matters: a glob covers an entry when the glob's literal
    prefix is the entry or an ancestor of it, never the other way round. An
    earlier copy of this had it backwards and read `src/Services/Ordering/**`
    as covering `src`, approving a filter that skips every other service.
    """
    if path == "**":
        return True
    if path.endswith("/**"):
        prefix = path[: -len("/**")].rstrip("/")
        return bool(prefix) and (entry == prefix or entry.startswith(prefix + "/"))
    if "*" in path:
        return False
    return entry == path


def check_workflow_covers_inputs() -> None:
    text = read(WORKFLOW)

    for name in ("push", "pull_request"):
        paths = trigger_paths(text, name)
        if paths is None:
            fail(f"{WORKFLOW.name}: no `{name}` trigger — the gate must run on both")
            continue
        if not paths:
            fail(f"{WORKFLOW.name}: the `{name}` trigger lists no paths — the parser, or the file")
            continue

        for entry in SOURCE_INPUTS + ["deploy/compose/rabbitmq", WORKFLOW_PATH]:
            if not any(covers(path, entry) for path in paths):
                fail(f"{WORKFLOW.name}: the `{name}` trigger does not cover `{entry}`, "
                     f"which check_permissions.py reads. A change to it would skip this gate")


def report() -> int:
    if failures:
        print("broker permission gate: FAILED", file=sys.stderr)
        for message in failures:
            print(f"  - {message}", file=sys.stderr)
        return 1
    print("broker permission gate: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
