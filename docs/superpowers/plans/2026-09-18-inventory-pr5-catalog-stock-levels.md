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
a hand-written migration for the read-model table (§7.4), xUnit with
Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-18-inventory-service-design.md`,
sections 9 (the Catalog bullet) and 14.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A.** Touch set: `src/Services/Catalog/**`, `tests/Catalog.*`,
  `tools/new-service/**` (Catalog is the scaffold's template and gains
  files, so the scaffold's suite must still pass and its exclusion list may
  need the new files named — see Task 5).
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
- Test: `tests/Catalog.Api.Tests/DatabaseSmokeTests.cs` (extend)

**Interfaces:**
- `catalog.StockLevels(ProductId uniqueidentifier PK, QuantityAvailable int
  NOT NULL, AsOf datetimeoffset(7) NOT NULL)`. Mapped through an
  `IEntityTypeConfiguration<StockLevel>` on an internal `StockLevel` class
  so `migrations add` emits it — the terms §7.4 states for `ProductPrices`
  and the spec's section 14 names for this table — and never read through
  EF.

- [ ] **Step 1: Write the failing smoke test**

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
dotnet test tests/Catalog.Api.Tests --filter DatabaseSmokeTests
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
using Catalog.Infrastructure.Projections;
using Catalog.TestSupport;
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

Add `using Common.Application;` and `using Common.Contracts.Inventory.V1;`
for the interface and the contract.

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
- Modify: `src/Services/Catalog/Catalog.Infrastructure/Messaging/DependencyInjection.cs`
- Modify: `tests/Catalog.Api.Tests/MessagingRegistrationTests.cs`
- Test: `tests/Catalog.Api.Tests/InventoryEventEndpointTests.cs`

**Interfaces:**
- `public const string InventoryEventsQueue = "catalog-inventory-events"`.

- [ ] **Step 1: Rewrite the registration test**

Replace `Catalog_binds_no_consumer_and_therefore_declares_no_receive_endpoint`
with:

```csharp
[Fact]
public void Catalog_binds_exactly_the_one_consumer_in_its_consumes_column()
{
    ServiceCollection services = new();

    services.AddMassTransitMessaging(Configuration());

    services
        .Where(d => IsConsumerRegistration(d))
        .Select(d => d.ImplementationType ?? d.ServiceType)
        .Distinct()
        .ShouldBe([typeof(IntegrationEventConsumer<StockLevelChanged>)],
            "§3.2 gives Catalog one Consumes cell, StockLevelChanged, and a second consumer here is a " +
            "subscription the table does not give it");
}
```

Keep `The_no_consumer_assertion_can_actually_fail` as the positive control,
renamed to `The_consumer_assertion_can_actually_see_a_consumer`. Cut the
comment paragraph that says Inventory does not exist and that §8.4's
invalidator needs a cached query.

- [ ] **Step 2: Write the failing endpoint test**

Over containers, publishing `StockLevelChanged` through `IPublishEndpoint`
and waiting on the inbox row for `InventoryEventsQueue`, then asserting the
`catalog.StockLevels` row. A second publish with the same `MessageId` must
leave one inbox row and one level row.

- [ ] **Step 3: Write the endpoint**

```csharp
public const string InventoryEventsQueue = "catalog-inventory-events";

services.AddMassTransit(x =>
{
    x.DisableUsageTelemetry();

    // §3.2's Consumes column for Catalog, and exactly it.
    x.AddConsumer<IntegrationEventConsumer<StockLevelChanged>>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(new Uri(connectionString));

        cfg.ReceiveEndpoint(
            InventoryEventsQueue,
            e =>
            {
                e.UseMessageRetry(r => RetryPolicy.Standard(r));
                e.UseConsumeFilter(typeof(InboxFilter<>), context);
                e.UseInMemoryOutbox(context);

                e.ConfigureConsumer<IntegrationEventConsumer<StockLevelChanged>>(context);
            });
    });
});
```

Catalog's `AddMassTransitMessaging` gains `IConfiguration` already; confirm
the inbox filter's dependencies (`InboxTable`, the `DbContext` alias) are
registered by `AddCatalogInfrastructure`, which the scaffold README says
they are.

The broker permission for `catalog-svc` must admit a `catalog-` queue and
reading `Common.Contracts` exchanges; check
`deploy/compose/rabbitmq/definitions.json`'s `catalog-svc` patterns and,
if the read pattern does not cover `Common.Contracts.Inventory.V1:`, widen
it in the same PR and name `deploy/compose/rabbitmq/definitions.json` in the
touch set (Class D beside A, declared as `A+D`).

- [ ] **Step 4: Run the API suite; commit**

```bash
dotnet test tests/Catalog.Api.Tests
git add src/Services/Catalog tests/Catalog.Api.Tests deploy/compose/rabbitmq/definitions.json
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
- Modify: `tools/new-service/new_service.py` — the `OMITTED` set gains the
  four Catalog-specific files this PR creates:
  `src/Services/Catalog/Catalog.Infrastructure/Persistence/StockLevelConfiguration.cs`,
  `src/Services/Catalog/Catalog.Infrastructure/Projections/StockLevelProjection.cs`,
  `tests/Catalog.Api.Tests/StockLevelProjectionTests.cs`,
  `tests/Catalog.Api.Tests/InventoryEventEndpointTests.cs`. The script
  refuses a Catalog file in neither `COPIED` nor `OMITTED`, so leaving any
  one out fails the render before it writes.

- [ ] **Step 1: Run the scaffold's suite to see it fail**

```bash
py -3.12 -m unittest discover -s tools/new-service
```

Expected: a failure naming the first unclassified file.

- [ ] **Step 2: Classify the four files as omitted**, beside the `Products`
  slice entries, and re-run. The messaging registration is wiring the
  script patches; because Task 3 changed `AddMassTransitMessaging`, check
  that the anchor it patches still matches exactly once and update the
  anchor and its test if not, with the care the script's README asks for.

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
- [ ] PR body: `| Class | A |` (or `A+D` if Task 3 widened the broker
  pattern), touch set from Global Constraints.

## Self-review

- Spec coverage: section 14's three bullets → Tasks 1–2 (table and
  projection), 4 (the column, null semantics), 3 (the test comment cut and
  the invalidator explicitly not written); section 9's Catalog tests →
  Tasks 2, 3, 4.
- Types: `StockLevelProjection`, `InventoryEventsQueue`,
  `ProductSummaryDto.QuantityAvailable` agree across tasks.
- The one thing the implementer must check before Task 3 is the broker
  permission pattern; the plan says what to do in both cases.
