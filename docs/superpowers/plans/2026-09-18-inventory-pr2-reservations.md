# Inventory PR-2 — reservations — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Inventory its `Reservation` aggregate, the `inventory-commands`
queue that answers `ReserveStock` and `ReleaseStock` under ADR-024's two
guarantees, the four events through the outbox, and the three reservation
admin endpoints the order-review runbook already promises.

**Architecture:** `Reservation` is the one aggregate root any message-driven
command modifies; `StockItems` moves underneath it by §7.3's statement through
`IStockLedger`, a port of its own on the unit of work's transaction, per line in
`ProductId` order under a T-SQL savepoint, so a failed reserve commits a
`Failed` row and an outbox row and no stock change. Every release path publishes
`StockReleased`; a release for an unknown order writes the tombstone row that
refuses the reserve that follows.

**Tech Stack:** EF Core with a rowversion on `Reservations`, Dapper through the
raw SQL port, MassTransit `CommandConsumer<,>` on a receive endpoint shaped
like Ordering's, xUnit with Shouldly and Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-18-inventory-service-design.md`,
sections 3 (the `Reservation` half), 4, 5 (the release table only), 6 (the
three reservation endpoints), 7, 8 and 10 (the §3.2 sentence).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+B+D.** Touch set: `src/Services/Inventory/**`,
  `tests/Inventory.*`, `docs/backend-architecture/03-bounded-contexts.md`
  (one sentence, the B half), and `deploy/compose/rabbitmq/definitions.json`
  (the D half: the scaffold copied Catalog's publisher-only broker grant, and
  a service with a receive endpoint widens its own entry, as the scaffold's
  README says).
- `ErrorType` stays at its three members. §10.5 reserves 409 for concurrency
  and idempotency exceptions, so every domain refusal here is `Error.Rule`
  and answers 422: `reservation.not_reinstatable` for every state with
  nothing to restore, `reservation.unavailable` for a shortage.
- §7.3's statements run through `IStockLedger`, on the unit of work's own
  connection and transaction, never through `ExecuteRawAsync`, which returns
  no rows where the statement's `OUTPUT` is the level (spec, section 3).
- Depends on PR-1 having merged: `StockItem`, `StockItems`,
  `InventoryPermissions.Admin` and the stock endpoints exist.
- §7.3's statement keeps its guard and its counter arithmetic unchanged —
  `WHERE ProductId = @ProductId AND Available >= @Quantity`,
  `Available - @Quantity`, `Reserved + @Quantity`, one atomic statement per
  row — and takes exactly two changes the spec's section 4 argues: the
  `UpdatedAt` assignment is the per-product monotonic `Stamp` expression
  rather than a bare `SYSDATETIMEOFFSET()`, and the `OUTPUT` returns
  `inserted.Available, inserted.UpdatedAt`. Every statement in Task 3 and
  PR-3's fulfilment statement stamp the same way.
- Lines are processed in ascending `ProductId` order, always.
- No purge of `Reservations`; the PR body files the issue naming the bound
  it would need (section 7 of the spec).
- Every step that adds behaviour writes its test first; container tests are
  never skipped.

---

### Task 1: `OrderId`, `Reservation`, its lines and its events

