# Inventory PR-3 — consume OrderCancelled and ShipmentDispatched — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bind §3.2's Consumes column for Inventory — `OrderCancelled` releases
the reservation on the same terms as `ReleaseStock`, `ShipmentDispatched`
fulfils it — and record the one case ADR-029 leaves open as state a Local-lane
projection counts.

**Architecture:** One event queue, `inventory-events`, binds both events with
`IntegrationEventConsumer<T>`. Each handler dispatches a command: the existing
`ReleaseStockCommand` with `CommandOrigin.System`, and a new
`FulfilReservationCommand` that moves `Reserved` down by statement and the row
to `Fulfilled`. A `Released` row at despatch sets `DespatchedUnreservedAt` on
the reservation and raises a domain event with a projection handler, so the
outbox stages a `Local` row and `inventory.fulfilment.unreserved` fires once
per fact by claiming the row.

**Tech Stack:** MassTransit receive endpoint with the inbox filter, Dapper
through the ledger port, `IProjectionHandler<T>` on the outbox's Local lane,
`System.Diagnostics.Metrics`.

**Spec:** `docs/superpowers/specs/2026-09-18-inventory-service-design.md`,
sections 5 (the despatch table), 8, 9 and 13.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+B+D+E.** Touch set: `src/Services/Inventory/**`,
  `tests/Inventory.*`, `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`
  and `tests/Common.Web.Tests/ObservabilityTests.cs` — the B half is two
  `AddMeter` lines, because §13.2's export lists meters by name and a meter
  it does not name is collected by nobody — `deploy/observability/check.py`,
  the D half: this PR deletes the exemption PR-1 added, and the gate refuses
  a stale one — and `Inventory.Infrastructure.csproj`, the E half: one
  package reference the copied `OutboxStats` needs (Task 5), with no
  `Version=`, since the pin is `Directory.Packages.props`'s and the package
  is already in it.
- Depends on PR-2 having merged.
- Every event in §3.2's Consumes column has both an `AddConsumer` and a
  `ConfigureConsumer`, and the registration test says so.
- A business counter is a claim against a row (§13.3), never an increment
  inside the write transaction.
- Every step that adds behaviour writes its test first; container tests are
  never skipped.

---

### Task 1: `Reservation.Fulfil` and `RecordDespatchUnreserved`

**Files:**
- Modify: `src/Services/Inventory/Inventory.Domain/Reservations/Reservation.cs`
- Modify: `src/Services/Inventory/Inventory.Domain/Reservations/Events/ReservationEvents.cs`
- Modify: `src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationHandler.cs`
  — the `NotReinstatable` precheck gains the despatched condition, so the
  endpoint answers 422 rather than reaching the ledger and faulting on the
  domain guard
- Modify: `src/Services/Inventory/Inventory.Application/Reservations/ReservationErrors.cs`
  (the description)
- Test: `tests/Inventory.Domain.Tests/ReservationTests.cs` (extend)
- Test: `tests/Inventory.Api.Tests/ReservationEndpointsTests.cs` (extend:
  reinstating after an unreserved despatch is 422; the row and the stock
  are unchanged)

**Interfaces:**
- Produces: `void Fulfil(DateTimeOffset now)` — from `Reserved` moves to
  `Fulfilled`, raises nothing; from `Fulfilled` does nothing; from `Failed`
  or a tombstone throws `DomainException`; from `Released` with lines throws
  too, because that case is `RecordDespatchUnreserved`'s.
  `void RecordDespatchUnreserved(DateTimeOffset now)` — allowed only from
  `Released` with lines; sets `DespatchedUnreservedAt` once and raises
  `DespatchedUnreservedDomainEvent(OrderId, DateTimeOffset)`; a second call
  is a no-op.
  `DateTimeOffset? DespatchedUnreservedAt { get; }`.
  `Reinstate` gains one guard: a row with `DespatchedUnreservedAt` set
  throws `DomainException`, because the parcel has gone and there is nothing
  to reinstate stock for; PR-2's `ReinstateReservationHandler` checks status
  and lines and answers `ReservationErrors.NotReinstatable`, so the handler
  gains the same condition beside those two, and the error's description
  becomes "Only a released reservation with lines, not yet despatched, can be
  reinstated." — the same code, one more reason in the text.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Fulfil_moves_a_held_reservation_to_fulfilled_and_raises_nothing()
{
    Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Reserved, Lines());

    reservation.Fulfil(Now);

    reservation.Status.ShouldBe(ReservationStatus.Fulfilled);
    reservation.DomainEvents.ShouldBeEmpty("no §3.2 event describes despatch and the level did not move");
}

[Fact]
public void Fulfil_twice_is_a_no_op()
{
    Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Fulfilled, Lines());

    reservation.Fulfil(Now);

    reservation.Status.ShouldBe(ReservationStatus.Fulfilled);
}

[Theory]
[InlineData(ReservationStatus.Failed)]
[InlineData(ReservationStatus.Released)]
public void Fulfil_refuses_what_was_never_held_or_is_no_longer_held(ReservationStatus status)
{
    Should.Throw<DomainException>(() => Reservation.Rehydrate(OrderId.New(), status, Lines()).Fulfil(Now));
}

