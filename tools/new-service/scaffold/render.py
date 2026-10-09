"""What a run writes: the service's projects and its Compose unit, and the
edits to the shared files that have to name it.

Every function here returns text; `apply` in `new_service.py` is the writer.
"""

from __future__ import annotations

import re
from datetime import datetime, timedelta
from pathlib import Path, PurePosixPath

from scaffold import API_HOST, TEMPLATE, Names, ScaffoldError, read, require_once, restore
from scaffold.patch import PATCHES, PURE_CONSUMER_PATCHES, PURE_CONSUMER_SPANS, WORKER_PATCHES

# The service projects §4.1 gives a service, its test projects, and
# Catalog.TestSupport, which §4.1 says is not a test project and which is copied
# all the same: the fixture is the template's.
COPY_ROOTS = (
    "src/Services/Catalog",
    "tests/Catalog.Domain.Tests",
    "tests/Catalog.Application.Tests",
    "tests/Catalog.Api.Tests",
    "tests/Catalog.TestSupport",
)

MIGRATIONS = "src/Services/Catalog/Catalog.Infrastructure/Persistence/Migrations"

# Every file under COPY_ROOTS is classified here or the run fails, the same
# allow-list argument as the gate in Catalog.Domain.Tests: extending the list
# is the decision the check exists to force. Otherwise the next aggregate
# added to Catalog ships into every later service, and no straggler check
# sees it, as a Categories folder carries none of the scaffold's tokens.
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
        # §9.8's ladder, declared once per service: a rendered service's first
        # endpoint wants it, and it names nothing of Catalog's.
        "src/Services/Catalog/Catalog.Infrastructure/Messaging/RetryPolicy.cs",
        "src/Services/Catalog/Catalog.Infrastructure/SqlConnectionFactory.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfDomainEventCollector.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxPublisher.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfUnitOfWork.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/InboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/IdempotencyMarkerConfiguration.cs",
        # ADR-079's check as every migrator reaches it, through its own Infrastructure (§4.2).
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/MigratorTransport.cs",
        # §13.6's per-lane gauges. They travel for the reason the outbox table
        # does: every publishing service hosts the dispatcher, the alerts group by
        # service_name, and a rendered service without them is covered by
        # alerts that can never fire for it — which reads exactly like health.
        "src/Services/Catalog/Catalog.Infrastructure/Observability/IOutboxStats.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxStats.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxMetrics.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/MetricsInitialiser.cs",
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
        "tests/Catalog.Api.Tests/BrokerTlsTests.cs",
        "tests/Catalog.Api.Tests/MetricsRegistrationTests.cs",
        "tests/Catalog.Api.Tests/InboxFilterTests.cs",
        "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs",
        "tests/Catalog.Api.Tests/RetentionPurgeTests.cs",
        "tests/Catalog.Api.Tests/TransientFaultInjection.cs",
        # ADR-058's gate travels, so a rendered host is born under the rule;
        # PATCHES inverts the floor that names the template's own writes.
        "tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs",
        # The example rule travels on the same terms, its body floor inverted.
        "tests/Catalog.Api.Tests/RequestExampleRuleTests.cs",
        # ADR-017's gate travels on the same terms; PATCHES inverts its
        # consumer floor, since a rendered host registers no consumer.
        "tests/Catalog.Api.Tests/ConsumerCallRuleTests.cs",
        # The building blocks' windows, which every rendered host holds.
        "tests/Catalog.Api.Tests/RetentionMapTests.cs",
        # §14.3's gate travels with the hook it guards, which every migrator holds.
        "tests/Catalog.Api.Tests/SeedGateTests.cs",
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

