# Inventory PR-1 — third service from the scaffold — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land `src/Services/Inventory` from the scaffold with one aggregate,
`StockItem`, its two admin endpoints, and everything the platform needs to
build, run and reach it locally: the Compose pair, the gateway's
`depends_on`, CI's filter and image matrix, and the realm grant that lets a
local login set stock.

**Architecture:** `py -3.12 tools/new-service/new_service.py Inventory --port
5103` renders the service and test projects §4.1 names and the shared-file
edits; this PR adds the `StockItem` slice on top in Ordering's shapes — a
typed id, an `AggregateRoot<ProductId>`, an `IEntityTypeConfiguration`, one
command, one query, one endpoint file — and touches the tracked files the
pipeline gate and the local platform demand.

**Tech Stack:** .NET at `global.json`'s pin, EF Core with SQL Server, Dapper
on the read side, MassTransit (scaffolded, no consumers yet), xUnit with
Shouldly and Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-18-inventory-service-design.md`,
sections 1, 2, 3 (the `StockItem` half), 6 (the two stock endpoints), 7,
11 and 12.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+B+D+E.** Touch set: `src/Services/Inventory/**`,
  `tests/Inventory.*`, `Platform.slnx` and the rendered `*.csproj` files
  (the E half: the scaffold adds projects to the solution, and Task 4 adds a
  package reference the scaffold stripped), `deploy/compose/**`,
  `.github/secret-scan/allowed/**`, `.github/workflows/ci.yml`,
  `deploy/observability/check.py` (the exemption in Task 7),
  `deploy/compose/keycloak/realm-export.json`,
  `tests/Common.Web.Tests/RealmImportTests.cs` (the B half: the building
  block's test that pins the realm's grants), and the three prose sites in
  section 10 of the spec.
- No new package: no `Directory.Packages.props` change, no Appendix B row.
- Comments say why and cite the owner. No history, no PR names.
- Explicit local types, file-scoped namespaces, 120 columns.
- `py -3.12`, never `python`, for anything Python.
- Container tests are `[Collection(nameof(IntegrationCollection))]` and never
  skipped: without a daemon they fail.
- Every step that adds behaviour writes its test first.

---

### Task 1: Run the scaffold and prove the empty service

**Files:**
- Create (by the script): `src/Services/Inventory/**`, `tests/Inventory.*/**`,
  `deploy/compose/services/inventory.yml`
- Modify (by the script): `Platform.slnx`,
  `deploy/compose/docker-compose.yml`,
  `deploy/compose/docker-compose.infra-only.yml`,
  `deploy/compose/.env.example`, `deploy/compose/README.md`,
  `deploy/compose/rabbitmq/definitions.json`,
  `.github/secret-scan/allowed/*.txt`

**Interfaces:**
- Produces: `Inventory.Api`, `Inventory.Application`, `Inventory.Domain`,
  `Inventory.Infrastructure`, `Inventory.Migrator`; `InventoryDbContext` with
  default schema `inventory`; `AddInventoryApplication()`,
  `AddInventoryInfrastructure(IConfiguration)`; `InventoryPermissions` with
  one commented-out policy line in `Program.cs`; the test fixtures
  `ServiceFixture`, `InventoryApiFactory`, `TestAuthHandler` in
  `tests/Inventory.TestSupport`.

- [ ] **Step 1: Confirm the tree is clean and the port is free**

Run: `git status --short` (expect empty) and
`grep -n "5103" deploy/compose/README.md deploy/compose/services/*.yml`
(expect no match).

- [ ] **Step 2: Run the scaffold**

```bash
py -3.12 tools/new-service/new_service.py Inventory --port 5103
```

Expected: the script prints the files it wrote and ends with
`Next: dotnet restore Platform.slnx && dotnet build Platform.slnx`.

- [ ] **Step 3: Build**

```bash
dotnet restore Platform.slnx && dotnet build Platform.slnx
```

Expected: 0 warnings, 0 errors. A warning is a failed build under ADR-019.

- [ ] **Step 4: Run the scaffolded suite against containers**

```bash
dotnet test tests/Inventory.Domain.Tests
dotnet test tests/Inventory.Application.Tests
dotnet test tests/Inventory.Api.Tests
```

One project per invocation: `dotnet test` takes a single project or solution
argument. Expected: every test green. The container half needs Docker; a failure
on `Failed to connect to Docker endpoint` is the daemon, not the scaffold.

- [ ] **Step 5: Confirm the secret scan accepts the rendered tree**

```bash
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: both exit 0. The suite runs first because a gate with a suite is
tested and then run (`docs/testing.md`), and a green gate whose suite is red
is a gate whose verdict means nothing. The scaffold wrote the allow-list
entries itself; a finding here means one it did not.

- [ ] **Step 6: Commit the scaffold output alone**

```bash
git add -A
git commit -m "feat(inventory): third service from the scaffold"
```

The body argues that the render is the scaffold's second dogfood and names
the port.

---

### Task 2: `ProductId` and `StockItem`

**Files:**
- Create: `src/Services/Inventory/Inventory.Domain/Stock/ProductId.cs`
- Create: `src/Services/Inventory/Inventory.Domain/Stock/StockItem.cs`
- Create: `src/Services/Inventory/Inventory.Domain/Stock/Events/StockEvents.cs`
- Create: `src/Services/Inventory/Inventory.Domain/Stock/IStockItemRepository.cs`
- Delete: `src/Services/Inventory/Inventory.Domain/AssemblyMarker.cs` — the
  scaffold's README says the first aggregate replaces it and the
  `ArchitectureTests` in `Inventory.Domain.Tests` and
  `Inventory.Application.Tests` re-anchor on the aggregate; `StockItem` is
  the anchor from here
- Modify: `tests/Inventory.Domain.Tests/ArchitectureTests.cs` and
  `tests/Inventory.Application.Tests/ArchitectureTests.cs` (the anchor type
  becomes `StockItem`)
- Modify: `src/Services/Inventory/Inventory.Domain/Inventory.Domain.csproj` —
  `<InternalsVisibleTo Include="Inventory.Domain.Tests" />`, because
  `Rehydrate` below is `internal` and no project in the repository declares
  a friend assembly today; the declaration is this service's and PR-2's
  `Reservation.Rehydrate` relies on it
- Test: `tests/Inventory.Domain.Tests/StockItemTests.cs`

**Interfaces:**
- Produces: `readonly record struct ProductId(Guid Value)`;
  `void StockItem.SetOnHand(int onHand, DateTimeOffset now)` throwing
  `DomainException` when `onHand < Reserved` or `onHand < 0`;
  `record StockLevelChangedDomainEvent(ProductId ProductId, int Available,
  DateTimeOffset OccurredAt) : IDomainEvent`;
  `IStockItemRepository { Task EnsureAsync(ProductId, DateTimeOffset now,
  CancellationToken); Task<StockItem?> GetAsync(ProductId, CancellationToken); }`.
  No `Create` factory and no `Add`: a row comes to exist through
  `EnsureAsync`, an insert-where-absent under a key-range lock, so two first
  writes for one product cannot both insert (Task 3), and the level event
  is `SetOnHand`'s in every case.

- [ ] **Step 1: Write the failing domain tests**

```csharp
using Common.Domain;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;
using Shouldly;
using Xunit;

namespace Inventory.Domain.Tests;

public class StockItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SetOnHand_on_an_empty_row_makes_everything_available_and_raises_the_level()
    {
        ProductId product = ProductId.New();
        StockItem item = StockItem.Rehydrate(product, available: 0, reserved: 0);

        item.SetOnHand(10, Now);

        item.Available.ShouldBe(10);
        item.Reserved.ShouldBe(0);
        StockLevelChangedDomainEvent raised = item.DomainEvents.ShouldHaveSingleItem()
            .ShouldBeOfType<StockLevelChangedDomainEvent>();
        raised.ProductId.ShouldBe(product);
        raised.Available.ShouldBe(10);
        raised.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void SetOnHand_keeps_what_is_reserved_and_moves_the_rest()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 3, reserved: 4);

        item.SetOnHand(10, Now);

        item.Available.ShouldBe(6, "on hand minus reserved is what can still be promised");
        item.Reserved.ShouldBe(4);
        item.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockLevelChangedDomainEvent>()
            .Available.ShouldBe(6);
    }

    [Fact]
    public void SetOnHand_refuses_a_count_below_what_is_reserved()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 0, reserved: 4);

        Should.Throw<DomainException>(() => item.SetOnHand(3, Now));
        item.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void SetOnHand_never_stamps_earlier_than_the_row_already_is()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 1, reserved: 0, updatedAt: Now.AddHours(1));

        item.SetOnHand(2, Now);

        item.UpdatedAt.ShouldBe(Now.AddHours(1).AddTicks(1), "a clock behind the row moves the stamp one tick, never back");
        item.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockLevelChangedDomainEvent>()
            .OccurredAt.ShouldBe(item.UpdatedAt);
    }

    [Fact]
    public void SetOnHand_refuses_a_negative_count()
    {
        Should.Throw<DomainException>(() => StockItem.Rehydrate(ProductId.New(), 0, 0).SetOnHand(-1, Now));
    }
}
```

`Rehydrate` is the one way a test builds a `StockItem`, since production
code never constructs one: rows are inserted by the repository (Task 3) and
loaded by EF. It is `internal`, and the `InternalsVisibleTo` in the Files
list is what lets the test assembly call it.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/Inventory.Domain.Tests --filter StockItemTests`
Expected: compile failure on the missing types.

- [ ] **Step 3: Write the domain**

`ProductId.cs`:

```csharp
namespace Inventory.Domain.Stock;

/// <summary>§5.2's typed identifier for the product a stock row is keyed on.</summary>
public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
```

`Events/StockEvents.cs`:

```csharp
using Common.Domain;

namespace Inventory.Domain.Stock.Events;

/// <summary>
/// The available quantity for a product moved (§3.2's <c>StockLevelChanged</c>).
/// A level, not a delta, so a redelivery cannot double-count.
/// </summary>
public sealed record StockLevelChangedDomainEvent(
    ProductId ProductId,
    int Available,
    DateTimeOffset OccurredAt) : IDomainEvent;
```

`StockItem.cs`:

```csharp
using Common.Domain;
using Inventory.Domain.Stock.Events;

namespace Inventory.Domain.Stock;

/// <summary>
/// One count per product (§3.2, §7.3). The aggregate for the admin path only:
/// the reservation path writes these columns by §7.3's statement and never
/// loads this type.
/// </summary>
public sealed class StockItem : AggregateRoot<ProductId>
{
    public int Available { get; private set; }
    public int Reserved { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private StockItem() { }

    private StockItem(ProductId id, int available, int reserved, DateTimeOffset now)
    {
        Id = id;
        Available = available;
        Reserved = reserved;
        UpdatedAt = now;
    }

    internal static StockItem Rehydrate(
        ProductId product,
        int available,
        int reserved,
        DateTimeOffset? updatedAt = null) =>
        new(product, available, reserved, updatedAt ?? DateTimeOffset.MinValue);

    public void SetOnHand(int onHand, DateTimeOffset now)
    {
        if (onHand < 0)
            throw new DomainException("On-hand stock cannot be negative.");

        // A stock-take cannot make the warehouse hold less than it has promised.
        if (onHand < Reserved)
            throw new DomainException("On-hand stock cannot be below what is reserved.");

        Available = onHand - Reserved;

        // Monotonic per product, from this side as the ledger's statement is
        // from its side: the level's instant is the clock when the clock is
        // ahead of the row and one tick past the row otherwise, so Catalog's
        // watermark never keeps an older level for a newer stamp whatever the
        // two clocks do. The row is locked from the load to the commit, so
        // no ledger stamp lands between the two.
        UpdatedAt = now > UpdatedAt ? now : UpdatedAt.AddTicks(1);
        Raise(new StockLevelChangedDomainEvent(Id, Available, UpdatedAt));
    }
}
```

`IStockItemRepository.cs`:

```csharp
namespace Inventory.Domain.Stock;

public interface IStockItemRepository
{
    /// <summary>
    /// Makes the row exist with nothing available and nothing reserved, under
    /// a lock that lets two first writes for one product both return and
    /// neither insert twice. On the unit of work's transaction.
    /// </summary>
    Task EnsureAsync(ProductId id, DateTimeOffset now, CancellationToken ct);

    Task<StockItem?> GetAsync(ProductId id, CancellationToken ct);
}
```

In `Inventory.Domain.csproj`, add an item group with
`<InternalsVisibleTo Include="Inventory.Domain.Tests" />`; the scaffold
carries none. Then delete `AssemblyMarker.cs` and point both
`ArchitectureTests` at `typeof(StockItem)` where they named the marker.

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/Inventory.Domain.Tests --filter StockItemTests`
Expected: green.

- [ ] **Step 5: Commit**

```bash
dotnet test tests/Inventory.Application.Tests --filter ArchitectureTests
git add src/Services/Inventory/Inventory.Domain tests/Inventory.Domain.Tests tests/Inventory.Application.Tests
git commit -m "feat(inventory): StockItem, the aggregate for the admin path"
```

---

### Task 3: Persistence — configuration, repository, migration

**Files:**
- Create: `src/Services/Inventory/Inventory.Infrastructure/Persistence/StockItemConfiguration.cs`
- Create: `src/Services/Inventory/Inventory.Infrastructure/Persistence/StockItemRepository.cs`
- Modify: `src/Services/Inventory/Inventory.Infrastructure/Persistence/InventoryDbContext.cs`
  (add `DbSet<StockItem> StockItems`)
- Modify: `src/Services/Inventory/Inventory.Infrastructure/DependencyInjection.cs`
  (register the repository beside `IUnitOfWork`)
- Create: `src/Services/Inventory/Inventory.Infrastructure/Persistence/Migrations/<ts>_AddStockItems.cs`
  (generated)
- Test: `tests/Inventory.Api.Tests/DatabaseSmokeTests.cs` (extend)

**Interfaces:**
- Produces: table `inventory.StockItems(ProductId uniqueidentifier PK,
  Available int, Reserved int, UpdatedAt datetimeoffset(7), RowVersion
  rowversion)`; `IStockItemRepository` resolvable from the container.

- [ ] **Step 1: Write the failing smoke test**

Add to the scaffolded `DatabaseSmokeTests`:

```csharp
[Fact]
public async Task The_migrator_creates_the_stock_table_with_its_rowversion()
{
    (await fixture.ScalarAsync<int>(
        "SELECT Value = COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('inventory.StockItems') " +
        "AND name = 'RowVersion' AND system_type_id = TYPE_ID('timestamp')"))
        .ShouldBe(1, "§7.3's admin path is optimistic, and the column is what makes it so");
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test tests/Inventory.Api.Tests --filter
The_migrator_creates_the_stock_table` Expected: FAIL, count 0.

- [ ] **Step 3: Write the configuration and repository**

`StockItemConfiguration.cs`:

```csharp
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence;

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("StockItems", "inventory");

        builder.HasKey(s => s.Id);

        builder
            .Property(s => s.Id)
            .HasColumnName("ProductId")
            .HasConversion(id => id.Value, value => new ProductId(value))
            .ValueGeneratedNever();

        builder.Property(s => s.Available).IsRequired();
        builder.Property(s => s.Reserved).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // §7.3: the admin path is optimistic; the reservation path bypasses
        // this column by statement and never loads the entity.
        builder.Property(s => s.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(s => s.DomainEvents);
    }
}
```

`StockItemRepository.cs`:

```csharp
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

internal sealed class StockItemRepository(InventoryDbContext db) : IStockItemRepository
{
    // UPDLOCK, HOLDLOCK on the probe, held to the commit: two first writes
    // for one product both reach this statement, the second blocks on the
    // first's key-range lock and finds the row when it proceeds — without it
    // both insert and the loser fails on the key, which
    // ConcurrencyExceptionHandler does not map. On a row that exists the same
    // lock serialises this write with the ledger's statements on the row.
    public async Task EnsureAsync(ProductId id, DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("EnsureAsync runs only inside the unit of work's transaction (§6.3).");

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt)
            SELECT {id.Value}, 0, 0, {now}
            WHERE NOT EXISTS (SELECT 1 FROM inventory.StockItems WITH (UPDLOCK, HOLDLOCK) WHERE ProductId = {id.Value});
            """,
            ct);
    }

    public Task<StockItem?> GetAsync(ProductId id, CancellationToken ct) =>
        db.StockItems.SingleOrDefaultAsync(s => s.Id == id, ct);
}
```

`ExecuteSqlAsync` with an interpolated string parameterises every hole and
runs on the context's current transaction, which is the one `EfUnitOfWork`
opened.

In `InventoryDbContext` add `public DbSet<StockItem> StockItems =>
Set<StockItem>();`. In `DependencyInjection.AddInventoryInfrastructure` add
`services.AddScoped<IStockItemRepository, StockItemRepository>();` on the line
after `IUnitOfWork`.

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddStockItems \
    --project src/Services/Inventory/Inventory.Infrastructure \
    --startup-project src/Services/Inventory/Inventory.Migrator \
    --output-dir Persistence/Migrations
```

Open the generated file and confirm it creates exactly `inventory.StockItems`
with the five columns above and nothing else. A migration that also alters a
scaffolded table means the model snapshot was stale; delete it and re-run.

- [ ] **Step 5: Run the smoke test and the whole Inventory suite**

Run: `dotnet test tests/Inventory.Api.Tests`
Expected: all green, including the new one.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Inventory/Inventory.Infrastructure tests/Inventory.Api.Tests
git commit -m "feat(inventory): map StockItems and add its migration"
```

---

### Task 4: `SetOnHandCommand` and `GetStockQuery`

**Files:**
- Modify: `src/Services/Inventory/Inventory.Application/Inventory.Application.csproj`
  — the scaffold strips Catalog's `Common.Contracts` project reference and
  its `Dapper` package reference from the Application project and leaves a
  comment where each stood; this task restores both, cuts the two comments,
  and adds no `Version=` (the pin is `Directory.Packages.props`'s). The
  mapper below names `Common.Contracts.Inventory.V1` and the query handler
  names Dapper, so the project does not compile without them.
- Create: `src/Services/Inventory/Inventory.Application/Stock/SetOnHand/SetOnHandCommand.cs`
- Create: `src/Services/Inventory/Inventory.Application/Stock/SetOnHand/SetOnHandHandler.cs`
- Create: `src/Services/Inventory/Inventory.Application/Stock/SetOnHand/SetOnHandValidator.cs`
- Create: `src/Services/Inventory/Inventory.Application/Stock/GetStock/GetStockQuery.cs`
- Create: `src/Services/Inventory/Inventory.Application/Stock/GetStock/GetStockHandler.cs`
- Create: `src/Services/Inventory/Inventory.Application/Stock/GetStock/StockDto.cs`
- Create: `src/Services/Inventory/Inventory.Application/Stock/StockErrors.cs`
- Create: `src/Services/Inventory/Inventory.Application/Integration/InventoryIntegrationEventMapper.cs`
  (replace the scaffolded one's registry)
- Test: `tests/Inventory.Application.Tests/SetOnHandValidatorTests.cs`
- Test: `tests/Inventory.Application.Tests/InventoryIntegrationEventMapperTests.cs`
- Test: `tests/Inventory.Application.Tests/OutboxSerialisationTests.cs` —
  the scaffold omits Ordering's file because a rendered service has no
  domain event to round-trip; this is the first, so the file comes back in
  Ordering's shape: a `DomainEventSamples.Create(Type)` dictionary with one
  entry per event, a test that serialises and deserialises every type in
  `MessageTypeMap.StageableDomainEvents` through `OutboxJson.Options` and
  asserts the JSON survives the round trip, and a test asserting the
  stageable set is exactly `[typeof(StockLevelChangedDomainEvent)]` (PR-2
  and PR-3 extend both). Its `Registered()` builds a provider from
  `AddInventoryApplication()` and `AddInventoryInfrastructure(configuration)`
  with an in-memory configuration carrying the connection-string keys, as
  Ordering's does.

**Interfaces:**
- Produces: `record SetOnHandCommand(Guid ProductId, int? OnHand) : ICommand<Result>`
  (nullable so an omitted count is a 400 rather than a reset to zero);
  `record GetStockQuery(Guid ProductId) : IQuery<StockDto?>`;
  `record StockDto(Guid ProductId, int Available, int Reserved, DateTimeOffset UpdatedAt)`;
  `StockErrors.BelowReserved` (`Error.Rule`); the mapper registry with
  `StockLevelChangedDomainEvent → StockLevelChanged`.

- [ ] **Step 1: Write the failing validator and mapper tests**

```csharp
using FluentValidation.TestHelper;
using Inventory.Application.Stock.SetOnHand;
using Xunit;

namespace Inventory.Application.Tests;

public class SetOnHandValidatorTests
{
    private readonly SetOnHandValidator _validator = new();

    [Fact]
    public void A_negative_count_is_refused_before_the_handler()
    {
        _validator.TestValidate(new SetOnHandCommand(Guid.CreateVersion7(), -1))
            .ShouldHaveValidationErrorFor(c => c.OnHand);
    }

    [Fact]
    public void An_empty_product_id_is_refused()
    {
        _validator.TestValidate(new SetOnHandCommand(Guid.Empty, 1))
            .ShouldHaveValidationErrorFor(c => c.ProductId);
    }

    [Fact]
    public void Zero_is_a_valid_stock_take()
    {
        _validator.TestValidate(new SetOnHandCommand(Guid.CreateVersion7(), 0))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void An_omitted_count_is_refused_rather_than_read_as_zero()
    {
        _validator.TestValidate(new SetOnHandCommand(Guid.CreateVersion7(), null))
            .ShouldHaveValidationErrorFor(c => c.OnHand);
    }
}
```

```csharp
using Common.Application;
using Common.Contracts.Inventory.V1;
using Inventory.Application;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

public class InventoryIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddInventoryApplication();
        return services.BuildServiceProvider().CreateScope().ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    [Fact]
    public void A_level_change_becomes_its_contract_with_the_product_as_correlation()
    {
        ProductId product = ProductId.New();

        IReadOnlyList<object> mapped = Mapper().Map([new StockLevelChangedDomainEvent(product, 7, Raised)]);

        StockLevelChanged contract = mapped.ShouldHaveSingleItem().ShouldBeOfType<StockLevelChanged>();
        contract.ProductId.ShouldBe(product.Value);
        contract.QuantityAvailable.ShouldBe(7);
        contract.CorrelationId.ShouldBe(product.Value);
        contract.OccurredAt.ShouldBe(Raised);
        contract.MessageId.ShouldNotBe(Guid.Empty);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Inventory.Application.Tests`
Expected: compile failure on the missing types.

- [ ] **Step 3: Restore the two references, then write the slice**

In `Inventory.Application.csproj`, beside the `Common.Application` project
reference and the `FluentValidation` package references respectively:

```xml
<ProjectReference Include="..\..\..\BuildingBlocks\Common.Contracts\Common.Contracts.csproj" />
<PackageReference Include="Dapper" />
```

`SetOnHandCommand.cs`:

```csharp
using Common.Application;

namespace Inventory.Application.Stock.SetOnHand;

// Nullable because a bare int cannot say "absent": an omitted count would
// bind as 0 and reset the stock indistinguishably from a deliberate stock-take
// of nothing. The validator's NotNull turns the omission into the field-keyed
// 400 every other bad field gets — the same reason Catalog's price is a
// decimal? on PublishProductCommand.
public sealed record SetOnHandCommand(Guid ProductId, int? OnHand) : ICommand<Result>;
```

`SetOnHandValidator.cs`:

```csharp
using FluentValidation;

namespace Inventory.Application.Stock.SetOnHand;

public sealed class SetOnHandValidator : AbstractValidator<SetOnHandCommand>
{
    public SetOnHandValidator()
    {
        RuleFor(c => c.ProductId).NotEmpty();
        RuleFor(c => c.OnHand).NotNull().GreaterThanOrEqualTo(0);
    }
}
```

`StockErrors.cs`:

```csharp
using Common.Application;

namespace Inventory.Application.Stock;

public static class StockErrors
{
    public static readonly Error BelowReserved =
        Error.Rule("stock.below_reserved", "On-hand stock cannot be below what is reserved.");
}
```

`SetOnHandHandler.cs`:

```csharp
using Common.Application;
using Common.Domain;
using Inventory.Domain.Stock;

namespace Inventory.Application.Stock.SetOnHand;

public sealed class SetOnHandHandler(IStockItemRepository items, TimeProvider clock)
    : ICommandHandler<SetOnHandCommand, Result>
{
    public async Task<Result> HandleAsync(SetOnHandCommand command, CancellationToken ct)
    {
        var product = new ProductId(command.ProductId);

        // Ensure, then load, then set: the row exists before it is read, so a
        // first write and a stock-take are one code path. EnsureAsync's lock
        // is held to the commit whether the row existed or not, so two admin
        // writes, or an admin write and a ledger statement, serialise on the
        // row rather than race — the second waits, then sees the first's
        // commit. The rowversion is EF's own guard on the update and fires
        // for nothing this path can meet.
        await items.EnsureAsync(product, clock.GetUtcNow(), ct);
        StockItem item = await items.GetAsync(product, ct)
            ?? throw new InvalidOperationException($"StockItems has no row for {product} after EnsureAsync.");

        try
        {
            item.SetOnHand(command.OnHand!.Value, clock.GetUtcNow());
        }
        catch (DomainException)
        {
            return Result.Failure(StockErrors.BelowReserved);
        }

        return Result.Success();
    }
}
```

`GetStockQuery.cs`, `StockDto.cs`, `GetStockHandler.cs`:

```csharp
using Common.Application;

namespace Inventory.Application.Stock.GetStock;

public sealed record GetStockQuery(Guid ProductId) : IQuery<StockDto?>;
```

```csharp
namespace Inventory.Application.Stock.GetStock;

public sealed record StockDto(Guid ProductId, int Available, int Reserved, DateTimeOffset UpdatedAt);
```

```csharp
using System.Data;
using Common.Application;
using Dapper;

namespace Inventory.Application.Stock.GetStock;

/// <summary>§6.5's read side: Dapper over the write table.</summary>
public sealed class GetStockHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetStockQuery, StockDto?>
{
    private const string Sql =
        """
        SELECT ProductId, Available, Reserved, UpdatedAt
        FROM inventory.StockItems
        WHERE ProductId = @ProductId;
        """;

    public async Task<StockDto?> HandleAsync(GetStockQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.QuerySingleOrDefaultAsync<StockDto>(
            new CommandDefinition(Sql, new { query.ProductId }, cancellationToken: ct));
    }
}
```

Mapper registry, in the scaffolded `InventoryIntegrationEventMapper`:

```csharp
private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
{
    [typeof(StockLevelChangedDomainEvent)] = e => ToContract((StockLevelChangedDomainEvent)e)
};

// The correlation is the PRODUCT: Catalog's projection keys on it, and a
// trace over one product's level history is what a support tool follows.
private static StockLevelChanged ToContract(StockLevelChangedDomainEvent e) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = e.ProductId.Value,
    OccurredAt = e.OccurredAt,
    ProductId = e.ProductId.Value,
    QuantityAvailable = e.Available
};
```

- [ ] **Step 4: Run the application tests**

Run: `dotnet test tests/Inventory.Application.Tests`
Expected: green, including the scaffolded architecture and DI tests.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Inventory/Inventory.Application tests/Inventory.Application.Tests
git commit -m "feat(inventory): SetOnHand and GetStock, and the level's contract mapping"
```

---

### Task 5: The two stock endpoints and `inventory:admin`

**Files:**
- Create: `src/Services/Inventory/Inventory.Api/Endpoints/StockEndpoints.cs`
- Modify: `src/Services/Inventory/Inventory.Api/InventoryPermissions.cs`
  (the scaffolded constant becomes `Admin = "inventory:admin"`)
- Modify: `src/Services/Inventory/Inventory.Api/Program.cs` (register the
  policy, map the endpoints)
- Test: `tests/Inventory.Api.Tests/StockEndpointsTests.cs`
- Test: `tests/Inventory.Api.Tests/GrantablePermissionTests.cs`
- Test: `tests/Inventory.Api.Tests/EndpointSecurityTests.cs`,
  `AuthorizationPolicyTests.cs` (rename the scaffolded assertions to the
  new constant and paths)

**Interfaces:**
- Produces: `PUT /v1/inventory/stock/{productId}` with body
  `{ "onHand": int }` → 204 / 409 (rowversion) / 422 (below reserved);
  `GET /v1/inventory/stock/{productId}` → 200 `StockDto` / 404. Both under
  `InventoryPermissions.Admin`.

- [ ] **Step 1: Write the failing endpoint tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using Inventory.Application.Stock.GetStock;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class StockEndpointsTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private HttpClient Admin()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);
        return client;
    }

    [Fact]
    public async Task Setting_stock_creates_the_row_and_stages_the_level()
    {
        using HttpClient client = Admin();
        var product = Guid.CreateVersion7();

        HttpResponseMessage put = await client.PutAsJsonAsync(
            $"/v1/inventory/stock/{product}", new { onHand = 12 }, TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        StockDto? read = await client.GetFromJsonAsync<StockDto>(
            $"/v1/inventory/stock/{product}", TestContext.Current.CancellationToken);
        read.ShouldNotBeNull();
        read.Available.ShouldBe(12);
        read.Reserved.ShouldBe(0);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal))
            .ShouldBe(1, "§3.2's Publishes column, through §9.3's allow-list");
    }

    [Fact]
    public async Task A_stock_take_below_the_reserved_count_is_refused_with_422()
    {
        using HttpClient client = Admin();
        var product = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) " +
            "VALUES ({0}, 1, 4, SYSDATETIMEOFFSET())",
            product);

        HttpResponseMessage put = await client.PutAsJsonAsync(
            $"/v1/inventory/stock/{product}", new { onHand = 3 }, TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_empty_body_is_400_and_resets_nothing()
    {
        using HttpClient client = Admin();
        var product = Guid.CreateVersion7();
        await client.PutAsJsonAsync($"/v1/inventory/stock/{product}", new { onHand = 4 }, TestContext.Current.CancellationToken);

        HttpResponseMessage put = await client.PutAsJsonAsync(
            $"/v1/inventory/stock/{product}", new { }, TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "an omitted count binds null and NotNull refuses it");
        (await client.GetFromJsonAsync<StockDto>($"/v1/inventory/stock/{product}", TestContext.Current.CancellationToken))!
            .Available.ShouldBe(4);
    }

    [Fact]
    public async Task Two_first_writes_for_one_product_leave_one_row_and_no_500()
    {
        using HttpClient client = Admin();
        var product = Guid.CreateVersion7();

        HttpResponseMessage[] responses = await Task.WhenAll(
            client.PutAsJsonAsync($"/v1/inventory/stock/{product}", new { onHand = 5 }, TestContext.Current.CancellationToken),
            client.PutAsJsonAsync($"/v1/inventory/stock/{product}", new { onHand = 7 }, TestContext.Current.CancellationToken));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent,
            "the second waited on the first's key-range lock and loaded its committed row; neither met the key");
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM inventory.StockItems WHERE ProductId = {0}", product))
            .ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_product_reads_404()
    {
        using HttpClient client = Admin();

        HttpResponseMessage get = await client.GetAsync(
            $"/v1/inventory/stock/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_the_admin_permission_both_endpoints_answer_403()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        (await client.GetAsync($"/v1/inventory/stock/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/v1/inventory/stock/{Guid.CreateVersion7()}", new { onHand = 1 }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
```

`GrantablePermissionTests` is Ordering's file with `OrderingPermissions`
replaced by `InventoryPermissions` and the second test (the admin claim)
removed, since Inventory has no claim a handler reads.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Inventory.Api.Tests --filter StockEndpointsTests`
Expected: compile failure on `InventoryPermissions.Admin` and 404s.

- [ ] **Step 3: Write the endpoints and the permission**

`InventoryPermissions.cs`:

```csharp
namespace Inventory.Api;

/// <summary>
/// Inventory's permission vocabulary (§11.4): one name, the gateway's own
/// <c>inventory:admin</c> (§10.2), re-validated here because §11.3 makes
/// every service check its own token.
/// </summary>
public static class InventoryPermissions
{
    public const string Admin = "inventory:admin";
}
```

`StockEndpoints.cs`:

```csharp
using Common.Application;
using Common.Web;
using Inventory.Application.Stock.GetStock;
using Inventory.Application.Stock.SetOnHand;

namespace Inventory.Api.Endpoints;

public static class StockEndpoints
{
    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/inventory/stock")
            .WithTags("Stock")
            .RequireAuthorization(InventoryPermissions.Admin);

        group
            .MapPut(
                "/{productId:guid}",
                async (Guid productId, SetOnHandRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(new SetOnHandCommand(productId, request.OnHand), ct);

                    return result.ToHttpResult();
                })
            .WithName("SetOnHand");

        group
            .MapGet(
                "/{productId:guid}",
                async (Guid productId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    StockDto? stock = await dispatcher.QueryAsync(new GetStockQuery(productId), ct);

                    return stock is null ? Results.NotFound() : Results.Ok(stock);
                })
            .WithName("GetStock");
    }
}

// int? for the reason SetOnHandCommand gives: `{}` must be a 400, not a reset.
public sealed record SetOnHandRequest(int? OnHand);
```

In `Program.cs`, replace the scaffold's commented policy line with
`.AddPolicy(InventoryPermissions.Admin, p =>
p.RequirePermission(InventoryPermissions.Admin))` and add
`app.MapStockEndpoints();` where the scaffold maps its endpoints.

- [ ] **Step 4: Run the API suite**

Run: `dotnet test tests/Inventory.Api.Tests` Expected: green.
`AuthorizationPolicyTests` must name `InventoryPermissions.Admin`.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Inventory/Inventory.Api tests/Inventory.Api.Tests
git commit -m "feat(inventory): the two stock admin endpoints under inventory:admin"
```

---

### Task 6: CI's filter and image matrix

**Files:**
- Modify: `.github/workflows/ci.yml` — the `changes` job's `outputs`, its
  `filters`, the `images` job's `if`, and its `matrix.include`

- [ ] **Step 1: Run the pipeline gate's suite, then the gate, to see it fail**

```bash
py -3.12 -m unittest discover -s .github/pipeline-gate
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
```

Expected: the suite is green, so the refusals that follow are the gate's
and not a broken gate's; `filters` refuses `src/Services/Inventory`;
`images` refuses the two Inventory Dockerfiles.

- [ ] **Step 2: Add the four edits**

Outputs, after `ordering`:

```yaml
      inventory: ${{ steps.changes.outputs.inventory }}
```

Filters, after the `ordering` block:

```yaml
            inventory:
              - *shared
              - 'src/Services/Inventory/**'
              - 'tests/Inventory.*/**'