**Files:**
- Create: `src/Services/Inventory/Inventory.Domain/Reservations/OrderId.cs`
- Create: `src/Services/Inventory/Inventory.Domain/Reservations/ReservationStatus.cs`
- Create: `src/Services/Inventory/Inventory.Domain/Reservations/ReservationLine.cs`
- Create: `src/Services/Inventory/Inventory.Domain/Reservations/Reservation.cs`
- Create: `src/Services/Inventory/Inventory.Domain/Reservations/Events/ReservationEvents.cs`
- Create: `src/Services/Inventory/Inventory.Domain/Reservations/IReservationRepository.cs`
- Test: `tests/Inventory.Domain.Tests/ReservationTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct OrderId(Guid Value)`.
  - `enum ReservationStatus { Reserved, Failed, Released, Fulfilled }`.
  - `sealed record ReservationLine(ProductId ProductId, int Quantity)`.
  - `sealed record ReservedLevel(ProductId ProductId, int Available, DateTimeOffset UpdatedAt)`
    — what a statement's `OUTPUT` returns, handed to the aggregate; the
    third member is the level event's `OccurredAt`.
  - `Reservation.Reserve(OrderId, IReadOnlyList<ReservationLine>, IReadOnlyList<ReservedLevel>, DateTimeOffset)`
    → status `Reserved`, raises `StockReservedDomainEvent` and one
    `StockLevelChangedDomainEvent` per level.
  - `Reservation.Fail(OrderId, IReadOnlyList<ReservationLine>, IReadOnlyList<ProductId> unavailable, DateTimeOffset)`
    → `Failed`, raises `StockReservationFailedDomainEvent`.
  - `Reservation.Tombstone(OrderId, DateTimeOffset)` → `Released`, no lines,
    raises `StockReleasedDomainEvent`.
  - `void Release(IReadOnlyList<ReservedLevel> levels, DateTimeOffset now)` —
    from `Reserved` moves to `Released` and raises `StockReleased` plus one
    level per entry; from any other status raises `StockReleased` only.
  - `void Reinstate(IReadOnlyList<ReservedLevel> levels, DateTimeOffset now)` —
    from `Released` with lines moves to `Reserved` and raises levels only;
    otherwise throws `DomainException`.
  - `void AnswerAgain(DateTimeOffset now)` — re-raises the event this
    status answers with (section 4's outcome table).
  - Events: `StockReservedDomainEvent(OrderId, DateTimeOffset)`,
    `StockReservationFailedDomainEvent(OrderId, IReadOnlyList<ProductId>, DateTimeOffset)`,
    `StockReleasedDomainEvent(OrderId, DateTimeOffset)`, all `IDomainEvent`.
  - `IReservationRepository { Task<Reservation?> GetForUpdateAsync(OrderId, CancellationToken); void Add(Reservation); }`
    — the one read locks; there is no plain `GetAsync` (Task 2).

- [ ] **Step 1: Write the failing domain tests**

```csharp
using Common.Domain;
using Inventory.Domain.Reservations;
using Inventory.Domain.Reservations.Events;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;
using Shouldly;
using Xunit;

namespace Inventory.Domain.Tests;

public class ReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProductId A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly ProductId B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    private static IReadOnlyList<ReservationLine> Lines() => [new(A, 2), new(B, 1)];

    private static IReadOnlyList<ReservedLevel> Levels() => [new(A, 8, Now), new(B, 0, Now.AddTicks(1))];

    [Fact]
    public void Reserve_holds_the_lines_and_raises_the_reservation_and_every_level()
    {
        OrderId order = OrderId.New();

        Reservation reservation = Reservation.Reserve(order, Lines(), Levels(), Now);

        reservation.Status.ShouldBe(ReservationStatus.Reserved);
        reservation.Lines.Count.ShouldBe(2);
        reservation.DomainEvents.OfType<StockReservedDomainEvent>().ShouldHaveSingleItem().OrderId.ShouldBe(order);
        reservation.DomainEvents.OfType<StockLevelChangedDomainEvent>().Select(e => (e.ProductId, e.Available, e.OccurredAt))
            .ShouldBe([(A, 8, Now), (B, 0, Now.AddTicks(1))], "each level carries the row's instant, not the command's");
    }

    [Fact]
    public void Fail_records_the_lines_and_names_what_was_short()
    {
        Reservation reservation = Reservation.Fail(OrderId.New(), Lines(), [B], Now);

        reservation.Status.ShouldBe(ReservationStatus.Failed);
        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockReservationFailedDomainEvent>()
            .UnavailableProductIds.ShouldBe([B]);
    }

    [Fact]
    public void A_tombstone_is_released_with_no_lines_and_publishes_the_postcondition()
    {
        Reservation reservation = Reservation.Tombstone(OrderId.New(), Now);

        reservation.Status.ShouldBe(ReservationStatus.Released);
        reservation.Lines.ShouldBeEmpty();
        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockReleasedDomainEvent>();
    }

    [Fact]
    public void Release_of_a_held_reservation_frees_the_lines_and_reports_the_levels()
    {
        Reservation reservation = Reservation.Reserve(OrderId.New(), Lines(), Levels(), Now);
        reservation.ClearDomainEvents();

        reservation.Release([new(A, 10, Now), new(B, 1, Now)], Now);

        reservation.Status.ShouldBe(ReservationStatus.Released);
        reservation.Lines.Count.ShouldBe(2, "the lines are kept for a later reinstatement");
        reservation.DomainEvents.OfType<StockReleasedDomainEvent>().ShouldHaveSingleItem();
        reservation.DomainEvents.OfType<StockLevelChangedDomainEvent>().Count().ShouldBe(2);
    }

    [Theory]
    [InlineData(ReservationStatus.Failed)]
    [InlineData(ReservationStatus.Released)]
    [InlineData(ReservationStatus.Fulfilled)]
    public void Release_of_anything_not_held_publishes_the_postcondition_and_moves_nothing(ReservationStatus status)
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), status, Lines());

        reservation.Release([], Now);

        reservation.Status.ShouldBe(status);
        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockReleasedDomainEvent>();
    }

    [Fact]
    public void Reinstate_restores_a_released_reservation_and_publishes_only_the_levels()
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Released, Lines());

        reservation.Reinstate(Levels(), Now);

        reservation.Status.ShouldBe(ReservationStatus.Reserved);
        reservation.DomainEvents.ShouldAllBe(e => e is StockLevelChangedDomainEvent);
        reservation.DomainEvents.Count.ShouldBe(2);
    }

    [Fact]
    public void Reinstate_refuses_a_tombstone_and_anything_not_released()
    {
        Should.Throw<DomainException>(() => Reservation.Tombstone(OrderId.New(), Now).Reinstate([], Now));
        Should.Throw<DomainException>(() =>
            Reservation.Rehydrate(OrderId.New(), ReservationStatus.Reserved, Lines()).Reinstate(Levels(), Now));
    }

    [Theory]
    [InlineData(ReservationStatus.Reserved, typeof(StockReservedDomainEvent))]
    [InlineData(ReservationStatus.Fulfilled, typeof(StockReservedDomainEvent))]
    [InlineData(ReservationStatus.Failed, typeof(StockReservationFailedDomainEvent))]
    [InlineData(ReservationStatus.Released, typeof(StockReleasedDomainEvent))]
    public void AnswerAgain_re_raises_the_event_this_state_answers_with(ReservationStatus status, Type expected)
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), status, Lines(), unavailable: [B]);

        reservation.AnswerAgain(Now);

        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType(expected);
    }

    [Fact]
    public void Reserve_refuses_no_lines_and_a_repeated_product()
    {
        Should.Throw<DomainException>(() => Reservation.Reserve(OrderId.New(), [], [], Now));
        Should.Throw<DomainException>(() =>
            Reservation.Reserve(OrderId.New(), [new(A, 1), new(A, 1)], [new(A, 1, Now)], Now));
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Inventory.Domain.Tests --filter ReservationTests`
Expected: compile failure.

- [ ] **Step 3: Write the domain**

`OrderId.cs` mirrors `ProductId` with the name changed.

`ReservationStatus.cs`:

```csharp
namespace Inventory.Domain.Reservations;

public enum ReservationStatus
{
    Reserved,
    Failed,
    Released,
    Fulfilled
}
```

`ReservationLine.cs`:

```csharp
using Inventory.Domain.Stock;

namespace Inventory.Domain.Reservations;

public sealed record ReservationLine(ProductId ProductId, int Quantity);

/// <summary>
/// What §7.3's statement returns for one line: the level it left and the
/// instant it assigned under the row lock, which is the level event's
/// OccurredAt — never a clock read before the statement, which two
/// serialised writers could take in the other order.
/// </summary>
public sealed record ReservedLevel(ProductId ProductId, int Available, DateTimeOffset UpdatedAt);
```

`Events/ReservationEvents.cs`:

```csharp
using Common.Domain;
using Inventory.Domain.Stock;

namespace Inventory.Domain.Reservations.Events;

public sealed record StockReservedDomainEvent(OrderId OrderId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record StockReservationFailedDomainEvent(
    OrderId OrderId,
    IReadOnlyList<ProductId> UnavailableProductIds,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A postcondition, not a state change (ADR-024): nothing is held for this order.</summary>
public sealed record StockReleasedDomainEvent(OrderId OrderId, DateTimeOffset OccurredAt) : IDomainEvent;
```

`Reservation.cs`:

```csharp
using Common.Domain;
using Inventory.Domain.Reservations.Events;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;

namespace Inventory.Domain.Reservations;

/// <summary>
/// One row per order (§3.2). The one aggregate every message-driven command
/// modifies; the stock counters move underneath it by §7.3's statement. A
/// <see cref="ReservationStatus.Released"/> row with no lines is ADR-024's
/// tombstone.
/// </summary>
public sealed class Reservation : AggregateRoot<OrderId>
{
    private readonly List<ReservationLine> _lines = [];
    private readonly List<ProductId> _unavailable = [];

    public ReservationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<ReservationLine> Lines => _lines.AsReadOnly();
    public IReadOnlyList<ProductId> UnavailableProductIds => _unavailable.AsReadOnly();

    private Reservation() { }

    private Reservation(OrderId id, ReservationStatus status, IEnumerable<ReservationLine> lines, DateTimeOffset now)
    {
        Id = id;
        Status = status;
        CreatedAt = now;
        UpdatedAt = now;
        _lines.AddRange(lines);
    }

    public static Reservation Reserve(
        OrderId order,
        IReadOnlyList<ReservationLine> lines,
        IReadOnlyList<ReservedLevel> levels,
        DateTimeOffset now)
    {
        CheckLines(lines);
        var reservation = new Reservation(order, ReservationStatus.Reserved, lines, now);
        reservation.Raise(new StockReservedDomainEvent(order, now));
        reservation.RaiseLevels(levels, now);
        return reservation;
    }

    public static Reservation Fail(
        OrderId order,
        IReadOnlyList<ReservationLine> lines,
        IReadOnlyList<ProductId> unavailable,
        DateTimeOffset now)
    {
        CheckLines(lines);
        var reservation = new Reservation(order, ReservationStatus.Failed, lines, now);
        reservation._unavailable.AddRange(unavailable);
        reservation.Raise(new StockReservationFailedDomainEvent(order, unavailable, now));
        return reservation;
    }

    public static Reservation Tombstone(OrderId order, DateTimeOffset now)
    {
        var reservation = new Reservation(order, ReservationStatus.Released, [], now);
        reservation.Raise(new StockReleasedDomainEvent(order, now));
        return reservation;
    }

    internal static Reservation Rehydrate(
        OrderId order,
        ReservationStatus status,
        IReadOnlyList<ReservationLine> lines,
        IReadOnlyList<ProductId>? unavailable = null)
    {
        var reservation = new Reservation(order, status, lines, DateTimeOffset.MinValue);
        reservation._unavailable.AddRange(unavailable ?? []);
        return reservation;
    }

    /// <summary>ADR-024's first guarantee: every call raises the postcondition.</summary>
    public void Release(IReadOnlyList<ReservedLevel> levels, DateTimeOffset now)
    {
        if (Status == ReservationStatus.Reserved)
        {
            Status = ReservationStatus.Released;
            UpdatedAt = now;
            RaiseLevels(levels, now);
        }

        Raise(new StockReleasedDomainEvent(Id, now));
    }

    /// <summary>The runbook's reinstatement: an operator's act, so no <c>StockReserved</c>.</summary>
    public void Reinstate(IReadOnlyList<ReservedLevel> levels, DateTimeOffset now)
    {
        if (Status != ReservationStatus.Released || _lines.Count == 0)
            throw new DomainException("Only a released reservation with lines can be reinstated.");

        Status = ReservationStatus.Reserved;
        UpdatedAt = now;
        RaiseLevels(levels, now);
    }

    /// <summary>A command that arrives again is answered again rather than ignored (ADR-024).</summary>
    public void AnswerAgain(DateTimeOffset now)
    {
        switch (Status)
        {
            case ReservationStatus.Reserved:
            case ReservationStatus.Fulfilled:
                Raise(new StockReservedDomainEvent(Id, now));
                break;
            case ReservationStatus.Failed:
                Raise(new StockReservationFailedDomainEvent(Id, UnavailableProductIds, now));
                break;
            case ReservationStatus.Released:
                Raise(new StockReleasedDomainEvent(Id, now));
                break;
            default:
                throw new DomainException($"No answer is defined for {Status}.");
        }
    }

    private void RaiseLevels(IReadOnlyList<ReservedLevel> levels, DateTimeOffset now)
    {
        // The row's own timestamp, not `now`: `now` orders the reservation's
        // events, the row's UpdatedAt orders the product's.
        foreach (ReservedLevel level in levels)
            Raise(new StockLevelChangedDomainEvent(level.ProductId, level.Available, level.UpdatedAt));
    }

    private static void CheckLines(IReadOnlyList<ReservationLine> lines)
    {
        if (lines.Count == 0)
            throw new DomainException("A reservation needs at least one line.");
        if (lines.Select(l => l.ProductId).Distinct().Count() != lines.Count)
            throw new DomainException("A product appears at most once in a reservation.");
        if (lines.Any(l => l.Quantity <= 0))
            throw new DomainException("A line's quantity must be positive.");
    }
}
```

`IReservationRepository.cs`:

```csharp
namespace Inventory.Domain.Reservations;

public interface IReservationRepository
{
    /// <summary>
    /// Loads the reservation under a lock held to the end of the unit of
    /// work: the row where one exists, the key range where none does. Two
    /// creators serialise here instead of meeting on the key, and a reply
    /// derived from the row's state runs against a row nobody else can
    /// change until this commits (spec, section 7).
    /// </summary>
    Task<Reservation?> GetForUpdateAsync(OrderId id, CancellationToken ct);

    void Add(Reservation reservation);
}
```

There is no plain `GetAsync`: every command that reads a reservation
decides something on it, and a read that decides is a read that locks.

PR-3 adds `Fulfil` to this class; nothing here anticipates it.
`Rehydrate` is `internal` and compiles in the test assembly through the
`InternalsVisibleTo` PR-1 declared in `Inventory.Domain.csproj`.

- [ ] **Step 4: Run the domain tests**

Run: `dotnet test tests/Inventory.Domain.Tests`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Inventory/Inventory.Domain tests/Inventory.Domain.Tests
git commit -m "feat(inventory): Reservation, the aggregate every message-driven command modifies"
```

---

### Task 2: Persistence — `Reservations`, `ReservationLines`, the migration

**Files:**
- Create: `src/Services/Inventory/Inventory.Infrastructure/Persistence/ReservationConfiguration.cs`
- Create: `src/Services/Inventory/Inventory.Infrastructure/Persistence/ReservationRepository.cs`
- Modify: `InventoryDbContext.cs` (`DbSet<Reservation> Reservations`)
- Modify: `Inventory.Infrastructure/DependencyInjection.cs` (register the repository)
- Create: `Persistence/Migrations/<ts>_AddReservations.cs` (generated)
- Test: `tests/Inventory.Api.Tests/DatabaseSmokeTests.cs` (extend)

**Interfaces:**
- Produces: `inventory.Reservations(OrderId PK, Status nvarchar(20),
  UnavailableProductIds nvarchar(max) JSON, CreatedAt, UpdatedAt, RowVersion)`;
  `inventory.ReservationLines(OrderId, ProductId PK, Quantity)` with a
  cascade from `Reservations`.

- [ ] **Step 1: Write the failing smoke test**

```csharp
[Fact]
public async Task The_migrator_creates_reservations_and_their_lines()
{
    (await fixture.ScalarAsync<int>(
        "SELECT Value = COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('inventory') " +
        "AND name IN ('Reservations', 'ReservationLines')"))
        .ShouldBe(2);
    (await fixture.ScalarAsync<int>(
        "SELECT Value = COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('inventory.Reservations') " +
        "AND name = 'RowVersion' AND system_type_id = TYPE_ID('timestamp')"))
        .ShouldBe(1, "a release and a fulfilment for one order can race on two endpoints");
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/Inventory.Api.Tests --filter
The_migrator_creates_reservations` Expected: FAIL.

- [ ] **Step 3: Write the configuration and repository**

```csharp
using System.Text.Json;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence;

internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations", "inventory");

        builder.HasKey(r => r.Id);
        builder
            .Property(r => r.Id)
            .HasColumnName("OrderId")
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        // The failed reserve's answer has to be repeatable (ADR-024), so the
        // ids it named are kept with the row. A JSON column rather than a
        // table: nothing queries by them.
        builder
            .Property<List<ProductId>>("_unavailable")
            .HasColumnName("UnavailableProductIds")
            .HasConversion(
                ids => JsonSerializer.Serialize(ids.Select(id => id.Value), (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<Guid>>(json, (JsonSerializerOptions?)null)!
                    .Select(value => new ProductId(value)).ToList())
            .HasColumnType("nvarchar(max)");

        builder.Property(r => r.Version).HasColumnName("RowVersion").IsRowVersion();
        builder.Ignore(r => r.DomainEvents);
        builder.Ignore(r => r.UnavailableProductIds);

        builder.OwnsMany(
            r => r.Lines,
            lines =>
            {
                lines.ToTable("ReservationLines", "inventory");
                lines.WithOwner().HasForeignKey("OrderId");
                lines
                    .Property(l => l.ProductId)
                    .HasConversion(id => id.Value, value => new ProductId(value));
                lines.HasKey("OrderId", nameof(ReservationLine.ProductId));
                lines.Property(l => l.Quantity).IsRequired();
            });
        builder.Navigation(r => r.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
```

```csharp
using Inventory.Domain.Reservations;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

internal sealed class ReservationRepository(InventoryDbContext db) : IReservationRepository
{
    public async Task<Reservation?> GetForUpdateAsync(OrderId id, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A reservation is read only inside the unit of work's transaction (§6.3).");

        // The lock probe runs first and on its own: UPDLOCK on the row, or a
        // key-range lock on its absence, held until the transaction ends. EF's
        // query below then reads what the probe locked.
        await db.Database.ExecuteSqlAsync(
            $"SELECT OrderId FROM inventory.Reservations WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = {id.Value};",
            ct);

        return await db.Reservations.SingleOrDefaultAsync(r => r.Id == id, ct);
    }

    public void Add(Reservation reservation) => db.Add(reservation);
}
```

`OwnsMany` loads the lines with the owner, so no `Include`. The probe is a
statement rather than a query hint because EF Core has no `UPDLOCK` hint
of its own, and `FromSqlInterpolated` with the hint would tie the owned
lines' loading to a raw query. Add the `DbSet`
and `services.AddScoped<IReservationRepository, ReservationRepository>();`.

- [ ] **Step 4: Generate the migration and run the smoke test**

```bash
dotnet ef migrations add AddReservations \
    --project src/Services/Inventory/Inventory.Infrastructure \
    --startup-project src/Services/Inventory/Inventory.Migrator \
    --output-dir Persistence/Migrations
dotnet test tests/Inventory.Api.Tests --filter DatabaseSmokeTests
```

Expected: the migration creates only the two tables; the test passes.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Inventory/Inventory.Infrastructure tests/Inventory.Api.Tests
git commit -m "feat(inventory): map Reservations and ReservationLines"
```

---

### Task 3: `IStockLedger` — §7.3's statements behind a port

**Files:**
- Create: `src/Services/Inventory/Inventory.Application/Reservations/IStockLedger.cs`
- Create: `src/Services/Inventory/Inventory.Infrastructure/Persistence/SqlStockLedger.cs`
- Modify: `Inventory.Infrastructure/DependencyInjection.cs` (register it scoped)
- Test: `tests/Inventory.Api.Tests/StockLedgerTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record LedgerOutcome(IReadOnlyList<ReservedLevel> Levels, IReadOnlyList<ProductId> Unavailable);

public interface IStockLedger
{
    /// Runs §7.3's statement per line in ProductId order under a savepoint;
    /// rolls back to it when any line is short. Never partial.
    Task<LedgerOutcome> TryTakeAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);

    /// Returns each line to Available with no guard. Levels after.
    Task<IReadOnlyList<ReservedLevel>> GiveBackAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);
}
```

The port lives in Application because handlers call it; the SQL lives in
Infrastructure because it is a write bound to the `DbContext`'s current
transaction, which Application cannot see — Dapper itself is already on
Application's read side. It follows
`IUnitOfWork.ExecuteRawAsync`'s rule without going through that member,
which returns no rows where the statement's `OUTPUT` is the level (spec,
section 3): `SqlStockLedger` takes the `InventoryDbContext` and runs Dapper
on `db.Database.GetDbConnection()` with `db.Database.CurrentTransaction`,
the same connection and transaction `EfUnitOfWork.ExecuteRawAsync` uses,
and throws if no transaction is open — the same refusal in the same place.

- [ ] **Step 1: Write the failing ledger tests**

```csharp
using Inventory.Application.Reservations;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Persistence;
using Inventory.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class StockLedgerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<T> InTransaction<T>(Func<IStockLedger, Task<T>> act, bool commit = true)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        InventoryDbContext db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        IStockLedger ledger = scope.ServiceProvider.GetRequiredService<IStockLedger>();
        await using var tx = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        T result = await act(ledger);
        if (commit)
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private Task Seed(Guid product, int available, int reserved = 0) =>
        fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) VALUES ({0}, {1}, {2}, SYSDATETIMEOFFSET())",
            product, available, reserved);

    private Task<int> Available(Guid product) =>
        fixture.ScalarAsync<int>("SELECT Value = Available FROM inventory.StockItems WHERE ProductId = {0}", product);

    [Fact]
    public async Task Taking_every_line_decrements_and_returns_the_levels_left()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await Seed(a, 5);
        await Seed(b, 1);

        LedgerOutcome outcome = await InTransaction(l =>
            l.TryTakeAsync([new(new ProductId(b), 1), new(new ProductId(a), 2)], TestContext.Current.CancellationToken));

        outcome.Unavailable.ShouldBeEmpty();
        outcome.Levels.Select(l => (l.ProductId, l.Available))
            .ShouldBe([(new ProductId(a), 3), (new ProductId(b), 0)], ignoreOrder: true);
        outcome.Levels.ShouldAllBe(l => l.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1),
            "the instant is the statement's, stamped under the row lock");
        // Version-7 ids are not creation-ordered under Guid.CompareTo, so the
        // order is asserted against the comparer the ledger sorts with, not
        // against which id was made first.
        outcome.Levels.Select(l => l.ProductId.Value)
            .ShouldBe(outcome.Levels.Select(l => l.ProductId.Value).OrderBy(g => g), "in ProductId order, whatever order the lines came in");
        (await Available(a)).ShouldBe(3);
        (await Available(b)).ShouldBe(0);
    }

    [Fact]
    public async Task A_row_stamped_in_the_future_is_stamped_one_tick_later_and_never_earlier()
    {
        var a = Guid.CreateVersion7();
        DateTimeOffset future = DateTimeOffset.UtcNow.AddHours(1);
        await fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) VALUES ({0}, 5, 0, {1})", a, future);

        LedgerOutcome outcome = await InTransaction(l =>
            l.TryTakeAsync([new(new ProductId(a), 1)], TestContext.Current.CancellationToken));

        outcome.Levels.ShouldHaveSingleItem().UpdatedAt.ShouldBe(future.AddTicks(1),
            "per-product monotonic: a clock behind the row's stamp does not move the stamp backwards");
        (await Available(a)).ShouldBe(4);
    }

    [Fact]
    public async Task A_short_line_rolls_every_decrement_back_and_names_every_short_product()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();
        await Seed(a, 5);
        await Seed(b, 0);

        LedgerOutcome outcome = await InTransaction(l =>
            l.TryTakeAsync(
                [new(new ProductId(a), 2), new(new ProductId(b), 1), new(new ProductId(c), 1)],
                TestContext.Current.CancellationToken));

        outcome.Unavailable.Select(p => p.Value).ShouldBe([b, c].Order(), ignoreOrder: true);
        outcome.Levels.ShouldBeEmpty();
        (await Available(a)).ShouldBe(5, "the savepoint undid the first line's decrement");
    }

    [Fact]
    public async Task Two_takes_for_the_last_unit_leave_exactly_one_holding_it()
    {
        var a = Guid.CreateVersion7();
        await Seed(a, 1);
        ReservationLine[] line = [new(new ProductId(a), 1)];

        Task<LedgerOutcome> first = InTransaction(l => l.TryTakeAsync(line, TestContext.Current.CancellationToken));
        Task<LedgerOutcome> second = InTransaction(l => l.TryTakeAsync(line, TestContext.Current.CancellationToken));
        LedgerOutcome[] outcomes = await Task.WhenAll(first, second);

        outcomes.Count(o => o.Unavailable.Count == 0).ShouldBe(1, "§7.3's whole argument");
        (await Available(a)).ShouldBe(0);
    }

    [Fact]
    public async Task Giving_back_adds_to_available_with_no_guard_and_reports_the_levels()
    {
        var a = Guid.CreateVersion7();
        await Seed(a, 0, reserved: 2);

        IReadOnlyList<ReservedLevel> levels = await InTransaction(l =>
            l.GiveBackAsync([new(new ProductId(a), 2)], TestContext.Current.CancellationToken));

        levels.ShouldHaveSingleItem().Available.ShouldBe(2);
        (await fixture.ScalarAsync<int>("SELECT Value = Reserved FROM inventory.StockItems WHERE ProductId = {0}", a))
            .ShouldBe(0);
    }

    [Fact]
    public async Task The_ledger_refuses_to_run_outside_a_transaction()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IStockLedger ledger = scope.ServiceProvider.GetRequiredService<IStockLedger>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ledger.TryTakeAsync([new(ProductId.New(), 1)], TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Run to see them fail**

Expected: compile failure on `IStockLedger`.

- [ ] **Step 3: Write the port and its SQL**

`IStockLedger.cs` as in Interfaces above, in namespace
`Inventory.Application.Reservations`.

`SqlStockLedger.cs`:

```csharp
using System.Data.Common;
using Dapper;
using Inventory.Application.Reservations;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// §7.3's targeted pessimistic update, per line, on the transaction the unit
/// of work opened. Lines run in <c>ProductId</c> order so two reservations
/// over the same products take their row locks in one sequence.
/// </summary>
internal sealed class SqlStockLedger(InventoryDbContext db) : IStockLedger
{
    private const string Savepoint = "Reserve";

    // §7.3's statement, as printed, with two additions the spec's section 4
    // argues: the OUTPUT returns the stamp, and the stamp is monotonic per
    // row — the clock when it is ahead of the row, one tick past the row
    // otherwise — so two serialised writers' levels carry strictly ordered
    // OccurredAt values whatever the server clock does between them.
    // Zero rows affected is "not enough stock".
    private const string Stamp =
        "CASE WHEN SYSDATETIMEOFFSET() > UpdatedAt THEN SYSDATETIMEOFFSET() ELSE DATEADD(ns, 100, UpdatedAt) END";

    private static readonly string TakeSql =
        $"""
        UPDATE inventory.StockItems
        SET Available = Available - @Quantity, Reserved = Reserved + @Quantity, UpdatedAt = {Stamp}
        OUTPUT inserted.Available, inserted.UpdatedAt
        WHERE ProductId = @ProductId
            AND Available >= @Quantity;
        """;

    // No guard: a release returns what was held, whatever the level is now.
    private static readonly string GiveBackSql =
        $"""
        UPDATE inventory.StockItems
        SET Available = Available + @Quantity, Reserved = Reserved - @Quantity, UpdatedAt = {Stamp}
        OUTPUT inserted.Available, inserted.UpdatedAt
        WHERE ProductId = @ProductId;
        """;

    private sealed record LevelRow(int Available, DateTimeOffset UpdatedAt);

    public async Task<LedgerOutcome> TryTakeAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        await connection.ExecuteAsync(new CommandDefinition(
            $"SAVE TRANSACTION {Savepoint};", transaction: transaction, cancellationToken: ct));

        List<ReservedLevel> levels = [];
        List<ProductId> unavailable = [];

        foreach (ReservationLine line in lines.OrderBy(l => l.ProductId.Value))
        {
            LevelRow? row = await connection.QuerySingleOrDefaultAsync<LevelRow>(new CommandDefinition(
                TakeSql,
                new { ProductId = line.ProductId.Value, line.Quantity },
                transaction: transaction,
                cancellationToken: ct));

            if (row is null)
                unavailable.Add(line.ProductId);
            else
                levels.Add(new ReservedLevel(line.ProductId, row.Available, row.UpdatedAt));
        }

        if (unavailable.Count == 0)
            return new LedgerOutcome(levels, unavailable);

        await connection.ExecuteAsync(new CommandDefinition(
            $"ROLLBACK TRANSACTION {Savepoint};", transaction: transaction, cancellationToken: ct));

        return new LedgerOutcome([], unavailable);
    }

    public async Task<IReadOnlyList<ReservedLevel>> GiveBackAsync(
        IReadOnlyList<ReservationLine> lines,
        CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();
        List<ReservedLevel> levels = [];

        foreach (ReservationLine line in lines.OrderBy(l => l.ProductId.Value))
        {
            LevelRow? row = await connection.QuerySingleOrDefaultAsync<LevelRow>(new CommandDefinition(
                GiveBackSql,
                new { ProductId = line.ProductId.Value, line.Quantity },
                transaction: transaction,
                cancellationToken: ct));

            if (row is not null)
                levels.Add(new ReservedLevel(line.ProductId, row.Available, row.UpdatedAt));
        }

        return levels;
    }

    private (DbConnection, DbTransaction) Current()
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // The same refusal EfUnitOfWork.ExecuteRawAsync makes: a statement
        // with no transaction autocommits on its own, outside the unit the
        // caller believes it is in.
        if (current is null)
            throw new InvalidOperationException("The stock ledger runs only inside the unit of work's transaction (§6.3).");

        return (db.Database.GetDbConnection(), current.GetDbTransaction());
    }
}
```

Register: `services.AddScoped<IStockLedger, SqlStockLedger>();`.

`QuerySingleOrDefaultAsync<LevelRow>` returns null on zero rows, which is the
"not enough stock" branch; on one row it carries the level and the instant the
statement stamped, and the second is what orders two writers' levels for Catalog
(spec, section 4). `SAVE TRANSACTION` and `ROLLBACK TRANSACTION
<name>` leave `@@TRANCOUNT` unchanged, so EF's commit still commits.

- [ ] **Step 4: Run the ledger tests**

Run: `dotnet test tests/Inventory.Api.Tests --filter StockLedgerTests`
Expected: green. The concurrent one is the point of the PR.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Inventory tests/Inventory.Api.Tests/StockLedgerTests.cs
git commit -m "feat(inventory): the stock ledger runs §7.3's statement per line under a savepoint"
```

---

### Task 4: `ReserveStockCommand` and `ReleaseStockCommand`

**Files:**
- Create: `src/Services/Inventory/Inventory.Application/Reservations/ReserveStock/ReserveStockCommand.cs`
- Create: `.../ReserveStock/ReserveStockHandler.cs`
- Create: `.../ReserveStock/ReserveStockValidator.cs`
- Create: `src/Services/Inventory/Inventory.Application/Reservations/ReleaseStock/ReleaseStockCommand.cs`
- Create: `.../ReleaseStock/ReleaseStockHandler.cs`
- Create: `.../ReleaseStock/ReleaseStockValidator.cs` — `RuleFor(c =>
  c.OrderId).NotEmpty()`, because a release for `Guid.Empty` would
  otherwise write a tombstone under an identity no order can have and
  publish `StockReleased` for it; the reserve command already refuses the
  same id
- Create: `src/Services/Inventory/Inventory.Application/Reservations/ReservationErrors.cs`
- Create: `src/Services/Inventory/Inventory.Application/CommandOrigin.cs` —
  Inventory's own two literals. Ordering declares its `CommandOrigin` inside
  `Ordering.Application`, which §4.3 forbids another service to reference,
  so this is not shared and is not moved to a building block either: §9.5's
  rule is that each service maps its two origins as literals of its own.
- Modify: `src/Services/Inventory/Inventory.Application/Integration/InventoryIntegrationEventMapper.cs`
  (three more registry entries)
- Test: `tests/Inventory.Application.Tests/ReserveStockValidatorTests.cs`
- Test: `tests/Inventory.Application.Tests/ReleaseStockValidatorTests.cs` —
  one test: `Guid.Empty` has a validation error for `OrderId`
- Test: `tests/Inventory.Application.Tests/InventoryIntegrationEventMapperTests.cs` (extend)
- Test: `tests/Inventory.Application.Tests/OutboxSerialisationTests.cs`
  (extend) — the three events become stageable the moment the mapper names
  them, so `DomainEventSamples` gains a sample for each and the exact
  stageable-set assertion PR-1 wrote grows to the four types; write that
  first and see it fail on the three unsampled types.

**Interfaces:**
- Produces:
  - `record ReserveStockCommand(Guid OrderId, IReadOnlyList<ReservationLine> Lines) : ICommand<Result>`
  - `record ReleaseStockCommand(Guid OrderId, CommandOrigin Origin) : ICommand<Result>`
  - Both always return `Result.Success()`: a failed reserve is a committed
    `Failed` row, not a failure result (section 4 of the spec).
  - Mapper: `StockReservedDomainEvent → StockReserved`,
    `StockReservationFailedDomainEvent → StockReservationFailed`,
    `StockReleasedDomainEvent → StockReleased`, correlation = the order.

- [ ] **Step 1: Write the failing validator and mapper tests**

```csharp
using FluentValidation.TestHelper;
using Inventory.Application.Reservations.ReserveStock;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Xunit;

namespace Inventory.Application.Tests;

public class ReserveStockValidatorTests
{
    private readonly ReserveStockValidator _validator = new();

    [Fact]
    public void No_lines_is_refused()
    {
        _validator.TestValidate(new ReserveStockCommand(Guid.CreateVersion7(), []))
            .ShouldHaveValidationErrorFor(c => c.Lines);
    }

    [Fact]
    public void A_repeated_product_is_refused()
    {
        ProductId product = ProductId.New();
        _validator.TestValidate(new ReserveStockCommand(Guid.CreateVersion7(), [new(product, 1), new(product, 2)]))
            .ShouldHaveValidationErrorFor(c => c.Lines);
    }

    [Fact]
    public void A_zero_quantity_is_refused()
    {
        _validator.TestValidate(new ReserveStockCommand(Guid.CreateVersion7(), [new(ProductId.New(), 0)]))
            .ShouldHaveValidationErrorFor("Lines[0].Quantity");
    }

    [Fact]
    public void A_quantity_past_the_contract_ceiling_is_refused()
    {
        _validator.TestValidate(new ReserveStockCommand(
                Guid.CreateVersion7(), [new(ProductId.New(), OrderLimits.MaxQuantity + 1)]))
            .ShouldHaveValidationErrorFor("Lines[0].Quantity");
    }
}
```

The test file imports `Common.Contracts.Ordering.V1` for `OrderLimits`.

```csharp
```

Mapper additions:

```csharp
[Fact]
public void The_three_reservation_events_become_their_contracts_correlated_on_the_order()
{
    OrderId order = OrderId.New();
    ProductId short1 = ProductId.New();

    IReadOnlyList<object> mapped = Mapper().Map(
    [
        new StockReservedDomainEvent(order, Raised),
        new StockReservationFailedDomainEvent(order, [short1], Raised),
        new StockReleasedDomainEvent(order, Raised)
    ]);

    mapped.Count.ShouldBe(3);
    mapped[0].ShouldBeOfType<StockReserved>().OrderId.ShouldBe(order.Value);
    mapped[1].ShouldBeOfType<StockReservationFailed>().UnavailableProductIds.ShouldBe([short1.Value]);
    mapped[2].ShouldBeOfType<StockReleased>().CorrelationId.ShouldBe(order.Value);
}
```

- [ ] **Step 2: Run to see them fail**

Expected: compile failure.

- [ ] **Step 3: Write the commands**

`ReserveStockCommand.cs` and validator:

```csharp
using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.ReserveStock;

public sealed record ReserveStockCommand(Guid OrderId, IReadOnlyList<ReservationLine> Lines) : ICommand<Result>;
```

```csharp
using Common.Contracts.Ordering.V1;
using FluentValidation;

namespace Inventory.Application.Reservations.ReserveStock;

/// <summary>
/// Ordering's contract bounds, because the saga built this from an order that
/// already satisfied them: a violation is the sender's bug, not a stock decision.
/// </summary>
public sealed class ReserveStockValidator : AbstractValidator<ReserveStockCommand>
{
    public ReserveStockValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.Lines).NotEmpty();
        RuleFor(c => c.Lines)
            .Must(lines => lines.Select(l => l.ProductId).Distinct().Count() == lines.Count)
            .WithMessage("A product appears at most once.");
        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Quantity)
                .GreaterThanOrEqualTo(OrderLimits.MinQuantity)
                .LessThanOrEqualTo(OrderLimits.MaxQuantity);
        });
    }
}
```

`ReserveStockHandler.cs`:

```csharp
using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.ReserveStock;

public sealed class ReserveStockHandler(
    IReservationRepository reservations,
    IStockLedger ledger,
    TimeProvider clock)
    : ICommandHandler<ReserveStockCommand, Result>
{
    public async Task<Result> HandleAsync(ReserveStockCommand command, CancellationToken ct)
    {
        var order = new OrderId(command.OrderId);
        DateTimeOffset now = clock.GetUtcNow();
        Reservation? existing = await reservations.GetForUpdateAsync(order, ct);

        // Section 4's outcome table: an existing row answers again rather
        // than reserving twice, and a Released row is ADR-024's refusal. The
        // row, or its absence, is locked until this commits, so a second
        // command for the same order waits here and then sees what this did.
        if (existing is not null)
        {
            existing.AnswerAgain(now);
            return Result.Success();
        }

        LedgerOutcome outcome = await ledger.TryTakeAsync(command.Lines, ct);

        reservations.Add(outcome.Unavailable.Count == 0
            ? Reservation.Reserve(order, command.Lines, outcome.Levels, now)
            : Reservation.Fail(order, command.Lines, outcome.Unavailable, now));

        return Result.Success();
    }
}
```

`CommandOrigin.cs`:

```csharp
namespace Inventory.Application;

/// <summary>
/// §9.5's two mappings of a command's origin, both literals: <c>User</c> at
/// an endpoint, <c>System</c> in a message mapper. Inventory's own, because
/// Ordering's is inside a service assembly §4.3 keeps to itself.
/// </summary>
public enum CommandOrigin
{
    User,
    System
}
```

`ReleaseStockCommand.cs` and handler:

```csharp
using Common.Application;

namespace Inventory.Application.Reservations.ReleaseStock;

public sealed record ReleaseStockCommand(Guid OrderId, CommandOrigin Origin) : ICommand<Result>;
```

```csharp
using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.ReleaseStock;

/// <summary>Both of ADR-024's guarantees, and the one place they are implemented.</summary>
public sealed class ReleaseStockHandler(
    IReservationRepository reservations,
    IStockLedger ledger,
    TimeProvider clock)
    : ICommandHandler<ReleaseStockCommand, Result>
{
    public async Task<Result> HandleAsync(ReleaseStockCommand command, CancellationToken ct)
    {
        var order = new OrderId(command.OrderId);
        DateTimeOffset now = clock.GetUtcNow();
        Reservation? reservation = await reservations.GetForUpdateAsync(order, ct);

        // Two releases for an unknown order serialise on the key-range lock the
        // read took: the second waits, then finds the tombstone the first wrote.
        if (reservation is null)
        {
            reservations.Add(Reservation.Tombstone(order, now));
            return Result.Success();
        }

        IReadOnlyList<ReservedLevel> levels = reservation.Status == ReservationStatus.Reserved
            ? await ledger.GiveBackAsync(reservation.Lines, ct)
            : [];

        reservation.Release(levels, now);
        return Result.Success();
    }
}
```

`Origin` is carried for §9.5's two literals and read by nothing here; the
endpoint passes `User`, the mapper `System`.

`ReleaseStockValidator.cs`:

```csharp
using FluentValidation;

namespace Inventory.Application.Reservations.ReleaseStock;

public sealed class ReleaseStockValidator : AbstractValidator<ReleaseStockCommand>
{
    public ReleaseStockValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
    }
}
```

The endpoint test in Task 6 posts a release for `Guid.Empty` and expects
400 with no row written; the message path's equivalent is a domain
rejection the consumer acks.

Mapper registry additions and `ToContract` methods, correlation on the order:

```csharp
[typeof(StockReservedDomainEvent)] = e => ToContract((StockReservedDomainEvent)e),
[typeof(StockReservationFailedDomainEvent)] = e => ToContract((StockReservationFailedDomainEvent)e),
[typeof(StockReleasedDomainEvent)] = e => ToContract((StockReleasedDomainEvent)e),
```

```csharp
private static StockReserved ToContract(StockReservedDomainEvent e) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = e.OrderId.Value,
    OccurredAt = e.OccurredAt,
    OrderId = e.OrderId.Value
};

private static StockReservationFailed ToContract(StockReservationFailedDomainEvent e) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = e.OrderId.Value,
    OccurredAt = e.OccurredAt,
    OrderId = e.OrderId.Value,
    UnavailableProductIds = [.. e.UnavailableProductIds.Select(p => p.Value)]
};

private static StockReleased ToContract(StockReleasedDomainEvent e) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = e.OrderId.Value,
    OccurredAt = e.OccurredAt,
    OrderId = e.OrderId.Value
};
```

- [ ] **Step 4: Run the application tests**

Run: `dotnet test tests/Inventory.Application.Tests`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Inventory/Inventory.Application tests/Inventory.Application.Tests
git commit -m "feat(inventory): ReserveStock and ReleaseStock under ADR-024's guarantees"
```

---

### Task 5: The command queue

**Files:**
- Create: `src/Services/Inventory/Inventory.Infrastructure/Messaging/CommandMappers.cs`
- Create: `src/Services/Inventory/Inventory.Infrastructure/Messaging/RetryPolicy.cs`
  — Ordering's `RetryPolicy` is `internal` to `Ordering.Infrastructure` and
  the scaffold, having no endpoint, carries none; this is Ordering's file
  with the namespace changed, because §9.8 prints the ladder per service
  and a building block would be a fourth thing to keep in step
- Modify: `src/Services/Inventory/Inventory.Infrastructure/Messaging/DependencyInjection.cs`
- Modify: `deploy/compose/rabbitmq/definitions.json` — `inventory-svc`'s
  three patterns, which the scaffold copied from `catalog-svc` and which
  admit no queue
- Test: `tests/Inventory.Api.Tests/MessagingRegistrationTests.cs` (extend)
- Test: `tests/Inventory.Api.Tests/InventoryCommandEndpointTests.cs`

**Interfaces:**
- Produces: `public const string CommandsQueue = "inventory-commands"` on
  `Inventory.Infrastructure.Messaging.DependencyInjection`;
  `ReserveStockMapper : ICommandMessageMapper<ReserveStock, ReserveStockCommand>`;
  `ReleaseStockMapper : ICommandMessageMapper<ReleaseStock, ReleaseStockCommand>`.

- [ ] **Step 1: Write the failing tests**

Registration, in the scaffolded `MessagingRegistrationTests`:

```csharp
[Fact]
public void Every_command_in_the_accepts_column_is_registered_and_bound()
{
    ServiceCollection services = new();

    services.AddMassTransitMessaging(Configuration());

    Type[] consumers =
    [
        typeof(CommandConsumer<ReserveStock, ReserveStockCommand>),
        typeof(CommandConsumer<ReleaseStock, ReleaseStockCommand>)
    ];
    foreach (Type consumer in consumers)
    {
        services.ShouldContain(
            d => d.ImplementationType == consumer || d.ServiceType == consumer,
            $"{consumer.Name} is in §3.2's Accepts column and has no AddConsumer");
    }
}
```

Endpoint, over containers, in Ordering's `OrderingCommandEndpointTests`
shape — `SendAsync` sends to `queue:inventory-commands` through the bus, and
`Eventually` polls with a 30-second budget:

```csharp
[Fact]
public async Task A_reserve_over_the_queue_takes_the_stock_and_stages_StockReserved()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();

    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));

    await EventuallyStatus(order, "Reserved");
    (await Available(product)).ShouldBe(1);
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal))
        .ShouldBe(1);
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal))
        .ShouldBe(1);
}