# Catalog's product slice. A scaffolded service carries the wiring, not the
# slice with the nouns changed: renaming Product to Order would hand the next
# service a deletion job and a vocabulary it did not choose.
OMITTED = frozenset(
    {
        "src/Services/Catalog/Catalog.Api/Endpoints/ProductEndpoints.cs",
        # The pricing hop, whole (§9.7): a service scaffolded from Catalog
        # would inherit a gRPC server nobody calls and a second Kestrel
        # endpoint. appsettings.json goes with it: it declares the Http2
        # endpoint gRPC needs, and overrides ASPNETCORE_HTTP_PORTS, which
        # would silence whatever port the service's deployment set.
        "src/Services/Catalog/Catalog.Api/appsettings.json",
        "src/Services/Catalog/Catalog.Api/Protos/pricing.proto",
        "src/Services/Catalog/Catalog.Api/Grpc/PricingService.cs",
        # Generic in subject — it translates any ValidationException into
        # InvalidArgument — and slice by requirement: it is registered on
        # AddGrpc, which leaves with the hop, so a service keeping it would
        # carry an interceptor nothing installs.
        "src/Services/Catalog/Catalog.Api/Grpc/ValidationInterceptor.cs",
        # The permission vocabulary (§11.4) is the slice's, not the service's:
        # `ordering:write` carried into a service that grants it to nothing
        # is a name in the realm nobody can act on. The first slice brings
        # the first permission, and the Program.cs patch in PATCHES drops the
        # policy that names this one.
        "src/Services/Catalog/Catalog.Api/CatalogPermissions.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesValidator.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/ProductPriceDto.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProduct/GetProductHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProduct/GetProductQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsValidator.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/ProductCursor.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/ProductSort.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/ProductSummaryDto.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductCommand.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductValidator.cs",
        "src/Services/Catalog/Catalog.Application/Products/ProductErrors.cs",
        "src/Services/Catalog/Catalog.Application/Products/PriceRules.cs",
        "src/Services/Catalog/Catalog.Application/Products/ChangePrice/ChangePriceCommand.cs",
        "src/Services/Catalog/Catalog.Application/Products/ChangePrice/ChangePriceHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/ChangePrice/ChangePriceValidator.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetOwnProducts/GetOwnProductsHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetOwnProducts/GetOwnProductsQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetOwnProducts/OwnProductDto.cs",
        "src/Services/Catalog/Catalog.Application/Products/ProductOwnership.cs",
        "src/Services/Catalog/Catalog.Application/Products/WithdrawProduct/WithdrawProductCommand.cs",
        "src/Services/Catalog/Catalog.Application/Products/WithdrawProduct/WithdrawProductHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/WithdrawProduct/WithdrawProductValidator.cs",
        "src/Services/Catalog/Catalog.Domain/Products/PriceChangedDomainEvent.cs",
        "src/Services/Catalog/Catalog.Domain/Products/ProductDiscontinuedDomainEvent.cs",
        "src/Services/Catalog/Catalog.Domain/Products/SellerId.cs",
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
        "src/Services/Catalog/Catalog.Infrastructure/Projections/StockLevelProjection.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Messaging/StockLevelConsumer.cs",
        # §14.3's rows are the slice's products. The hook is not: MigratorHost
        # and MigrationRunner travel, and STAND_INS renders this file empty.
        "src/Services/Catalog/Catalog.Migrator/CatalogSeeder.cs",
        "tests/Catalog.Api.Tests/CatalogSeederTests.cs",
        "tests/Catalog.Domain.Tests/MoneyTests.cs",
        "tests/Catalog.Domain.Tests/ProductTests.cs",
        "tests/Catalog.Application.Tests/CatalogIntegrationEventMapperTests.cs",
        "tests/Catalog.Application.Tests/GetPricesValidatorTests.cs",
        "tests/Catalog.Application.Tests/GetProductHandlerTests.cs",
        "tests/Catalog.Application.Tests/GetProductsHandlerTests.cs",
        "tests/Catalog.Application.Tests/GetProductsValidatorTests.cs",
        "tests/Catalog.Application.Tests/ProductCursorTests.cs",
        "tests/Catalog.Application.Tests/OutboxSerialisationTests.cs",
        "tests/Catalog.TestSupport/Outbox/StagesThenFails.cs",
        "tests/Catalog.Application.Tests/PublishProductHandlerTests.cs",
        "tests/Catalog.Application.Tests/PublishProductValidatorTests.cs",
        "tests/Catalog.Application.Tests/ChangePriceHandlerTests.cs",
        "tests/Catalog.Application.Tests/ChangePriceValidatorTests.cs",
        "tests/Catalog.Application.Tests/Caller.cs",
        "tests/Catalog.Application.Tests/WithdrawProductHandlerTests.cs",
        "tests/Catalog.Application.Tests/WithdrawProductValidatorTests.cs",
        # Wiring by subject, slice by requirement: the trace is proved through
        # Catalog's own consumer of StockLevelChanged, which a rendered service
        # does not have.
        "tests/Catalog.Api.Tests/OutboxTraceContextTests.cs",
        "tests/Catalog.Api.Tests/OutboxTransportIdentityTests.cs",
        # The gRPC service's own suite, and it leaves for two reasons at
        # once: there is no PricingService to drive, and the channel it
        # builds needs the generated client the csproj patch in PATCHES drops.
        "tests/Catalog.Api.Tests/PricingServiceTests.cs",
        # The provider verification leaves for a third reason as well: it is
        # one named consumer's expectations of one named provider. Web.Bff
        # asks Catalog for prices (§9.7), so a contract copied to a service
        # no consumer calls is an expectation nobody holds. The csproj patch
        # in PATCHES drops the linked PricingContract.cs with it.
        "tests/Catalog.Api.Tests/PricingContractVerificationTests.cs",
        # Both name /v1/catalog/products, so both are slice by requirement:
        # they read the host as a deployment, and a service with no endpoint
        # has nothing to read. They return with the first slice, beside the
        # endpoint tests below; HostSmokeTests keeps the factory they share.
        "tests/Catalog.Api.Tests/EndpointSecurityTests.cs",
        # §11.4's callout, executed: every policy an endpoint names must
        # resolve. With no endpoint there is no policy to enumerate, and the
        # suite's own guard against passing vacuously is what fails first.
        "tests/Catalog.Api.Tests/AuthorizationPolicyTests.cs",
        # §11.4's grantable check reads CatalogPermissions, which is omitted
        # above as the slice's vocabulary, so it returns with that vocabulary.
        "tests/Catalog.Api.Tests/GrantablePermissionTests.cs",
        # Not slice by subject — it is about EfUnitOfWork's rollback — but slice
        # by requirement: the claim is that a rejected command leaves nothing
        # tracked, and making it needs a tracked aggregate. A service with no
        # entity cannot assert it, so it returns with the first real slice.
        "tests/Catalog.Api.Tests/UnitOfWorkRollbackTests.cs",
        "tests/Catalog.Api.Tests/ProductEndpointsTests.cs",
        "tests/Catalog.Api.Tests/StockLevelsSchemaTests.cs",
        "tests/Catalog.Api.Tests/ProductsSchemaTests.cs",
        "tests/Catalog.Api.Tests/StockLevelProjectionTests.cs",
        "tests/Catalog.Api.Tests/StockLevelRegistrationTests.cs",
        "tests/Catalog.Api.Tests/InventoryEventEndpointTests.cs",
        # Not slice, but container wiring with nothing left to wire: with the
        # handler tests gone, the collection has no member and the fixture no
        # consumer here. Both return with the service's first handler test,
        # beside the two project references and the provider package the
        # csproj patch in PATCHES drops for the same reason.
        "tests/Catalog.Application.Tests/IntegrationCollection.cs",
    }
)