```

The `images` job's `if` gains `|| needs.changes.outputs.inventory == 'true'`.

Matrix, after the two `ordering` entries:

```yaml
          - filter: inventory
            image: inventory-api
            dockerfile: src/Services/Inventory/Inventory.Api/Dockerfile
          - filter: inventory
            image: inventory-migrator
            dockerfile: src/Services/Inventory/Inventory.Migrator/Dockerfile
```

Cut the comment that says the other services "repeat those three lines" only
if it now lists Inventory by name; otherwise leave it. The `images` job's
`fail-fast` comment says "which of six Dockerfiles broke": drop the number
— "which Dockerfile broke is the useful signal" — because a count in a
comment is a document that goes stale on the next service.

- [ ] **Step 3: Run both gates again**

Expected: both exit 0.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: build and filter Inventory's two images"
```

---

### Task 7: The gateway's dependency and the realm grant

**Files:**
- Modify: `deploy/compose/services/gateway.yml` (`depends_on` gains
  `inventory-api: { condition: service_started }`, the condition its three
  siblings use; the comment that says the inventory route 502s is cut)
- Modify: `deploy/observability/check.py` — `OUTBOX_METRICS_EXEMPT` gains
  `"Inventory"`, because the scaffold renders Catalog's dispatcher without
  Catalog's gauges and the gate fails a dispatcher-hosting service that is
  neither instrumented nor exempt. The reason text: "Rendered from Catalog
  and inherits its gap; removed when Inventory registers OutboxMetrics."