[Fact]
public async Task A_short_reserve_commits_Failed_and_moves_no_stock()
{
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    await SeedStock(a, 5);
    await SeedStock(b, 0);
    var order = Guid.CreateVersion7();

    await SendAsync(new ReserveStock(order, [new StockLine(a, 1), new StockLine(b, 1)]));

    await EventuallyStatus(order, "Failed");
    (await Available(a)).ShouldBe(5);
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReservationFailed", StringComparison.Ordinal))
        .ShouldBe(1);
}

[Fact]
public async Task A_release_before_its_reserve_leaves_a_tombstone_that_refuses_the_reserve()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();

    await SendAsync(new ReleaseStock(order));
    await EventuallyStatus(order, "Released");
    await SendAsync(new ReserveStock(order, [new StockLine(product, 1)]));

    await Eventually(
        async () => (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)),
        expected: 2,
        because: "ADR-024: the tombstone answers the refused reserve with the same postcondition");
    (await Available(product)).ShouldBe(3, "nothing was held");
    (await fixture.OutboxAsync()).ShouldNotContain(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal));
}

[Fact]
public async Task A_release_of_a_held_reservation_gives_the_stock_back()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");

    await SendAsync(new ReleaseStock(order));

    await EventuallyStatus(order, "Released");
    (await Available(product)).ShouldBe(3);
}