# What a pure consumer is not given, all of it COPIED for every other render:
# the Domain project and its suite (§4.1), and §9.4's outbox and §9.3's mapper,
# since a service that publishes nothing stages nothing (§3.2). `classify`
# refuses an entry COPIED does not hold.
PURE_CONSUMER_OMITTED = frozenset(
    {
        "src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj",
        "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfDomainEventCollector.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxPublisher.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/IOutboxStats.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxStats.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxMetrics.cs",
        "tests/Catalog.Domain.Tests/ArchitectureTests.cs",
        "tests/Catalog.Domain.Tests/Catalog.Domain.Tests.csproj",
        "tests/Catalog.Api.Tests/MessageTypeMapValidatorTests.cs",
        "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxRows.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxTestEvents.cs",
    }
)

# A pure consumer's one file with no counterpart in Catalog: §6.3's TransactionBehavior
# needs a dispatcher, and §7.5's needs the collector, mapper and publisher this shape lacks.
NO_DOMAIN_EVENT_DISPATCHER = """using Common.Application;

namespace Catalog.Application;

/// <summary>§7.5's dispatcher for a service §4.1 gives no Domain project, where no aggregate raises an event.</summary>
internal sealed class NoDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(CancellationToken ct) => Task.CompletedTask;
}
"""

# An API or worker render's one file with no counterpart in Catalog, and it is written to be deleted.
ASSEMBLY_MARKER = """namespace Catalog.Domain;

/// <summary>The type §4.2's architecture gates anchor on until the first aggregate replaces it.</summary>
public sealed class AssemblyMarker;
"""

# §14.3's seeder with no rows, the type the copied MigratorHost registers and
# MigrationRunner takes. It says so when the gate opens, since a gate opening
# onto silence reads exactly like a seeder that is broken.
EMPTY_SEEDER = """using Microsoft.Extensions.Logging;

namespace Catalog.Migrator;

/// <summary>§14.3's development seed, which holds no rows until this service has an aggregate to seed.</summary>
public sealed class CatalogSeeder(ILogger<CatalogSeeder> logger)
{
    private static readonly Action<ILogger, Exception?> NothingToSeed =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(NothingToSeed)),
            "Catalog has no seed rows yet; the gate is open and nothing was written.");

    public Task SeedAsync(CancellationToken ct)
    {
        NothingToSeed(logger, null);
        return Task.CompletedTask;
    }
}
"""

# An OMITTED file whose type the copied wiring still names, rendered from text
# of its own in the template's place: the hook travels and the slice does not.
STAND_INS = {
    "src/Services/Catalog/Catalog.Migrator/CatalogSeeder.cs": EMPTY_SEEDER,
}

# Anything left in the rendered tree fails the run, once `production` and EF's
# `ProductVersion` are removed. The template token is searched after the rename
# with the requested name masked, since a service may contain it; the slice
# token before, since masking would hide every leftover in a service called
# `Product`. Both are case-insensitive.
BENIGN = re.compile(r"production|productversion", re.IGNORECASE)
TEMPLATE_TOKEN = re.compile(re.escape(TEMPLATE), re.IGNORECASE)
SLICE_TOKEN = re.compile(r"roduct", re.IGNORECASE)

# The three shapes EF puts in a migrations directory. Anything else there is
# somebody's addition, and the scaffold refuses rather than dropping it.
INITIAL_CREATE = re.compile(r"^\d{14}_InitialCreate(\.Designer)?\.cs$")
# The outbox table is wiring, not slice: §9.4 gives every publishing service
# one, and one that carried the dispatcher without the table would log a failed
# claim twice a second from its first boot. So this migration is copied with
# InitialCreate rather than dropped with Catalog's model changes.
OUTBOX_MIGRATION = re.compile(r"^\d{14}_AddOutbox(\.Designer)?\.cs$")
# The inbox table travels for the mirror of the outbox's reason: §9.5 gives
# every service one, the retention purge runs from first boot and deletes from
# it, and a service that carried the purge without the table would log a
# failed delete every pass. A service that consumes nothing today still owns
# the table its first consumer needs.
INBOX_MIGRATION = re.compile(r"^\d{14}_AddInbox(\.Designer)?\.cs$")
# The purge's index travels for the same reason the tables do: the claim's
# index is filtered `WHERE ProcessedAt IS NULL` and so excludes every row
# the purge deletes. A service without it scans its whole outbox table
# hourly from first boot, a cost invisible while the table is small.
RETENTION_INDEX_MIGRATION = re.compile(r"^\d{14}_AddOutboxRetentionIndex(\.Designer)?\.cs$")
# §8.5's durable marker. A service that protects no command writes no row
# here, but `RetentionPurgeService` deletes from this table from first boot
# and `EfIdempotencyMarkerStore` reads it on the first command that opts
# in. Without it the purge fails hourly and so does that first command,
# both against a table that is not there.
IDEMPOTENCY_MIGRATION = re.compile(r"^\d{14}_AddIdempotencyMarkers(\.Designer)?\.cs$")