- Modify: `deploy/compose/keycloak/realm-export.json` (the `demo` user's
  `commerce-api` roles gain `"inventory:admin"`; the role's description is
  rewritten)
- Modify: `tests/Common.Web.Tests/RealmImportTests.cs` — the assertion
  `Permissions("demo").ShouldBe(["catalog:write", "orders:write",
  "orders:cancel"], ignoreOrder: true)` gains `"inventory:admin"`, and the
  comment above it, which explains `orders:admin`'s absence, gains one
  sentence saying `inventory:admin` is held because stock exists only
  through the API it guards. Write this change first and see the suite fail
  on the realm before editing the realm.

- [ ] **Step 1: Edit the gateway unit and the observability gate**

Add `inventory-api: { condition: service_started }` beside `ordering-api`
and remove the sentence in the comment that names inventory as the one
remaining 502. In `deploy/observability/check.py`, add the `"Inventory"`
entry to `OUTBOX_METRICS_EXEMPT` with the reason above, then run
`py -3.12 deploy/observability/check.py` and expect it to pass; without the
entry it fails naming Inventory, which is the check that this step is owed.

- [ ] **Step 2: Edit the realm**

In the `demo` user's `clientRoles.commerce-api` array append
`"inventory:admin"`. Replace the role's `description` with one JSON string
on one physical line — the export is JSON and a literal newline inside a
string is invalid — reading, without the wrapping this document adds:
"Administer inventory through the gateway's inventory-admin route (§10.2):
set a product's on-hand stock and inspect, release or reinstate a
reservation. Granted to demo locally because stock exists only through this
API (§14.1)."

