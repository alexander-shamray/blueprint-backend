"""What a run writes: the service's projects and its Compose unit, and the
edits to the shared files that have to name it.

Every function here returns text and none of them touches disk; `apply` in
`new_service.py` is the one writer.
"""

from __future__ import annotations

import re
from datetime import datetime, timedelta
from pathlib import Path, PurePosixPath

from scaffold import TEMPLATE, Names, ScaffoldError, read, require_once, restore
from scaffold.patch import (
    IDEMPOTENCY_MIGRATION_PATCHES,
    INBOX_MIGRATION_PATCHES,
    INITIAL_CREATE_PATCHES,
    OUTBOX_MIGRATION_PATCHES,
    PATCHES,
    RETENTION_INDEX_MIGRATION_PATCHES,
)

# The five service projects §4.1 gives a service, its three test projects, and
# Catalog.TestSupport — which §4.1 is explicit is NOT a test project, and which
# is copied all the same: the fixture is the template's, and its second consumer
# arrives with the new service's first handler test, exactly as it did for
# Catalog in PR-10. Nine projects, not "five and four".
COPY_ROOTS = (
    "src/Services/Catalog",
    "tests/Catalog.Domain.Tests",
    "tests/Catalog.Application.Tests",
    "tests/Catalog.Api.Tests",
    "tests/Catalog.TestSupport",
)

MIGRATIONS = "src/Services/Catalog/Catalog.Infrastructure/Persistence/Migrations"

# Every file under COPY_ROOTS is classified here or the run fails. That is
# deliberate friction, and it is the same argument the domain allow-list gate
# makes in Catalog.Domain.Tests: extending the list is the decision the check
# exists to force. Without it, the next aggregate someone adds to Catalog ships
# silently into every service scaffolded afterwards, and no straggler check
# would notice — a Categories folder carries none of the tokens the scaffold
# searches for.
COPIED = frozenset(
    {
        "src/Services/Catalog/Catalog.Api/Catalog.Api.csproj",
        "src/Services/Catalog/Catalog.Api/Dockerfile",
        "src/Services/Catalog/Catalog.Api/Program.cs",
        "src/Services/Catalog/Catalog.Application/Catalog.Application.csproj",
        "src/Services/Catalog/Catalog.Application/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs",
        "src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj",
        "src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj",
        "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Messaging/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Infrastructure/SqlConnectionFactory.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfDomainEventCollector.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxPublisher.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfUnitOfWork.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/InboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/IdempotencyMarkerConfiguration.cs",
        "src/Services/Catalog/Catalog.Migrator/Catalog.Migrator.csproj",
        "src/Services/Catalog/Catalog.Migrator/Dockerfile",
        "src/Services/Catalog/Catalog.Migrator/MigrationRunner.cs",
        "src/Services/Catalog/Catalog.Migrator/MigratorHost.cs",
        "src/Services/Catalog/Catalog.Migrator/Program.cs",
        "tests/Catalog.Domain.Tests/ArchitectureTests.cs",
        "tests/Catalog.Domain.Tests/Catalog.Domain.Tests.csproj",
        "tests/Catalog.Application.Tests/ArchitectureTests.cs",
        "tests/Catalog.Application.Tests/Catalog.Application.Tests.csproj",
        "tests/Catalog.Application.Tests/DependencyInjectionTests.cs",
        "tests/Catalog.Application.Tests/IdempotencyOptInTests.cs",
        "tests/Catalog.Api.Tests/ArchitectureTests.cs",
        "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj",
        "tests/Catalog.Api.Tests/DatabaseSmokeTests.cs",
        "tests/Catalog.Api.Tests/HostSmokeTests.cs",
        "tests/Catalog.Api.Tests/IdempotencyMarkerTests.cs",
        "tests/Catalog.Api.Tests/IntegrationCollection.cs",
        "tests/Catalog.Api.Tests/MessageTypeMapValidatorTests.cs",
        "tests/Catalog.Api.Tests/MessagingRegistrationTests.cs",
        "tests/Catalog.Api.Tests/InboxFilterTests.cs",
        "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs",
        "tests/Catalog.Api.Tests/RetentionPurgeTests.cs",
        "tests/Catalog.Api.Tests/TransientFaultInjection.cs",
        "tests/Catalog.TestSupport/Catalog.TestSupport.csproj",
        "tests/Catalog.TestSupport/CatalogApiFactory.cs",
        # §12.4's test scheme. Copied rather than omitted even though a
        # scaffolded service has no endpoint to authorise: CatalogApiFactory
        # installs it unconditionally, so a service without this file does not
        # compile, and the first slice needs it on the day it arrives.
        "tests/Catalog.TestSupport/TestAuthHandler.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxRows.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxTestEvents.cs",
        "tests/Catalog.TestSupport/ServiceFixture.cs",
    }
)