# The marker's `CommittedAt` default. The column default and the SQL cutoff that
# reads it are one guarantee, so a service scaffolded without the default ages
# its markers on the pod's clock while the purge ages them on the server's.
COMMITTED_AT_DEFAULT_MIGRATION = re.compile(
    r"^\d{14}_IdempotencyMarkerCommittedAtDefault(\.Designer)?\.cs$"
)

# The marker's `rowversion`. `RetentionPurgeService` names the column in both of
# its marker statements, so a service scaffolded without the migration has a
# purge that raises `Invalid column name 'RowVersion'` on its first pass.
ROW_VERSION_MIGRATION = re.compile(
    r"^\d{14}_AddIdempotencyMarkerRowVersion(\.Designer)?\.cs$"
)
# The outbox's trace context. It travels because the outbox mapping does: the
# copied configuration maps both columns and the dispatcher's claim reads them,
# so a service without the migration fails its first claim on a missing column.
TRACE_CONTEXT_MIGRATION = re.compile(r"^\d{14}_AddOutboxTraceContext(\.Designer)?\.cs$")
LATER_MIGRATION = re.compile(r"^\d{14}_\w+(\.Designer)?\.cs$")

# The template migrations that build §9.4's outbox, which a pure consumer does
# not copy. Its other migrations keep their offsets, so their ids keep the gaps.
PURE_CONSUMER_MIGRATIONS = (OUTBOX_MIGRATION, RETENTION_INDEX_MIGRATION, TRACE_CONTEXT_MIGRATION)

# The migrations a scaffolded service starts with, in the order they are
# applied, which is the order their ids are generated in. A tuple because
# each user needs the position: the id is the base plus the index in
# minutes, and the snapshot is derived from the last one's designer. The
# tuple is the count.
TEMPLATE_MIGRATIONS = (
    INITIAL_CREATE,
    OUTBOX_MIGRATION,
    INBOX_MIGRATION,
    RETENTION_INDEX_MIGRATION,
    IDEMPOTENCY_MIGRATION,
    COMMITTED_AT_DEFAULT_MIGRATION,
    ROW_VERSION_MIGRATION,
    TRACE_CONTEXT_MIGRATION,
)

# The name each shape above is known by in a diagnostic, in the same order.
# Declared beside the shapes so `classify` can say which of the two is short
# as a `ScaffoldError`; a bare `zip(..., strict=True)` raises `ValueError`,
# which `main` does not catch, so the contract of one stderr line and exit 1
# would break.
MIGRATION_LABELS = (
    "InitialCreate",
    "AddOutbox",
    "AddInbox",
    "AddOutboxRetentionIndex",
    "AddIdempotencyMarkers",
    "IdempotencyMarkerCommittedAtDefault",
    "AddIdempotencyMarkerRowVersion",
    "AddOutboxTraceContext",
)

# A service key in a Compose file, at the model's own indent, and the marker
# that bounds one service's block in `.env.example` — the one file that still
# accumulates a block per service.
SERVICE_KEY = re.compile(r"^  ([A-Za-z0-9][A-Za-z0-9_-]*):$")
ENV_MARKER = re.compile(r"^# ([A-Za-z0-9]+)'s two §7\.1 keys")

# §14.1's Compose model is an index and one file per deployable unit, so a
# service's environment is a file this script creates, and the index gains
# one line. The index's `include:` list is the anchor, read rather than
# assumed: two spaces, a dash, a space and a path under the compose
# directory. A list this script cannot find is a template that moved.
COMPOSE_DIR = "deploy/compose"
COMPOSE_INDEX = f"{COMPOSE_DIR}/docker-compose.yml"
COMPOSE_UNITS = "services"
COMPOSE_TEMPLATE_UNIT = f"{COMPOSE_UNITS}/{TEMPLATE.lower()}.yml"
INCLUDE_ENTRY = re.compile(r"^  - (\S+)$")

# One service's `environment:` mapping in that file, and the keys inside it,
# read off the block just rendered rather than the template, because the
# collision below is something the rename creates. Indent is the selector:
# keys sit one level inside the service's own, so `build:`'s nested pair,
# `depends_on:`'s entries and every comment are excluded by position.
ENVIRONMENT_BLOCK = re.compile(r"^    environment:$")
ENVIRONMENT_KEY = re.compile(r"^      ([A-Za-z0-9_]+):(?:\s|$)")

# §14.1 publishes every mapping on loopback: the file's credentials are
# development defaults, so the interface is the control in front of them.
# LOOPBACK is what the render emits and the template must carry; HOST_IP is
# wider on purpose, because a port taken on some other interface is taken
# all the same for the collision check.
LOOPBACK = "127.0.0.1"
HOST_IP = r"\d+\.\d+\.\d+\.\d+"