[Fact]
public async Task A_release_of_a_failed_reservation_publishes_the_postcondition_and_moves_nothing()
{
    var a = Guid.CreateVersion7();
    await SeedStock(a, 0);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(a, 1)]));
    await EventuallyStatus(order, "Failed");

    await SendAsync(new ReleaseStock(order));

    await Eventually(
        async () => (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)),
        expected: 1,
        because: "section 5's table: a Failed row still answers with the postcondition");
    (await StatusAsync(order)).ShouldBe("Failed");
    (await Available(a)).ShouldBe(0);
}

[Fact]
public async Task A_second_release_of_a_released_reservation_publishes_again_and_returns_nothing_twice()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");
    await SendAsync(new ReleaseStock(order));
    await EventuallyStatus(order, "Released");

    await SendAsync(new ReleaseStock(order));

    await Eventually(
        async () => (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)),
        expected: 2,
        because: "ADR-024's first guarantee holds on the second release as on the first");
    (await Available(product)).ShouldBe(3, "the lines were given back once, not twice");
}

[Theory]
[InlineData(0, false)]
[InlineData(OrderLimits.MaxQuantity + 1, false)]
[InlineData(1, true)]
public async Task A_malformed_reserve_is_a_contract_fault_and_is_not_retried(int quantity, bool emptyProduct)
{
    var order = Guid.CreateVersion7();
    Guid product = emptyProduct ? Guid.Empty : Guid.CreateVersion7();

    // drain: false, because a message the mapper refuses never reaches the
    // inbox filter and so leaves no row for the default drain to wait on.
    await SendAsync(new ReserveStock(order, [new StockLine(product, quantity)]), drain: false);

    // A well-formed sentinel behind it on the same queue: once the sentinel
    // has been consumed, the malformed message in front of it has been too,
    // and "no row" is then a verdict rather than a race.
    var sentinelProduct = Guid.CreateVersion7();
    await SeedStock(sentinelProduct, 1);
    var sentinelOrder = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(sentinelOrder, [new StockLine(sentinelProduct, 1)]));
    await EventuallyStatus(sentinelOrder, "Reserved");

    (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0}", order))
        .ShouldBe(0, "the mapper threw ContractMappingException, which the endpoint excludes from retry, and nothing was written");
}
```

`SeedStock`, `Available`, `EventuallyStatus` (reads `Status` from
`inventory.Reservations`) and `SendAsync` are private helpers in the test
file; `SendAsync` resolves `ISendEndpointProvider` from
`fixture.Factory.Services`, gets `queue:inventory-commands`, sends with the
transport `MessageId` pinned, and — with its `drain` parameter at its
default of `true` — waits on the inbox row, the way Ordering's does;
`drain: false` sends and returns, for a message that will never write one.

- [ ] **Step 2: Run to see them fail**

Expected: registration test fails on missing consumers; endpoint tests time out.

- [ ] **Step 3: Write the mappers and the endpoint**

`CommandMappers.cs`:

```csharp
using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Inventory.Application;
using Inventory.Application.Reservations.ReleaseStock;
using Inventory.Application.Reservations.ReserveStock;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;