[Fact]
public void A_despatch_against_a_released_reservation_is_recorded_once_as_state()
{
    Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Released, Lines());

    reservation.RecordDespatchUnreserved(Now);
    reservation.RecordDespatchUnreserved(Now.AddMinutes(1));

    reservation.Status.ShouldBe(ReservationStatus.Released, "no stock moves: ADR-029's gap stays open");
    reservation.DespatchedUnreservedAt.ShouldBe(Now);
    reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<DespatchedUnreservedDomainEvent>();
}

[Fact]
public void A_tombstone_cannot_record_a_despatch()
{
    Should.Throw<DomainException>(() => Reservation.Tombstone(OrderId.New(), Now).RecordDespatchUnreserved(Now));
}

[Fact]
public void A_reservation_whose_parcel_has_gone_cannot_be_reinstated()
{
    Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Released, Lines());
    reservation.RecordDespatchUnreserved(Now);

    Should.Throw<DomainException>(() => reservation.Reinstate(Levels(), Now));
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Inventory.Domain.Tests --filter ReservationTests`
Expected: compile failure.

- [ ] **Step 3: Write the members**

Event, in `ReservationEvents.cs`:

```csharp
/// <summary>
/// A despatch met a reservation already released — ADR-029's open case. Local
/// lane only: no contract in §3.2 describes it, and the projection that counts
/// it is the reason it is an event at all.
/// </summary>
public sealed record DespatchedUnreservedDomainEvent(OrderId OrderId, DateTimeOffset OccurredAt) : IDomainEvent;
```

In `Reservation`:

```csharp
public DateTimeOffset? DespatchedUnreservedAt { get; private set; }

public void Fulfil(DateTimeOffset now)
{
    if (Status == ReservationStatus.Fulfilled)
        return;
    if (Status != ReservationStatus.Reserved)
        throw new DomainException("Only a held reservation can be fulfilled.");

    Status = ReservationStatus.Fulfilled;
    UpdatedAt = now;
}

public void RecordDespatchUnreserved(DateTimeOffset now)
{
    if (Status != ReservationStatus.Released || _lines.Count == 0)
        throw new DomainException("Only a released reservation with lines can record an unreserved despatch.");
    if (DespatchedUnreservedAt is not null)
        return;

    DespatchedUnreservedAt = now;
    UpdatedAt = now;
    Raise(new DespatchedUnreservedDomainEvent(Id, now));
}
```

And in `Reinstate`, the existing guard becomes:

```csharp
if (Status != ReservationStatus.Released || _lines.Count == 0 || DespatchedUnreservedAt is not null)
    throw new DomainException("Only a released reservation with lines, not yet despatched, can be reinstated.");
```

and in `ReinstateReservationHandler`, the precheck that answers
`NotReinstatable` before the ledger is called:

```csharp
if (reservation.Status != ReservationStatus.Released
    || reservation.Lines.Count == 0
    || reservation.DespatchedUnreservedAt is not null)
{
    return Result.Failure(ReservationErrors.NotReinstatable);
}
```

so the endpoint answers 422 rather than taking stock and then faulting on
the domain guard. The endpoint test for it — a reservation released, then
despatched, then reinstated, answering 422 with the row still `Released`
and the level unmoved — joins `ReservationEndpointsTests` in this task;
Task 4 repeats the assertion from the broker's side.

- [ ] **Step 4: Run the domain tests; commit**

```bash
dotnet test tests/Inventory.Domain.Tests
dotnet test tests/Inventory.Api.Tests --filter ReservationEndpointsTests
git add src/Services/Inventory/Inventory.Domain src/Services/Inventory/Inventory.Application tests/Inventory.Domain.Tests tests/Inventory.Api.Tests
git commit -m "feat(inventory): Reservation.Fulfil, and the unreserved despatch as state"
```

---

### Task 2: The two columns and the ledger's fulfil statement

**Files:**
- Modify: `Inventory.Infrastructure/Persistence/ReservationConfiguration.cs`
  (`DespatchedUnreservedAt datetimeoffset(7) NULL`, `UnreservedCounted bit NOT NULL DEFAULT 0` as a shadow property)
- Create: `Persistence/Migrations/<ts>_AddDespatchTracking.cs` (generated)
- Modify: `Inventory.Application/Reservations/IStockLedger.cs` (add `FulfilAsync`)
- Modify: `Inventory.Infrastructure/Persistence/SqlStockLedger.cs`
- Test: `tests/Inventory.Api.Tests/StockLedgerTests.cs` (extend)

**Interfaces:**
- Produces: `Task FulfilAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct)`
  — per line in `ProductId` order:
  `UPDATE inventory.StockItems SET Reserved = Reserved - @Quantity, UpdatedAt = <Stamp> WHERE ProductId = @ProductId AND Reserved >= @Quantity;`
  where `<Stamp>` is PR-2's monotonic expression, never a bare clock read.
  A zero-row line throws `InvalidOperationException` naming the product: a
  reserved count below what this reservation holds is a ledger fault, not a
  business outcome, and the transaction rolls back.

- [ ] **Step 1: Write the failing ledger test**

```csharp
[Fact]
public async Task Fulfilling_moves_reserved_down_and_available_not_at_all()
{
    var a = Guid.CreateVersion7();
    await Seed(a, 3, reserved: 2);

    await InTransaction(async l =>
    {
        await l.FulfilAsync([new(new ProductId(a), 2)], TestContext.Current.CancellationToken);
        return 0;
    });

    (await Available(a)).ShouldBe(3);
    (await fixture.ScalarAsync<int>("SELECT Value = Reserved FROM inventory.StockItems WHERE ProductId = {0}", a))
        .ShouldBe(0);
}

