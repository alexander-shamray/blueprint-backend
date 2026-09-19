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
CONTRACTS = ROOT / "src" / "BuildingBlocks" / "Common.Contracts"
DOCKERFILE = HERE / "Dockerfile"
CATALOG_FIXTURE = ROOT / "tests" / "Catalog.TestSupport" / "ServiceFixture.cs"

# EVERY PATH OUTSIDE deploy/compose/rabbitmq THAT THIS SCRIPT READS, declared
# once, on deploy/helm/smoke.sh's terms and check.py's. The workflow's filters
# must cover each, or a change to one is a green pull request that skips the
# gate watching it — and docs/lessons.md records that a fourth copy of this pattern
# arrives owing a test whose subject is the READS rather than the workflow,
# because a list can only be compared for entries it already contains. That is
# check 7 here.
SOURCE_INPUTS = [
    "src/Services",
    "src/BuildingBlocks/Common.Contracts",
    "tests/Catalog.TestSupport",
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

# The polymorphic publish exchange. MassTransit binds each concrete contract
# exchange to one exchange per interface the message implements, and the
# SENDER declares that binding: `exchange.bind` takes write on the destination
# — this exchange — and read on the source, which is the concrete contract. So
# a publisher declares and writes it, and the check below asks for no more.
#
# A consumer needs no read here, which is why a grant that enumerates
# contract prefixes rather than naming `Common\.Contracts` whole is not thereby
# too narrow. Measured on a real broker running these definitions with a
# consumer bound: this exchange appears in exactly one binding and appears
# there as the DESTINATION, while the receive endpoint binds to the concrete
# contract exchange — which is the read a consumer does need, on its peer's
# versioned prefix.
INTERFACE_EXCHANGE = "Common.Contracts:IIntegrationEvent"

failures: list[str] = []


def fail(message: str) -> None:
    failures.append(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def matches(pattern: str, resource: str) -> bool:
    """RabbitMQ applies a permission as an unanchored Erlang regex."""
    return re.search(pattern, resource) is not None


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
    """Every service's Messaging directory, keyed by the service's name.

    Globbed rather than listed, so a service §4.5's scaffold renders tomorrow
    is read by this gate on the day it lands.
    """
    found = {}
    for path in sorted(SERVICES.glob("*/*.Infrastructure/Messaging")):
        if path.is_dir():
            found[path.parents[1].name] = path
    return found


def sends_and_consumes(directory: Path) -> tuple[set[str], set[str]]:
    """What a service SENDS to, and what it RUNS, from its own source.

    `queue:NAME` addresses are peer command endpoints the service drives;
    `…Queue = "NAME"` constants are the receive endpoints it hosts. Both are
    read as text, because this gate runs before anything is restored.
    """
    sends: set[str] = set()
    consumes: set[str] = set()
    for path in sorted(directory.glob("*.cs")):
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
        found = re.findall(r"^namespace\s+([A-Za-z0-9_.]+);", read(path), re.M)
        if found:
            return f"{found[0]}:"
    return None


def contract_prefixes() -> set[str]:
    """Every `Common.Contracts.<Context>.V1:` exchange prefix, from namespaces."""
    prefixes = set()
    for path in CONTRACTS.rglob("*.cs"):
        for namespace in re.findall(r"^namespace\s+([A-Za-z0-9_.]+);", read(path), re.M):
            if namespace.count(".") >= 2:
                prefixes.add(f"{namespace}:")
    return prefixes


def main() -> int:
    for path in (DEFINITIONS, WORKFLOW, SERVICES, CONTRACTS, DOCKERFILE, CATALOG_FIXTURE):
        if not path.exists():
            fail(f"missing: {path.relative_to(ROOT).as_posix()}")
    if failures:
        return report()

    definitions = json.loads(read(DEFINITIONS))
    permissions = {entry["user"]: entry for entry in definitions["permissions"]}
    users = {user["name"] for user in definitions["users"]}
    tags = {user["name"]: user.get("tags") or [] for user in definitions["users"]}

    directories = messaging_dirs()
    prefixes = contract_prefixes()
    code = {name: sends_and_consumes(path) for name, path in directories.items()}
    private = {name: private_namespace(path) for name, path in directories.items()}

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
        if held:
            fail(f"{user}: carries tags {held}. A service account needs none, and "
                 f"`administrator` is what made `guest` worth stealing")

    # Every service with a broker account has source, and every service with
    # source has a broker account. Both directions, because a service the
    # scaffold rendered and nobody granted cannot connect at all, and an
    # account for a service that no longer exists is a live credential nothing
    # uses.
    named = {name.lower() for name in directories}
    for user in services:
        if user[: -len(USER_SUFFIX)] not in named:
            fail(f"{user}: has broker permissions and no service under src/Services. "
                 f"Delete the account or restore the service")
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

        # 3. It declares and writes the framework's fault exchanges and the
        #    polymorphic interface exchange.
        for resource in (f"{FRAMEWORK_PREFIX}ReceiveFault", INTERFACE_EXCHANGE):
            for verb in ("configure", "write"):
                if not matches(entry[verb], resource):
                    fail(f"{user}: {verb} does not cover `{resource}`")

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
            for verb in ("configure", "write"):
                if not matches(entry[verb], f"{owned}Anything"):
                    fail(f"{user}: {verb} does not cover its own contracts `{owned}`")

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

        for queue in sorted(every_endpoint - sends):
            if queue.startswith(f"{name}-"):
                continue
            if matches(entry["write"], queue):
                fail(f"{user}: write COVERS `{queue}`, which is neither its own nor "
                     f"addressed by its source. Broker write access would again be "
                     f"sufficient to execute another service's business command (#44)")

        for prefix in sorted(prefixes - {owned_contract(user)}):
            if prefix == f"{INTERFACE_EXCHANGE.split(':')[0]}:":
                continue
            if matches(entry["write"], f"{prefix}Anything"):
                fail(f"{user}: write COVERS `{prefix}`, another context's contracts. "
                     f"A service that can publish a peer's events can forge them")

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
            if matches(entry["write"], f"{prefix}Anything"):
                fail(f"{user}: write COVERS `{prefix}`, which is {owner}'s private "
                     f"messaging vocabulary. Nothing outside that service publishes "
                     f"those messages, and a peer that can is a peer that can forge "
                     f"a scheduled timeout (§9.6)")

    # 6. The two ways the broker's configuration reaches a container agree.
    check_fixture_matches_dockerfile()

    check_source_inputs_covers_reads()
    check_workflow_covers_inputs()
    return report()


def check_fixture_matches_dockerfile() -> None:
    """The configuration files reach the broker two ways; they must agree.

    §14.1's image COPYs `definitions.json` and `20-commerce.conf` into place.
    Catalog's fixture does NOT build that image — it maps the same two files
    onto the stock one, because it needs the accounts and not ADR-021's plugin,
    and because building put two test processes in a race for one build-context
    tar.

    **That makes the container paths a second copy**, and a second copy is what
    this repository keeps having to gate. A mapping that drifted would not fail
    loudly: RabbitMQ boots without the definitions, seeds `guest` on an empty
    database, and Catalog's suite passes against the single shared
    administrator #44 exists to remove — green, and testing nothing.
    """
    dockerfile = read(DOCKERFILE)
    fixture = read(CATALOG_FIXTURE)

    copies = dict(re.findall(r"^COPY\s+(\S+)\s+(\S+)\s*$", dockerfile, re.M))
    if not copies:
        fail("Dockerfile: no COPY lines found — the pattern, not the file")
        return

    mapped = dict(re.findall(
        r'WithResourceMapping\(\s*new FileInfo\(Path\.Combine\(BrokerContextPath\(\),\s*"([^"]+)"\)\),\s*"([^"]+)"',
        fixture))
    if not mapped:
        fail("Catalog.TestSupport/ServiceFixture.cs: no WithResourceMapping calls found. "
             "Either the fixture stopped mapping the broker's configuration — in which "
             "case it runs a broker with no accounts — or this pattern went stale")
        return

    for source, target in sorted(copies.items()):
        if source not in mapped:
            fail(f"Dockerfile COPYs `{source}` into the broker image and Catalog's "
                 f"fixture does not map it. That fixture runs the STOCK image, so a "
                 f"file only the Dockerfile carries is a file its broker does not have")
            continue
        # The Dockerfile names the file; WithResourceMapping names the directory.
        want = target.rsplit("/", 1)[0] + "/"
        if mapped[source] != want:
            fail(f"`{source}`: the Dockerfile puts it at `{target}` and Catalog's "
                 f"fixture maps it into `{mapped[source]}`. One of the two brokers is "
                 f"not reading it")

    for source in sorted(set(mapped) - set(copies)):
        fail(f"Catalog's fixture maps `{source}` and the Dockerfile does not COPY it, "
             f"so the Compose broker and the test broker disagree about what they hold")


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