# PR-10's slice, and nothing else. A scaffolded service is PR-07's state with
# the wiring accumulated through PR-14 on it — not PR-10's state with the
# nouns changed. Renaming Product to Order would hand the next service a
# deletion job and a vocabulary it did not choose.
OMITTED = frozenset(
    {
        "src/Services/Catalog/Catalog.Api/Endpoints/ProductEndpoints.cs",
        # PR-19's pricing hop, whole. §9.7 permits exactly one synchronous
        # downstream call in the platform and Catalog is the callee, so a
        # service scaffolded from it inherits a gRPC server nobody calls,
        # a contract nobody consumes and a second Kestrel endpoint serving
        # neither. The .proto is Catalog's own API rather than a shape
        # every service has.
        #
        # appsettings.json goes with it because it exists ONLY for that
        # hop: it declares the Http2 endpoint gRPC needs, and a cleartext
        # port cannot serve HTTP/1.1 and h2c at once. Omitting it returns
        # the service to the container image's own port configuration,
        # which is what every other host here uses — and NOT omitting it
        # would be worse than redundant, because that file overrides
        # ASPNETCORE_HTTP_PORTS, so a service inheriting it would silently
        # stop listening on whatever its deployment set.
        "src/Services/Catalog/Catalog.Api/appsettings.json",
        "src/Services/Catalog/Catalog.Api/Protos/pricing.proto",
        "src/Services/Catalog/Catalog.Api/Grpc/PricingService.cs",
        # Generic in subject — it translates any ValidationException into
        # InvalidArgument — and slice by requirement: it is registered on
        # AddGrpc, which leaves with the hop, so a service keeping it would
        # carry an interceptor nothing installs.
        "src/Services/Catalog/Catalog.Api/Grpc/ValidationInterceptor.cs",
        # The permission vocabulary (§11.4) is the slice's, not the service's.
        # A host with no endpoint requires no permission, and carrying
        # `ordering:write` into a service that grants it to nothing would put a
        # name in the realm nobody can act on — the same objection as a policy
        # registered and never referenced. The first slice brings the first
        # permission, and the Program.cs patch in PATCHES drops the policy that
        # names this one.
        "src/Services/Catalog/Catalog.Api/CatalogPermissions.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesValidator.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/ProductPriceDto.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/ProductSummaryDto.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductCommand.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductValidator.cs",
        "src/Services/Catalog/Catalog.Domain/Common/Money.cs",
        "src/Services/Catalog/Catalog.Domain/Products/IProductRepository.cs",
        "src/Services/Catalog/Catalog.Domain/Products/Product.cs",
        "src/Services/Catalog/Catalog.Domain/Products/ProductId.cs",
        "src/Services/Catalog/Catalog.Domain/Products/ProductPublishedDomainEvent.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/MoneyJsonConverter.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/ProductConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/ProductRepository.cs",
        # §3.2 gives Catalog one Consumes cell, and the projection of it is the
        # slice's: a rendered service consumes nothing and projects nothing.
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/StockLevelConfiguration.cs",
        "tests/Catalog.Domain.Tests/MoneyTests.cs",
        "tests/Catalog.Domain.Tests/ProductTests.cs",
        "tests/Catalog.Application.Tests/CatalogIntegrationEventMapperTests.cs",
        "tests/Catalog.Application.Tests/GetPricesValidatorTests.cs",
        "tests/Catalog.Application.Tests/GetProductsHandlerTests.cs",
        "tests/Catalog.Application.Tests/OutboxSerialisationTests.cs",
        "tests/Catalog.TestSupport/Outbox/StagesThenFails.cs",
        "tests/Catalog.Application.Tests/PublishProductHandlerTests.cs",
        "tests/Catalog.Application.Tests/PublishProductValidatorTests.cs",
        "tests/Catalog.Api.Tests/OutboxTransportIdentityTests.cs",
        # The gRPC service's own suite, and it leaves for two reasons at
        # once: there is no PricingService to drive, and the channel it
        # builds needs the generated client the csproj patch in PATCHES drops.
        "tests/Catalog.Api.Tests/PricingServiceTests.cs",
        # PR-26's provider verification, which leaves for a third reason on
        # top of that pair: it is one named consumer's expectations of one
        # named provider. Web.Bff asks Catalog for prices (§9.7 permits
        # exactly one synchronous hop and this is it), so a scaffolded
        # service inherits neither the RPC nor anyone consuming it — and a
        # contract copied to a service no consumer calls is an expectation
        # nobody holds, which is the one thing a consumer-driven contract
        # must never become. The csproj patch in PATCHES drops the linked
        # PricingContract.cs with it, for the same reason.
        "tests/Catalog.Api.Tests/PricingContractVerificationTests.cs",
        # Both name /v1/catalog/products, so both are slice by requirement:
        # they read the host as a deployment rather than a fixture, and a
        # service with no endpoint has nothing to read. They return with the
        # first slice, beside the endpoint tests below. HostSmokeTests keeps
        # the factory they share — which is why those two tests live in a file
        # of their own rather than in it.
        "tests/Catalog.Api.Tests/EndpointSecurityTests.cs",
        # §11.4's callout, executed: every policy an endpoint names must
        # resolve. With no endpoint there is no policy to enumerate, and the
        # suite's own guard against passing vacuously is what fails first.
        #
        # IT CARRIES A SECOND GATE and dropping it drops that too: §8.5's
        # rule that an idempotent command's endpoint must require
        # authentication, since ICurrentUser.IsAuthenticated is false for an
        # anonymous request and every such caller then claims under the
        # shared "system" subject. A rendered service has neither an endpoint
        # nor an idempotent command, so nothing is unguarded today — what
        # would be unguarded is the first slice that adds both, which is why
        # the inverted floor in IdempotencyOptInTests names this file in the
        # message it fails with.
        "tests/Catalog.Api.Tests/AuthorizationPolicyTests.cs",
        # Not slice by subject — it is about EfUnitOfWork's rollback — but slice
        # by requirement: the claim is that a rejected command leaves nothing
        # tracked, and making it needs a tracked aggregate. A service with no
        # entity cannot assert it, so it returns with the first real slice.
        "tests/Catalog.Api.Tests/UnitOfWorkRollbackTests.cs",
        "tests/Catalog.Api.Tests/ProductEndpointsTests.cs",
        "tests/Catalog.Api.Tests/StockLevelsSchemaTests.cs",
        # Not slice, but container wiring with nothing left to wire: with the
        # handler tests gone, the collection has no member and the fixture no
        # consumer here. Both return with the service's first handler test,
        # beside the two project references and the provider package the
        # csproj patch in PATCHES drops for the same reason.
        "tests/Catalog.Application.Tests/IntegrationCollection.cs",
    }
)

# The one file with no counterpart in Catalog, and it is written to be deleted.
ASSEMBLY_MARKER = """namespace Catalog.Domain;

/// <summary>
/// The <c>typeof</c> anchor §4.2's architecture gates need, and nothing else.
/// A gate that reasons about an assembly has to name a type inside it, and
/// this project has none until its first aggregate.
/// </summary>
/// <remarks>
/// Written to be deleted. When that aggregate lands, re-anchor
/// <c>ArchitectureTests</c> in <c>Catalog.Domain.Tests</c> and
/// <c>Catalog.Application.Tests</c> on it and remove this file. A marker is
/// what a service has before it has a domain; leaving one in place after the
/// first aggregate arrives means the gates are judging an empty type instead
/// of the model they exist to constrain.
/// </remarks>
public sealed class AssemblyMarker;
"""

# Anything left in the rendered tree fails the run. `production` and EF's own
# `ProductVersion` annotation are the two benign substrings, and they are
# removed before the search rather than excused after it.
#
# **Two searches, at two different moments, and the split is load-bearing.**
# The template token is looked for *after* the rename, with the requested name
# masked out, because a service may legitimately contain it — `CatalogSearch`.
# The slice token is looked for *before* the rename, because masking cannot
# help there: a service called `Product` would mask away every real leftover
# along with its own name and the render would call itself domain-neutral.
# Before the rename a `Product` is unambiguous, since the rename maps the
# template's casings and never the slice's.
# Both case-insensitive, because the two halves have to hold to the same
# standard: `SLICE_TOKEN` was not, so `PRODUCT_ENDPOINT` in a copied file
# passed a guard that rejects `ProductEndpoint`.
BENIGN = re.compile(r"production|productversion", re.IGNORECASE)
TEMPLATE_TOKEN = re.compile(re.escape(TEMPLATE), re.IGNORECASE)
SLICE_TOKEN = re.compile(r"roduct", re.IGNORECASE)