[Fact]
public async Task Fulfilling_more_than_is_reserved_is_a_fault()
{
    var a = Guid.CreateVersion7();
    await Seed(a, 3, reserved: 1);

    await Should.ThrowAsync<InvalidOperationException>(() => InTransaction(async l =>
    {
        await l.FulfilAsync([new(new ProductId(a), 2)], TestContext.Current.CancellationToken);
        return 0;
    }));
}
```

- [ ] **Step 2: Run to see them fail; write the statement**

```csharp
// The same Stamp expression as the reserve and give-back statements: a
// fulfilment publishes no level, but it moves UpdatedAt, and a stamp that
// went backwards here would let the next stock-take carry an OccurredAt
// behind Catalog's watermark (§7.3's exception, spec section 4).
private static readonly string FulfilSql =
    $"""
    UPDATE inventory.StockItems
    SET Reserved = Reserved - @Quantity, UpdatedAt = {Stamp}
    WHERE ProductId = @ProductId
        AND Reserved >= @Quantity;
    """;

public async Task FulfilAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct)
{
    (DbConnection connection, DbTransaction transaction) = Current();

    foreach (ReservationLine line in lines.OrderBy(l => l.ProductId.Value))
    {
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            FulfilSql,
            new { ProductId = line.ProductId.Value, line.Quantity },
            transaction: transaction,
            cancellationToken: ct));

        // Reserved below what this row holds is the ledger disagreeing with
        // itself; retrying will not fix it and acking would hide it.
        if (affected == 0)
            throw new InvalidOperationException($"Product {line.ProductId} has fewer reserved than this reservation holds.");
    }
}
```

- [ ] **Step 3: Configuration and migration**

In `ReservationConfiguration.Configure`:

```csharp
builder.Property(r => r.DespatchedUnreservedAt);
builder.Property<bool>("UnreservedCounted").HasDefaultValue(false);
```

```bash
dotnet ef migrations add AddDespatchTracking \
    --project src/Services/Inventory/Inventory.Infrastructure \
    --startup-project src/Services/Inventory/Inventory.Migrator \
    --output-dir Persistence/Migrations
```

Expected: two `AddColumn` operations on `inventory.Reservations` and nothing
else.

- [ ] **Step 4: Run the ledger tests and the smoke tests; commit**

```bash
dotnet test tests/Inventory.Api.Tests --filter "StockLedgerTests|DatabaseSmokeTests"
git add src/Services/Inventory tests/Inventory.Api.Tests
git commit -m "feat(inventory): the ledger's fulfil statement and the despatch columns"
```

---

### Task 3: `FulfilReservationCommand` and the two event handlers

**Files:**
- Create: `src/Services/Inventory/Inventory.Application/Reservations/Fulfil/FulfilReservationCommand.cs`
- Create: `.../Fulfil/FulfilReservationHandler.cs`
- Create: `src/Services/Inventory/Inventory.Application/Reservations/Integration/OrderCancelledHandler.cs`
- Create: `.../Integration/ShipmentDispatchedHandler.cs`
- Test: `tests/Inventory.Application.Tests/IntegrationHandlerTests.cs`

**Interfaces:**
- `record FulfilReservationCommand(Guid OrderId) : ICommand<Result>`. Always
  `Result.Success()` except an unknown order, which is `Success` with a
  warning log — section 5's table acks it.
- `OrderCancelledHandler : IIntegrationEventHandler<OrderCancelled>` →
  `ReleaseStockCommand(e.OrderId, CommandOrigin.System)`.
- `ShipmentDispatchedHandler : IIntegrationEventHandler<ShipmentDispatched>` →
  `FulfilReservationCommand(e.OrderId)`.

- [ ] **Step 1: Write the failing handler tests**

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Shipping.V1;
using Inventory.Application.Reservations.Fulfil;
using Inventory.Application.Reservations.Integration;
using Inventory.Application.Reservations.ReleaseStock;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

public class IntegrationHandlerTests
{
    [Fact]
    public async Task A_cancellation_dispatches_a_system_release()
    {
        IDispatcher dispatcher = Substitute.For<IDispatcher>();
        var order = Guid.CreateVersion7();

        await new OrderCancelledHandler(dispatcher).HandleAsync(
            new OrderCancelled
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = order,
                OccurredAt = DateTimeOffset.UtcNow,
                OrderId = order,
                CustomerId = Guid.CreateVersion7(),
                Reason = CancelReasons.CustomerRequest
            },
            CancellationToken.None);

        await dispatcher.Received(1).SendAsync(
            Arg.Is<ReleaseStockCommand>(c => c.OrderId == order && c.Origin == CommandOrigin.System),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_despatch_dispatches_a_fulfilment()
    {
        IDispatcher dispatcher = Substitute.For<IDispatcher>();
        var order = Guid.CreateVersion7();

        await new ShipmentDispatchedHandler(dispatcher).HandleAsync(
            new ShipmentDispatched
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = order,
                OccurredAt = DateTimeOffset.UtcNow,
                OrderId = order,
                TrackingNumber = "TRACK-1"
            },
            CancellationToken.None);

        await dispatcher.Received(1).SendAsync(
            Arg.Is<FulfilReservationCommand>(c => c.OrderId == order), Arg.Any<CancellationToken>());
    }
}
```