namespace Inventory.Infrastructure.Messaging;

public sealed class ReserveStockMapper : ICommandMessageMapper<ReserveStock, ReserveStockCommand>
{
    public ReserveStockCommand Map(ReserveStock message)
    {
        if (message.Lines is null || message.Lines.Count == 0)
            throw new ContractMappingException($"No lines on {nameof(ReserveStock)}.");

        // A null element is a malformed payload, and reading its members
        // would throw NullReferenceException — a fault the endpoint retries
        // where this exception is the one it does not.
        if (message.Lines.Any(l => l is null))
            throw new ContractMappingException($"A null line on {nameof(ReserveStock)}.");

        if (message.Lines.Any(l => l.Quantity < OrderLimits.MinQuantity || l.Quantity > OrderLimits.MaxQuantity))
            throw new ContractMappingException($"A quantity outside the contract's bounds on {nameof(ReserveStock)}.");

        // An empty product id is a malformed payload, not an unknown product:
        // let through, it would reach the ledger, affect no row, and be
        // published as an out-of-stock decision about a product that is not one.
        if (message.OrderId == Guid.Empty || message.Lines.Any(l => l.ProductId == Guid.Empty))
            throw new ContractMappingException($"An empty identifier on {nameof(ReserveStock)}.");

        if (message.Lines.Select(l => l.ProductId).Distinct().Count() != message.Lines.Count)
            throw new ContractMappingException($"A repeated product on {nameof(ReserveStock)}.");

        return new ReserveStockCommand(
            message.OrderId,
            [.. message.Lines.Select(l => new ReservationLine(new ProductId(l.ProductId), l.Quantity))]);
    }
}

