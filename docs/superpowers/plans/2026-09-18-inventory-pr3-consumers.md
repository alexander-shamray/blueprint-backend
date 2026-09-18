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
- **Class A.** Touch set: `src/Services/Inventory/**`, `tests/Inventory.*`.
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
- Test: `tests/Inventory.Domain.Tests/ReservationTests.cs` (extend)

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

- [ ] **Step 4: Run the domain tests; commit**

```bash
dotnet test tests/Inventory.Domain.Tests
git add src/Services/Inventory/Inventory.Domain tests/Inventory.Domain.Tests
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
  `UPDATE inventory.StockItems SET Reserved = Reserved - @Quantity, UpdatedAt = SYSDATETIMEOFFSET() WHERE ProductId = @ProductId AND Reserved >= @Quantity;`
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
private const string FulfilSql =
    """
    UPDATE inventory.StockItems
    SET Reserved = Reserved - @Quantity, UpdatedAt = SYSDATETIMEOFFSET()
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
    private static readonly Action<ILogger, Guid, Exception?> NoReservation =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(1, nameof(NoReservation)),
            "ShipmentDispatched for order {OrderId} met no reservation.");

    private static readonly Action<ILogger, Guid, Exception?> Unreserved =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(2, nameof(Unreserved)),
            "ShipmentDispatched for order {OrderId} met a released reservation: the level is now wrong by its lines (ADR-029).");

    public async Task<Result> HandleAsync(FulfilReservationCommand command, CancellationToken ct)
    {
        var order = new OrderId(command.OrderId);
        DateTimeOffset now = clock.GetUtcNow();
        Reservation? reservation = await reservations.GetAsync(order, ct);

        switch (reservation?.Status)
        {
            case null:
            case ReservationStatus.Failed:
                NoReservation(log, command.OrderId, null);
                return Result.Success();

            case ReservationStatus.Released when reservation.Lines.Count == 0:
                NoReservation(log, command.OrderId, null);
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

Endpoint, over containers, in the shape of PR-2's command tests with a
`PublishAsync<T>` helper that publishes through `IPublishEndpoint` and waits
for the inbox row on `EventsQueue`:

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
}
```

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

- [ ] **Step 3: Run the API suite; commit**

```bash
dotnet test tests/Inventory.Api.Tests
git add src/Services/Inventory/Inventory.Infrastructure tests/Inventory.Api.Tests
git commit -m "feat(inventory): the inventory-events endpoint"
```

---

### Task 5: The counter, as a claim against the row

**Files:**
- Create: `src/Services/Inventory/Inventory.Application/Reservations/InventoryMetrics.cs`
- Create: `src/Services/Inventory/Inventory.Infrastructure/Projections/UnreservedDespatchProjection.cs`
- Modify: `Inventory.Application/DependencyInjection.cs` (`AddSingleton<InventoryMetrics>()`)
- Test: `tests/Inventory.Api.Tests/UnreservedDespatchProjectionTests.cs`

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

If `OutboxMessage.Stage`'s signature differs from PR-15's
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

Also add the projection to `MetricsInitialiser`'s registration if the
scaffold carries one that pre-touches meters, so the instrument exists
before the first claim.

- [ ] **Step 3: Run the API suite; commit**

```bash
dotnet test tests/Inventory.Api.Tests
git add src/Services/Inventory tests/Inventory.Api.Tests
git commit -m "feat(inventory): count an unreserved despatch once, by claiming the row"
```

---

### Task 6: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green.
- [ ] Neither `/validate-blueprint` nor `/check-links` is owed: no chapter moved.
- [ ] PR body: `| Class | A |`, touch set `src/Services/Inventory/**`, `tests/Inventory.*`.

## Self-review

- Spec coverage: section 5's despatch table → Tasks 1, 3, 4 (every row has a
  test); section 8's `inventory-events` and registration test → Task 4;
  section 13's counter and claim → Task 5; section 9's rowversion race
  (release vs fulfil) is exercised implicitly by the two-endpoint tests and
  explicitly nowhere — add a test in Task 4 publishing `OrderCancelled` and
  `ShipmentDispatched` for one order concurrently and asserting the row ends
  in exactly one of `Released` or `Fulfilled` with `Available` consistent
  with it, if the run's flakiness budget allows; otherwise note it in the PR
  body as covered by the rowversion and MassTransit's retry.
- Types: `Fulfil`, `RecordDespatchUnreserved`, `DespatchedUnreservedAt`,
  `DespatchedUnreservedDomainEvent`, `FulfilAsync`,
  `FulfilReservationCommand`, `EventsQueue`, `InventoryMetrics`,
  `UnreservedDespatchProjection` agree across tasks.