- [ ] **Step 3: Run the realm gate and bring the platform up**

```bash
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/keycloak/realm_check.py check --kind local
docker compose -f deploy/compose/docker-compose.yml up --build --wait
```

Expected: the suite and then the gate exit 0 — suite first, as
`docs/testing.md` orders it and `realm.yml` runs it; every service reports
healthy including `inventory-api`. Then, with a token for `demo` per the README:

```bash
curl -X PUT http://localhost:5000/api/v1/inventory/stock/00000000-0000-0000-0000-000000000001 \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"onHand": 5}'
```

Expected: 204 through the gateway. Tear down with `docker compose down -v`.

- [ ] **Step 4: Commit**

```bash
dotnet test tests/Common.Web.Tests --filter RealmImportTests
py -3.12 deploy/observability/check.py
git add deploy/compose/services/gateway.yml deploy/compose/keycloak/realm-export.json tests/Common.Web.Tests/RealmImportTests.cs deploy/observability/check.py
git commit -m "feat(dev): the gateway waits for inventory-api, and demo may administer stock"
```

---

### Task 8: The three sentences that said 502

**Files:**
- Modify: `docs/backend-architecture/10-api-gateway.md` (the paragraph
  opening "This file shipped whole")
- Modify: `docs/backend-architecture/14-local-development.md` (the sentence
  "One answers 502 today — inventory" and the paragraph after it that
  predicts the divergence)