# The three shapes EF puts in a migrations directory. Anything else there is
# somebody's addition, and the scaffold refuses rather than dropping it.
INITIAL_CREATE = re.compile(r"^\d{14}_InitialCreate(\.Designer)?\.cs$")
# The outbox table is wiring, not slice: §9.4 gives every service one, and a
# scaffolded service that carried the dispatcher without the table would log a
# failed claim twice a second from its first boot. So this migration is copied
# with InitialCreate rather than dropped with Catalog's model changes.
OUTBOX_MIGRATION = re.compile(r"^\d{14}_AddOutbox(\.Designer)?\.cs$")
# The inbox table travels for the mirror of the outbox's reason: §9.5 gives
# every service one, the retention purge runs from first boot and deletes from
# both, and a service that carried the purge without the table would log a
# failed delete every pass. Consuming nothing does not exempt it — Catalog
# itself consumes nothing and has the table for exactly this.
INBOX_MIGRATION = re.compile(r"^\d{14}_AddInbox(\.Designer)?\.cs$")
# The purge's index, and it travels for the same reason the tables do: the
# claim's index is filtered `WHERE ProcessedAt IS NULL` and so excludes every
# row the purge deletes. A service scaffolded without this one scans its whole
# outbox table hourly from its first boot — the same class of silent cost as a
# dispatcher with no table, and invisible for exactly as long as the table is
# small.
RETENTION_INDEX_MIGRATION = re.compile(r"^\d{14}_AddOutboxRetentionIndex(\.Designer)?\.cs$")
# §8.5's durable marker, and it travels on a stronger version of the inbox's
# argument. A service that protects no command yet writes no row here — but
# `RetentionPurgeService` deletes from this table from first boot, and
# `EfIdempotencyMarkerStore` reads it on the first command that does opt in. A
# service scaffolded without it fails a purge every hour and then fails the
# first idempotent command it is ever given, both against a table that is
# simply not there.
IDEMPOTENCY_MIGRATION = re.compile(r"^\d{14}_AddIdempotencyMarkers(\.Designer)?\.cs$")

# The marker's `CommittedAt` default (#167). It travels for the reason the
# table itself does: the column default and the SQL cutoff that reads it are
# two halves of one guarantee, so a service scaffolded with the table and
# without the default ages its markers on the writing pod's clock while the
# purge ages them on the server's — which is the skew this migration exists to
# remove, shipped to every new service by omission.
COMMITTED_AT_DEFAULT_MIGRATION = re.compile(
    r"^\d{14}_IdempotencyMarkerCommittedAtDefault(\.Designer)?\.cs$"
)

# The marker's `rowversion` (#173). It travels for the same reason the default
# above does, and the failure it prevents is louder: `RetentionPurgeService`
# names this column in both of its marker statements, so a service scaffolded
# without the migration does not merely age its markers wrongly — its purge
# raises `Invalid column name 'RowVersion'` on the first pass and the table
# grows for ever. The column and the statements that read it are one mechanism,
# and half of it is not shippable.
ROW_VERSION_MIGRATION = re.compile(
    r"^\d{14}_AddIdempotencyMarkerRowVersion(\.Designer)?\.cs$"
)
LATER_MIGRATION = re.compile(r"^\d{14}_\w+(\.Designer)?\.cs$")

# The migrations a scaffolded service starts with, in the order they are
# applied — which is the order their ids have to be generated in. A tuple
# rather than one named constant each, because every place below that cares
# needs the position rather than the name: the id is the base plus the index in
# minutes, and the snapshot is derived from the last one's designer.
#
# No count anywhere in this comment, and that is deliberate. It has said two,
# then three, then four inside one pull request, and each stale sentence
# survived alongside its replacement. The tuple is the count.
TEMPLATE_MIGRATIONS = (
    INITIAL_CREATE,
    OUTBOX_MIGRATION,
    INBOX_MIGRATION,
    RETENTION_INDEX_MIGRATION,
    IDEMPOTENCY_MIGRATION,
    COMMITTED_AT_DEFAULT_MIGRATION,
    ROW_VERSION_MIGRATION,
)

# The name each shape above is known by in a diagnostic, in the same order and
# beside it rather than spelt out where `classify` pairs the two. There the
# pairing was `zip(..., strict=True)`, which is a real guard raising the wrong
# exception: a tuple grown without its label gave a bare `ValueError`, past
# `main`'s `except ScaffoldError` and out as a traceback, from a script whose
# stated contract is one line on stderr and exit 1. Declared here the two are
# read in one place, and `classify` says which of them is short.
MIGRATION_LABELS = (
    "InitialCreate",
    "AddOutbox",
    "AddInbox",
    "AddOutboxRetentionIndex",
    "AddIdempotencyMarkers",
    "IdempotencyMarkerCommittedAtDefault",
    "AddIdempotencyMarkerRowVersion",
)

# A service key in a Compose file, at the model's own indent, and the marker
# that bounds one service's block in `.env.example` — the one file that still
# accumulates a block per service.
SERVICE_KEY = re.compile(r"^  ([A-Za-z0-9][A-Za-z0-9_-]*):$")
ENV_MARKER = re.compile(r"^# ([A-Za-z0-9]+)'s two §7\.1 keys")

# §14.1's Compose model is an index and one file per deployable unit, so a
# service's environment is a file this script CREATES rather than a block it
# splices into a file every other service also owns. That is the whole of what
# changed here: the index gains one line, the unit file is written whole, and
# two services being scaffolded at once no longer meet in one file.
#
# The index's `include:` list is the anchor, and it is read rather than
# assumed: an entry is two spaces, a dash, a space and a path relative to the
# compose directory. A list this script cannot find is a template that has
# moved, which is a refusal and never a guess.
COMPOSE_DIR = "deploy/compose"
COMPOSE_INDEX = f"{COMPOSE_DIR}/docker-compose.yml"
COMPOSE_UNITS = "services"
COMPOSE_TEMPLATE_UNIT = f"{COMPOSE_UNITS}/{TEMPLATE.lower()}.yml"
INCLUDE_ENTRY = re.compile(r"^  - (\S+)$")

# One service's `environment:` mapping in that same file, and the keys inside
# it — read back off the block this script has just rendered rather than off
# the template it was lifted from, because the collision below is something the
# rename creates. Indent is the whole selector: a mapping opens at the
# service's own level and its keys sit one level inside it, so `build:`'s
# nested pair, `depends_on:`'s entries and every comment are excluded by
# position rather than by a list of names to skip past.
ENVIRONMENT_BLOCK = re.compile(r"^    environment:$")
ENVIRONMENT_KEY = re.compile(r"^      ([A-Za-z0-9_]+):(?:\s|$)")

# §14.1 publishes every mapping on loopback: the credentials in that file are
# deliberate development defaults, so the interface is the control standing in
# front of them, and a scaffolded service that bound 0.0.0.0 would reopen the
# hole one service at a time. LOOPBACK is what the render emits and what the
# template is required to carry; HOST_IP is deliberately wider, because the
# collision check asks whether a port is taken and a port taken on some other
# interface is taken all the same.
LOOPBACK = "127.0.0.1"
HOST_IP = r"\d+\.\d+\.\d+\.\d+"