# The template api's dependencies on the hosts that consume its seed (§14.3).
TEMPLATE_START_ORDER = (
    "      # §14.3's seed is published once, and a fanout publish with no queue bound\n"
    "      # is dropped, so the hosts that consume it are ready first.\n"
    "      ordering-api: { condition: service_healthy }\n"
    "      web-bff: { condition: service_healthy }\n"
)


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
            # Migration file names carry a timestamp, so they are classified
            # by shape: InitialCreate (the hand-written EnsureSchema of §7.4),
            # and the later migrations and the snapshot, which belong to
            # Catalog's model. Three shapes and no others: a helper or README
            # beside the migrations reaches the guard below and is refused.
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

    # Each pair is counted separately, one shape at a time: a single total
    # would be satisfied by duplicates of one migration and none of another,
    # which ships a dispatcher with no table or a purge with no index.
    # The pairing is checked first so a TEMPLATE_MIGRATIONS grown without
    # its label ends in a ScaffoldError rather than a `ValueError`.
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

    # And no patch may be inert. A key for a file that is not copied never
    # reaches `require_once`, so the anchor it guards would be unbound while
    # every other anchor still looked enforced.
    tables = set(PATCHES) | set(WORKER_PATCHES) | set(PURE_CONSUMER_PATCHES) | set(PURE_CONSUMER_SPANS)
    if (inert := tables - set(copied)):
        raise ScaffoldError(
            "a patch table names files the scaffold does not copy: "
            + ", ".join(sorted(inert))
            + ". A patch that never runs is an anchor that guards nothing."
        )

    # The pure consumer's two lists, held to the manifest on the same terms: an
    # omission COPIED does not hold omits nothing, and a patch for a file the
    # mode omits is an anchor no pure render reaches.
    if (stray := set(STAND_INS) - OMITTED):
        raise ScaffoldError("STAND_INS names files OMITTED does not: " + ", ".join(sorted(stray)))
    if (stray := PURE_CONSUMER_OMITTED - COPIED):
        raise ScaffoldError(
            "PURE_CONSUMER_OMITTED names files COPIED does not: " + ", ".join(sorted(stray)))
    if (unreached := (set(PURE_CONSUMER_PATCHES) | set(PURE_CONSUMER_SPANS)) & PURE_CONSUMER_OMITTED):
        raise ScaffoldError(
            "a pure-consumer table names files the mode omits: " + ", ".join(sorted(unreached)))
    return copied


def replace_span(text: str, first: str, last: str, replacement: str, where: str) -> str:
    """The text with everything from `first` through `last` replaced, each anchor bound exactly once."""
    require_once(text, first, where)
    require_once(text, last, where)
    start, end = text.index(first), text.index(last)
    if end < start + len(first):
        raise ScaffoldError(f"{where}: a span's last anchor does not follow its first")
    return text[:start] + replacement + text[end + len(last):]


def pure_consumer_omits(relative: str) -> bool:
    """Whether a pure-consumer render leaves this template file out."""
    if relative in PURE_CONSUMER_OMITTED:
        return True
    name = PurePosixPath(relative).name
    return relative.startswith(MIGRATIONS + "/") and any(
        shape.fullmatch(name) for shape in PURE_CONSUMER_MIGRATIONS)


SLICE_ENTITY = f'            modelBuilder.Entity("{TEMPLATE}.Domain.Products.Product", b =>\n'
# The leading newline matters: without it this matches inside a nested
# block's deeper closer, since sixteen spaces then `});` is a substring of
# twenty-four spaces then `});`, as in the ComplexProperty block inside
# Catalog's aggregate. The check at the end of the function guards it.
ENTITY_END = "\n                });\n\n"
PROJECTION_ENTITY = f'            modelBuilder.Entity("{TEMPLATE}.Infrastructure.Persistence.StockLevel", b =>\n'


def without_slice_entity(designer: str) -> str:
    """The model body with Catalog's aggregate removed, and nothing else touched.

    A publishing service maps the outbox entity and no aggregate, so its
    snapshot is Catalog's minus one `Entity(...)` block, anchored at both ends.
    """
    require_once(designer, SLICE_ENTITY, "the outbox migration's designer")
    start = designer.index(SLICE_ENTITY)

    end = designer.find(ENTITY_END, start)
    if end == -1:
        raise ScaffoldError(
            "the slice entity block in the designer has no closing `});` at its own indent"
        )

    stripped = designer[:start] + designer[end + len(ENTITY_END):]

    # Catalog's projection of its one Consumes cell, which a designer written
    # after AddStockLevels also describes; it is the slice's for the reason
    # StockLevelConfiguration is OMITTED, and it names `ProductId`.
    if PROJECTION_ENTITY in stripped:
        require_once(stripped, PROJECTION_ENTITY, "a designer after AddStockLevels")
        start = stripped.index(PROJECTION_ENTITY)
        end = stripped.find(ENTITY_END, start)
        if end == -1:
            raise ScaffoldError(
                "the projection entity block in the designer has no closing `});` at its own indent"
            )
        stripped = stripped[:start] + stripped[end + len(ENTITY_END):]

    # The aggregate took a using with it. EF emits `using
    # System.Collections.Generic;` for a ComplexProperty mapped as a
    # Dictionary<string, object>, which is how §5.3's Money reaches the
    # model; with the entity gone the using is unreferenced and EF would not
    # write it. If any Dictionary< survives the removal the using stays.
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