public sealed class ReleaseStockMapper : ICommandMessageMapper<ReleaseStock, ReleaseStockCommand>
{
    public ReleaseStockCommand Map(ReleaseStock message) =>
        new(message.OrderId, CommandOrigin.System);
}
```

In `AddMassTransitMessaging`, inside `AddMassTransit`:

```csharp
public const string CommandsQueue = "inventory-commands";

x.AddConsumer<CommandConsumer<ReserveStock, ReserveStockCommand>>();
x.AddConsumer<CommandConsumer<ReleaseStock, ReleaseStockCommand>>();
```

and inside `UsingRabbitMq`, after `cfg.Host(...)`:

```csharp
cfg.ReceiveEndpoint(
    CommandsQueue,
    e =>
    {
        e.UseMessageRetry(r =>
        {
            r.Ignore<ContractMappingException>();
            RetryPolicy.Standard(r);
        });
        e.UseConsumeFilter(typeof(InboxFilter<>), context);
        e.UseInMemoryOutbox(context);

        e.ConfigureConsumer<CommandConsumer<ReserveStock, ReserveStockCommand>>(context);
        e.ConfigureConsumer<CommandConsumer<ReleaseStock, ReleaseStockCommand>>(context);
    });
```

The mappers are found by the Infrastructure `AddPluggableFrom` scan, as
Ordering's are: `ICommandMessageMapper<,>` is one of `PluggableInterfaces`'
five entries.

**The broker has to let `inventory-svc` declare and read the queue.** The
scaffold renamed `catalog-svc`'s patterns, which cover the service's own
contract exchanges and MassTransit's and nothing else — a publisher's grant.
A receive endpoint declares a queue and binds it, so the entry becomes
`ordering-svc`'s shape with the names changed, in
`deploy/compose/rabbitmq/definitions.json`:

```json
"configure": "^(inventory-|Common\\.Contracts|Inventory\\.Infrastructure\\.Messaging:|MassTransit:)",
"write": "^(inventory-|Common\\.Contracts(\\.Inventory\\.V1:|:)|Inventory\\.Infrastructure\\.Messaging:|MassTransit:)",
"read": "^(inventory-|Common\\.Contracts|Inventory\\.Infrastructure\\.Messaging:|MassTransit:)"
```

`write` admits Inventory's own contract exchanges and the bare
`Common.Contracts:` fault exchange and no other context's, which is the
rule `deploy/compose/rabbitmq/check_permissions.py` enforces; run it and its
suite after the edit:

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

Suite first, then gate, which is `docs/testing.md`'s order and the broker
workflow's.

The test fixture sends `ReserveStock` to `queue:inventory-commands` as
`inventory-svc`, which the `inventory-` prefix admits, so this PR needs no
harness-only widening; PR-3, whose tests publish another context's events,
does.

- [ ] **Step 4: Run the API suite**

Run: `dotnet test tests/Inventory.Api.Tests`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Inventory/Inventory.Infrastructure tests/Inventory.Api.Tests deploy/compose/rabbitmq/definitions.json
git commit -m "feat(inventory): the inventory-commands endpoint, and the broker grant it needs"
```