def classify(repo_root: Path, labels: tuple[str, ...]) -> list[str]:
    """The template files to copy, and a refusal if any is unclassified.

    `labels` is the caller's MIGRATION_LABELS rather than this module's, so
    the pairing below checks the table the run was actually given.
    """
    discovered: list[str] = []
    for root in COPY_ROOTS:
        for path in sorted((repo_root / root).rglob("*")):
            if not path.is_file():
                continue
            relative = PurePosixPath(path.relative_to(repo_root).as_posix())
            if "bin" in relative.parts or "obj" in relative.parts:
                continue
            discovered.append(str(relative))

    copied: list[str] = []
    for relative in discovered:
        if relative.startswith(MIGRATIONS + "/"):
            # Migration file names carry a timestamp, so they are classified by
            # shape rather than by name: a scaffolded service starts at
            # InitialCreate — the hand-written EnsureSchema of §7.4 — and every
            # later migration, and the snapshot, belongs to Catalog's model.
            #
            # Three shapes and no others. An unconditional `continue` here
            # treated *anything* in this directory as classified, so a helper
            # or a README added beside the migrations would be dropped without
            # the guard below ever seeing it — the one directory where the
            # scaffold's "it will not guess" promise silently did not hold.
            name = PurePosixPath(relative).name
            if any(shape.fullmatch(name) for shape in TEMPLATE_MIGRATIONS):
                copied.append(relative)
            elif LATER_MIGRATION.fullmatch(name) or name == f"{TEMPLATE}DbContextModelSnapshot.cs":
                pass
            else:
                raise ScaffoldError(
                    f"{relative} is not a migration, a designer file or the model snapshot. "
                    f"Classify it in scaffold/render.py — the scaffold will not guess."
                )
            continue
        if relative in COPIED:
            copied.append(relative)
        elif relative not in OMITTED:
            raise ScaffoldError(
                f"{relative} is not classified. Add it to COPIED if every service "
                f"needs it, or to OMITTED if it belongs to Catalog's slice — "
                f"the scaffold will not guess."
            )

    # Each pair counted separately, one shape at a time. A single total over
    # the whole directory would be satisfied by duplicates of one migration and
    # none of another — which is precisely the state that ships a dispatcher
    # with no table behind it, or a purge with no index.
    #
    # The pairing is checked before it is used, and `strict=True` is what this
    # replaces: it caught the same mistake and raised `ValueError`, which
    # `main` does not catch, so growing TEMPLATE_MIGRATIONS without its label
    # ended the run in a traceback naming neither constant.
    if len(TEMPLATE_MIGRATIONS) != len(labels):
        raise ScaffoldError(
            f"TEMPLATE_MIGRATIONS has {len(TEMPLATE_MIGRATIONS)} shape(s) and "
            f"MIGRATION_LABELS has {len(labels)} name(s). A migration was "
            f"added to one and not the other; the shorter one is the edit that is "
            f"missing."
        )
    for shape, label in zip(TEMPLATE_MIGRATIONS, labels, strict=True):
        pair = [p for p in copied if shape.fullmatch(PurePosixPath(p).name)]
        if len(pair) != 2:
            raise ScaffoldError(
                f"expected {label}.cs and {label}.Designer.cs under "
                f"{MIGRATIONS}, found {len(pair)}"
            )

    # The mirror of the check above, and the half that was missing. A file
    # *added* to Catalog stops the run; a file *deleted* from it did not — the
    # loop simply never saw it, so its patches never ran and `plan` succeeded
    # with a service missing a piece. That is the fail-open shape this whole
    # script is built to avoid, on the manifest itself.
    if (deleted := COPIED - set(discovered)):
        raise ScaffoldError(
            "the template no longer has: "
            + ", ".join(sorted(deleted))
            + ". Remove them from COPIED — with their PATCHES entries — or restore them."
        )

    # And no patch may be inert. A PATCHES key for a file that is not copied
    # never reaches `require_once`, so the anchor it guards would be unbound
    # while every other anchor still looked enforced.
    if (inert := set(PATCHES) - set(copied)):
        raise ScaffoldError(
            "PATCHES names files the scaffold does not copy: "
            + ", ".join(sorted(inert))
            + ". A patch that never runs is an anchor that guards nothing."
        )
    return copied


SLICE_ENTITY = f'            modelBuilder.Entity("{TEMPLATE}.Domain.Products.Product", b =>\n'
# The leading newline matters: without it this matches inside a nested block's
# deeper closer, because sixteen spaces then `});` is a substring of
# twenty-four spaces then `});`. The ComplexProperty block inside Catalog's
# aggregate is exactly that shape, so the removal stopped halfway and left the
# entity's own tail behind — caught by the check at the end of the function,
# which is the reason that check is there rather than trusted away.
ENTITY_END = "\n                });\n\n"


def without_slice_entity(designer: str) -> str:
    """The model body with Catalog's aggregate removed, and nothing else touched.

    A scaffolded service has the outbox entity and no aggregate, so the model
    EF would describe for it is exactly Catalog's minus one `Entity(...)`
    block. Removing that block is the one edit made to a machine-owned file
    here, and it is anchored at both ends rather than parsed: the opening line
    is exact and unique, and the closing `});` at that indent is the first one
    after it. Everything else — property order, annotations, the `using` block
    — stays byte-for-byte what the tool wrote.

    The alternative was to keep deriving from `InitialCreate.Designer.cs`,
    which describes an empty model. That stopped being the truth when the
    outbox joined the template: the snapshot would omit an entity the
    `DbContext` maps, and the first `migrations add` in a scaffolded service
    would generate a second `CreateTable` for a table its own InitialCreate had
    already created.
    """
    require_once(designer, SLICE_ENTITY, "the outbox migration's designer")
    start = designer.index(SLICE_ENTITY)

    end = designer.find(ENTITY_END, start)
    if end == -1:
        raise ScaffoldError(
            "the slice entity block in the designer has no closing `});` at its own indent"
        )

    stripped = designer[:start] + designer[end + len(ENTITY_END):]

    # The aggregate took a using with it. EF emits
    # `using System.Collections.Generic;` for a ComplexProperty mapped as a
    # Dictionary<string, object>, which is how §5.3's Money reaches the model —
    # so with the entity gone the using is unreferenced, and EF would not have
    # written it. Guarded rather than assumed: if any Dictionary< survives the
    # removal the using is still earning its place and stays.
    #
    # Found by diffing against the tool, which is the only way it could be:
    # the scaffolded service built and its migration produced an empty Up, and
    # the sole difference from EF's own rewritten snapshot was this line.
    dictionary_using = "using System.Collections.Generic;\n"
    if "Dictionary<" not in stripped and dictionary_using in stripped:
        stripped = stripped.replace(dictionary_using, "", 1)

    # Masked, like the render loop's own check: EF stamps a ProductVersion
    # annotation on every model it describes, and that token is nobody's
    # aggregate.
    if SLICE_TOKEN.search(BENIGN.sub("", stripped)) is not None:
        raise ScaffoldError(
            "the designer still names the slice after its entity block was removed — "
            "Catalog has gained a second entity, and the scaffold will not guess which"
        )
    return stripped