# §9.4's outbox entity, the last block in every designer after AddOutbox: EF
# orders entities by name. A pure consumer's model has no outbox, so its
# designers and its snapshot describe none (§7.4).
OUTBOX_ENTITY = '\n            modelBuilder.Entity("Common.Infrastructure.Outbox.OutboxMessage", b =>\n'
LAST_ENTITY_END = "                });\n#pragma warning restore 612, 618\n"


def without_outbox_entity(designer: str, where: str) -> str:
    """The model body with the outbox entity and the blank line above it removed."""
    require_once(designer, OUTBOX_ENTITY, where)
    start = designer.index(OUTBOX_ENTITY)
    end = designer.find(LAST_ENTITY_END, start)
    if end == -1:
        raise ScaffoldError(f"{where}: the outbox entity is no longer the model's last block")
    return designer[:start] + designer[end + len("                });\n"):]


def snapshot_from_designer(designer: str, migration_id: str, migration: str) -> str:
    """The model snapshot, rewritten from the last template migration's designer.

    Catalog's snapshot names `Product` and an earlier designer lacks a later table, so either
    would make the service's first `migrations add` generate a wrong change.
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

    EF writes it sorted; sorting after the rename keeps the file identical to
    the next `dotnet ef migrations add` output in the service.
    """
    lines = text.splitlines(keepends=True)
    first = next((i for i, line in enumerate(lines) if line.startswith("using ")), None)
    if first is None:
        raise ScaffoldError("a machine-owned migration file with no using block")

    last = first
    while last < len(lines) and lines[last].startswith("using "):
        last += 1

    # Keyed on the namespace, not the whole line: `;` sorts after `.`, so a
    # plain line sort puts Microsoft.EntityFrameworkCore.Infrastructure
    # ahead of Microsoft.EntityFrameworkCore, against the tool. System goes
    # first, as EF writes `using System;` above everything else; a service
    # name sorting before it would get a block `migrations add` rewrites.
    def namespace(line: str) -> tuple[int, str]:
        name = line[len("using "):].strip().rstrip(";")
        return (0 if name == "System" or name.startswith("System.") else 1, name)

    lines[first:last] = sorted(lines[first:last], key=namespace)
    return "".join(lines)