---

### Task 6: The three reservation endpoints

**Files:**
- Create: `src/Services/Inventory/Inventory.Application/Reservations/GetReservation/GetReservationQuery.cs`
- Create: `.../GetReservation/GetReservationHandler.cs`
- Create: `.../GetReservation/ReservationDto.cs`
- Create: `src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationCommand.cs`
- Create: `.../Reinstate/ReinstateReservationHandler.cs`
- Create: `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs`
- Modify: `Program.cs` (`app.MapReservationEndpoints();`)
- Test: `tests/Inventory.Api.Tests/ReservationEndpointsTests.cs`

**Interfaces:**
- `GET /v1/inventory/reservations/{orderId}` → `ReservationDto(Guid OrderId,
  string Status, IReadOnlyList<ReservationLineDto> Lines, DateTimeOffset UpdatedAt)`
  or 404.
- `POST /v1/inventory/reservations/{orderId}/release` → 204 always.
- `POST /v1/inventory/reservations/{orderId}/reinstate` → 204; 404 when no
  reservation exists for the order, as the spec's table says beside `GET`;
  422 `reservation.not_reinstatable` when the row is not `Released` or has
  no lines; 422 `reservation.unavailable` naming the unavailable ids in its
  description, since `ResultExtensions` serialises the description and
  nothing else.
- `ReservationErrors.NotFound` (`Error.NotFound`),
  `ReservationErrors.NotReinstatable` (`Error.Rule`),
  `ReservationErrors.Unavailable(ids)` (`Error.Rule`, ids in the text).

- [ ] **Step 1: Write the failing endpoint tests**

```csharp
[Fact]
public async Task The_runbook_can_read_a_reservation()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");

    ReservationDto? dto = await Admin().GetFromJsonAsync<ReservationDto>(
        $"/v1/inventory/reservations/{order}", TestContext.Current.CancellationToken);

    dto.ShouldNotBeNull();
    dto.Status.ShouldBe("Reserved");
    dto.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);
}

[Fact]
public async Task The_runbook_can_release_by_hand_and_the_release_answers_like_the_command()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");

    HttpResponseMessage response = await Admin().PostAsync(
        $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken);

    response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    (await Available(product)).ShouldBe(3);
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)).ShouldBe(1);
}

[Fact]
public async Task Releasing_the_empty_order_id_is_400_and_writes_nothing()
{
    HttpResponseMessage response = await Admin().PostAsync(
        $"/v1/inventory/reservations/{Guid.Empty}/release", null, TestContext.Current.CancellationToken);

    response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0}", Guid.Empty))
        .ShouldBe(0, "no tombstone for an identity no order can have");
}

[Fact]
public async Task Releasing_an_unknown_order_is_204_and_writes_the_tombstone()
{
    var order = Guid.CreateVersion7();

    HttpResponseMessage response = await Admin().PostAsync(
        $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken);

    response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    (await StatusAsync(order)).ShouldBe("Released");
}

[Fact]
public async Task Reinstating_a_released_reservation_takes_the_stock_again_and_publishes_only_levels()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");
    await SendAsync(new ReleaseStock(order));
    await EventuallyStatus(order, "Released");
    int before = (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal));

    HttpResponseMessage response = await Admin().PostAsync(
        $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken);

    response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    (await StatusAsync(order)).ShouldBe("Reserved");
    (await Available(product)).ShouldBe(1);
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal))
        .ShouldBe(before, "an operator's act answers no saga");
}

[Fact]
public async Task Reinstating_a_tombstone_and_reinstating_into_a_shortage_are_both_422_under_their_own_codes()
{
    var order = Guid.CreateVersion7();
    await Admin().PostAsync($"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken);
    HttpResponseMessage tombstone = await Admin().PostAsync(
        $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken);
    tombstone.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    (await tombstone.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
        .ShouldContain("reservation.not_reinstatable");

    var product = Guid.CreateVersion7();
    await SeedStock(product, 2);
    var held = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(held, [new StockLine(product, 2)]));
    await EventuallyStatus(held, "Reserved");
    await SendAsync(new ReleaseStock(held));
    await EventuallyStatus(held, "Released");
    await fixture.ExecuteAsync("UPDATE inventory.StockItems SET Available = 1 WHERE ProductId = {0}", product);

    HttpResponseMessage shortage = await Admin().PostAsync(
        $"/v1/inventory/reservations/{held}/reinstate", null, TestContext.Current.CancellationToken);
    shortage.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    (await shortage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
        .ShouldContain(product.ToString(), "the operator needs to know which product is short");
    (await StatusAsync(held)).ShouldBe("Released");
}

[Fact]
public async Task Two_releases_for_one_unknown_order_at_once_leave_one_tombstone_and_no_500()
{
    var order = Guid.CreateVersion7();

    HttpResponseMessage[] responses = await Task.WhenAll(
        Admin().PostAsync($"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken),
        Admin().PostAsync($"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken));

    responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent,
        "the second creator waited on the first's key-range lock and found the tombstone");
    (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0}", order))
        .ShouldBe(1);
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal))
        .ShouldBe(2, "ADR-024: both releases answered");
}

[Fact]
public async Task A_release_and_a_reinstate_at_once_end_in_exactly_one_state()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");
    await SendAsync(new ReleaseStock(order));
    await EventuallyStatus(order, "Released");

    HttpResponseMessage[] responses = await Task.WhenAll(
        Admin().PostAsync($"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken),
        Admin().PostAsync($"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken));

    responses.ShouldAllBe(r => r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict);
    string status = await StatusAsync(order);
    int available = await Available(product);
    (status, available).ShouldBeOneOf(
        ("Reserved", 1),   // the reinstate ran second and re-took the lines
        ("Released", 3));  // the release ran second and gave them back, or the reinstate lost
}

[Fact]
public async Task Every_reservation_endpoint_requires_the_admin_permission()
{
    HttpClient client = fixture.Factory.CreateClient();
    client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
    var order = Guid.CreateVersion7();

    (await client.GetAsync($"/v1/inventory/reservations/{order}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    (await client.PostAsync($"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    (await client.PostAsync($"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
}
```