Check `ShipmentDispatched`'s members in
`Common.Contracts/Shipping/V1/ShipmentEvents.cs` before writing the sample;
the test must construct the contract as it is. NSubstitute is already a
test dependency in `Directory.Packages.props` if Ordering's application
tests use it; if not, use a hand-written `IDispatcher` fake that records the
command, as those tests do.

- [ ] **Step 2: Run to see them fail; write the handlers**

```csharp
using Common.Application;

namespace Inventory.Application.Reservations.Fulfil;

public sealed record FulfilReservationCommand(Guid OrderId) : ICommand<Result>;
```

```csharp
using Common.Application;
using Inventory.Domain.Reservations;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Reservations.Fulfil;

/// <summary>§3.2's derivation for ShipmentDispatched: the stock has left.</summary>
public sealed class FulfilReservationHandler(
    IReservationRepository reservations,
    IStockLedger ledger,
    TimeProvider clock,
    ILogger<FulfilReservationHandler> log)
    : ICommandHandler<FulfilReservationCommand, Result>
{
    private static readonly Action<ILogger, Guid, string, Exception?> NothingHeld =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(1, nameof(NothingHeld)),
            "ShipmentDispatched for order {OrderId} met no held reservation: {State}.");

    private static readonly Action<ILogger, Guid, Exception?> Unreserved =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(2, nameof(Unreserved)),
            "ShipmentDispatched for order {OrderId} met a released reservation: the level is now wrong by its lines (ADR-029).");

    public async Task<Result> HandleAsync(FulfilReservationCommand command, CancellationToken ct)
    {
        var order = new OrderId(command.OrderId);
        DateTimeOffset now = clock.GetUtcNow();
        Reservation? reservation = await reservations.GetForUpdateAsync(order, ct);

        switch (reservation?.Status)
        {
            case null:
                NothingHeld(log, command.OrderId, "no reservation", null);
                return Result.Success();

            case ReservationStatus.Failed:
                NothingHeld(log, command.OrderId, "the reserve failed", null);
                return Result.Success();

            case ReservationStatus.Released when reservation.Lines.Count == 0:
                NothingHeld(log, command.OrderId, "a tombstone", null);
                return Result.Success();

            case ReservationStatus.Released:
                Unreserved(log, command.OrderId, null);
                reservation.RecordDespatchUnreserved(now);
                return Result.Success();

            case ReservationStatus.Fulfilled:
                return Result.Success();

            case ReservationStatus.Reserved:
                await ledger.FulfilAsync(reservation.Lines, ct);
                reservation.Fulfil(now);
                return Result.Success();

            default:
                throw new InvalidOperationException($"No fulfilment path for {reservation.Status}.");
        }
    }
}
```

`LoggerMessage.Define` rather than `log.LogWarning`, because CA1848 under
ADR-019 is an error.

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;
using Inventory.Application.Reservations.ReleaseStock;

namespace Inventory.Application.Reservations.Integration;

/// <summary>§3.2's derivation, ADR-029's decision: a cancellation releases directly.</summary>
public sealed class OrderCancelledHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct)
    {
        await dispatcher.SendAsync(new ReleaseStockCommand(integrationEvent.OrderId, CommandOrigin.System), ct);
    }
}
```

```csharp
using Common.Application;
using Common.Contracts.Shipping.V1;
using Inventory.Application.Reservations.Fulfil;

namespace Inventory.Application.Reservations.Integration;