def next_migration_id(migration_id: str, minutes: int = 1) -> str:
    """The id `minutes` after the given one, keeping EF's 14-digit shape.

    Parsed and re-formatted, as `int(...) + 1` breaks at every boundary.
    `strptime` and year 9999 raise errors that become a ScaffoldError.
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
    """The projects §4.1 gives the mode, the marker where one is owed, the migration and its snapshot."""
    created: dict[str, str] = {}
    csharp_newline = ""
    last_copied = max(
        index for index, shape in enumerate(TEMPLATE_MIGRATIONS)
        if not (names.pure_consumer and shape in PURE_CONSUMER_MIGRATIONS))

    for relative in classify(repo_root, labels):
        if names.pure_consumer and pure_consumer_omits(relative):
            continue
        text, newline = read(repo_root, relative)
        if relative.endswith(".cs"):
            csharp_newline = newline

        patches = PATCHES.get(relative, ())
        if names.host != API_HOST:
            patches = (*patches, *WORKER_PATCHES.get(relative, ()))
        for needle, replacement in patches:
            require_once(text, needle, relative)
            text = text.replace(needle, replacement)
        # Spans first, then the pure consumer's own patches, so a patch is bound
        # against what the spans left rather than against text they remove.
        if names.pure_consumer:
            for first, last, replacement in PURE_CONSUMER_SPANS.get(relative, ()):
                text = replace_span(text, first, last, replacement, relative)
            for needle, replacement in PURE_CONSUMER_PATCHES.get(relative, ()):
                require_once(text, needle, relative)
                text = text.replace(needle, replacement)

        # The outbox designer describes Catalog's whole model, aggregate
        # included, stripped here so the slice check below sees the result.
        # Every designer, not only the last: each describes the model as of
        # its own migration, so an earlier one left alone would claim a
        # table it never creates.
        if PurePosixPath(relative).name.endswith(
            (
                "_AddOutbox.Designer.cs",
                "_AddInbox.Designer.cs",
                "_AddOutboxRetentionIndex.Designer.cs",
                "_AddIdempotencyMarkers.Designer.cs",
                "_IdempotencyMarkerCommittedAtDefault.Designer.cs",
                "_AddIdempotencyMarkerRowVersion.Designer.cs",
                "_AddOutboxTraceContext.Designer.cs",
            )):
            text = without_slice_entity(text)
            if names.pure_consumer:
                text = without_outbox_entity(text, relative)

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

            # One id per template migration, in the order they are applied,
            # since EF sorts by this prefix: an outbox table ordered before
            # its schema would fail the first run. A minute apart, spaced by
            # position in TEMPLATE_MIGRATIONS rather than by name, so a new
            # migration is one entry in that tuple. The tuple is the count.
            offset = next(
                index for index, shape in enumerate(TEMPLATE_MIGRATIONS) if shape.fullmatch(name)
            )
            new_id = next_migration_id(migration_id, offset) if offset else migration_id
            target = f"{MIGRATIONS}/{name.replace(template_id, new_id, 1)}"
            text = text.replace(template_id, new_id)
            rendered = names.rename(text)
            if name.endswith(".Designer.cs"):
                rendered = sort_usings(rendered)
                if offset == last_copied:
                    # Only the last migration's designer describes the model
                    # the service ends up with, and the snapshot is a
                    # description of exactly that. Last of the ones this mode
                    # copies, since a pure consumer leaves the outbox's out.
                    snapshot = names.rename(
                        snapshot_from_designer(
                            text,
                            new_id,
                            name.split("_", 1)[1].removesuffix(".Designer.cs")))
                    created[names.rename(f"{MIGRATIONS}/{TEMPLATE}DbContextModelSnapshot.cs")] = (
                        restore(sort_usings(snapshot), newline)
                    )

        created[names.rename(target)] = restore(rendered, newline)

    # The marker, or a pure consumer's dispatcher, is the one file with no
    # template beside it to take endings from, so it takes the ones the
    # template's own C# has. Observed rather than assumed: `.gitattributes`
    # decides this, and reading it here carries a change to that rule through.
    if not csharp_newline:
        raise ScaffoldError("no C# file in the template to take line endings from")

    # A pure consumer has no Domain project for the marker to anchor (§4.1), and
    # the dispatcher it registers in that project's place.
    if names.pure_consumer:
        created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Application/NoDomainEventDispatcher.cs")] = (
            restore(names.rename(NO_DOMAIN_EVENT_DISPATCHER), csharp_newline)
        )
    else:
        created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Domain/AssemblyMarker.cs")] = (
            restore(names.rename(ASSEMBLY_MARKER), csharp_newline)
        )
    for template, text in STAND_INS.items():
        created[names.rename(template)] = restore(names.rename(text), csharp_newline)
    return created


def update_solution(repo_root: Path, names: Names) -> str:
    """The service's projects in their own solution folder and its test entries, alphabetical."""
    text, newline = read(repo_root, "Platform.slnx")
    lines = text.splitlines(keepends=True)

    domain = () if names.pure_consumer else ("Domain",)
    folder = [
        f'  <Folder Name="/src/Services/{names.pascal}/">\n',
        *(
            f'    <Project Path="src/Services/{names.pascal}/{names.pascal}.{layer}'
            f'/{names.pascal}.{layer}.csproj" />\n'
            for layer in sorted(("Application", *domain, "Infrastructure", "Migrator", names.host))
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

    domain_tests = () if names.pure_consumer else ("Domain.Tests",)
    tests = [
        f'    <Project Path="tests/{names.pascal}.{suite}/{names.pascal}.{suite}.csproj" />\n'
        for suite in sorted(("Application.Tests", *domain_tests, "TestSupport", f"{names.host}.Tests"))
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

    Per mapping, because §14.1's pair rule renders two services: a key in both
    is ordinary, the same key twice in one mapping is the defect.
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

    Read from the index, not globbed: Compose obeys the index, so a unit
    file no line includes is not part of the model.
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


def update_compose(repo_root: Path, names: Names, port: int | None) -> str:
    """The index gains one line, and nothing else in it moves.

    The port check reads every included file, since the index itself publishes
    nothing; its pattern carries the host-IP prefix §14.1 gives every mapping.
    """
    entries = compose_included(repo_root)

    # A worker publishes nothing, so there is no allocation to collide with —
    # and running the loop with `port is None` would build the pattern `:None:`
    # and find every port free, which is the fail-open shape this check exists
    # to be the opposite of.
    if port is not None:
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


def render_service_compose(repo_root: Path, names: Names, port: int | None) -> str:
    """Catalog's own unit file, renamed, re-ported and re-headed.

    The pair's comments travel with the copy (§7.1); the header above
    `services:` is replaced and names no template token, as `plan` checks.
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

    # §14.3's start order is the template's own: a rendered service seeds
    # nothing those hosts consume, so it waits on none of them.
    require_once(block, TEMPLATE_START_ORDER, f"{COMPOSE_DIR}/{COMPOSE_TEMPLATE_UNIT}")
    block = block.replace(TEMPLATE_START_ORDER, "")

    # The loopback prefix is required of the template rather than copied
    # from it: a prefix read off Catalog would follow whatever Catalog does,
    # so removing the bind there would publish every later service on every
    # interface. Anchored, the same removal is a scaffold that refuses to run.
    published = re.search(rf'ports: \[ "{re.escape(LOOPBACK)}:(\d+):8080" \]', block)
    if published is None:
        raise ScaffoldError(
            f"the template's api block publishes no {LOOPBACK}-bound port to substitute "
            f"(§14.1 binds every mapping to loopback)"
        )

    if port is None:
        # §3.2 gives a worker no API and nothing dials it, so the mapping is
        # removed rather than set to something. The whole line, indent and
        # newline included: a bare substitution would leave a blank line the
        # YAML keeps and a reader reads as an omission.
        block = re.sub(rf'^ *{re.escape(published.group(0))}\n', "", block, flags=re.MULTILINE)
    else:
        block = block.replace(published.group(0), f'ports: [ "{LOOPBACK}:{port}:8080" ]')

    # §7.1's runtime key is `ConnectionStrings__<Service>` and the rename
    # writes it, so a service named after one of §14.1's infrastructure
    # connections renders a key the api block already declares. A predicate
    # over the rendered block, not a list of names, compared casefolded
    # because §14.2 makes configuration keys case-insensitive.
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

    header = f"# {names.pascal}: the pair tools/new-service renders from the template's unit (§4.5).\n"
    return restore(header + block, newline)


def update_infra_only(repo_root: Path, names: Names) -> str:
    """Both halves of the pair join the excluded profile — §14.1's own rule."""
    text, newline = read(repo_root, "deploy/compose/docker-compose.infra-only.yml")

    # The anchor is the contiguous pair, and the pair is also what gets
    # written, so the two cannot drift apart.
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

    Bounded by the next service's marker, not EOF, as in `update_compose`.
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


# What the template's configure and read grant for the StockLevelConsumer.cs the
# render omits. A rendered service subscribes to nothing, and check_permissions.py
# refuses an account that may bind a context its Messaging code never names.
TEMPLATE_SUBSCRIPTION = {
    "configure": r"\.Inventory\.V1:|",
    "read": r"Common\.Contracts\.Inventory\.V1:|",
}


def update_broker_definitions(repo_root: Path, names: Names) -> str:
    """A broker account for the new service, without which it cannot authenticate (§14.1, ADR-036).

    Catalog's publisher grant renamed less its subscription, or a consumer's for a pure consumer; check_permissions.py
    derives what each service needs and fails when a grant is short. The hash is computed, never copied,
    so the template's password does not authenticate under the new name."""
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
    def unsubscribed(verb: str) -> str:
        granted = template_permission[verb]
        cut = TEMPLATE_SUBSCRIPTION.get(verb)
        if cut is None:
            return granted
        if granted.count(cut) != 1:
            raise ScaffoldError(
                f"{relative}: {template_user}'s {verb} grant does not hold its subscription `{cut}` once")
        return granted.replace(cut, "")

    if names.pure_consumer:
        # A consumer's shape, not the template's publisher's: it subscribes to
        # nothing yet, so it binds no contract exchange, and writes only its own
        # endpoints and the fault exchanges, since §3.2 gives it nothing to publish.
        bound = f"^({names.lower}-|MassTransit:)"
        faults = f"^({names.lower}-|MassTransit:(ReceiveFault$|Fault--))"
        grant = {"configure": bound, "write": bound, "read": faults}
    else:
        grant = {verb: names.rename(unsubscribed(verb)) for verb in ("configure", "write", "read")}

    definitions["permissions"].append({
        "user": user,
        "vhost": template_permission["vhost"],
        **grant,
    })

    return restore(json.dumps(definitions, indent=2) + "\n", newline)


REDIS_USERS = f"{COMPOSE_DIR}/redis/users.conf"


def update_redis_users(repo_root: Path, names: Names) -> str:
    """The template's §8.1 Redis user renamed, which the rendered unit's connection strings name."""
    text, newline = read(repo_root, REDIS_USERS)
    lines = text.split("\n")

    user = f"user {names.lower}-svc "
    if any(line.startswith(user) for line in lines):
        raise ScaffoldError(f"{REDIS_USERS}: a Redis user named {names.lower}-svc already exists")

    # Renamed whole, so the key pattern follows the host's ApplicationName (§8.3).
    template = [line for line in lines if line.startswith(f"user {TEMPLATE.lower()}-svc ")]
    if len(template) != 1:
        raise ScaffoldError(
            f"{REDIS_USERS}: expected exactly one {TEMPLATE.lower()}-svc user to copy, "
            f"found {len(template)} (§8.1)")

    return restore(text.rstrip("\n") + "\n" + names.rename(template[0]) + "\n", newline)


# §13.2's export names meters one by one, so a service's own meter is a line in
# a building block rather than something its own tree can declare. The line is
# written here for the reason the broker account is: without it the rendered
# service publishes §13.6's gauges and the platform collects none of them, and
# nothing else in the render would say so.
OBSERVABILITY = "src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs"

# The anchor is the shared block's first line rather than the template's own
# meter, because a service's meters are listed in §4.1's order and the
# template's sits at the top of that list. Read rather than assumed: a list
# this script cannot find is a building block that has moved.
SHARED_METERS = "                // Shared names, not service-prefixed: the service.name resource attribute separates them.\n"


def update_observability_meters(repo_root: Path, names: Names) -> str:
    """One `AddMeter` line for the rendered service's outbox meter (§13.2)."""
    text, newline = read(repo_root, OBSERVABILITY)

    line = f'                .AddMeter("{names.pascal}.Outbox")'
    if line in text:
        raise ScaffoldError(
            f"{OBSERVABILITY} already registers {names.pascal}.Outbox; this script "
            f"adds the line and never a second copy of it")

    require_once(text, "\n" + SHARED_METERS, OBSERVABILITY)
    padded = line.ljust(67) + "// §13.6 per-lane\n"
    # Before the blank line, so the new meter joins the service-prefixed
    # group instead of opening the shared one (§4.1's order, §13.2's export).
    return restore(text.replace("\n" + SHARED_METERS, padded + "\n" + SHARED_METERS), newline)
