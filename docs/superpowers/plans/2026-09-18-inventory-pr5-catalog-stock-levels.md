# Inventory PR-5 — Catalog consumes StockLevelChanged — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give §3.2's one Catalog Consumes cell a consumer: a
`catalog-inventory-events` queue, a `catalog.StockLevels` projection with an
`OccurredAt` watermark, and a nullable `quantityAvailable` on the product
listing.

**Architecture:** Catalog gains its first receive endpoint, in the shape of
Ordering's `ordering-catalog-events`. The handler is a Dapper `MERGE` in the
shape of Ordering's `ProductPriceProjection`, keyed on `ProductId` and
guarded by `AsOf < @OccurredAt`. `GetProductsHandler` left-joins the new
table. No cache exists in Catalog, so no invalidator is written.

**Tech Stack:** MassTransit receive endpoint with the inbox filter, Dapper,
an EF-generated migration for the read model mapped on §7.4's `ProductPrices`
terms, xUnit with Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-18-inventory-service-design.md`,
sections 9 (the Catalog bullet) and 14.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D.** Touch set: `src/Services/Catalog/**`, `tests/Catalog.*`,
  `tools/new-service/**` (Catalog is the scaffold's template, so every
  Catalog-only file is classified and every Catalog-only line in a copied
  file is patched out — see Task 5), and
  `deploy/compose/rabbitmq/definitions.json` (the D half: `catalog-svc`'s
  grant admits no queue and reads no other context's exchange today).
- Catalog-only messaging code lives in Catalog-only files. The copied
  `DependencyInjection.cs` carries two one-line calls into them and nothing
  else, so the scaffold has two lines to strip rather than a block.
- Depends on PR-2 having merged, so a real producer exists; nothing here
  compiles against Inventory.
- `null` and `0` are different facts: a product Inventory has never reported
  lists `null`.
- Every step that adds behaviour writes its test first; container tests are
  never skipped.

---

### Task 1: The table

**Files:**
- Create: `src/Services/Catalog/Catalog.Infrastructure/Persistence/StockLevelConfiguration.cs`
- Create: `src/Services/Catalog/Catalog.Infrastructure/Persistence/Migrations/<ts>_AddStockLevels.cs`
- Test: `tests/Catalog.Api.Tests/StockLevelsSchemaTests.cs` — a new
  Catalog-only file, not an addition to `DatabaseSmokeTests.cs`, which the
  scaffold copies into every service: an assertion about `catalog.StockLevels`
  in a copied file is a failing test in every render.

**Interfaces:**
- `catalog.StockLevels(ProductId uniqueidentifier PK, QuantityAvailable int
  NOT NULL, AsOf datetimeoffset(7) NOT NULL)`. Mapped through an
  `IEntityTypeConfiguration<StockLevel>` on an internal `StockLevel` class
  so `migrations add` emits it — the terms §7.4 states for `ProductPrices`
  and the spec's section 14 names for this table — and never read through
  EF.

- [ ] **Step 1: Write the failing schema test**

In `StockLevelsSchemaTests.cs`, a `[Collection(nameof(IntegrationCollection))]`
class over `ServiceFixture` in `DatabaseSmokeTests`' shape:

```csharp
[Fact]
public async Task The_migrator_creates_the_stock_level_projection()
{
    (await fixture.ScalarAsync<int>(
        "SELECT Value = COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('catalog') AND name = 'StockLevels'"))
        .ShouldBe(1);
}
```

- [ ] **Step 2: Run to see it fail; write the configuration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// §3.2's one Catalog projection: Inventory's level per product, with the
/// watermark the contract's remark asks for. Mapped so the migration is
/// generated; read only by Dapper.
/// </summary>
internal sealed class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        builder.ToTable("StockLevels", "catalog");
        builder.HasKey(s => s.ProductId);
        builder.Property(s => s.ProductId).ValueGeneratedNever();
        builder.Property(s => s.QuantityAvailable).IsRequired();
        builder.Property(s => s.AsOf).IsRequired();
    }
}

internal sealed class StockLevel
{
    public Guid ProductId { get; set; }
    public int QuantityAvailable { get; set; }
    public DateTimeOffset AsOf { get; set; }
}
```

```bash
dotnet ef migrations add AddStockLevels \
    --project src/Services/Catalog/Catalog.Infrastructure \
    --startup-project src/Services/Catalog/Catalog.Migrator \
    --output-dir Persistence/Migrations
```

- [ ] **Step 3: Run the smoke test; commit**

```bash
dotnet test tests/Catalog.Api.Tests --filter StockLevelsSchemaTests
git add src/Services/Catalog/Catalog.Infrastructure tests/Catalog.Api.Tests
git commit -m "feat(catalog): the StockLevels projection table"
```

---

### Task 2: The projection handler

**Files:**
- Create: `src/Services/Catalog/Catalog.Infrastructure/Projections/StockLevelProjection.cs`
- Test: `tests/Catalog.Api.Tests/StockLevelProjectionTests.cs`

**Interfaces:**
- `StockLevelProjection(IDbConnectionFactory) : IIntegrationEventHandler<StockLevelChanged>`.

- [ ] **Step 1: Write the failing tests**

Drive the handler directly from a scope, the way Ordering's
`ProductPriceProjectionTests` do:

```csharp
using Catalog.TestSupport;
using Common.Application;
using Common.Contracts.Inventory.V1;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class StockLevelProjectionTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task Apply(Guid product, int level, DateTimeOffset at)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        // As its interface: AddPluggableFrom registers with AsImplementedInterfaces(),
        // so the concrete type is not resolvable, the same as Ordering's
        // ProductPriceProjectionTests resolve.
        IIntegrationEventHandler<StockLevelChanged> projection =
            scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<StockLevelChanged>>();
        await projection.HandleAsync(
            new StockLevelChanged
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = product,
                OccurredAt = at,
                ProductId = product,
                QuantityAvailable = level
            },
            TestContext.Current.CancellationToken);
    }

    private Task<int?> Level(Guid product) =>
        fixture.ScalarAsync<int?>("SELECT Value = QuantityAvailable FROM catalog.StockLevels WHERE ProductId = {0}", product);

    [Fact]
    public async Task A_first_level_inserts()
    {
        var product = Guid.CreateVersion7();

        await Apply(product, 5, T0);

        (await Level(product)).ShouldBe(5);
    }

    [Fact]
    public async Task A_newer_level_replaces_and_an_older_one_arriving_late_does_not()
    {
        var product = Guid.CreateVersion7();
        await Apply(product, 5, T0);

        await Apply(product, 3, T0.AddSeconds(10));
        await Apply(product, 9, T0.AddSeconds(5));

        (await Level(product)).ShouldBe(3, "§9.4 orders nothing; the watermark does");
    }

    [Fact]
    public async Task The_same_level_twice_is_one_row()
    {
        var product = Guid.CreateVersion7();

        await Apply(product, 5, T0);
        await Apply(product, 5, T0);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.StockLevels WHERE ProductId = {0}", product))
            .ShouldBe(1);
    }
}
```

`Common.Application` supplies `IIntegrationEventHandler<>` and is not a
global using in this test project; the handler's concrete namespace is not
imported because nothing resolves the concrete type.

- [ ] **Step 2: Run to see them fail; write the handler**

```csharp
using System.Data;
using Common.Application;
using Common.Contracts.Inventory.V1;
using Dapper;

namespace Catalog.Infrastructure.Projections;

/// <summary>
/// A level, not a delta, with a watermark on OccurredAt: a stale level
/// arriving late changes nothing, and a redelivered one changes nothing
/// twice (§9.4, the contract's own remark).
/// </summary>
public sealed class StockLevelProjection(IDbConnectionFactory connections)
    : IIntegrationEventHandler<StockLevelChanged>
{
    private const string UpsertSql =
        """
        MERGE catalog.StockLevels WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId) AS source
            ON target.ProductId = source.ProductId
        WHEN NOT MATCHED THEN
            INSERT (ProductId, QuantityAvailable, AsOf)
            VALUES (@ProductId, @QuantityAvailable, @OccurredAt)
        WHEN MATCHED AND target.AsOf < @OccurredAt THEN
            UPDATE SET QuantityAvailable = @QuantityAvailable, AsOf = @OccurredAt;
        """;

    public async Task HandleAsync(StockLevelChanged integrationEvent, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(
            UpsertSql,
            new { integrationEvent.ProductId, integrationEvent.QuantityAvailable, integrationEvent.OccurredAt },
            cancellationToken: ct));
    }
}
```

`HOLDLOCK` on the `MERGE` is what keeps two deliveries for one new product
from both taking the `NOT MATCHED` branch.

- [ ] **Step 3: Run the tests; commit**

```bash
dotnet test tests/Catalog.Api.Tests --filter StockLevelProjectionTests
git add src/Services/Catalog/Catalog.Infrastructure tests/Catalog.Api.Tests
git commit -m "feat(catalog): project Inventory's level under an OccurredAt watermark"
```

---

### Task 3: The receive endpoint, and the test that said there was none

**Files:**
- Create: `src/Services/Catalog/Catalog.Infrastructure/Messaging/StockLevelConsumer.cs`
  — Catalog-only: the queue name and the two registration halves
- Create: `src/Services/Catalog/Catalog.Infrastructure/Messaging/RetryPolicy.cs`
  — Ordering's `RetryPolicy` is `internal` to `Ordering.Infrastructure`
  and Catalog, having had no endpoint, has none; Ordering's file with the
  namespace changed, for the reason PR-2 gives when Inventory takes the
  same copy. Classified `COPIED` in Task 5: a rendered service's first
  endpoint wants it, and it names nothing of Catalog's
- Modify: `src/Services/Catalog/Catalog.Infrastructure/Messaging/DependencyInjection.cs`
  — two one-line calls into that file, and its `using`
- Modify: `tests/Catalog.Api.Tests/MessagingRegistrationTests.cs` — the
  no-consumer test leaves this copied file (Task 5 says where it goes)
- Create: `tests/Catalog.Api.Tests/StockLevelRegistrationTests.cs` —
  Catalog-only: the one-consumer assertion
- Create: `tests/Catalog.Api.Tests/InventoryEventEndpointTests.cs`
- Modify: `tests/Catalog.TestSupport/ServiceFixture.cs` — the harness-only
  broker widening
- Modify: `deploy/compose/rabbitmq/definitions.json` — `catalog-svc`'s
  patterns

**Interfaces:**
- `StockLevelConsumer.Queue = "catalog-inventory-events"`;
  `static void AddStockLevelConsumer(this IBusRegistrationConfigurator x)`;
  `static void ConfigureStockLevelEndpoint(this IRabbitMqBusFactoryConfigurator cfg, IBusRegistrationContext context)`.

- [ ] **Step 1: Move the no-consumer test and write the one-consumer test**

`Catalog_binds_no_consumer_and_therefore_declares_no_receive_endpoint` is
true of every rendered service and false of Catalog from this PR on, and it
sits in a file the scaffold copies. Cut it from `MessagingRegistrationTests`
along with the comment paragraph that says Inventory does not exist and
that §8.4's invalidator needs a cached query; Task 5 puts the assertion
where it now belongs. Keep `The_no_consumer_assertion_can_actually_fail`
and `IsConsumerRegistration`, renaming the test
`The_consumer_assertion_can_actually_see_a_consumer`, and make
`IsConsumerRegistration` `internal static` so the new file can use it.

`StockLevelRegistrationTests.cs`:

```csharp
using Catalog.Infrastructure.Messaging;
using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

public class StockLevelRegistrationTests
{
    [Fact]
    public void Catalog_binds_exactly_the_one_consumer_in_its_consumes_column()
    {
        ServiceCollection services = new();

        services.AddMassTransitMessaging(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", "amqp://guest:guest@catalog-rabbit.invalid:5672")])
                .Build());

        services
            .Where(MessagingRegistrationTests.IsConsumerRegistration)
            .Select(d => d.ImplementationType ?? d.ServiceType)
            .Distinct()
            .ShouldBe([typeof(IntegrationEventConsumer<StockLevelChanged>)],
                "§3.2 gives Catalog one Consumes cell, StockLevelChanged, and a second consumer here is a " +
                "subscription the table does not give it");
    }
}
```

- [ ] **Step 2: Write the failing endpoint test**

Over containers, publishing `StockLevelChanged` through `IPublishEndpoint`
and waiting on the inbox row for `StockLevelConsumer.Queue`, then asserting
the `catalog.StockLevels` row. A second publish with the same `MessageId`
must leave one inbox row and one level row.

- [ ] **Step 3: Write the consumer file and the two calls**

`StockLevelConsumer.cs`:

```csharp
using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;

namespace Catalog.Infrastructure.Messaging;

/// <summary>
/// §3.2's Consumes column for Catalog, and exactly it. Its own file because
/// Catalog is the scaffold's template and a rendered service subscribes to
/// nothing: the two calls into this file are what the scaffold strips.
/// </summary>
internal static class StockLevelConsumer
{
    public const string Queue = "catalog-inventory-events";

    public static void AddStockLevelConsumer(this IBusRegistrationConfigurator x) =>
        x.AddConsumer<IntegrationEventConsumer<StockLevelChanged>>();

    public static void ConfigureStockLevelEndpoint(
        this IRabbitMqBusFactoryConfigurator cfg,
        IBusRegistrationContext context) =>
        cfg.ReceiveEndpoint(
            Queue,
            e =>
            {
                e.UseMessageRetry(r => RetryPolicy.Standard(r));
                e.UseConsumeFilter(typeof(InboxFilter<>), context);
                e.UseInMemoryOutbox(context);

                e.ConfigureConsumer<IntegrationEventConsumer<StockLevelChanged>>(context);
            });
}
```

In `DependencyInjection.AddMassTransitMessaging`, one line in each half:

```csharp
x.AddStockLevelConsumer();
```

inside `AddMassTransit` after `DisableUsageTelemetry()`, and

```csharp
cfg.ConfigureStockLevelEndpoint(context);
```

inside `UsingRabbitMq` after `cfg.Host(...)`. Confirm the inbox filter's
dependencies (`InboxTable`, the `DbContext` alias) are registered by
`AddCatalogInfrastructure`, which the scaffold README says they are.

- [ ] **Step 4: The broker, in production and in the harness**

`catalog-svc`'s patterns in `deploy/compose/rabbitmq/definitions.json`
admit its own contract exchanges and MassTransit's and nothing else — a
publisher's grant, and the read pattern names only
`Common\.Contracts\.Catalog\.V1:`. A receive endpoint declares a queue and
binds another context's exchange, so:

```json
"configure": "^(catalog-|Common\\.Contracts(\\.Catalog\\.V1:|:)|MassTransit:)",
"write": "^(catalog-|Common\\.Contracts(\\.Catalog\\.V1:|:)|MassTransit:)",
"read": "^(catalog-|Common\\.Contracts\\.Catalog\\.V1:|Common\\.Contracts\\.Inventory\\.V1:|MassTransit:)"
```

`write` gains the queue and nothing of Inventory's: `check_permissions.py`
refuses a context writing another's exchange, and reading one is all a
consumer needs. Run it and its suite:

```bash
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
```

The endpoint test publishes `StockLevelChanged` as `catalog-svc`, which
that grant refuses by design. `Catalog.TestSupport/ServiceFixture.cs` gains
Ordering's `WidenWriteForTheHarnessAsync` — a `rabbitmqctl set_permissions`
against the test container alone, after it starts and before the factory is
built — with `catalog-svc` and a scope of
`^(catalog-|Common\.Contracts|MassTransit:)`. The production file does not
move for the harness's sake.

- [ ] **Step 5: Run the API suite; commit**

```bash
dotnet test tests/Catalog.Api.Tests
git add src/Services/Catalog tests/Catalog.Api.Tests tests/Catalog.TestSupport deploy/compose/rabbitmq/definitions.json
git commit -m "feat(catalog): bind StockLevelChanged on catalog-inventory-events"
```

---

### Task 4: `quantityAvailable` on the listing

**Files:**
- Modify: `src/Services/Catalog/Catalog.Application/Products/GetProducts/ProductSummaryDto.cs`
  (add `int? QuantityAvailable` as the last member)
- Modify: `.../GetProducts/GetProductsHandler.cs` (left join)
- Test: `tests/Catalog.Application.Tests/GetProductsHandlerTests.cs` (extend)
- Test: `tests/Catalog.Api.Tests/ProductEndpointsTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

In the handler tests, after seeding a product and a `StockLevels` row for it
and a second product with no row:

```csharp
[Fact]
public async Task A_reported_product_lists_its_level_and_an_unreported_one_lists_null()
{
    // arrange two products in catalog.Products as the existing tests do, then:
    await fixture.ExecuteAsync(
        "INSERT INTO catalog.StockLevels (ProductId, QuantityAvailable, AsOf) VALUES ({0}, 4, SYSDATETIMEOFFSET())", reported);

    CursorPage<ProductSummaryDto> page = await Handler().HandleAsync(new GetProductsQuery(null, 20), TestContext.Current.CancellationToken);

    page.Items.Single(p => p.ProductId == reported).QuantityAvailable.ShouldBe(4);
    page.Items.Single(p => p.ProductId == unreported).QuantityAvailable.ShouldBeNull(
        "unknown and none are different facts to a screen");
}
```

In the endpoint tests, assert the JSON carries `"quantityAvailable": null`
for an unreported product rather than omitting the member.

- [ ] **Step 2: Run to see them fail; change the query**

```sql
SELECT TOP (@Take)
    ProductId         = p.Id,
    Name              = p.Name,
    ThumbnailUrl      = p.ThumbnailUrl,
    Amount            = p.PriceAmount,
    Currency          = p.PriceCurrency,
    PublishedAt       = p.PublishedAt,
    QuantityAvailable = s.QuantityAvailable
FROM catalog.Products p
LEFT JOIN catalog.StockLevels s ON s.ProductId = p.Id
WHERE (@AfterPublishedAt IS NULL
    OR p.PublishedAt < @AfterPublishedAt
    OR (p.PublishedAt = @AfterPublishedAt AND p.Id < @AfterId))
ORDER BY p.PublishedAt DESC, p.Id DESC;
```

`ProductSummaryDto` gains `int? QuantityAvailable` last, so Dapper's
positional constructor mapping keeps working for the existing columns.

- [ ] **Step 3: Run both suites; commit**

```bash
dotnet test tests/Catalog.Application.Tests tests/Catalog.Api.Tests
git add src/Services/Catalog/Catalog.Application tests/Catalog.Application.Tests tests/Catalog.Api.Tests
git commit -m "feat(catalog): the listing carries Inventory's level, null when unreported"
```

---

### Task 5: The scaffold still renders

**Files:**
- Modify: `tools/new-service/new_service.py` — the `OMITTED` set gains
  every Catalog-only file this PR creates:
  `src/Services/Catalog/Catalog.Infrastructure/Persistence/StockLevelConfiguration.cs`,
  `src/Services/Catalog/Catalog.Infrastructure/Projections/StockLevelProjection.cs`,
  `src/Services/Catalog/Catalog.Infrastructure/Messaging/StockLevelConsumer.cs`,
  `tests/Catalog.Api.Tests/StockLevelsSchemaTests.cs`,
  `tests/Catalog.Api.Tests/StockLevelProjectionTests.cs`,
  `tests/Catalog.Api.Tests/StockLevelRegistrationTests.cs`,
  `tests/Catalog.Api.Tests/InventoryEventEndpointTests.cs`; and `COPIED`
  gains `src/Services/Catalog/Catalog.Infrastructure/Messaging/RetryPolicy.cs`.
  The script refuses a Catalog file in neither set, so leaving any one out
  fails the render before it writes. And one new anchored patch on
  the copied `Catalog.Infrastructure/Messaging/DependencyInjection.cs`,
  removing the three Catalog-only lines Task 3 added — the `using`, the
  `AddStockLevelConsumer()` call and the `ConfigureStockLevelEndpoint(context)`
  call — each an anchor that must match exactly once, in the shape of the
  script's existing slice patches. And the script's existing patch entry for
  `tests/Catalog.Api.Tests/MessagingRegistrationTests.cs` is **deleted**:
  both of its anchors — the comment paragraph naming Inventory and the
  assertion message naming Catalog — leave that file in Task 3, so the
  patch would refuse the render on a missing anchor before writing.
- Modify: `tools/new-service/test_new_service.py` — the rendered-text
  assertion that replaces the test Task 3 cut: the rendered
  `DependencyInjection.cs` contains no `AddConsumer`, no `ReceiveEndpoint`
  and no `StockLevel`, because a rendered service subscribes to nothing.

- [ ] **Step 1: Run the scaffold's suite to see it fail**

```bash
py -3.12 -m unittest discover -s tools/new-service
```

Expected: a failure naming the first unclassified file.

- [ ] **Step 2: Classify, patch and assert**

Add the files to `OMITTED` beside the `Products` slice entries. Delete the
`MessagingRegistrationTests.cs` patch entry and the test that exercised it. Add
the three-line patch with its test, which renders the file and asserts the three
lines are gone and the rest of the registration is byte-for-byte Catalog's. Add
the no-consumer assertion to the scaffold's suite. Re-run the suite green. The
messaging registration is wiring the script already patches (the `RabbitMq`
connection-string line), so check the existing anchor still matches exactly once
beside the new one.

- [ ] **Step 3: Dogfood, on a clean tree only**

```bash
git status --short          # must print nothing; commit or stop otherwise
py -3.12 tools/new-service/new_service.py Probe --port 5199
dotnet build Platform.slnx
rm -rf src/Services/Probe tests/Probe.* deploy/compose/services/probe.yml
git checkout -- Platform.slnx deploy/compose/ .github/secret-scan/allowed/
```

The first line is the guard: the undo restores whole tracked files, and on
a tree carrying unrelated edits it would discard them, which the repository
forbids. The scaffold README says the same — run it on a clean worktree —
and a dirty tree here means commit the pending task first, never proceed.

Expected: the rendered service builds. The PR body carries the evidence.

- [ ] **Step 4: Commit**

```bash
git add tools/new-service
git commit -m "chore(tooling): the scaffold leaves Catalog's stock projection behind"
```

---

### Task 6: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green.
- [ ] Neither `/validate-blueprint` nor `/check-links` is owed.
- [ ] PR body: `| Class | A+D |`, touch set from Global Constraints.

## Self-review

- Spec coverage: section 14's three bullets → Tasks 1–2 (table and
  projection), 4 (the column, null semantics), 3 (the test comment cut and
  the invalidator explicitly not written); section 9's Catalog tests →
  Tasks 2, 3, 4.
- Types: `StockLevelProjection`, `StockLevelConsumer.Queue`,
  `ProductSummaryDto.QuantityAvailable` agree across tasks.
- The broker grant, the harness widening and the scaffold's patch are each
  stated unconditionally, with the gate that refuses their absence named.