def snapshot_from_designer(designer: str, migration_id: str, migration: str) -> str:
    """The model snapshot, from the tool's own description of the same model.

    Catalog's snapshot cannot be copied — it describes `Product`, and the next
    `migrations add` in a service that has no such entity would generate a
    drop. Writing one by hand would break the rule that machine-owned files are
    left exactly as the tool wrote them. The *last* template migration's
    designer resolves both: it already holds EF's description of a model with
    both messaging entities in it, which is what a scaffolded service has once
    `without_slice_entity` has taken the aggregate out, so the class wrapper is
    rewritten and the model body is never retyped.

    The last one, and taking an earlier one would be wrong in a way with no
    symptom until the service's first `migrations add`: the outbox designer
    knows nothing of the inbox, so the snapshot would omit a table the
    `DbContext` maps and EF would generate a second `CreateTable` for one the
    scaffolded migrations had already created.

    The designer reaching here has already had the aggregate removed — the
    render loop does that before its slice check, so that a failed removal
    stops the run rather than reaching a shipped file. Stripping again here
    would find nothing and `require_once` would say so.

    `migration` is that last migration's class name, passed in rather than
    written here. It was a literal — `AddOutboxRetentionIndex` — until §8.5's
    marker table became the last entry in TEMPLATE_MIGRATIONS: the literal then
    named the second-to-last migration, every anchor below missed at once, and
    the run stopped. Loudly, which was luck.

    **What deriving it bought is narrow and worth stating narrowly**: this
    function no longer memorises a migration name, so it is not one of the
    places a sixth migration has to be edited. It is not the only place — the
    shape regex, TEMPLATE_MIGRATIONS, MIGRATION_LABELS, `without_slice_entity`
    and the patch dispatch all name the new one, and an earlier draft of this
    paragraph claimed the tuple was the whole edit. It was not, and a docstring
    that undercounts the work is how the next entry lands half-applied.
    """
    text = designer
    for needle, replacement in (
        ("using Microsoft.EntityFrameworkCore.Migrations;\n", ""),
        (f'    [Migration("{migration_id}_{migration}")]\n', ""),
        (
            f"    partial class {migration}\n",
            "    partial class CatalogDbContextModelSnapshot : ModelSnapshot\n",
        ),
        ("        /// <inheritdoc />\n", ""),
        (
            "        protected override void BuildTargetModel(ModelBuilder modelBuilder)\n",
            "        protected override void BuildModel(ModelBuilder modelBuilder)\n",
        ),
    ):
        require_once(text, needle, f"{migration}.Designer.cs")
        text = text.replace(needle, replacement)
    return text


def sort_usings(text: str) -> str:
    """Re-sort the leading `using` block, which the rename can reorder.

    EF writes the block sorted, and where the template's namespace sorts is not
    where the new service's does — `Catalog` comes before `Microsoft` and
    `Ordering` comes after it. Sorting after the rename is what keeps the file
    byte-identical to what the next `dotnet ef migrations add` in that service
    would write, so the first real migration produces no spurious diff. Checked
    against the tool rather than assumed: the difference is how it was found.
    """
    lines = text.splitlines(keepends=True)
    first = next((i for i, line in enumerate(lines) if line.startswith("using ")), None)
    if first is None:
        raise ScaffoldError("a machine-owned migration file with no using block")

    last = first
    while last < len(lines) and lines[last].startswith("using "):
        last += 1

    # Keyed on the namespace, not on the whole line: `;` sorts after `.`, so a
    # plain line sort puts Microsoft.EntityFrameworkCore.Infrastructure ahead
    # of Microsoft.EntityFrameworkCore and disagrees with the tool. Also found
    # by diffing against it.
    #
    # System first, which is the other half of the tool's order and did not
    # show until the outbox designer arrived: until then the only usings were
    # Microsoft.* and the service's own, and a plain sort happened to agree.
    # EF writes `using System;` and `using System.Collections.Generic;` above
    # everything else, so a service whose name sorts before `System` — every
    # one of them, since these are the only two — would otherwise get a block
    # the next `migrations add` immediately rewrites.
    def namespace(line: str) -> tuple[int, str]:
        name = line[len("using "):].strip().rstrip(";")
        return (0 if name == "System" or name.startswith("System.") else 1, name)

    lines[first:last] = sorted(lines[first:last], key=namespace)
    return "".join(lines)


def next_migration_id(migration_id: str, minutes: int = 1) -> str:
    """The id `minutes` after the given one, keeping EF's 14-digit shape.

    A plain `int(...) + 1` is wrong on every boundary the format has: second 59
    rolls into 60, and so do minute, hour and month. Parsed and re-formatted
    instead, which is the only arithmetic that is right for all of them.

    MIGRATION_ID accepts any fourteen digits, which is the right shape check
    and not a calendar one — `20261301000000` passes it and is month thirteen.
    `strptime` is what notices, and its ValueError is not a ScaffoldError, so
    without this the CLI printed a traceback where every other refusal prints
    one line. OverflowError joins it for the year-9999 end of the range, where
    adding a minute leaves what `datetime` can represent.
    """
    try:
        stamp = datetime.strptime(migration_id, "%Y%m%d%H%M%S") + timedelta(minutes=minutes)
    except (ValueError, OverflowError) as error:
        raise ScaffoldError(
            f"--migration-id {migration_id} is fourteen digits but not a timestamp: {error}"
        ) from error

    return stamp.strftime("%Y%m%d%H%M%S")