- Modify: `deploy/compose/README.md` (the paragraph opening "One of the four
  routes has no service behind it yet")

- [ ] **Step 1: Rewrite each to the past it describes**

§10.2: the paragraph opening "This file shipped whole" keeps its argument
about the asymmetry, which is still true, and loses the clause that names
one service as still missing: "ahead of three of the four services it
routes to, each of which has since arrived behind its route — which is the
opposite of the rule …". The two PR numbers the sentence carried today go
with the clause; a chapter cites the owner, not the history.

§14.1: replace "One answers 502 today — inventory — the BFF's route having
gained its service with PR-19." with "Every route now has a destination
Compose can see, Inventory's being the last to join." and in the following
paragraph replace "so the two will diverge again at Inventory" with "so the
two would diverge again at the next service".

README: replace the paragraph with one sentence: "Every route in §10.2's file
now has a service behind it; the two configuration tests over the file are
what let it ship whole, ahead of three of them."

- [ ] **Step 2: Run the link check**

Run `/check-links` and `/validate-blueprint` from the session: two chapters
moved, and `docs/change-locality.md`'s procedure owes the audit after any
edit to a chapter, whatever the edit says. Expect no findings from either;
a finding is fixed here, before the commit.

- [ ] **Step 3: Commit**

```bash
git add docs/backend-architecture/10-api-gateway.md docs/backend-architecture/14-local-development.md deploy/compose/README.md
git commit -m "docs: Inventory's route has a service behind it"
```

---

### Task 9: Whole-solution verification

- [ ] **Step 1: Build and test everything**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx
```

Expected: 0 warnings; every suite green.

- [ ] **Step 2: Run the gates with suites**

```bash
py -3.12 -m unittest discover -s .github/pipeline-gate
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 -m unittest discover -s tools/new-service
```

Expected: all exit 0.

- [ ] **Step 3: Write the PR body's class row**

`| Class | A+B+D+E |` and the touch set from the Global Constraints, then
`/ship`.

## Self-review

- Spec coverage: section 1 (port, stock origin) → Tasks 1, 7; section 3's
  `StockItem` → Task 2; section 6's two stock endpoints → Task 5; section 7's
  `StockItems` and `AddStockItems` → Task 3; section 11 → Task 7; section 12's
  keys → Task 1 (the scaffold writes them); section 10's three sentences →
  Task 8; the CI half of section 2 → Task 6.
- Not in this PR by design: `Reservation`, the command queue, the three
  reservation endpoints (PR-2); consumers (PR-3); Helm (PR-4).
- Types: `ProductId`, `StockItem.SetOnHand`, `StockLevelChangedDomainEvent`,
  `IStockItemRepository.EnsureAsync/GetAsync`, `SetOnHandCommand`,
  `GetStockQuery`, `StockDto`, `InventoryPermissions.Admin` are named
  identically across Tasks 2–5.