public sealed class ShipmentDispatchedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<ShipmentDispatched>
{
    public async Task HandleAsync(ShipmentDispatched integrationEvent, CancellationToken ct)
    {
        await dispatcher.SendAsync(new FulfilReservationCommand(integrationEvent.OrderId), ct);
    }
}
```

- [ ] **Step 3: Run the application tests; commit**

```bash
dotnet test tests/Inventory.Application.Tests
git add src/Services/Inventory/Inventory.Application tests/Inventory.Application.Tests
git commit -m "feat(inventory): fulfil on despatch, release on cancellation"
```

---

### Task 4: The `inventory-events` queue

**Files:**
- Modify: `Inventory.Infrastructure/Messaging/DependencyInjection.cs`
- Modify: `tests/Inventory.TestSupport/ServiceFixture.cs` (the harness-only
  broker widening, below)
- Test: `tests/Inventory.Api.Tests/MessagingRegistrationTests.cs` (extend)
- Test: `tests/Inventory.Api.Tests/InventoryEventEndpointTests.cs`

**Interfaces:**
- `public const string EventsQueue = "inventory-events"`.

- [ ] **Step 1: Write the failing tests**

Registration:

```csharp
[Fact]
public void Every_event_in_the_consumes_column_is_registered()
{
    ServiceCollection services = new();

    services.AddMassTransitMessaging(Configuration());

    foreach (Type consumer in new[]
             {
                 typeof(IntegrationEventConsumer<OrderCancelled>),
                 typeof(IntegrationEventConsumer<ShipmentDispatched>)
             })
    {
        services.ShouldContain(
            d => d.ImplementationType == consumer || d.ServiceType == consumer,
            $"{consumer.Name} is in §3.2's Consumes column and has no AddConsumer");
    }
}
```

The test file's usings gain `Common.Infrastructure.Messaging` for
`IntegrationEventConsumer<>`, `Common.Contracts.Ordering.V1` and
`Common.Contracts.Shipping.V1` for the two events; nothing imports them
globally.

Endpoint, over containers, in the shape of PR-2's command tests with a
`PublishAsync<T>` helper that publishes through `IPublishEndpoint` and waits
for the inbox row on `EventsQueue`. The helper sets both transport headers
from the contract, as Ordering's `CatalogEventEndpointTests.PublishAsync`
does:

```csharp
await publisher.Publish(
    message,
    c =>
    {
        c.MessageId = message.MessageId;
        c.CorrelationId = message.CorrelationId;
    },
    ct);
```

§9.5's inbox keys on `ConsumeContext.MessageId`, not on the body's property,
so a helper that leaves the transport id to MassTransit polls a row that
never appears and cannot prove duplicate suppression; and §9.1 makes the
body, the row and the transport carry one correlation, so a test that pins
only the first exercises a split identity no producer emits.

```csharp
[Fact]
public async Task A_cancellation_releases_the_reservation_and_publishes_StockReleased()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");

    await PublishAsync(OrderCancelledFor(order));

    await EventuallyStatus(order, "Released");
    (await Available(product)).ShouldBe(3);
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)).ShouldBe(1);
}

[Fact]
public async Task A_cancellation_for_an_unknown_order_writes_the_tombstone_and_still_publishes()
{
    var order = Guid.CreateVersion7();

    await PublishAsync(OrderCancelledFor(order));

    await EventuallyStatus(order, "Released");
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)).ShouldBe(1);
}

[Fact]
public async Task A_despatch_fulfils_the_reservation_moves_reserved_and_publishes_nothing()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");
    int staged = (await fixture.OutboxAsync()).Count;

    await PublishAsync(ShipmentDispatchedFor(order));

    await EventuallyStatus(order, "Fulfilled");
    (await Available(product)).ShouldBe(1);
    (await fixture.ScalarAsync<int>("SELECT Value = Reserved FROM inventory.StockItems WHERE ProductId = {0}", product)).ShouldBe(0);
    (await fixture.OutboxAsync()).Count.ShouldBe(staged, "no §3.2 event describes despatch");
}

[Fact]
public async Task A_release_after_despatch_publishes_the_postcondition_and_returns_nothing()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");
    await PublishAsync(ShipmentDispatchedFor(order));
    await EventuallyStatus(order, "Fulfilled");

    await PublishAsync(OrderCancelledFor(order));

    await Eventually(
        async () => (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)),
        expected: 1,
        because: "ADR-024's first guarantee holds after despatch too");
    (await StatusAsync(order)).ShouldBe("Fulfilled");
    (await Available(product)).ShouldBe(1, "the stock left; nothing comes back");
}