def render_projects(repo_root: Path, names: Names, migration_id: str,
                    labels: tuple[str, ...]) -> dict[str, str]:
    """The nine projects, the marker, the migration and its snapshot."""
    created: dict[str, str] = {}
    csharp_newline = ""

    for relative in classify(repo_root, labels):
        text, newline = read(repo_root, relative)
        if relative.endswith(".cs"):
            csharp_newline = newline

        patches = PATCHES.get(relative, ())
        if PurePosixPath(relative).name.endswith("_InitialCreate.cs"):
            patches = (*patches, *INITIAL_CREATE_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddOutbox.cs"):
            patches = (*patches, *OUTBOX_MIGRATION_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddInbox.cs"):
            patches = (*patches, *INBOX_MIGRATION_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddOutboxRetentionIndex.cs"):
            patches = (*patches, *RETENTION_INDEX_MIGRATION_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddIdempotencyMarkers.cs"):
            patches = (*patches, *IDEMPOTENCY_MIGRATION_PATCHES)
        for needle, replacement in patches:
            require_once(text, needle, relative)
            text = text.replace(needle, replacement)

        # The outbox designer describes Catalog's whole model, aggregate
        # included. Stripped here rather than further down, because the slice
        # check immediately below is exactly the check that should see the
        # result — a Product block surviving the removal must stop the run, not
        # reach the file the service ships.
        # Every designer, not only the last one. Each describes the model as of
        # its own migration and each therefore carries Catalog's aggregate, so
        # leaving the earlier one alone would ship a service a designer that
        # claims a table it never creates — and would trip the slice check
        # below, which is the guard that made this obvious.
        if PurePosixPath(relative).name.endswith(
            (
                "_AddOutbox.Designer.cs",
                "_AddInbox.Designer.cs",
                "_AddOutboxRetentionIndex.Designer.cs",
                "_AddIdempotencyMarkers.Designer.cs",
                "_IdempotencyMarkerCommittedAtDefault.Designer.cs",
                "_AddIdempotencyMarkerRowVersion.Designer.cs",
            )):
            text = without_slice_entity(text)

        # Before the rename, where a slice token means only itself. Doing this
        # after it — with the requested name masked, as the template check
        # must be — would let a service called `Product` mask away the very
        # leftovers this looks for.
        if (slice_token := SLICE_TOKEN.search(BENIGN.sub("", text))) is not None:
            line = BENIGN.sub("", text)[: slice_token.start()].count("\n") + 1
            raise ScaffoldError(
                f"{relative}:{line}: the slice survived. This file names "
                f"Product somewhere the patches do not reach."
            )
        if SLICE_TOKEN.search(relative) is not None:
            raise ScaffoldError(f"{relative}: the path itself names the slice")

        target = relative
        rendered = names.rename(text)
        if relative.startswith(MIGRATIONS + "/"):
            name = PurePosixPath(relative).name
            template_id = name.split("_", 1)[0]

            # One id per template migration, and the order between them is the
            # order they are applied in — EF sorts by this prefix, so a service
            # whose outbox table were ordered before its schema would fail on
            # the first run. A minute apart, spaced by position in
            # TEMPLATE_MIGRATIONS rather than by name, so the next one added is
            # an entry in that tuple and no arithmetic here.
            #
            # No count in this comment on purpose. It has said two, then three,
            # then four inside one pull request, and the stale sentences stacked
            # rather than being replaced — three contradictory claims about the
            # same tuple, which is what a review caught. The tuple is the count.
            offset = next(
                index for index, shape in enumerate(TEMPLATE_MIGRATIONS) if shape.fullmatch(name)
            )
            new_id = next_migration_id(migration_id, offset) if offset else migration_id
            target = f"{MIGRATIONS}/{name.replace(template_id, new_id, 1)}"
            text = text.replace(template_id, new_id)
            rendered = names.rename(text)
            if name.endswith(".Designer.cs"):
                rendered = sort_usings(rendered)
                if offset == len(TEMPLATE_MIGRATIONS) - 1:
                    # Only the last migration's designer describes the model
                    # the service ends up with, and the snapshot is a
                    # description of exactly that.
                    snapshot = names.rename(
                        snapshot_from_designer(
                            text,
                            new_id,
                            name.split("_", 1)[1].removesuffix(".Designer.cs")))
                    created[names.rename(f"{MIGRATIONS}/{TEMPLATE}DbContextModelSnapshot.cs")] = (
                        restore(sort_usings(snapshot), newline)
                    )

        created[names.rename(target)] = restore(rendered, newline)

    # The marker is the only file with no template beside it to take endings
    # from, so it takes the ones the template's own C# has. Observed rather
    # than assumed: `.gitattributes` decides this, and reading it here means a
    # change to that rule carries into generated code without a second edit.
    if not csharp_newline:
        raise ScaffoldError("no C# file in the template to take line endings from")

    created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Domain/AssemblyMarker.cs")] = (
        restore(names.rename(ASSEMBLY_MARKER), csharp_newline)
    )
    return created


def update_solution(repo_root: Path, names: Names) -> str:
    """Five projects in their own solution folder, four test entries, alphabetical."""
    text, newline = read(repo_root, "Platform.slnx")
    lines = text.splitlines(keepends=True)

    folder = [
        f'  <Folder Name="/src/Services/{names.pascal}/">\n',
        *(
            f'    <Project Path="src/Services/{names.pascal}/{names.pascal}.{layer}'
            f'/{names.pascal}.{layer}.csproj" />\n'
            for layer in ("Api", "Application", "Domain", "Infrastructure", "Migrator")
        ),
        "  </Folder>\n",
    ]

    service_folder = re.compile(r'^  <Folder Name="/src/Services/([^/]+)/">')
    existing = [(i, m.group(1)) for i, line in enumerate(lines) if (m := service_folder.match(line))]
    if not existing:
        raise ScaffoldError("Platform.slnx has no /src/Services/<service>/ folder to insert beside")

    at = len(lines)
    for index, service in existing:
        if service > names.pascal:
            at = index
            break
    else:
        last, _ = existing[-1]
        at = lines.index("  </Folder>\n", last) + 1
    lines[at:at] = folder

    tests = [
        f'    <Project Path="tests/{names.pascal}.{suite}/{names.pascal}.{suite}.csproj" />\n'
        for suite in ("Api.Tests", "Application.Tests", "Domain.Tests", "TestSupport")
    ]
    entry = re.compile(r'^    <Project Path="tests/([^"]+)" />')
    positions = [(i, m.group(1)) for i, line in enumerate(lines) if (m := entry.match(line))]
    if not positions:
        raise ScaffoldError("Platform.slnx has no /tests/ project entries to insert beside")

    for line in reversed(tests):
        path = entry.match(line).group(1)
        at = next((i for i, existing_path in positions if existing_path > path), positions[-1][0] + 1)
        lines.insert(at, line)
        positions = [(i, m.group(1)) for i, l in enumerate(lines) if (m := entry.match(l))]

    return restore("".join(lines), newline)


def environment_keys(block: str) -> list[list[str]]:
    """The mapping keys of every `environment:` block, one list per mapping.

    **Per mapping and never one flat set**, because §14.1's pair rule renders
    two services and each declares its own: a key appearing in both is two
    containers agreeing about a variable, which is ordinary, while the same key
    twice in one mapping is a service saying one thing twice, which is the
    defect. Flattening the two would report the first as a collision and lose
    the second in the noise.

    **Returned rather than judged, so that what this reads is testable.** A
    duplicate check is only as good as the keys handed to it, and a pattern
    that stops matching the template's shape hands it nothing — over which
    every name there is passes. That is this repository's most-repeated
    failure, so the extraction is a value a test can assert about instead of a
    step buried inside the caller.
    """
    mappings: list[list[str]] = []
    keys: list[str] | None = None
    for line in block.split("\n"):
        if ENVIRONMENT_BLOCK.fullmatch(line):
            keys = []
            mappings.append(keys)
        elif keys is None:
            continue
        elif line.strip() and not line.startswith("      "):
            # Anything back out at the service's own level ends the mapping —
            # `ports:`, `depends_on:`, the next service. A blank line does not.
            keys = None
        elif (key := ENVIRONMENT_KEY.match(line)) is not None:
            keys.append(key.group(1))
    return mappings


def compose_unit(names: Names) -> str:
    """Where a service's own Compose file lives, repository-relative."""
    return f"{COMPOSE_DIR}/{COMPOSE_UNITS}/{names.lower}.yml"


def compose_included(repo_root: Path) -> list[tuple[int, str]]:
    """The index's include list: each entry's line number and its path.

    Read out of the index rather than globbed off the directory, because the
    index is what Compose obeys. A unit file sitting in `services/` that no
    line includes is not part of the model, and a port published in it is not
    a port that is taken — so globbing would refuse a free port on the strength
    of a file nothing reads.
    """
    text, _ = read(repo_root, COMPOSE_INDEX)
    entries = [
        (number, match.group(1))
        for number, line in enumerate(text.split("\n"))
        if (match := INCLUDE_ENTRY.fullmatch(line))
    ]
    if not entries:
        raise ScaffoldError(
            f"{COMPOSE_INDEX} declares no `include:` entry this script recognises "
            f"(two spaces, a dash, a space, a path). The template has moved; "
            f"reconcile tools/new-service with it."
        )
    return entries


def update_compose(repo_root: Path, names: Names, port: int) -> str:
    """The index gains one line, and nothing else in it moves.

    **The port collision check reads every included file, not this one.** The
    index publishes nothing at all now, so a check that kept reading it would
    have found no mapping anywhere and called every port free — a silent
    fail-open on the one guard that stops two services publishing the same
    port. It reads what the index includes instead, which is the same set of
    mappings the check has always been about.

    **Both port regexes carry the host-IP prefix, and the collision check is
    the one that fails quietly without it.** Every mapping in the model is
    published on `127.0.0.1` (§14.1), so `"5102:` no longer follows a quote
    and a pattern anchored on one matches nothing — which reads exactly like
    a free port, and would have published a second service on one already
    taken.
    """
    entries = compose_included(repo_root)

    for _, entry in entries:
        included, _ = read(repo_root, f"{COMPOSE_DIR}/{entry}")
        if re.search(rf'"(?:{HOST_IP}:)?{port}:\d+"', included):
            raise ScaffoldError(
                f"port {port} is already published in {COMPOSE_DIR}/{entry}"
            )

    text, newline = read(repo_root, COMPOSE_INDEX)
    lines = text.split("\n")

    unit = f"{COMPOSE_UNITS}/{names.lower}.yml"
    if unit in {entry for _, entry in entries}:
        raise ScaffoldError(f"{COMPOSE_INDEX} already includes {unit}")

    # The template's own entry is the anchor. Its absence means the layout this
    # script renders into is not the layout on disk, and inserting beside a
    # list whose shape is unknown is the guess this script does not make.
    units = [number for number, entry in entries if entry.startswith(f"{COMPOSE_UNITS}/")]
    if COMPOSE_TEMPLATE_UNIT not in {entry for _, entry in entries}:
        raise ScaffoldError(
            f"{COMPOSE_INDEX} does not include {COMPOSE_TEMPLATE_UNIT}, so there is "
            f"no template unit to render from (§14.1's pair rule lives in it)"
        )

    # After the last unit, so services accumulate in the order they were
    # created — the property the spliced block had, kept where it now lives.
    after = units[-1] + 1
    return restore("\n".join([*lines[:after], f"  - {unit}", *lines[after:]]), newline)


def render_service_compose(repo_root: Path, names: Names, port: int) -> str:
    """Catalog's own unit file, renamed, re-ported and re-headed.

    An extraction rather than a template: the pair's comments argue the
    inline-default rule and §7.1's two keys, and they travel with the copy.
    What does NOT travel is the header above `services:` — it is prose about
    the template, and a rename would turn true sentences about Catalog into
    false ones about the service being rendered. It is replaced rather than
    renamed, and the replacement names no template token, because the
    straggler check in `plan` reads what this returns.
    """
    text, newline = read(repo_root, f"{COMPOSE_DIR}/{COMPOSE_TEMPLATE_UNIT}")

    # §14.1's pair rule, asserted on the file that carries it — and asserted as
    # the WHOLE of it, which is what the split bought: a unit holds one
    # service's pair and nothing after it, so the check is an equality rather
    # than the two anchors and a bound that a spliced block needed. A template
    # that gained a third service would render one this script never saw.
    pair = [f"  {TEMPLATE.lower()}-migrator:", f"  {TEMPLATE.lower()}-api:"]
    declared = [line for line in text.split("\n") if SERVICE_KEY.fullmatch(line)]
    if declared != pair:
        raise ScaffoldError(
            f"{COMPOSE_DIR}/{COMPOSE_TEMPLATE_UNIT} declares {declared or 'no service'}; "
            f"§14.1's pair rule makes it exactly {pair}. The template has moved; "
            f"reconcile tools/new-service with it."
        )

    marker = "\nservices:\n"
    if marker not in text:
        raise ScaffoldError(
            f"{COMPOSE_DIR}/{COMPOSE_TEMPLATE_UNIT} has no `services:` key to render from"
        )
    block = names.rename(text[text.index(marker) + 1:])

    # The loopback prefix is REQUIRED of the template rather than copied from
    # it. Reading the prefix off Catalog would make the scaffold agree with
    # whatever Catalog does, so removing the bind there would silently publish
    # every service scaffolded afterwards on every interface — a gate that
    # follows its subject cannot catch its subject regressing. Anchored, the
    # same removal is a scaffold that refuses to run and says why.
    published = re.search(rf'ports: \[ "{re.escape(LOOPBACK)}:(\d+):8080" \]', block)
    if published is None:
        raise ScaffoldError(
            f"the template's api block publishes no {LOOPBACK}-bound port to substitute "
            f"(§14.1 binds every mapping to loopback)"
        )
    block = block.replace(published.group(0), f'ports: [ "{LOOPBACK}:{port}:8080" ]')

    # §7.1's runtime key is `ConnectionStrings__<Service>` and the rename is
    # what writes it, so a service named after one of §14.1's infrastructure
    # connections renders a key the api block already declares. Nothing above
    # can see it: the rename worked exactly as specified, the straggler check
    # finds no template token left, and duplicate keys leave the YAML well
    # formed — so the run reports success and the file quietly means one of the
    # two values.
    #
    # A predicate over the rendered block, never the three names it happens to
    # catch today. A list of names goes stale the moment §14.1 gives this block
    # a sixth `ConnectionStrings__*` key, and a gate that silently stops
    # covering the newest surface is this repository's most-repeated failure.
    #
    # **Compared casefolded, because the loader on the other side of this file
    # is.** §14.2 states it in the one line where it costs an Aspire resource
    # name: configuration is case-insensitive but not punctuation-insensitive.
    # So `ConnectionStrings__Rabbitmq` and `ConnectionStrings__RabbitMq` are
    # two YAML keys and one configuration key, and the first version of this
    # check — a case-sensitive `in seen` — saw two distinct strings and passed.
    # That handed the exact defect it was written for back to every spelling of
    # an infrastructure connection with different capitals, `RabbitMQ` (the
    # product's own) among them. The same predicate again, one level less
    # literal: a list of names would have gone stale, and so does an equality
    # that is stricter than the thing it is standing in for.
    #
    # `casefold` rather than `lower`, which is the spelling Python defines for
    # case-insensitive comparison — it folds what `lower` leaves alone, and the
    # cost of choosing the weaker one is a refusal that does not fire.
    for mapping in environment_keys(block):
        seen: dict[str, str] = {}
        for key in mapping:
            if (first := seen.get(key.casefold())) is not None:
                # Both spellings, and never one of them twice: the collision is
                # a fact about the configuration loader rather than about the
                # YAML, so a message quoting `ConnectionStrings__RabbitMq` and
                # blaming "the same key twice" reads as simply false to whoever
                # hit it having typed `Rabbitmq`.
                collision = (
                    f"renders {key} twice in one environment: mapping"
                    if first == key
                    else f"renders both {first} and {key} into one environment: mapping"
                )
                raise ScaffoldError(
                    f"'{names.pascal}' {collision}. .NET configuration keys are "
                    f"case-insensitive (§14.2), so those two collapse onto one another the "
                    f"moment configuration loads: the loader keeps one of the values and "
                    f"discards the other, and which one survives is its choice rather than "
                    f"this script's. This service's own §7.1 connection key takes the "
                    f"service's name, and §14.1 already declares an infrastructure "
                    f"connection under that name in the same block. Nothing else refuses "
                    f"it: the rename is correct, no template token is left, and two "
                    f"spellings are two valid YAML keys — so the file stays well formed and "
                    f"the run reports success. Give the service a different name."
                )
            seen[key.casefold()] = key

    header = (
        f"# {names.pascal}'s deployment (§14.1), included by {COMPOSE_INDEX}.\n"
        f"# Rendered by tools/new-service from the template service's unit file: the\n"
        f"# pair rule below belongs to the chapter, and the file boundary belongs to\n"
        f"# docs/change-locality.md, so a {names.pascal} PR edits this file and never\n"
        f"# another service's.\n"
        f"#\n"
        f"# `include` resolves a relative path against the directory of the file that\n"
        f"# declares it, so the repository root — the build context — is three levels up\n"
        f"# from here.\n"
    )
    return restore(header + block, newline)


def update_infra_only(repo_root: Path, names: Names) -> str:
    """Both halves of the pair join the excluded profile — §14.1's own rule."""
    text, newline = read(repo_root, "deploy/compose/docker-compose.infra-only.yml")

    # The anchor is the contiguous pair, and the pair is also what gets
    # written — one string, so the two cannot drift apart. Anchoring the API
    # half alone let a change to the migrator's entry through unnoticed while
    # this went on emitting the shape it used to have, which is the drift the
    # anchors exist to stop.
    pair = (
        f'  {TEMPLATE.lower()}-migrator:\n'
        f'    profiles: [ "excluded" ]\n'
        f'  {TEMPLATE.lower()}-api:\n'
        f'    profiles: [ "excluded" ]\n'
    )
    require_once(text, pair, "infra-only override")
    return restore(text + names.rename(pair), newline)


def update_env_example(repo_root: Path, names: Names) -> str:
    """Catalog's commented pair, extracted so its argument comes with it.

    Bounded by the next service's own marker rather than by the end of the
    file — the same defect as `update_compose`, and found the same way: to EOF
    is the template's block only until one service has been added, after which
    it drags that service's variables along and writes them twice.
    """
    text, newline = read(repo_root, "deploy/compose/.env.example")
    lines = text.split("\n")

    marks = [(i, m.group(1)) for i, line in enumerate(lines) if (m := ENV_MARKER.match(line))]
    template = [i for i, service in marks if service == TEMPLATE]
    if len(template) != 1:
        raise ScaffoldError(
            f".env.example: expected exactly one \"# {TEMPLATE}'s two §7.1 keys\" block, "
            f"found {len(template)}"
        )

    start = template[0]
    following = [i for i, _ in marks if i > start]
    stop = following[0] if following else len(lines)

    block = names.rename("\n".join(lines[start:stop]).rstrip("\n"))
    return restore(text.rstrip("\n") + "\n\n" + block + "\n", newline)


def update_broker_definitions(repo_root: Path, names: Names) -> str:
    """A broker account for the new service (#44).

    Since per-service identity, a service that reaches the broker as nobody in
    `definitions.json` cannot connect AT ALL — and the compose block this
    script already renders names `{service}-svc`. So the account is not an
    optional extra: without it the scaffolded service starts and then fails
    authentication against a broker that has never heard of it.

    Catalog's entry is the template, exactly as it is everywhere else here, so
    the permissions a new service gets are a PUBLISHER's: its own contracts,
    the framework's fault exchanges, and nothing of anybody else's. A service
    that grows a receive endpoint widens its own entry in the same change, and
    `deploy/compose/rabbitmq/check_permissions.py` is what says so — it derives
    what each service needs from the code and fails when the grant is short.

    The password is `local-dev-{service}`, on §14.1's terms for every other
    credential here, and the hash is computed rather than copied: RabbitMQ
    stores `base64(salt || sha256(salt || utf8(password)))`, so a copied hash
    would authenticate the template's password under the new name.
    """
    import base64
    import hashlib
    import json

    relative = "deploy/compose/rabbitmq/definitions.json"
    text, newline = read(repo_root, relative)
    definitions = json.loads(text)

    user = f"{names.lower}-svc"
    if any(entry["name"] == user for entry in definitions["users"]):
        raise ScaffoldError(f"{relative}: a broker user named {user} already exists")

    template_user = f"{TEMPLATE.lower()}-svc"
    template_permission = next(
        (entry for entry in definitions["permissions"] if entry["user"] == template_user),
        None)
    if template_permission is None:
        raise ScaffoldError(
            f"{relative}: no permissions for {template_user} to copy (§14.1, #44)")

    # A deterministic salt per service, so re-rendering the same name twice
    # produces the same file and a reviewer can recompute the hash.
    salt = hashlib.sha256(user.encode()).digest()[:4]
    digest = hashlib.sha256(salt + f"local-dev-{names.lower}".encode()).digest()

    definitions["users"].append({
        "name": user,
        "password_hash": base64.b64encode(salt + digest).decode(),
        "hashing_algorithm": "rabbit_password_hashing_sha256",
        "tags": [],
    })
    definitions["permissions"].append({
        "user": user,
        "vhost": template_permission["vhost"],
        "configure": names.rename(template_permission["configure"]),
        "write": names.rename(template_permission["write"]),
        "read": names.rename(template_permission["read"]),
    })

    return restore(json.dumps(definitions, indent=2) + "\n", newline)


def update_ports_readme(repo_root: Path, names: Names, port: int) -> str:
    """One row in the application-services table — the keyboard inventory (§14.1)."""
    text, newline = read(repo_root, "deploy/compose/README.md")
    header = "| Service | Host port(s) | Notes |\n"
    require_once(text, header, "deploy/compose/README.md")

    start = text.index(header)
    end = text.index("\n\n", start) + 1
    row = (
        f"| {names.pascal} API | http://localhost:{port} | "
        # The token note is not decoration: ADR-030 fallback policy covers
        # MapOpenApi, so a rendered service document answers 401 to an
        # anonymous request exactly as Catalog and Ordering do. A row
        # that omitted it would re-introduce the claim the README was
        # corrected to remove, once per scaffolded service.
        f"`/health/live`, `/health/ready`, "
        f"`/openapi/v1.json` (needs a token — see below) |\n"
    )
    return restore(text[:end] + row + text[end:], newline)