- [ ] **Step 2: Run to see them fail**

Expected: 404s and compile failures.

- [ ] **Step 3: Write the query, the command and the endpoints**

`ReservationDto.cs`:

```csharp
namespace Inventory.Application.Reservations.GetReservation;

public sealed record ReservationLineDto(Guid ProductId, int Quantity);

public sealed record ReservationDto(
    Guid OrderId,
    string Status,
    IReadOnlyList<ReservationLineDto> Lines,
    DateTimeOffset UpdatedAt);
```

`GetReservationQuery.cs` / handler (Dapper, two statements, one connection):

```csharp
using Common.Application;

namespace Inventory.Application.Reservations.GetReservation;

public sealed record GetReservationQuery(Guid OrderId) : IQuery<ReservationDto?>;
```

```csharp
using System.Data;
using Common.Application;
using Dapper;

namespace Inventory.Application.Reservations.GetReservation;

public sealed class GetReservationHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetReservationQuery, ReservationDto?>
{
    private const string Sql =
        """
        SELECT OrderId, Status, UpdatedAt FROM inventory.Reservations WHERE OrderId = @OrderId;
        SELECT ProductId, Quantity FROM inventory.ReservationLines WHERE OrderId = @OrderId ORDER BY ProductId;
        """;

    public async Task<ReservationDto?> HandleAsync(GetReservationQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();
        using SqlMapper.GridReader grid = await connection.QueryMultipleAsync(
            new CommandDefinition(Sql, new { query.OrderId }, cancellationToken: ct));

        // A named record, not a ValueTuple: Dapper maps columns by name, and a
        // tuple's members are Item1..Item3.
        ReservationHead? head = await grid.ReadSingleOrDefaultAsync<ReservationHead>();
        if (head is null)
            return null;

        List<ReservationLineDto> lines = (await grid.ReadAsync<ReservationLineDto>()).AsList();
        return new ReservationDto(head.OrderId, head.Status, lines, head.UpdatedAt);
    }

    private sealed record ReservationHead(Guid OrderId, string Status, DateTimeOffset UpdatedAt);
}
```

`ReinstateReservationCommand.cs` / handler:

```csharp
using Common.Application;

namespace Inventory.Application.Reservations.Reinstate;

public sealed record ReinstateReservationCommand(Guid OrderId) : ICommand<Result>;
```

```csharp
using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.Reinstate;

public sealed class ReinstateReservationHandler(
    IReservationRepository reservations,
    IStockLedger ledger,
    TimeProvider clock)
    : ICommandHandler<ReinstateReservationCommand, Result>
{
    public async Task<Result> HandleAsync(ReinstateReservationCommand command, CancellationToken ct)
    {
        Reservation? reservation = await reservations.GetForUpdateAsync(new OrderId(command.OrderId), ct);
        if (reservation is null)
            return Result.Failure(ReservationErrors.NotFound);
        if (reservation.Status != ReservationStatus.Released || reservation.Lines.Count == 0)
            return Result.Failure(ReservationErrors.NotReinstatable);

        LedgerOutcome outcome = await ledger.TryTakeAsync(reservation.Lines, ct);
        if (outcome.Unavailable.Count > 0)
            return Result.Failure(ReservationErrors.Unavailable(outcome.Unavailable));

        reservation.Reinstate(outcome.Levels, clock.GetUtcNow());
        return Result.Success();
    }
}
```

A failure result rolls the transaction back, which undoes nothing here
because the savepoint already did, and stages nothing, which is right: a
refused reinstatement publishes no level.

`ReservationErrors.cs`:

```csharp
using Common.Application;
using Inventory.Domain.Stock;

namespace Inventory.Application.Reservations;

public static class ReservationErrors
{
    public static readonly Error NotFound =
        Error.NotFound("reservation.not_found", "No reservation for that order.");

    public static readonly Error NotReinstatable =
        Error.Rule("reservation.not_reinstatable", "Only a released reservation with lines can be reinstated.");

    // The ids travel in the description because that is the one member
    // ResultExtensions serialises, and an operator reinstating by hand needs
    // to know which product to receive before trying again.
    public static Error Unavailable(IReadOnlyList<ProductId> products) =>
        Error.Rule(
            "reservation.unavailable",
            $"Not enough stock for: {string.Join(", ", products.Select(p => p.Value))}.");
}
```

`ReservationEndpoints.cs`:

```csharp
using Common.Application;
using Common.Web;
using Inventory.Application;
using Inventory.Application.Reservations.GetReservation;
using Inventory.Application.Reservations.Reinstate;
using Inventory.Application.Reservations.ReleaseStock;

namespace Inventory.Api.Endpoints;

public static class ReservationEndpoints
{
    public static void MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/inventory/reservations")
            .WithTags("Reservations")
            .RequireAuthorization(InventoryPermissions.Admin);

        group
            .MapGet(
                "/{orderId:guid}",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    ReservationDto? dto = await dispatcher.QueryAsync(new GetReservationQuery(orderId), ct);

                    return dto is null ? Results.NotFound() : Results.Ok(dto);
                })
            .WithName("GetReservation");

        group
            .MapPost(
                "/{orderId:guid}/release",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(new ReleaseStockCommand(orderId, CommandOrigin.User), ct);

                    return result.ToHttpResult();
                })
            .WithName("ReleaseReservation");

        group
            .MapPost(
                "/{orderId:guid}/reinstate",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(new ReinstateReservationCommand(orderId), ct);

                    return result.ToHttpResult();
                })
            .WithName("ReinstateReservation");
    }
}
```

- [ ] **Step 4: Run the API suite**

Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Inventory tests/Inventory.Api.Tests
git commit -m "feat(inventory): the reservation admin endpoints the runbook promises"
```

---

### Task 7: §3.2's sentence and the concurrency test

**Files:**
- Modify: `docs/backend-architecture/03-bounded-contexts.md` — after the
  bullet "Consuming `OrderCancelled` releases the stock…", add a sibling
  bullet.
- Test: `tests/Inventory.Api.Tests/InventoryCommandEndpointTests.cs` (extend)

- [ ] **Step 1: The bullet**

```markdown
- **Consuming `ShipmentDispatched` fulfils the reservation and publishes
  nothing.** Each line's reserved count falls by its quantity and the
  available level stays where the reserve put it, so no `StockLevelChanged`
  is owed; a later release for that order publishes `StockReleased` on the
  terms above and returns nothing to the level, because the stock has left.
```

PR-3 implements it; the sentence lands here because this PR is the one that
opens the chapter, and a derivation the table leaves to the reader is the
gap this section already names.

- [ ] **Step 2: The race test**

```csharp
[Fact]
public async Task Two_orders_for_the_last_unit_leave_one_Reserved_and_one_Failed()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 1);
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();

    await Task.WhenAll(
        SendAsync(new ReserveStock(first, [new StockLine(product, 1)])),
        SendAsync(new ReserveStock(second, [new StockLine(product, 1)])));

    await Eventually(
        () => fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId IN ({0}, {1})", first, second),
        expected: 2,
        because: "both commands are answered");
    string[] statuses = [await StatusAsync(first), await StatusAsync(second)];
    statuses.Count(s => s == "Reserved").ShouldBe(1);
    statuses.Count(s => s == "Failed").ShouldBe(1);
    (await Available(product)).ShouldBe(0);
}
```

- [ ] **Step 3: Run `/validate-blueprint`, `/check-links` and the suite; commit**

```bash
dotnet test tests/Inventory.Api.Tests
git add docs/backend-architecture/03-bounded-contexts.md tests/Inventory.Api.Tests
git commit -m "docs: state what consuming ShipmentDispatched does to a reservation"
```

---

### Task 8: Whole-solution verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green.
- [ ] `/validate-blueprint` and `/check-links`, because §3.2 moved:
  `docs/change-locality.md`'s procedure owes the audit after any chapter
  edit. Run them in Task 7, before that commit.
- [ ] PR body: `| Class | A+B+D |`, touch set from Global Constraints. Body
  files the reservation-purge issue.

## Self-review

- Spec coverage: section 3's `Reservation` and tombstone → Task 1; section 4
  (savepoint, ordering, outcome table, malformed command) → Tasks 3, 4, 5;
  section 5's release table → Tasks 4, 5; section 6's three endpoints → Task
  6; section 7's tables, migration and rowversion race → Task 2 (the race
  itself is exercised in PR-3, where fulfilment exists); section 8's queue →
  Task 5; section 9's tests → Tasks 3, 5, 6, 7; section 10's §3.2 sentence →
  Task 7.
- Types: `ReservationLine`, `ReservedLevel`, `LedgerOutcome`, `IStockLedger`,
  `Reservation.Reserve/Fail/Tombstone/Release/Reinstate/AnswerAgain`,
  `ReserveStockCommand`, `ReleaseStockCommand`, `ReinstateReservationCommand`,
  `GetReservationQuery`, `ReservationDto` agree across tasks.
- Every refusal is `Error.Rule` and 422; no building block moves, and the B
  half of `A+B` is §3.2's sentence alone.