[Fact]
public async Task A_despatch_against_a_released_reservation_moves_nothing_and_is_recorded()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");
    await PublishAsync(OrderCancelledFor(order));
    await EventuallyStatus(order, "Released");

    await PublishAsync(ShipmentDispatchedFor(order));

    await Eventually(
        () => fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0} AND DespatchedUnreservedAt IS NOT NULL", order),
        expected: 1,
        because: "the open case is recorded as state, not logged and forgotten");
    (await Available(product)).ShouldBe(3, "ADR-029's gap is left open, visibly");
    (await fixture.OutboxAsync()).ShouldContain(
        r => r.MessageType.Contains("DespatchedUnreservedDomainEvent", StringComparison.Ordinal) && r.Lane == OutboxLane.Local);

    HttpResponseMessage reinstate = await Admin().PostAsync(
        $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken);
    reinstate.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "the parcel has gone; there is nothing to reinstate");
    (await Available(product)).ShouldBe(3, "and no stock was re-taken for a shipped order");
}
```

`Admin()` is the helper PR-2's `ReservationEndpointsTests` defines; copy it
into this file rather than sharing it across test classes.

The race the rowversion settles, as a test rather than a sentence:

```csharp
[Fact]
public async Task A_cancellation_and_a_despatch_for_one_order_arriving_together_end_in_exactly_one_state()
{
    var product = Guid.CreateVersion7();
    await SeedStock(product, 3);
    var order = Guid.CreateVersion7();
    await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
    await EventuallyStatus(order, "Reserved");

    await Task.WhenAll(
        PublishAsync(OrderCancelledFor(order), drain: false),
        PublishAsync(ShipmentDispatchedFor(order), drain: false));

    await Eventually(
        () => fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM inventory.InboxMessages WHERE Endpoint = {0}", MessagingRegistration.EventsQueue),
        expected: 2,
        because: "both deliveries are consumed, the loser after a retry against the winner's row");
    string status = await StatusAsync(order);
    int available = await Available(product);
    int reserved = await fixture.ScalarAsync<int>("SELECT Value = Reserved FROM inventory.StockItems WHERE ProductId = {0}", product);
    (status, available, reserved).ShouldBeOneOf(
        ("Released", 3, 0),
        ("Fulfilled", 1, 0));
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal))
        .ShouldBe(1, "whichever won, the cancellation published the postcondition exactly once");
}
```

Both orderings are legitimate outcomes and the assertion admits exactly
those two; what it refuses is the third — a row left `Reserved`, a counter
moved twice, or `Available` at any other value — which is what a missing
concurrency token or a swallowed `DbUpdateConcurrencyException` would
produce. `drain: false` publishes without waiting for the inbox row, so
the two deliveries genuinely overlap; the `Eventually` on two inbox rows is
the wait.

`OrderCancelledFor(order)` and `ShipmentDispatchedFor(order)` build the
contracts with fresh `MessageId`s; take their member lists from
`Common.Contracts`.

- [ ] **Step 2: Run to see them fail; write the endpoint**

In `AddMassTransit`:

```csharp
public const string EventsQueue = "inventory-events";

x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();
x.AddConsumer<IntegrationEventConsumer<ShipmentDispatched>>();
```

In `UsingRabbitMq`, beside the command endpoint:

```csharp
// One queue for both events: each dispatches a command whose rejections
// are acked, so they share one retry vocabulary (spec §8).
cfg.ReceiveEndpoint(
    EventsQueue,
    e =>
    {
        e.UseMessageRetry(RetryPolicy.Standard);
        e.UseConsumeFilter(typeof(InboxFilter<>), context);
        e.UseInMemoryOutbox(context);

        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDispatched>>(context);
    });
```

Match `RetryPolicy.Standard`'s signature to Ordering's usage on its event
endpoints (`e.UseMessageRetry(r => RetryPolicy.Standard(r))` if it takes the
configurator).

**The broker grant PR-2 widened already covers this queue and these
reads.** `inventory-` admits `inventory-events` for configure, write and
read, and `Common\.Contracts` on the read pattern admits every context's
contract exchange, which is what binding `OrderCancelled` and
`ShipmentDispatched` needs. Confirm rather than assume, suite first so the
confirmation comes from a gate that is itself verified:

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

**The test fixture is a different matter.** The tests below publish
`OrderCancelled` and `ShipmentDispatched` through the harness as
`inventory-svc`, and `write` on another context's exchange is exactly what the
production grant refuses. Ordering's `ServiceFixture` solves this with
`WidenWriteForTheHarnessAsync`, a `rabbitmqctl set_permissions` against the test
container alone after it starts; `Inventory.TestSupport/ServiceFixture.cs` gains
the same method with `inventory-svc` and a scope of
`^(inventory-|Common\.Contracts|Inventory\.Infrastructure\.Messaging:|MassTransit:)`,
called from `InitializeAsync` before the factory is built. The production file
in `deploy/compose/rabbitmq/` does not move.

- [ ] **Step 3: Run the API suite; commit**

```bash
dotnet test tests/Inventory.Api.Tests
git add src/Services/Inventory/Inventory.Infrastructure tests/Inventory.Api.Tests tests/Inventory.TestSupport
git commit -m "feat(inventory): the inventory-events endpoint"
```

---

### Task 5: The counter, as a claim against the row

**Files:**
- Create: `src/Services/Inventory/Inventory.Application/Reservations/InventoryMetrics.cs`
- Create: `src/Services/Inventory/Inventory.Infrastructure/Projections/UnreservedDespatchProjection.cs`
- Create: `src/Services/Inventory/Inventory.Infrastructure/Observability/OutboxMetrics.cs`,
  `OutboxStats.cs`, `IOutboxStats.cs`, `MetricsInitialiser.cs` — Ordering's
  four files under Inventory's namespace, the meter renamed `Inventory.Outbox`
- Modify: `Inventory.Application/DependencyInjection.cs` (`AddSingleton<InventoryMetrics>()`)
- Modify: `Inventory.Infrastructure/DependencyInjection.cs` (the outbox
  stats, metrics and initialiser registrations, as Ordering's)
- Modify: `Inventory.Infrastructure/Inventory.Infrastructure.csproj` —
  `<PackageReference Include="Microsoft.Extensions.Caching.Memory" />`,
  which the copied `OutboxStats` needs and Ordering's project declares
  directly on the same terms
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` (two
  `AddMeter` lines)
- Modify: `tests/Common.Web.Tests/ObservabilityTests.cs` (the two names,
  and its comment "guards seven strings against a list of seven strings"
  loses both numbers: "guards the strings above against the list below")
- Modify: `deploy/observability/check.py` (the `"Inventory"` exemption is
  deleted)
- Test: `tests/Inventory.Api.Tests/UnreservedDespatchProjectionTests.cs`
- Test: `tests/Inventory.Application.Tests/OutboxSerialisationTests.cs`
  (extend) — a registered `IProjectionHandler` makes
  `DespatchedUnreservedDomainEvent` a Local-lane stageable type, so
  `DomainEventSamples` gains its sample and the exact stageable-set
  assertion grows to five; write that first and see it fail
- Test: `tests/Inventory.Api.Tests/OutboxStatsTests.cs` and
  `MetricsRegistrationTests.cs` — Ordering's, with the namespace and meter
  name changed

**Interfaces:**
- `InventoryMetrics(IMeterFactory)` with meter `Inventory.Reservations` and
  `Counter<long> inventory.fulfilment.unreserved` (`unit: "{reservation}"`);
  `void UnreservedDespatch()`.
- `UnreservedDespatchProjection : IProjectionHandler<DespatchedUnreservedDomainEvent>`:
  one statement, `UPDATE inventory.Reservations SET UnreservedCounted = 1
  WHERE OrderId = @OrderId AND DespatchedUnreservedAt IS NOT NULL AND
  UnreservedCounted = 0;` then `metrics.UnreservedDespatch()` only when one
  row was affected.

- [ ] **Step 1: Write the failing test**

The Local lane runs from the outbox dispatcher, which the test fixture runs
by hand through `fixture.ProcessOutboxBatchAsync()`; the counter is read
through a `MeterListener` on `Inventory.Reservations`.

```csharp
[Fact]
public async Task Two_deliveries_of_one_unreserved_despatch_count_once()
{
    // Arrange: a Released reservation with lines, and the Local row staged twice.
    var order = Guid.CreateVersion7();
    await fixture.ExecuteAsync(
        "INSERT INTO inventory.Reservations (OrderId, Status, UnavailableProductIds, CreatedAt, UpdatedAt, DespatchedUnreservedAt, UnreservedCounted) " +
        "VALUES ({0}, 'Released', '[]', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 0)", order);
    var raised = new DespatchedUnreservedDomainEvent(new OrderId(order), DateTimeOffset.UtcNow);
    await fixture.StageOutboxAsync(
        OutboxMessage.Stage(raised, OutboxLane.Local, order, fixture.MessageTypes, fixture.OutboxJson),
        OutboxMessage.Stage(raised, OutboxLane.Local, order, fixture.MessageTypes, fixture.OutboxJson));
    long observed = 0;
    using var listener = new MeterListener
    {
        InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Inventory.Reservations" && instrument.Name == "inventory.fulfilment.unreserved")
                l.EnableMeasurementEvents(instrument);
        }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
    listener.Start();

    await fixture.ProcessOutboxBatchAsync();

    Interlocked.Read(ref observed).ShouldBe(1, "§13.3: a counter is a claim against the row, fired once per fact");
    (await fixture.ScalarAsync<int>("SELECT Value = UnreservedCounted FROM inventory.Reservations WHERE OrderId = {0}", order))
        .ShouldBe(1);
}
```

The file's usings: `System.Diagnostics.Metrics` for the listener,
`Common.Infrastructure.Outbox` for `OutboxMessage` and `OutboxLane`,
`Inventory.Domain.Reservations` and `Inventory.Domain.Reservations.Events`
for the event, and `Inventory.TestSupport` for the fixture. If
`OutboxMessage.Stage`'s signature differs from PR-15's
(`Stage(object, OutboxLane, Guid correlation, MessageTypeMap, OutboxJson)`),
use the one `Ordering.TestSupport.Outbox.OutboxRows` uses.

- [ ] **Step 2: Run to see it fail; write the metrics and projection**

```csharp
using System.Diagnostics.Metrics;

namespace Inventory.Application.Reservations;

/// <summary>
/// The one business-shaped counter (spec §13). Fired by the projection that
/// claims the row, never by the handler that writes it.
/// </summary>
public sealed class InventoryMetrics
{
    private readonly Counter<long> _unreserved;

    public InventoryMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Inventory.Reservations");
        _unreserved = meter.CreateCounter<long>(
            "inventory.fulfilment.unreserved",
            unit: "{reservation}",
            description: "Despatches that met a reservation already released (ADR-029's open case).");
    }

    public void UnreservedDespatch() => _unreserved.Add(1);
}
```

```csharp
using Common.Application;
using Dapper;
using Inventory.Application.Reservations;
using Inventory.Domain.Reservations.Events;
using System.Data;

namespace Inventory.Infrastructure.Projections;

/// <summary>§13.3's claim: flip and read in one statement, count only when it flipped.</summary>
public sealed class UnreservedDespatchProjection(IDbConnectionFactory connections, InventoryMetrics metrics)
    : IProjectionHandler<DespatchedUnreservedDomainEvent>
{
    private const string ClaimSql =
        """
        UPDATE inventory.Reservations
        SET UnreservedCounted = 1
        WHERE OrderId = @OrderId
            AND DespatchedUnreservedAt IS NOT NULL
            AND UnreservedCounted = 0;
        """;

    public async Task HandleAsync(DespatchedUnreservedDomainEvent domainEvent, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            ClaimSql, new { OrderId = domainEvent.OrderId.Value }, cancellationToken: ct));

        if (affected == 1)
            metrics.UnreservedDespatch();
    }
}
```

Register `services.AddSingleton<InventoryMetrics>();` in
`AddInventoryApplication` beside `RequestMetrics`. The projection is found
by Infrastructure's `AddPluggableFrom` scan through `IProjectionHandler<>`;
because a handler now exists for the event, `DomainEventDispatcher` stages
the Local row, and `MessageTypeMapValidator` needs the event type in its
source — confirm the Domain assembly is in `MessageTypeSource` as Ordering's
is, or the validator stops the host at startup naming the type.

**The instrument has to exist before the first claim, and the outbox gauges have
to exist at all.** The scaffold carries no `MetricsInitialiser` and no
`OutboxMetrics`: both are Ordering's, in
`Ordering.Infrastructure/Observability`, and §13.6 requires every metrics type
forced at startup so a quiet service reports zero rather than nothing. So this
task takes Ordering's `OutboxMetrics.cs`, `OutboxStats.cs`, `IOutboxStats.cs`
and `MetricsInitialiser.cs` into `Inventory.Infrastructure/Observability` with
the namespace changed, the meter renamed `Inventory.Outbox`, and
`InventoryMetrics` added to the initialiser's constructor so the counter is
created on startup; registers them in `AddInventoryInfrastructure` exactly as
Ordering's `DependencyInjection.cs` does, including `IOutboxStats` over a
`SqlConnectionFactory` on the `Inventory` connection string with
`OutboxStats.ConnectTimeoutSeconds`; and deletes `"Inventory"` from
`OUTBOX_METRICS_EXEMPT` in `deploy/observability/check.py`, which now fails if
the entry stays, because an exemption for an instrumented service is a stale
excuse. Run `py -3.12 deploy/observability/check.py` and expect it to pass with
Inventory instrumented. The four outbox alerts group by service name and read
the gauges from then on.

**The meter is exported only if §13.2's registration names it.**
`Common.Web/ObservabilityExtensions.cs` lists every meter the OTLP exporter
collects — `Ordering.Orders`, `Ordering.Outbox`, the shared `Commerce.*`
names — and a meter absent from that list is one the `MeterListener` test
above sees while production collects nothing. So this task adds
`.AddMeter("Inventory.Reservations")` and `.AddMeter("Inventory.Outbox")`
beside Ordering's two, with the same one-line comments citing §13.3 and
§13.6, and adds both names to the list
`tests/Common.Web.Tests/ObservabilityTests.cs` asserts against — write the
test's lines first and see them fail. That is the Class B half of this PR.

- [ ] **Step 3: Run the API suite; commit**

```bash
dotnet test tests/Inventory.Api.Tests
dotnet test tests/Common.Web.Tests
py -3.12 deploy/observability/check.py
git add src/Services/Inventory tests/Inventory.Api.Tests tests/Inventory.Application.Tests src/BuildingBlocks/Common.Web tests/Common.Web.Tests deploy/observability/check.py
git commit -m "feat(inventory): outbox gauges, the metrics initialiser, and one claimed counter"
```

---

### Task 6: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green.
- [ ] Neither `/validate-blueprint` nor `/check-links` is owed: no chapter moved.
- [ ] PR body: `| Class | A+B+D+E |`, touch set from Global Constraints.

## Self-review

- Spec coverage: section 5's despatch table → Tasks 1, 3, 4 (every row has a
  test); section 8's `inventory-events` and registration test → Task 4;
  section 13's counter and claim → Task 5; section 7's rowversion race
  (release against fulfil) → Task 4's last test, which is mandatory.
- `RetryPolicy.Standard` is PR-2's `Inventory.Infrastructure/Messaging/RetryPolicy.cs`,
  which this plan depends on having merged.
- Types: `Fulfil`, `RecordDespatchUnreserved`, `DespatchedUnreservedAt`,
  `DespatchedUnreservedDomainEvent`, `FulfilAsync`,
  `FulfilReservationCommand`, `EventsQueue`, `InventoryMetrics`,
  `UnreservedDespatchProjection` agree across tasks.
