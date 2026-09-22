# Shipping PR-5 — consume OrderConfirmed and OrderCancelled, and book the shipment — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the service do its first work. `shipping-events` binds both of
Ordering's events, each consumer writes one row and nothing else, and a
fulfilment worker claims those rows under a lease, reads the delivery address
from Ordering over ADR-052's gRPC method, keeps it in `DeliveryAddresses`,
books the shipment with the carrier, and services a cancellation the carrier
still has to be told about. §3.2's Consumes cell gains `OrderCancelled` and
§2.2's diagram gains the two edges the design draws.

**Architecture:** the two outbound calls sit in **one `BackgroundService` and
in neither consumer** (spec, section 4). A consumer writes a `Shipments` row —
a new shipment, or a `Voided` tombstone when the cancellation arrives first —
and the two writes commute, so the queue needs no delayed redelivery. The
worker is `OutboxDispatcher`'s shape one table over: an atomic claim under
`UPDLOCK, READPAST, ROWLOCK` that stamps `LockedUntil`, a per-row backoff of
`2^min(Attempts, 8) × 5 s` on the row itself, and a per-pass catch whose
filter asks the token rather than the exception's type. Each call that writes
carries `book:{ShipmentId}` or `cancel:{ShipmentId}`, so a crash between the
carrier's answer and the commit repeats the call and receives the first
answer. The address adapter is the generated gRPC client over Ordering's
method, with `ClientCredentialsHandler` inside its resilience pipeline and
`AddressHop`'s five numbers inside §9.7's bands, because Ordering is a peer
and not a third party.

**Tech Stack:** MassTransit over RabbitMQ, EF Core and Dapper over SQL Server,
`Grpc.Net.ClientFactory` with `Microsoft.Extensions.Http.Resilience`,
`System.Diagnostics.Metrics`, xUnit with Shouldly, Testcontainers and
WireMock.Net in process, stdlib Python 3.12 for the gates.

**Spec:** `docs/superpowers/specs/2026-09-22-shipping-service-design.md`,
sections 1 (what a cancellation after confirmation does), 3, 4 (where the
outbound calls sit), 5 (the state moves this PR drives), 6 (both
interleavings), 7 (`DeliveryAddresses` and its port), 8 (`shipping-events` and
the tombstone), 9 (the address port and its adapter), 10
(`AddressSource__BaseUrl` and the three `Identity__Client__*` keys), 11
(`shipping.address.refused`), 12 (the Application and Worker suites) and 13
(§3.2's cell and §2.2's diagram).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Shipping/**`, `tests/Shipping.*`,
  `Platform.slnx`, `deploy/compose/**`, `.github/workflows/ci.yml`,
  `.github/secret-scan/allowed/deploy.txt`,
  `docs/backend-architecture/02-architecture-at-a-glance.md`,
  `docs/backend-architecture/03-bounded-contexts.md`,
  `docs/backend-architecture/09-messaging.md`,
  `docs/backend-architecture/15-cicd-deployment.md`, `docs/secrets.md`,
  `docs/runbooks/latency.md`.
  Why each, since the row above is paths only: the service's own code and its
  suites are A; `Platform.slnx` and the `*.csproj` files this PR edits — the
  gRPC client packages in `Shipping.Infrastructure`, the JWT reader, and the
  new stub library — are E; the Compose model, CI's filter, the secret scan's
  allow-list, the four chapters and the two documents are D.
- **The locality gate admits `A+D+E` today** —
  `.github/locality-gate/locality_gate.py` names it as the one three-member
  class and reads it as its three members, while `classes.yml` owns only the
  class-to-path map — so the class row is spelled exactly that way and no gate
  change is owed.
- Depends on **PR-1, PR-2, PR-3a, PR-3b and PR-4 having merged.** From PR-1:
  `Shipment` with `For`/`Book`/`MarkUnfulfillable`/`Cancel`/`CarrierCancelled`/
  `CarrierRefusedCancellation`/`Record`, `ShipmentId`, `OrderId`,
  `ShipmentStatus`, `ShipmentLimits`, `ShippingDbContext.Shipments`, the
  `Shipments` columns `Attempts`/`NextAttemptAt`/`LockedUntil`/`NextPollAt`/
  `RowVersion`, `ServiceFixture` and `ShippingWorkerFactory`, the Compose
  unit and the `shipping-svc` grant. From PR-2: `ICarrierGateway`,
  `BookingRequest`, `BookingResult`, `CancellationRequest`,
  `CancellationResult`, `DeliveryAddress`, `CarrierUnavailableException`,
  `CarrierHop`, `CarrierMetrics`, `SimulatorMappings`. From PR-3b:
  `Common.Infrastructure.Identity`'s `ITokenCache`, `CachingTokenClient`,
  `ClientCredentialsHandler`, `ServiceIdentityOptions` and
  `AuthorityKeyName`. From PR-4: `delivery_addresses.proto` in
  `src/Services/Ordering/Ordering.Api/Protos/`, the `shipping-worker` client
  and `local-dev-shipping-secret` in the realm, and `KeycloakFixture`'s
  `WorkerClient`/`WorkerSecret`. Where an earlier PR spelled one of those
  differently, the spelling moves and nothing else in this plan does.
- **No `Directory.Packages.props` change and no Appendix B row.**
  `Grpc.Net.ClientFactory`, `Google.Protobuf`, `Grpc.Tools`,
  `Grpc.AspNetCore` and `System.IdentityModel.Tokens.Jwt` are all pinned and
  all registered. Each `PackageReference` this PR adds carries no `Version=`.
- **The consumers make no outbound call**, and nothing in this service throws
  into a queue. A superseded arrival is the aggregate's `false`, logged and
  returned.
- **No integration event is published.** `ShippingIntegrationEventMapper`'s
  registry stays empty, because nothing here promotes a shipment past
  `Booked`.
- **The blueprint's vocabulary**: despatch and despatched in prose,
  `Dispatched` in identifiers.
- No log line holds an address, as an attribute or inside an exception's text:
  the log takes the shipment's id and the order's id. `SensitiveKeys` is not
  widened.
- Comments say why and cite the owner — a section, an ADR or a symbol, never a
  pull request or a test — and no comment block runs past ten lines. Explicit
  local types, file-scoped namespaces with a blank line after, braces on two
  statements or more, one space before `=`, `=>` and `{`, 120 columns for code
  and 80 for prose, British spelling.
- `py -3.12`, never `python`. Container tests are
  `[Collection(nameof(IntegrationCollection))]` and never skipped.
- Every step that adds behaviour writes its test first.

---

### Task 1: `shipping-events`, the two consumers and the tombstone

**Files:**
- Create: `src/Services/Shipping/Shipping.Application/Shipments/IShipmentRepository.cs`
- Create: `src/Services/Shipping/Shipping.Application/Orders/RecordOrderConfirmed/OrderConfirmedHandler.cs`
- Create: `.../RecordOrderConfirmed/CreateShipmentCommand.cs`
- Create: `.../RecordOrderConfirmed/CreateShipmentHandler.cs`
- Create: `src/Services/Shipping/Shipping.Application/Orders/RecordOrderCancelled/OrderCancelledHandler.cs`
- Create: `.../RecordOrderCancelled/VoidShipmentCommand.cs`
- Create: `.../RecordOrderCancelled/VoidShipmentHandler.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Persistence/ShipmentRepository.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs` — the repository
- Modify: `src/Services/Shipping/Shipping.Infrastructure/Messaging/DependencyInjection.cs`
- Test: `tests/Shipping.Application.Tests/ShipmentConsumerTests.cs`
- Test: `tests/Shipping.Application.Tests/ShippingIntegrationEventMapperTests.cs`
- Test: `tests/Shipping.Worker.Tests/MessagingRegistrationTests.cs`

**Interfaces:**
- Consumes: `Common.Contracts.Ordering.V1.OrderConfirmed` and `OrderCancelled`
  (`MessageId`, `CorrelationId`, `OccurredAt`, `OrderId`, `CustomerId`, and
  `OrderCancelled`'s `Reason`/`Origin`); `Shipment.For`, `Shipment.Cancel`.
- Produces:

```csharp
namespace Shipping.Application.Shipments;

public interface IShipmentRepository
{
    Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct);
    Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct);
    void Add(Shipment shipment);
}

namespace Shipping.Infrastructure.Messaging;
public static class DependencyInjection { public const string EventsQueue = "shipping-events"; }
```

- [ ] **Step 1: Write the failing Application tests**

`tests/Shipping.Application.Tests/ShipmentConsumerTests.cs` — both consumers
against a fake repository, in both orders (spec, sections 6 and 8):

```csharp
using Common.Contracts.Ordering.V1;
using Shipping.Application.Orders.RecordOrderCancelled;
using Shipping.Application.Orders.RecordOrderConfirmed;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

/// <summary>
/// §3.2's Consumes column. Each consumer writes one row and makes no call
/// (spec, section 4), and the two writes commute: whichever arrives second
/// finds the other's row and reaches the same terminal state (spec, section 6).
/// </summary>
public class ShipmentConsumerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeShipments _shipments = new();

    private CreateShipmentHandler Confirm() => new(_shipments, new FixedClock(Now));

    private VoidShipmentHandler Void() => new(_shipments, new FixedClock(Now.AddMinutes(1)));

    private static Guid Order() => Guid.CreateVersion7();

    [Fact]
    public async Task A_confirmed_order_becomes_a_pending_shipment()
    {
        Guid order = Order();

        await Confirm().HandleAsync(new CreateShipmentCommand(order, Now), TestContext.Current.CancellationToken);

        Shipment shipment = _shipments.Added.ShouldHaveSingleItem();
        shipment.OrderId.ShouldBe(new OrderId(order));
        shipment.Status.ShouldBe(ShipmentStatus.Pending);
        shipment.NextAttemptAt.ShouldBe(Now, "the first pass may claim it at once");
    }

    [Fact]
    public async Task A_redelivered_confirmation_writes_no_second_shipment()
    {
        Guid order = Order();
        await Confirm().HandleAsync(new CreateShipmentCommand(order, Now), TestContext.Current.CancellationToken);

        await Confirm().HandleAsync(new CreateShipmentCommand(order, Now), TestContext.Current.CancellationToken);

        _shipments.Added.Count.ShouldBe(1, "§3.2 gives one shipment per confirmed order");
    }

    [Fact]
    public async Task A_cancellation_after_a_confirmation_voids_the_pending_shipment()
    {
        Guid order = Order();
        await Confirm().HandleAsync(new CreateShipmentCommand(order, Now), TestContext.Current.CancellationToken);

        await Void().HandleAsync(
            new VoidShipmentCommand(order, Now.AddMinutes(1)), TestContext.Current.CancellationToken);

        _shipments.Added.ShouldHaveSingleItem().Status.ShouldBe(ShipmentStatus.Voided);
    }

    [Fact]
    public async Task A_cancellation_before_a_confirmation_writes_a_voided_tombstone()
    {
        Guid order = Order();

        await Void().HandleAsync(new VoidShipmentCommand(order, Now), TestContext.Current.CancellationToken);

        Shipment tombstone = _shipments.Added.ShouldHaveSingleItem();
        tombstone.OrderId.ShouldBe(new OrderId(order));
        tombstone.Status.ShouldBe(ShipmentStatus.Voided, "§9.4 orders nothing, so the late confirmation finds this");
    }

    [Fact]
    public async Task The_late_confirmation_finds_the_tombstone_and_does_nothing()
    {
        Guid order = Order();
        await Void().HandleAsync(new VoidShipmentCommand(order, Now), TestContext.Current.CancellationToken);

        await Confirm().HandleAsync(
            new CreateShipmentCommand(order, Now.AddMinutes(1)), TestContext.Current.CancellationToken);

        _shipments.Added.Count.ShouldBe(1);
        _shipments.Added[0].Status.ShouldBe(
            ShipmentStatus.Voided,
            "the two writes commute: whichever arrives second reaches the same terminal state");
    }

    [Fact]
    public async Task A_second_cancellation_moves_nothing()
    {
        Guid order = Order();
        await Void().HandleAsync(new VoidShipmentCommand(order, Now), TestContext.Current.CancellationToken);

        await Void().HandleAsync(
            new VoidShipmentCommand(order, Now.AddHours(1)), TestContext.Current.CancellationToken);

        _shipments.Added.Count.ShouldBe(1, "a superseded arrival returns rather than throwing");
    }

    [Fact]
    public async Task A_cancellation_of_a_delivered_shipment_moves_nothing_and_does_not_throw()
    {
        Guid order = Order();
        Shipment delivered = Shipment.For(ShipmentId.New(), new OrderId(order), Now);
        delivered.Book("crr_1", "TRK1", Now);
        delivered.Record("e1", TrackingStatus.Delivered, Now, Now);
        _shipments.Seed(delivered);

        await Void().HandleAsync(
            new VoidShipmentCommand(order, Now.AddHours(1)), TestContext.Current.CancellationToken);

        delivered.Status.ShouldBe(ShipmentStatus.Delivered);
        delivered.CancellationRequestedAt.ShouldBeNull();
    }

    [Fact]
    public void Each_handler_maps_the_contract_and_nothing_else()
    {
        // The contract half, separately: a handler that read the wrong member
        // would still satisfy every assertion above, because they all build
        // the command by hand.
        OrderConfirmed confirmed = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = Now,
            OrderId = Order(),
            CustomerId = Guid.CreateVersion7(),
            TotalAmount = 42.10m,
            Currency = "EUR",
            Lines = []
        };

        RecordingDispatcher dispatcher = new();
        new OrderConfirmedHandler(dispatcher).HandleAsync(confirmed, TestContext.Current.CancellationToken);

        dispatcher.Sent.ShouldHaveSingleItem()
            .ShouldBe(new CreateShipmentCommand(confirmed.OrderId, Now));
    }
}
```

`FakeShipments` implements `IShipmentRepository` over a list keyed by
`OrderId`, with `Added` and `Seed`; `FixedClock` is a `TimeProvider` returning
a constant; `RecordingDispatcher` is an `IDispatcher` recording `SendAsync`'s
argument. All three are file-local to this suite, copied rather than shared,
on the rule the Payments suites follow.

The mapper's registry, in
`tests/Shipping.Application.Tests/ShippingIntegrationEventMapperTests.cs`:

```csharp
[Fact]
public void The_registry_is_empty_because_nothing_here_promotes_a_shipment()
{
    // §9.3's allow-list is §3.2's Publishes column, and both contracts are
    // raised by a tracking event that promotes the shipment — which is a
    // carrier's fact and reaches no code in this service yet. An entry added
    // before that would put a domain event on the bus with nothing to raise
    // it (§5.5).
    ShippingIntegrationEventMapper.RegisteredEvents.ShouldBeEmpty();
}
```

with `public static IReadOnlyCollection<Type> RegisteredEvents => Registry.Keys;`
added to the rendered mapper, exactly as Payments' carries it.

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Shipping.Application.Tests
```

Expected: compile failure on `Shipping.Application.Orders`,
`IShipmentRepository` and `RegisteredEvents`.

- [ ] **Step 3: Write the port, the handlers and the commands**

```csharp
using Shipping.Domain.Shipments;

namespace Shipping.Application.Shipments;

/// <summary>
/// §6.3's repository for §3.2's aggregate. Two reads because the two callers
/// hold different keys: a consumer knows the order, a worker's claim returns
/// the shipment.
/// </summary>
public interface IShipmentRepository
{
    /// <summary>The shipment for one order, tracking events included, or null.</summary>
    Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct);

    Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct);

    void Add(Shipment shipment);
}
```

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

/// <summary>
/// §3.2's first subscription. It dispatches rather than writing, so the write
/// runs inside the command pipeline's transaction (§6.3), and it reads no
/// address: <c>OrderConfirmed</c> carries none (ADR-035), and the worker asks
/// Ordering for one (ADR-052).
/// </summary>
public sealed class OrderConfirmedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new CreateShipmentCommand(integrationEvent.OrderId, integrationEvent.OccurredAt),
            ct);
}
```

```csharp
using Common.Application;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

public sealed record CreateShipmentCommand(Guid OrderId, DateTimeOffset ConfirmedAt) : ICommand<Result>;
```

```csharp
using Common.Application;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

/// <summary>
/// Creates the shipment, or finds one and does nothing. The second case is
/// both a redelivery past the inbox and the late half of section 6's first
/// interleaving, where the row already exists as a <c>Voided</c> tombstone —
/// and the two are deliberately indistinguishable here, because the state
/// machine has already decided what each means.
/// </summary>
public sealed class CreateShipmentHandler(IShipmentRepository shipments, TimeProvider clock)
    : ICommandHandler<CreateShipmentCommand, Result>
{
    public async Task<Result> HandleAsync(CreateShipmentCommand command, CancellationToken ct)
    {
        OrderId order = new(command.OrderId);

        if (await shipments.GetByOrderAsync(order, ct) is not null)
            return Result.Success();

        shipments.Add(Shipment.For(ShipmentId.New(), order, clock.GetUtcNow()));

        return Result.Success();
    }
}
```

`OrderCancelledHandler` is the same shape over `OrderCancelled`, dispatching
`VoidShipmentCommand(integrationEvent.OrderId, integrationEvent.OccurredAt)`,
with the summary "Voids a shipment that has not been booked and records the
request against one that has (spec, sections 5 and 6); the carrier is told by
a worker and never from here (spec, section 4)." `VoidShipmentHandler`:

```csharp
using Common.Application;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Orders.RecordOrderCancelled;

public sealed class VoidShipmentHandler(IShipmentRepository shipments, TimeProvider clock)
    : ICommandHandler<VoidShipmentCommand, Result>
{
    public async Task<Result> HandleAsync(VoidShipmentCommand command, CancellationToken ct)
    {
        OrderId order = new(command.OrderId);
        DateTimeOffset now = clock.GetUtcNow();

        Shipment? shipment = await shipments.GetByOrderAsync(order, ct);

        if (shipment is null)
        {
            // The tombstone (spec, section 8). A row rather than nothing,
            // because the late confirmation has to find something: with no row
            // it would create a Pending shipment for a cancelled order and a
            // worker would book it.
            shipment = Shipment.For(ShipmentId.New(), order, now);
            shipment.Cancel(now);
            shipments.Add(shipment);

            return Result.Success();
        }

        // The return says whether it moved, and a false is the superseded
        // arrival section 5 makes a no-op: a second cancellation, or one of a
        // shipment already despatched. The caller is where a line about it
        // would go, and the consumer's own log carries the message id.
        shipment.Cancel(now);

        return Result.Success();
    }
}
```

`ShipmentRepository` in `Shipping.Infrastructure/Persistence/`:

```csharp
using Microsoft.EntityFrameworkCore;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

internal sealed class ShipmentRepository(ShippingDbContext db) : IShipmentRepository
{
    // Include, because every caller that moves the shipment may raise the
    // despatch event off a tracking arrival, and a deduplication over an
    // unloaded collection would store a carrier's page twice (§5.2).
    public Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct) =>
        db.Shipments.Include(s => s.TrackingEvents).SingleOrDefaultAsync(s => s.OrderId == orderId, ct);

    public Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct) =>
        db.Shipments.Include(s => s.TrackingEvents).SingleOrDefaultAsync(s => s.Id == id, ct);

    public void Add(Shipment shipment) => db.Shipments.Add(shipment);
}
```

registered in `AddShippingInfrastructure` beside the other persistence
registrations:

```csharp
        services.AddScoped<IShipmentRepository, ShipmentRepository>();
```

- [ ] **Step 4: Declare the receive endpoint**

In `src/Services/Shipping/Shipping.Infrastructure/Messaging/DependencyInjection.cs`,
which the render left with no consumer, §9.5's printed form:

```csharp
    /// <summary>
    /// §3.2's Consumes column for Shipping. One queue for both events: each
    /// writes one row in this service's own database and makes no call, so
    /// neither can meet a fault that is a wait and one retry vocabulary
    /// covers both (spec, section 8).
    /// </summary>
    public const string EventsQueue = "shipping-events";
```

```csharp
            // §3.2's Consumes column. Registering and binding are two
            // statements and both are needed; a consumer registered and never
            // bound receives nothing.
            x.AddConsumer<IntegrationEventConsumer<OrderConfirmed>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();
```

```csharp
                cfg.ReceiveEndpoint(
                    EventsQueue,
                    e =>
                    {
                        // RetryPolicy.Standard bare, and no UseDelayedRedelivery:
                        // neither consumer can meet a fault that is a wait, and
                        // no mapping exception is possible because neither
                        // message is mapped by an ICommandMessageMapper —
                        // §9.8's exclusions have nothing to exclude here.
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8): the other
                        // nesting commits the inbox row before the buffered
                        // sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderConfirmed>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                    });
```

`RetryPolicy` is a copy of Payments' file under
`Shipping.Infrastructure.Messaging`, unchanged but its namespace: §4.3 permits
exactly one assembly to cross a service boundary and a retry ladder is not it.
No `AddDelayedMessageScheduler` and no `UseDelayedMessageScheduler`: this
service sends itself no scheduled message, and the two halves exist for
§9.6's saga, which is Ordering's. The `ConfigureEndpoints` comment the render
carries stays as it is.

- [ ] **Step 5: Write the registration test**

`tests/Shipping.Worker.Tests/MessagingRegistrationTests.cs` is Payments' file
with the namespace, the connection-string host and the Consumes set changed:
`IntegrationEventConsumer<OrderConfirmed>` and
`IntegrationEventConsumer<OrderCancelled>`, and no Accepts test, because §3.2
gives Shipping no accepted command. Keep the two harness timeouts, the
`IBus`/`IHostedService` pair, the usage-telemetry assertion and the
missing-key theory verbatim; drop the redelivery-ladder test, which is about a
type this service does not have.

The binding half — that each cell also has a `ConfigureConsumer` — is not
provable here and its comment says so, in Payments' words: the harness
replaces the `UsingRabbitMq` callback where the receive endpoint is declared,
so what the harness can see is the registration and not the binding. Task 5
asserts the binding against the broker's own list.

- [ ] **Step 6: Run and commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Application.Tests
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~MessagingRegistrationTests"
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

Expected: green, and both Python commands exit 0 — `shipping-svc`'s three
patterns took `ordering-svc`'s shape under a `shipping-` prefix in PR-1
precisely so that this queue needed no grant change.

```bash
git add src/Services/Shipping tests/Shipping.Application.Tests tests/Shipping.Worker.Tests
git commit -m "feat(shipping): shipping-events binds OrderConfirmed and OrderCancelled"
```

The body argues the two decisions a reviewer would question: that the queue
has no delayed redelivery, and why; and that a cancellation arriving first
writes a `Voided` tombstone rather than nothing, because the alternative is a
late confirmation booking a cancelled order.

---

### Task 2: `DeliveryAddresses`, its port and `AddDeliveryAddresses`

**Files:**
- Create: `src/Services/Shipping/Shipping.Application/Addresses/IDeliveryAddressStore.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Persistence/DeliveryAddressRow.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Persistence/DeliveryAddressRowConfiguration.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Persistence/SqlDeliveryAddressStore.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`
- Create (generated): `Shipping.Infrastructure/Persistence/Migrations/<ts>_AddDeliveryAddresses.cs`
  and its designer, and the rewritten `ShippingDbContextModelSnapshot.cs`
- Test: `tests/Shipping.Worker.Tests/DeliveryAddressStoreTests.cs`
- Modify: `tests/Shipping.Worker.Tests/DatabaseSmokeTests.cs` — the migration count

**Interfaces:**
- Consumes: `DeliveryAddress` from `Shipping.Application.Carrier` (PR-2).
- Produces:

```csharp
namespace Shipping.Application.Addresses;

public interface IDeliveryAddressStore
{
    Task SaveAsync(
        OrderId orderId,
        Guid customerId,
        DeliveryAddress address,
        DateTimeOffset fetchedAt,
        CancellationToken ct);
    Task<DeliveryAddress?> GetAsync(OrderId orderId, CancellationToken ct);
}
```

- Produces: `shipping.DeliveryAddresses(OrderId uniqueidentifier PK, CustomerId
  uniqueidentifier, Line1 nvarchar(200), Line2 nvarchar(200) NULL, City
  nvarchar(100), PostalCode nvarchar(32), Country char(2), FetchedAt
  datetimeoffset(7))`.

- [ ] **Step 1: Write the failing store tests**

`tests/Shipping.Worker.Tests/DeliveryAddressStoreTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The one table in this service that holds personal data (spec, section 7),
/// over a real engine because every claim here is the column's rather than the
/// model's: the width, the collation and the round trip.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class DeliveryAddressStoreTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// A Kazakh-script address (spec, section 7). Every text column is
    /// nvarchar, and the letters below are the ones a Cyrillic code page would
    /// lose: an address stored as question marks reaches the carrier as an
    /// undeliverable parcel and nothing in the platform reports it.
    /// </summary>
    private static readonly DeliveryAddress Kazakh =
        new("Абай даңғылы 1, ә ғ қ ң ө ұ ү һ і", "пәтер 12", "Алматы", "050000", "KZ");

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IDeliveryAddressStore Store(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDeliveryAddressStore>();

    [Fact]
    public async Task A_kazakh_address_round_trips_through_the_table()
    {
        OrderId order = new(Guid.CreateVersion7());

        await SaveAsync(order, Guid.CreateVersion7(), Kazakh);

        (await ReadAsync(order)).ShouldBe(Kazakh);
    }

    [Fact]
    public async Task A_second_read_of_the_same_order_answers_the_first_write()
    {
        OrderId order = new(Guid.CreateVersion7());
        Guid customer = Guid.CreateVersion7();
        await SaveAsync(order, customer, Kazakh);

        // The pass that crashed between the answer and the commit repeats the
        // write (spec, section 4), so the statement has to be an upsert rather
        // than an insert — otherwise the retry fails on the primary key and
        // the shipment never books.
        await SaveAsync(order, customer, Kazakh);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses WHERE OrderId = {0}", order.Value))
            .ShouldBe(1);
    }

    [Fact]
    public async Task An_absent_second_line_reads_back_as_absent_rather_than_blank()
    {
        OrderId order = new(Guid.CreateVersion7());
        DeliveryAddress single = Kazakh with { Line2 = null };

        await SaveAsync(order, Guid.CreateVersion7(), single);

        (await ReadAsync(order))!.Line2.ShouldBeNull();
    }

    [Fact]
    public async Task An_order_with_no_row_reads_as_null()
    {
        (await ReadAsync(new OrderId(Guid.CreateVersion7()))).ShouldBeNull();
    }

    [Fact]
    public async Task The_customer_is_stored_beside_the_address_and_erasure_deletes_by_it()
    {
        OrderId order = new(Guid.CreateVersion7());
        Guid customer = Guid.CreateVersion7();
        await SaveAsync(order, customer, Kazakh);

        // §11.7's erasure statement, written here because ADR-052 asks for the
        // path to be named beside the table and the extension that runs it is
        // owed whole. What it must leave behind is the shipment's own record,
        // which holds no customer at all (spec, section 7).
        await fixture.ExecuteAsync("DELETE FROM shipping.DeliveryAddresses WHERE CustomerId = {0};", customer);

        (await ReadAsync(order)).ShouldBeNull();
    }

    [Fact]
    public async Task The_columns_are_the_ones_section_7_names()
    {
        string[] columns = await fixture.ColumnsAsync("shipping", "DeliveryAddresses");

        columns.ShouldBe(
            ["OrderId", "CustomerId", "Line1", "Line2", "City", "PostalCode", "Country", "FetchedAt"],
            ignoreOrder: true,
            "the shipment's own record holds no personal data, so a column added here is a decision");
    }

    private async Task SaveAsync(OrderId order, Guid customer, DeliveryAddress address)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await Store(scope).SaveAsync(order, customer, address, Now, TestContext.Current.CancellationToken);
    }

    private async Task<DeliveryAddress?> ReadAsync(OrderId order)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await Store(scope).GetAsync(order, TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~DeliveryAddressStoreTests"`
Expected: compile failure on `IDeliveryAddressStore`, then — once it exists and
before the migration — a SQL error naming `shipping.DeliveryAddresses` as an
invalid object name.

- [ ] **Step 3: Write the port, the row and the store**

```csharp
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Addresses;

/// <summary>
/// The delivery address this service keeps for one order (spec, section 7): a
/// table of its own beside <c>Shipments</c>, so erasure and retention each
/// delete a row and leave the shipment's record whole (ADR-052).
/// </summary>
/// <remarks>
/// Raw statements through a port, following <c>IUnitOfWork.ExecuteRawAsync</c>'s
/// rule for a table with no aggregate behaviour; a port rather than that
/// member because <see cref="GetAsync"/> returns what it read.
/// <para>
/// §11.7's erasure is a <c>DELETE ... WHERE CustomerId = @CustomerId</c> and
/// is not a member here: the consumer that issues it arrives with that
/// section's extension, and <see cref="SaveAsync"/> carries the customer for
/// no other purpose.
/// </para>
/// </remarks>
public interface IDeliveryAddressStore
{
    Task SaveAsync(
        OrderId orderId,
        Guid customerId,
        DeliveryAddress address,
        DateTimeOffset fetchedAt,
        CancellationToken ct);

    Task<DeliveryAddress?> GetAsync(OrderId orderId, CancellationToken ct);
}
```

`DeliveryAddressRow` is `PaymentOrderRow`'s twin — mapped only so that
`migrations add` emits the table, loaded and saved by nothing:

```csharp
namespace Shipping.Infrastructure.Persistence;

/// <summary>
/// The shape of <c>shipping.DeliveryAddresses</c>, mapped only so that
/// <c>migrations add</c> emits the table. Nothing loads or saves it through
/// EF: <see cref="SqlDeliveryAddressStore"/> is the only reader and writer.
/// </summary>
internal sealed class DeliveryAddressRow
{
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; }
}
```

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shipping.Application.Carrier;

namespace Shipping.Infrastructure.Persistence;

internal sealed class DeliveryAddressRowConfiguration : IEntityTypeConfiguration<DeliveryAddressRow>
{
    public void Configure(EntityTypeBuilder<DeliveryAddressRow> builder)
    {
        builder.ToTable("DeliveryAddresses", "shipping");

        // Keyed by the order and not by the shipment: the address is Ordering's
        // fact about an order, and the two are one-to-one only because §3.2
        // gives one shipment per confirmed order (ADR-052).
        builder.HasKey(r => r.OrderId);
        builder.Property(r => r.OrderId).ValueGeneratedNever();

        // nvarchar everywhere, which is the default here and is the whole of
        // why a Kazakh-script address survives (spec, section 7).
        builder.Property(r => r.Line1).HasMaxLength(AddressLimits.MaxLineLength).IsRequired();
        builder.Property(r => r.Line2).HasMaxLength(AddressLimits.MaxLineLength);
        builder.Property(r => r.City).HasMaxLength(AddressLimits.MaxCityLength).IsRequired();
        builder.Property(r => r.PostalCode).HasMaxLength(AddressLimits.MaxPostalCodeLength).IsRequired();

        // Two ASCII letters by contract; IsFixedLength plus IsUnicode(false)
        // is what emits char(2) rather than nvarchar(2).
        builder.Property(r => r.Country).HasMaxLength(2).IsFixedLength().IsUnicode(false).IsRequired();
    }
}
```

`AddressLimits` joins `Shipping.Application/Carrier/` beside `CarrierLimits`,
because the adapter bounds what Ordering sends with the same numbers the
columns hold:

```csharp
namespace Shipping.Application.Carrier;

/// <summary>
/// What an address may carry and still be stored. The columns of
/// <c>shipping.DeliveryAddresses</c> are these widths, and the address adapter
/// refuses a longer answer before a row is written rather than at the insert.
/// </summary>
public static class AddressLimits
{
    public const int MaxLineLength = 200;

    public const int MaxCityLength = 100;

    public const int MaxPostalCodeLength = 32;
}
```

`SqlDeliveryAddressStore` is `SqlPaymentOrderStore`'s shape, with one
difference argued in the file: the write runs on its own connection rather
than on the unit of work's transaction, because the worker writes it before
the booking and outside any unit:

```csharp
using Common.Application;
using Dapper;
using Microsoft.Data.SqlClient;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

/// <summary>
/// The only reader and writer of <c>shipping.DeliveryAddresses</c> (spec,
/// section 7).
/// </summary>
/// <remarks>
/// On its own connection, unlike <c>IUnitOfWork.ExecuteRawAsync</c>'s callers:
/// the worker writes this row before it calls the carrier and commits the
/// shipment afterwards (spec, section 4), so there is no unit of work open to
/// enlist in — and joining one would hold a transaction across a third party's
/// latency, which is the thing that design exists to avoid.
/// </remarks>
internal sealed class SqlDeliveryAddressStore(IDbConnectionFactory connections) : IDeliveryAddressStore
{
    // UPDLOCK with HOLDLOCK on the update: the pass that repeats after a crash
    // meets the key-range lock rather than the primary key, so it updates the
    // first write's row instead of failing its insert (spec, section 4).
    private const string SaveSql =
        """
        UPDATE shipping.DeliveryAddresses WITH (UPDLOCK, HOLDLOCK)
        SET CustomerId = @CustomerId, Line1 = @Line1, Line2 = @Line2, City = @City,
            PostalCode = @PostalCode, Country = @Country, FetchedAt = @FetchedAt
        WHERE OrderId = @OrderId;

        IF @@ROWCOUNT = 0
            INSERT INTO shipping.DeliveryAddresses
                (OrderId, CustomerId, Line1, Line2, City, PostalCode, Country, FetchedAt)
            VALUES (@OrderId, @CustomerId, @Line1, @Line2, @City, @PostalCode, @Country, @FetchedAt);
        """;

    private const string GetSql =
        """
        SELECT Line1, Line2, City, PostalCode, Country
        FROM shipping.DeliveryAddresses
        WHERE OrderId = @OrderId;
        """;

    private sealed record Row(string Line1, string? Line2, string City, string PostalCode, string Country);

    public async Task SaveAsync(
        OrderId orderId,
        Guid customerId,
        DeliveryAddress address,
        DateTimeOffset fetchedAt,
        CancellationToken ct)
    {
        using SqlConnection connection = (SqlConnection)connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(
            SaveSql,
            new
            {
                OrderId = orderId.Value,
                CustomerId = customerId,
                address.Line1,
                address.Line2,
                address.City,
                address.PostalCode,
                address.Country,
                FetchedAt = fetchedAt
            },
            cancellationToken: ct));
    }

    public async Task<DeliveryAddress?> GetAsync(OrderId orderId, CancellationToken ct)
    {
        using SqlConnection connection = (SqlConnection)connections.Create();

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            GetSql, new { OrderId = orderId.Value }, cancellationToken: ct));

        // Country is char(2) and comes back padded only if it ever narrows;
        // trimmed here for the reason the order record's currency is.
        return row is null
            ? null
            : new DeliveryAddress(row.Line1, row.Line2, row.City, row.PostalCode, row.Country.Trim());
    }
}
```

registered in `AddShippingInfrastructure`:

```csharp
        services.AddScoped<IDeliveryAddressStore, SqlDeliveryAddressStore>();
```

`IDbConnectionFactory` and `SqlConnectionFactory` are the scaffold's, already
registered on the `Shipping` runtime connection string (§6.5). Replace the
cast with whatever `Create()` already returns if the rendered factory declares
`SqlConnection` directly; the cast exists only because the port is typed as
`IDbConnection`.

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddDeliveryAddresses \
    --project src/Services/Shipping/Shipping.Infrastructure \
    --startup-project src/Services/Shipping/Shipping.Migrator \
    --output-dir Persistence/Migrations
```

Open it: exactly one `CreateTable` for `shipping.DeliveryAddresses` with the
columns in Interfaces and no index beyond the primary key. **No index on
`CustomerId`, and that is a decision**: §11.7's erasure deletes by it, the
consumer that would is owed with that extension, and an index for a query no
code makes is a guess at its shape. Give the file the house dress: file-scoped
namespace, and a doc comment saying the configuration is the source of truth
and the `.Designer.cs` and snapshot beside it are machine-owned and untouched.

`DatabaseSmokeTests` counts the applied migrations, so PR-1's
`applied.Length.ShouldBe(8)` becomes `9` with
`applied[8].ShouldEndWith("_AddDeliveryAddresses")`.

- [ ] **Step 5: Run and commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~DeliveryAddressStoreTests"
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~DatabaseSmokeTests"
```

Expected: green, 0 warnings. Then, because a first-run migration rewritten in
review is not believed until the engine has built it from empty:

```bash
docker compose -f deploy/compose/docker-compose.yml down -v
```

```bash
git add src/Services/Shipping tests/Shipping.Worker.Tests
git commit -m "feat(shipping): the DeliveryAddresses table and the port that writes it"
```

The body says why the address is a table of its own rather than five columns
on the shipment — erasure and retention each delete a row and leave the
shipment's record whole — and names the erasure statement the table is
designed against.

---

### Task 3: The address port, its gRPC adapter and `shipping.address.refused`

**Files:**
- Create: `src/Services/Shipping/Shipping.Application/Addresses/IDeliveryAddressSource.cs`
- Create: `src/Services/Shipping/Shipping.Application/Addresses/AddressLookup.cs`
- Create: `src/Services/Shipping/Shipping.Application/Addresses/AddressSourceRefusedException.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Addresses/AddressHop.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Addresses/AddressMetrics.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Addresses/GrpcDeliveryAddressSource.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Addresses/GrantCheckedTokenCache.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Addresses/DependencyInjection.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/Shipping.Infrastructure.csproj`
- Modify: `src/Services/Shipping/Shipping.Worker/Program.cs`
- Create: `tests/Shipping.OrderingStub/Shipping.OrderingStub.csproj`
- Create: `tests/Shipping.OrderingStub/StubOrdering.cs`
- Modify: `Platform.slnx`
- Modify: `tests/Shipping.TestSupport/ShippingWorkerFactory.cs`,
  `tests/Shipping.TestSupport/Shipping.TestSupport.csproj`
- Create: `tests/Shipping.TestSupport/RecordingTokenCache.cs`
- Test: `tests/Shipping.Worker.Tests/DeliveryAddressSourceTests.cs`
- Test: `tests/Shipping.Worker.Tests/GrantCheckedTokenCacheTests.cs`
- Modify: `tests/Shipping.Worker.Tests/Shipping.Worker.Tests.csproj`

**Interfaces:**
- Consumes: `Ordering.Delivery.V1.DeliveryAddresses.DeliveryAddressesClient`,
  `GetDeliveryAddressRequest`, `GetDeliveryAddressReply` with `CustomerId`,
  `Line1`, `Line2`, `City`, `PostCode`, `Country` (PR-4's proto);
  `ITokenCache`, `ClientCredentialsHandler`, `ServiceIdentityOptions`,
  `CachingTokenClient`, `AuthorityKeyName` (PR-3b).
- Produces:

```csharp
namespace Shipping.Application.Addresses;

public abstract record AddressLookup
{
    public sealed record Found(DeliveryAddress Address, Guid CustomerId) : AddressLookup;
    public sealed record NoSuchOrder : AddressLookup;
}

public interface IDeliveryAddressSource
{
    Task<AddressLookup> GetAsync(OrderId orderId, CancellationToken ct);
}

public sealed class AddressSourceRefusedException : Exception { /* the three standard constructors */ }

namespace Shipping.Infrastructure.Addresses;
public static class AddressHop { /* ClientName, five numbers, the breaker */ }
public sealed class AddressMetrics { public void Refused(); }
public static class DependencyInjection { public const string BaseUrlKey = "AddressSource:BaseUrl"; }
```

- Produces: `Shipping.OrderingStub.StubOrdering` with `Address`, `Addresses`,
  `Statuses`, `Calls` and `Tokens`; `ShippingWorkerFactory`'s
  `addressSourceBaseUrl` parameter and `Tokens` property.

- [ ] **Step 1: Write the stub**

`tests/Shipping.OrderingStub/Shipping.OrderingStub.csproj` is
`Web.Bff.TestSupport`'s, one contract over:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    NOT a test project (§4.1), and not a second TestSupport either: it holds
    one stub server and references nothing of Shipping's.

    Its own library rather than Shipping.TestSupport, for Web.Bff.TestSupport's
    reason. Shipping.Infrastructure compiles delivery_addresses.proto as a
    CLIENT, so generating the SERVER half into a project that also references
    it would put every message type in one compilation twice, and CS0436 is an
    error under ADR-019. A project that references both halves must not NAME a
    generated type — it drives this stub through the surface below instead.

    Generating the server half into Shipping.Infrastructure would avoid the
    split and is refused: §3.2 gives Shipping no API at all, and an abstract
    gRPC service base in its production assembly is a surface the service is
    defined not to have.
  -->

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <!-- MapGrpcService and the Kestrel host behind it. Grpc.Tools rides in
         with this one and compiles the Protobuf item below. -->
    <PackageReference Include="Grpc.AspNetCore" />
    <!-- IAsyncLifetime. xunit.v3 itself refuses non-Exe output and names this
         package as the alternative. -->
    <PackageReference Include="xunit.v3.extensibility.core" />
  </ItemGroup>

  <ItemGroup>
    <!-- The SERVER half of the contract Shipping.Infrastructure compiles as a
         client. Ordering owns the file because Ordering serves the RPC
         (ADR-052); this project consumes the .proto and no assembly, which is
         what keeps §4.3 true. -->
    <Protobuf
      Include="..\..\src\Services\Ordering\Ordering.Api\Protos\delivery_addresses.proto"
      Link="Protos\delivery_addresses.proto"
      GrpcServices="Server" />
  </ItemGroup>

</Project>
```

`StubOrdering.cs`, in `StubCatalog`'s shape:

```csharp
using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ordering.Delivery.V1;
using Xunit;

namespace Shipping.OrderingStub;

/// <summary>One address this stub will answer with.</summary>
public sealed record StubAddress(
    Guid CustomerId,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string Country);

/// <summary>
/// A real gRPC server on an ephemeral loopback port, standing in for
/// Ordering's <c>DeliveryAddresses.Get</c> (ADR-052).
/// </summary>
/// <remarks>
/// A real server rather than a substituted client, for the reason the BFF's
/// stub Catalog states: everything interesting about this hop is what the
/// SERVER decides — the h2c negotiation, the bearer token that survives to the
/// wire, the status that becomes a refusal rather than a backoff.
/// <para>
/// <c>Http2</c> explicitly, because a cleartext endpoint at the default
/// answers a client asking for HTTP/2 exactly with <c>HTTP_1_1_REQUIRED</c> —
/// the measurement that gave Ordering a second Kestrel endpoint.
/// </para>
/// </remarks>
public sealed class StubOrdering : IAsyncLifetime
{
    private readonly ConcurrentQueue<StatusCode> _statuses = new();
    private readonly ConcurrentQueue<Guid> _calls = new();
    private readonly ConcurrentQueue<string> _tokens = new();

    private WebApplication? _app;

    /// <summary>The address the worker's gRPC client is pointed at.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>Addresses this stub knows, by order id.</summary>
    public ConcurrentDictionary<Guid, StubAddress> Addresses { get; } = new();

    /// <summary>Every order id this stub has been asked for, in order.</summary>
    public IReadOnlyCollection<Guid> Calls => _calls;

    /// <summary>Every <c>Authorization</c> value this stub has been sent, in order.</summary>
    public IReadOnlyCollection<string> Tokens => _tokens;

    /// <summary>
    /// Statuses to fail the next calls with, one per call, before answering
    /// normally. A queue rather than a flag, because the owner-taken-away test
    /// is about a SEQUENCE — refused, refused, then answered.
    /// </summary>
    public void Fail(params StatusCode[] statuses)
    {
        foreach (StatusCode status in statuses)
            _statuses.Enqueue(status);
    }

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(this);

        _app = builder.Build();
        _app.MapGrpcService<Service>();

        await _app.StartAsync(TestContext.Current.CancellationToken);

        Address = new Uri(_app.Urls.Single());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class Service(StubOrdering stub) : DeliveryAddresses.DeliveryAddressesBase
    {
        public override Task<GetDeliveryAddressReply> Get(
            GetDeliveryAddressRequest request,
            ServerCallContext context)
        {
            stub._tokens.Enqueue(context.RequestHeaders.GetValue("authorization") ?? "");

            Guid order = Guid.Parse(request.OrderId);
            stub._calls.Enqueue(order);

            if (stub._statuses.TryDequeue(out StatusCode status))
                throw new RpcException(new Status(status, "stubbed"));

            if (!stub.Addresses.TryGetValue(order, out StubAddress? address))
                throw new RpcException(new Status(StatusCode.NotFound, "No delivery address for that order."));

            return Task.FromResult(new GetDeliveryAddressReply
            {
                CustomerId = address.CustomerId.ToString(),
                Line1 = address.Line1,
                Line2 = address.Line2 ?? string.Empty,
                City = address.City,
                PostCode = address.PostalCode,
                Country = address.Country
            });
        }
    }
}
```

Add the project to `Platform.slnx` beside `tests/Shipping.TestSupport`, and a
`ProjectReference` to it from `tests/Shipping.TestSupport` — with a comment
repeating the rule that neither that project nor the suites above it may name
a generated type.

- [ ] **Step 2: Write the failing adapter tests**

`tests/Shipping.Worker.Tests/DeliveryAddressSourceTests.cs`, over the stub and
the real host, with no container:

```csharp
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Addresses;
using Shipping.OrderingStub;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// ADR-052's five outcomes, read from the client's side, over a real gRPC
/// server on loopback.
/// </summary>
public sealed class DeliveryAddressSourceTests : IAsyncLifetime
{
    private const string UnreachableSql =
        "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true";

    private const string UnreachableRabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";

    private readonly StubOrdering _ordering = new();

    private ShippingWorkerFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await _ordering.InitializeAsync();
        _factory = new ShippingWorkerFactory(
            UnreachableSql,
            UnreachableRabbit,
            addressSourceBaseUrl: _ordering.Address.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        _factory.Dispose();
        await _ordering.DisposeAsync();
    }

    private IDeliveryAddressSource Source() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<IDeliveryAddressSource>();

    private Guid KnownOrder(string postalCode = "050000")
    {
        Guid order = Guid.CreateVersion7();
        _ordering.Addresses[order] =
            new StubAddress(Guid.CreateVersion7(), "1 Abay Avenue", null, "Almaty", postalCode, "KZ");

        return order;
    }

    [Fact]
    public async Task An_order_answers_with_its_address_and_its_customer()
    {
        Guid order = KnownOrder();
        StubAddress expected = _ordering.Addresses[order];

        AddressLookup lookup = await Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken);

        AddressLookup.Found found = lookup.ShouldBeOfType<AddressLookup.Found>();
        found.CustomerId.ShouldBe(expected.CustomerId, "the row erasure deletes by is carried on the reply (ADR-052)");
        found.Address.ShouldBe(new DeliveryAddress("1 Abay Avenue", null, "Almaty", "050000", "KZ"));
    }

    [Fact]
    public async Task An_empty_second_line_arrives_as_absent_rather_than_blank()
    {
        Guid order = Guid.CreateVersion7();
        _ordering.Addresses[order] =
            new StubAddress(Guid.CreateVersion7(), "1 Abay Avenue", "", "Almaty", "050000", "KZ");

        AddressLookup lookup = await Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken);

        lookup.ShouldBeOfType<AddressLookup.Found>().Address.Line2.ShouldBeNull(
            "proto3 has no null string, so the absence arrives as \"\" and is stored as the absence it is");
    }

    [Fact]
    public async Task The_call_carries_the_hosts_own_token()
    {
        await Source().GetAsync(new OrderId(KnownOrder()), TestContext.Current.CancellationToken);

        _ordering.Tokens.ShouldHaveSingleItem().ShouldStartWith("Bearer ");
    }

    [Fact]
    public async Task NotFound_is_the_one_answer_that_means_the_order_has_no_address()
    {
        AddressLookup lookup = await Source().GetAsync(
            new OrderId(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        lookup.ShouldBeOfType<AddressLookup.NoSuchOrder>();
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task A_refused_credential_is_counted_and_thrown_rather_than_treated_as_an_absence(StatusCode status)
    {
        using RefusedCount counted = CountRefused();
        _ordering.Fail(status);

        await Should.ThrowAsync<AddressSourceRefusedException>(() =>
            Source().GetAsync(new OrderId(KnownOrder()), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(1, "a revoked grant is a defect somebody must see, not an outage to wait out");
    }

    [Fact]
    public async Task A_transient_status_is_retried_and_then_thrown_uncounted()
    {
        using RefusedCount counted = CountRefused();
        Guid order = KnownOrder();
        _ordering.Fail(StatusCode.Unavailable, StatusCode.Unavailable, StatusCode.Unavailable);

        await Should.ThrowAsync<RpcException>(() =>
            Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(0, "an outage is not a decision anybody took");
    }

    [Fact]
    public async Task A_transient_status_that_clears_inside_the_budget_answers()
    {
        Guid order = KnownOrder();
        _ordering.Fail(StatusCode.Unavailable);

        AddressLookup lookup = await Source().GetAsync(new OrderId(order), TestContext.Current.CancellationToken);

        lookup.ShouldBeOfType<AddressLookup.Found>();
        _ordering.Calls.Count.ShouldBe(2, "the pipeline retried inside AddressHop's budget");
    }

    [Fact]
    public async Task An_unreachable_owner_throws_rather_than_answering()
    {
        using ShippingWorkerFactory dead = new(
            UnreachableSql, UnreachableRabbit, addressSourceBaseUrl: "http://ordering-api.invalid/");

        await Should.ThrowAsync<RpcException>(() => dead.Services.CreateScope().ServiceProvider
            .GetRequiredService<IDeliveryAddressSource>()
            .GetAsync(new OrderId(Guid.CreateVersion7()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = AddressHop.AttemptTimeout * (AddressHop.MaxRetryAttempts + 1)
                         + AddressHop.MaxRetryDelay * AddressHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(
            AddressHop.TotalRequestTimeout,
            "PricingHop's argument: a total that cancels the last retry makes the retry count a fiction");

        AddressHop.TotalRequestTimeout.ShouldBeLessThan(Common.Web.ServiceOptions.OperationTimeout);

        // §9.7's bands, because Ordering is a peer and not a third party — the
        // one place this hop differs from CarrierHop, which sits outside them.
        AddressHop.AttemptTimeout.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        AddressHop.TotalRequestTimeout.ShouldBeInRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }
}
```

`RefusedCount` is `HttpCarrierGatewayTests`' `UnavailableCount` with
`"shipping.address.refused"` as the instrument name, and `CountRefused()`
resolves `AddressMetrics` and then `IMeterFactory.Create(CarrierMetrics.MeterName)`
— this host's meter, never one matched by name. It asserts `Enabled`, so a
zero can never be produced by a listener that attached to nothing. One extra
assertion belongs with it, because two classes now put instruments on one
meter:

```csharp
    [Fact]
    public void Both_instruments_land_on_the_one_meter_section_13_2_exports()
    {
        IMeterFactory factory = _factory.Services.GetRequiredService<IMeterFactory>();

        // The factory caches by name, which is what lets CarrierMetrics and
        // AddressMetrics each create §11's meter and still produce one. If it
        // ever stopped, the second class's instruments would be on a meter no
        // AddMeter line names and would be collected by nothing (§13.2).
        factory.Create(CarrierMetrics.MeterName).ShouldBeSameAs(factory.Create(CarrierMetrics.MeterName));
    }
```

- [ ] **Step 3: Run to see them fail**

Run: `dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~DeliveryAddressSourceTests"`
Expected: compile failure on `Shipping.Application.Addresses`,
`Shipping.Infrastructure.Addresses` and the factory's new parameter.

- [ ] **Step 4: Write the port and the budget**

```csharp
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Addresses;

/// <summary>
/// ADR-052's read, in this service's vocabulary: the order's delivery address
/// from the service that owns it.
/// </summary>
/// <remarks>
/// Two answers and no third. Anything transient throws and the row backs off;
/// a refused credential throws <see cref="AddressSourceRefusedException"/>,
/// which backs off the same way and is counted, because a revoked grant is a
/// decision somebody took rather than an outage to wait out.
/// </remarks>
public interface IDeliveryAddressSource
{
    Task<AddressLookup> GetAsync(OrderId orderId, CancellationToken ct);
}
```

```csharp
using Shipping.Application.Carrier;

namespace Shipping.Application.Addresses;

/// <summary>The owner's answer about one order.</summary>
public abstract record AddressLookup
{
    private AddressLookup() { }

    /// <summary>
    /// The address, and the customer it belongs to. The second member is
    /// carried for erasure's sake alone (ADR-052): it is stored beside the
    /// address and nowhere else, and the shipment's own record holds none.
    /// </summary>
    public sealed record Found(DeliveryAddress Address, Guid CustomerId) : AddressLookup;

    /// <summary>
    /// No such order, a cancelled one, or one whose address erasure has
    /// cleared. One answer for the three, because ADR-052 collapses them at
    /// the owner so that no reader can recover an order's state from a status.
    /// </summary>
    public sealed record NoSuchOrder : AddressLookup;
}
```

`AddressSourceRefusedException` takes the three standard constructors, as
`CarrierUnavailableException` does, with the summary "The owner refused this
host's credential, or the identity provider refused the host (ADR-052). The
worker's backoff owns it, and it is counted because somebody has to see it."

```csharp
namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// The address call's budget, in <c>PricingHop</c>'s shape and inside §9.7's
/// bands, because Ordering is a peer rather than a third party behind an
/// anti-corruption layer.
/// </summary>
/// <remarks>
/// Public for the reason <c>CarrierHop</c> is (§4.2): read from another
/// assembly, and one modifier commits less than an <c>InternalsVisibleTo</c>.
/// The fulfilment worker's lease sits above this total and the carrier's
/// together, which is the inequality section 4 asks a test to hold.
/// </remarks>
public static class AddressHop
{
    /// <summary>
    /// The <see cref="IHttpClientFactory"/> name the gRPC client registers
    /// under, given explicitly rather than defaulted to the generated client's
    /// type name, which is a fragile thing for a test to spell.
    /// </summary>
    public const string ClientName = "ordering-delivery-addresses";

    /// <summary>The per-attempt bound, inside §9.7's one-to-two-second band.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.2);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 2;

    /// <summary>The nominal first backoff, before jitter.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The cap applied after jitter, which is what makes the budget arithmetic
    /// rather than statistical.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The outermost bound, inside §9.7's three-to-five-second band and below
    /// <c>ServiceOptions.OperationTimeout</c>.
    /// </summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(4.5);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>
    /// Sized to a worker's call rate, as the carrier's is: the endpoint
    /// default of a hundred calls in a window is a threshold a loop making one
    /// call every few seconds never reaches, so the breaker would never open.
    /// </summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shorter than the window above, so the breaker does not forget its
    /// failures while open and reopen on the first error after it closes.
    /// </summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);
}
```

- [ ] **Step 5: Write the meter, the grant check and the adapter**

```csharp
using System.Diagnostics.Metrics;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// §11's second instrument, on the same meter as the carrier's: one refused
/// credential on the way to an address, whether the identity provider refused
/// this host or Ordering refused its token.
/// </summary>
/// <remarks>
/// <c>CarrierMetrics.MeterName</c> rather than a string of its own, so §13.2's
/// one <c>AddMeter</c> line covers both. <c>IMeterFactory</c> caches by name,
/// so the two classes hold one meter between them.
/// </remarks>
public sealed class AddressMetrics
{
    private readonly Counter<long> _refused;

    public AddressMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(CarrierMetrics.MeterName);
        _refused = meter.CreateCounter<long>(
            "shipping.address.refused",
            unit: "{refusal}",
            description: "Address reads refused for a credential rather than failing; a grant somebody changed.");
    }

    public void Refused() => _refused.Add(1);
}
```

```csharp
using System.IdentityModel.Tokens.Jwt;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.Logging;

namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// The half of ADR-052 the realm gate cannot make: this host holds its own
/// token to the one grant that record names, and refuses a token whose
/// <c>permission</c> set is anything else.
/// </summary>
/// <remarks>
/// A decorator rather than a change to <c>CachingTokenClient</c>: the grant is
/// this service's and that building block is every host's. Keycloak's default
/// roles live in <c>realm_access</c> and on the <c>account</c> client, which
/// this claim does not carry, and are outside the check by that record's own
/// instruction.
/// </remarks>
internal sealed partial class GrantCheckedTokenCache(
    ITokenCache inner,
    AddressMetrics metrics,
    ILogger<GrantCheckedTokenCache> log) : ITokenCache
{
    /// <summary>
    /// The permission this host's client holds, spelt as a literal because its
    /// owner is <c>OrderingPermissions.DeliveryAddress</c> and §4.3 lets no
    /// assembly cross the boundary to read it. The realm's closed-set
    /// assertion is what ties the two spellings together.
    /// </summary>
    private const string DeliveryAddress = "orders:delivery-address";

    private static readonly string[] Grant = [DeliveryAddress];

    // Compiled once rather than parsed per call; CA1848 is enforced by ADR-019.
    // Neither message names the token or the secret (§13.4).
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "The token this host was issued does not carry exactly its one grant (ADR-052).")]
    private partial void GrantIsWrong();

    public async Task<string> GetAsync(string scope, CancellationToken ct)
    {
        string token;

        try
        {
            token = await inner.GetAsync(scope, ct);
        }
        catch (InvalidOperationException e)
        {
            // The token endpoint refused this client. CachingTokenClient throws
            // HttpRequestException for the transient half, which passes through
            // and backs the row off uncounted, so this catch is the refusal.
            metrics.Refused();
            throw new AddressSourceRefusedException(
                "The identity provider refused this host's client credentials (§11.5).", e);
        }

        string[] granted =
        [
            .. new JwtSecurityTokenHandler()
                .ReadJwtToken(token)
                .Claims
                .Where(c => c.Type == "permission")
                .Select(c => c.Value)
                .Order(StringComparer.Ordinal)
        ];

        // Read, never validated: this host did not issue the token and is not
        // its audience. What it is checking is the size of its own grant, and a
        // signature says nothing about that.
        if (!granted.SequenceEqual(Grant, StringComparer.Ordinal))
        {
            metrics.Refused();
            GrantIsWrong();

            throw new AddressSourceRefusedException(
                $"The realm issued this host {granted.Length} permission(s) where ADR-052 names exactly one.");
        }

        return token;
    }
}
```

```csharp
using Grpc.Core;
using Ordering.Delivery.V1;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// The client half of ADR-052's read, and the only code here that knows
/// Ordering's wire format. Every status it maps is that record's table.
/// </summary>
internal sealed class GrpcDeliveryAddressSource(
    DeliveryAddresses.DeliveryAddressesClient addresses,
    AddressMetrics metrics) : IDeliveryAddressSource
{
    public async Task<AddressLookup> GetAsync(OrderId orderId, CancellationToken ct)
    {
        GetDeliveryAddressReply reply;

        try
        {
            reply = await addresses.GetAsync(
                new GetDeliveryAddressRequest { OrderId = orderId.Value.ToString() },
                cancellationToken: ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            // No such order, a cancelled one, or one whose address erasure has
            // cleared: one answer for the three (ADR-052), and terminal.
            return new AddressLookup.NoSuchOrder();
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            metrics.Refused();

            throw new AddressSourceRefusedException(
                $"Ordering refused this host's token with {e.StatusCode} (ADR-052).", e);
        }

        // Bounded here, before a row is written: the owner is a peer rather
        // than a stranger, but a value the column refuses is a commit that
        // fails one layer away from whatever produced it (AddressLimits).
        return new AddressLookup.Found(
            new DeliveryAddress(
                Bounded(reply.Line1, AddressLimits.MaxLineLength, "line1"),
                reply.Line2.Length == 0 ? null : Bounded(reply.Line2, AddressLimits.MaxLineLength, "line2"),
                Bounded(reply.City, AddressLimits.MaxCityLength, "city"),
                Bounded(reply.PostCode, AddressLimits.MaxPostalCodeLength, "post_code"),
                Bounded(reply.Country, 2, "country")),
            Guid.Parse(reply.CustomerId));

        // The field, never the value: the value is an address, and §13.4's
        // redactor cannot see one interpolated into a message.
        static string Bounded(string value, int maxLength, string field) =>
            value.Length <= maxLength
                ? value
                : throw new InvalidOperationException($"Ordering answered with a {field} longer than {maxLength}.");
    }
}
```

Every transient status — `Unavailable`, `DeadlineExceeded`, `Internal`, a
refused connection, an open circuit — escapes as `RpcException` and the
worker's backoff owns it. That is deliberate and is what the transient test
asserts: no second exception type is invented for a case nothing branches on.

- [ ] **Step 6: Write the registration**

`Addresses/DependencyInjection.cs` is `Carrier/DependencyInjection.cs`'s shape
with the section renamed and no API key:

```csharp
using Common.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Ordering.Delivery.V1;
using Polly;
using Shipping.Application.Addresses;

namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// ADR-052's client, apart from <c>AddShippingInfrastructure</c> because its
/// scheme rule needs the host's environment, which that method is not given.
/// </summary>
public static class DependencyInjection
{
    private const string Section = "AddressSource";
    public const string BaseUrlKey = $"{Section}:BaseUrl";

    public static IServiceCollection AddDeliveryAddressSource(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Eager, as the carrier's address is: a worker that cannot name the
        // owner of its addresses does not start, rather than failing its first
        // booking. A key rather than PricingHop's literal because the value is
        // checked here and inventoried by §15.4, and a checked value is one
        // the deployment has to supply.
        string? configured = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{BaseUrlKey} is not configured. Shipping cannot read an address.");

        // No message below echoes the configured value: a start-up failure is
        // logged, and an address can carry user information.
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{BaseUrlKey} is not an absolute HTTP(S) address.");
        }

        if (parsed.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries user information; this host authenticates with §11.5's grant alone.");
        }

        // Cleartext HTTP/2 inside the cluster is the platform's model (§10.1),
        // so no https rule applies here as it does to the carrier: the hop
        // never leaves the deployment (ADR-053).
        ArgumentNullException.ThrowIfNull(environment);

        services.AddSingleton<AddressMetrics>();
        services.AddScoped<IDeliveryAddressSource, GrpcDeliveryAddressSource>();

        IHttpClientBuilder client = services
            .AddGrpcClient<DeliveryAddresses.DeliveryAddressesClient>(AddressHop.ClientName, o => o.Address = parsed);

        // Resilience is registered FIRST so that it sits OUTERMOST, and the
        // credential handler runs inside it (§9.7, §11.5): the handler then
        // runs once per ATTEMPT, so a retried attempt asks the token cache
        // again instead of replaying the first attempt's token.
        client.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = AddressHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = AddressHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = AddressHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = AddressHop.RetryDelay;
            options.Retry.MaxDelay = AddressHop.MaxRetryDelay;

            options.CircuitBreaker.FailureRatio = AddressHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = AddressHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = AddressHop.CircuitBreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = AddressHop.CircuitBreakerBreakDuration;
        });

        client.AddHttpMessageHandler<ClientCredentialsHandler>();

        return services;
    }
}
```

In `Shipping.Worker/Program.cs`, the BFF's composition root mirrored — the one
place this host makes §11.5's grant:

```csharp
// §9.7, §11.5 — the client-credentials registrations, in the second host that
// makes them (ADR-052). The types are Common.Infrastructure.Identity's and the
// binding is each host's own.
builder.Services.AddTransient<ClientCredentialsHandler>();
builder.Services.AddSingleton<CachingTokenClient>();

// ADR-052: this host holds itself to its grant, because the realm gate cannot
// read a service account's roles. Decorating rather than replacing, so the
// caching and the discovery rules are still the building block's.
builder.Services.AddSingleton<ITokenCache>(sp => new GrantCheckedTokenCache(
    sp.GetRequiredService<CachingTokenClient>(),
    sp.GetRequiredService<AddressMetrics>(),
    sp.GetRequiredService<ILogger<GrantCheckedTokenCache>>()));

// Bound, validated and validated AT START. IOptions<T> always resolves —
// unbound it hands back a default instance — so a forgotten binding is
// invisible to ValidateOnBuild and surfaces as Ordering refusing this host's
// calls (§15.4).
builder.Services
    .AddOptions<ServiceIdentityOptions>()
    .BindConfiguration(ServiceIdentityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The token client's own transport, which deliberately carries NO
// ClientCredentialsHandler: a client that attached a token in order to fetch a
// token would recurse until the stack ran out.
string authority = builder.Configuration[AuthenticationExtensions.AuthorityKey]!;

builder.Services
    .AddHttpClient(CachingTokenClient.HttpClientName, client =>
        client.BaseAddress = new Uri(authority.TrimEnd('/') + "/"));

// The same key's NAME, carried into the token client because a building block
// below Common.Web cannot name it and a refused discovery document has to say
// which key to fix (§11.3, §11.5).
builder.Services.AddSingleton(new AuthorityKeyName(AuthenticationExtensions.AuthorityKey));

// ADR-052's read; its address is read, and its scheme checked, eagerly.
builder.Services.AddDeliveryAddressSource(builder.Configuration, builder.Environment);
```

`GrantCheckedTokenCache` is `internal`, so `Program.cs` reaches it through the
`Shipping.Infrastructure.Addresses` namespace only if the type is made
`public`. Make it `public sealed partial class`, on the modifier rule
`CarrierHop` states: the composition root is another assembly, and one
modifier commits less than an `InternalsVisibleTo`.

`Shipping.Infrastructure.csproj` gains, beside PR-2's two rows:

```xml
    <!-- The client half of ADR-052's read. Grpc.Net.ClientFactory is the
         AddGrpcClient integration, Grpc.Tools the protoc build task, and
         Google.Protobuf the runtime the generated code compiles against. All
         four are one row in Appendix B because they ship and version
         together, and §4.2's third row permits an Infrastructure project any
         package — as Microsoft.Extensions.Http.Resilience already is here. -->
    <PackageReference Include="Grpc.Net.ClientFactory" />
    <PackageReference Include="Google.Protobuf" />
    <PackageReference Include="Grpc.Tools">
      <!-- Build-time only: protoc and the C# plugin. Nothing of it ships. -->
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <!-- JwtSecurityTokenHandler, which the grant check reads its own token's
         permission claim with (ADR-052). Carried transitively through
         Common.Web's JwtBearer reference; named here on the register's
         honesty rule, because this project names the type. -->
    <PackageReference Include="System.IdentityModel.Tokens.Jwt" />
```

and the linked contract:

```xml
  <ItemGroup>
    <!-- LINKED, not copied (§4.3): one contract with two generated halves, so
         the client and the server cannot drift. Ordering owns the file because
         Ordering serves the RPC. A FILE reference and not an assembly one, so
         no project dependency is created and Common.Contracts is still the
         only assembly crossing a service boundary.

         The cost is a build-time path into another service's tree, paid twice:
         here, and as a COPY line in each of this service's two Dockerfiles. -->
    <Protobuf
      Include="..\..\Ordering\Ordering.Api\Protos\delivery_addresses.proto"
      Link="Protos\delivery_addresses.proto"
      GrpcServices="Client" />
  </ItemGroup>
```

- [ ] **Step 7: Give the factory the address source and a token**

In `tests/Shipping.TestSupport/ShippingWorkerFactory.cs`, a fifth parameter
and a substituted token cache:

```csharp
    /// <summary>
    /// Where the address client points when a test does not care. Unreachable
    /// for the authority's reason — <c>.invalid</c> never resolves, so a test
    /// that dials Ordering by accident fails loudly.
    /// </summary>
    public const string UnreachableAddressSource = "http://ordering-api.invalid/";

    /// <summary>
    /// The token source the credential handler draws on, replacing
    /// <c>CachingTokenClient</c> and its grant check so that no test needs an
    /// identity provider to prove what the handler does with a token. The
    /// check has a suite of its own, which is the only place the substitution
    /// costs anything.
    /// </summary>
    public RecordingTokenCache Tokens { get; } = new();
```

```csharp
            .UseSetting(AddressRegistration.BaseUrlKey, addressSourceBaseUrl)
            .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientId", "shipping-worker-test")
            .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientSecret", "not-a-real-secret")
            .UseSetting($"{ServiceIdentityOptions.SectionName}:Scope", "commerce-api")
```

```csharp
                services.RemoveAll<ITokenCache>();
                services.AddSingleton<ITokenCache>(Tokens);
```

with `using AddressRegistration = Shipping.Infrastructure.Addresses.DependencyInjection;`.
`RecordingTokenCache` is the BFF suite's file, copied under
`Shipping.TestSupport` with its namespace changed and its remarks intact: §4.3
permits one assembly to cross a service boundary and a test double is not it.

- [ ] **Step 8: Write the grant check's own suite**

`tests/Shipping.Worker.Tests/GrantCheckedTokenCacheTests.cs` builds tokens by
hand, because the substitution above means no other test reaches this class:

```csharp
[Theory]
[InlineData(new string[0])]
[InlineData(new[] { "orders:write" })]
[InlineData(new[] { "orders:delivery-address", "orders:write" })]
public async Task A_token_whose_grant_is_not_exactly_the_one_record_names_is_refused(string[] permissions)
{
    AddressMetrics metrics = new(new DefaultMeterFactory());
    GrantCheckedTokenCache tokens = new(new FixedTokenCache(Jwt(permissions)), metrics, NullLogger<…>.Instance);

    AddressSourceRefusedException thrown = await Should.ThrowAsync<AddressSourceRefusedException>(
        () => tokens.GetAsync("commerce-api", TestContext.Current.CancellationToken));

    // The count, never the values: a permission set in a message is a
    // configuration detail, and the message is what a log carries.
    thrown.Message.ShouldNotContain("orders:");
}

[Fact]
public async Task A_token_carrying_exactly_the_grant_is_handed_on_unchanged()
{
    string issued = Jwt(["orders:delivery-address"]);

    (await Cache(issued).GetAsync("commerce-api", TestContext.Current.CancellationToken)).ShouldBe(issued);
}

[Fact]
public async Task A_refused_client_credential_is_a_refusal_and_a_transport_fault_is_not()
{
    // CachingTokenClient's own split, relied on here: InvalidOperationException
    // for a provider that refused this client, HttpRequestException for one
    // that failed as a server does (§11.5).
    await Should.ThrowAsync<AddressSourceRefusedException>(
        () => Cache(new InvalidOperationException("refused")).GetAsync("commerce-api", default));
    await Should.ThrowAsync<HttpRequestException>(
        () => Cache(new HttpRequestException("down")).GetAsync("commerce-api", default));
}
```

`Jwt(string[] permissions)` writes an unsigned `JwtSecurityToken` carrying one
`permission` claim per element — no signing credentials, because the class
reads the token and deliberately never validates it. `FixedTokenCache` is a
file-local `ITokenCache` answering with one string or throwing one exception.
Add `System.IdentityModel.Tokens.Jwt` to
`tests/Shipping.Worker.Tests/Shipping.Worker.Tests.csproj` on the same honesty
rule, and a `ProjectReference` to `tests/Shipping.OrderingStub`.

**No second Keycloak container.** ADR-052 asks each client to be proved both
ways against a token Keycloak issued, and PR-4's suite does exactly that
against the realm; what is left here is what this host does with the token it
was handed, which needs no realm at all. §12.4's cost rule is the reason the
container is not started twice to say it again.

- [ ] **Step 9: Run and commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~DeliveryAddressSourceTests"
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~GrantCheckedTokenCacheTests"
py -3.12 .github/licence-gate/licence_gate.py
```

Expected: green, 0 warnings, the gate exits 0 — every package is pinned and
registered already.

```bash
git add src/Services/Shipping tests/Shipping.OrderingStub tests/Shipping.TestSupport \
        tests/Shipping.Worker.Tests Platform.slnx
git commit -m "feat(shipping): the address port, its gRPC adapter and the grant this host holds itself to"
```

The body argues the three decisions: the credential handler inside the
resilience pipeline, the single `NoSuchOrder` for ADR-052's three facts, and
the stub living in a project of its own because two copies of one message type
in a compilation is CS0436.

---

### Task 4: The fulfilment worker

**Files:**
- Modify: `src/Services/Shipping/Shipping.Domain/Shipments/Shipment.cs` — `ReleaseClaim`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Fulfilment/FulfilmentWork.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Fulfilment/FulfilmentClaims.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Fulfilment/FulfilmentWorker.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`
- Modify: `tests/Shipping.TestSupport/ShippingWorkerFactory.cs` — the hosted service
- Test: `tests/Shipping.Domain.Tests/ShipmentTests.cs` — `ReleaseClaim`

**Interfaces:**
- Consumes: `IDeliveryAddressSource`, `IDeliveryAddressStore`,
  `IShipmentRepository`, `ICarrierGateway`, `IUnitOfWork`, `CarrierHop`,
  `OutboxDispatcher.BackoffBaseSeconds` and `BackoffAttemptCap`.
- Produces:

```csharp
namespace Shipping.Domain.Shipments;
public sealed class Shipment { public void ReleaseClaim(); }

namespace Shipping.Infrastructure.Fulfilment;
public sealed record FulfilmentWork(Guid Id, Guid OrderId, string Status, string? CarrierReference);

public sealed class FulfilmentWorker : BackgroundService
{
    public const int ClaimBatchSize = 1;
    public const int LeaseSeconds = 60;
    public Task<int> RunOnceAsync(CancellationToken ct);
}
```

- [ ] **Step 1: Write the failing lease arithmetic and domain tests**

In `tests/Shipping.Worker.Tests/DeliveryAddressSourceTests.cs`'s inequality
test's file, one more assertion — the lease above both hops, which section 4
asks a test to hold:

```csharp
    [Fact]
    public void The_lease_outlives_both_hops_a_pass_can_make()
    {
        TimeSpan pass = AddressHop.TotalRequestTimeout + CarrierHop.TotalRequestTimeout;

        pass.ShouldBeLessThan(
            TimeSpan.FromSeconds(FulfilmentWorker.LeaseSeconds),
            "a lease that lapsed mid-pass would let a second replica claim a row this one is still booking");

        // And the pass fits §15.3's thirty-second drain — HostOptions.
        // ShutdownTimeout's default, which that section fixes the grace period
        // against — which is what decides the batch size: N rows would be N
        // times the worst case above.
        (pass * FulfilmentWorker.ClaimBatchSize).ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }
```

and in `tests/Shipping.Domain.Tests/ShipmentTests.cs`:

```csharp
    [Fact]
    public void Releasing_a_claim_drops_the_lease_and_resets_the_backoff()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);

        shipment.ReleaseClaim();

        shipment.LockedUntil.ShouldBeNull();
        shipment.Attempts.ShouldBe(0, "the next wait is the row's first again, not the last failure's");
    }
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Shipping.Domain.Tests --filter "FullyQualifiedName~ShipmentTests"`
Expected: compile failure on `ReleaseClaim`.

- [ ] **Step 3: Write the aggregate's release**

In `Shipment`, beside the bookkeeping properties PR-1 left behind:

```csharp
    /// <summary>
    /// The pass that claimed this row has finished with it: the lease is
    /// dropped and the backoff reset (spec, section 4). Behaviour here rather
    /// than in the worker because the columns are this row's; the claim and
    /// the failure are raw statements, because neither has the aggregate in
    /// hand.
    /// </summary>
    public void ReleaseClaim()
    {
        LockedUntil = null;
        Attempts = 0;
    }
```

- [ ] **Step 4: Write the claim and the backoff**

```csharp
namespace Shipping.Infrastructure.Fulfilment;

/// <summary>
/// One row a pass has leased, projected to exactly what the pass needs before
/// it loads the aggregate — the shipment, the order it answers for, and the
/// carrier's reference when there is a booking to cancel.
/// </summary>
public sealed record FulfilmentWork(Guid Id, Guid OrderId, string Status, string? CarrierReference);
```

```csharp
using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;

namespace Shipping.Infrastructure.Fulfilment;

/// <summary>
/// The lease and the backoff over <c>shipping.Shipments</c>, in
/// <c>OutboxDispatcher</c>'s shape and for its reasons.
/// </summary>
/// <remarks>
/// Raw statements rather than the repository: the claim is an atomic
/// select-and-lease one statement cannot express through the change tracker,
/// and the failure path runs when the aggregate was never loaded.
/// </remarks>
internal sealed class FulfilmentClaims(IDbConnectionFactory connections)
{
    // Atomic claim: selects and leases in one statement, so two replicas
    // cannot take the same row. READPAST skips rows another replica holds.
    //
    // Two populations, one claim: a Pending shipment to book, and a Booked one
    // whose cancellation the carrier has not answered yet (spec, section 5).
    // Every terminal state is outside both, so nothing already finished is
    // ever claimed.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP ({FulfilmentWorker.ClaimBatchSize}) *
            FROM shipping.Shipments WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE NextAttemptAt <= SYSDATETIMEOFFSET()
                AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
                AND (Status = 'Pending'
                     OR (Status = 'Booked'
                         AND CancellationRequestedAt IS NOT NULL
                         AND CancellationRefusedAt IS NULL))
            ORDER BY NextAttemptAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, {FulfilmentWorker.LeaseSeconds}, SYSDATETIMEOFFSET())
        OUTPUT inserted.Id, inserted.OrderId, inserted.Status, inserted.CarrierReference;
        """;

    // Increments the attempt counter and backs off by pushing NextAttemptAt
    // forward, and drops the lease so a replica does not wait out a minute for
    // a row that is already scheduled. The ladder is the dispatcher's
    // (spec, section 4), read from its constants so the two cannot drift.
    //
    // Nothing is abandoned by count: the shipment's deadline is the saga's,
    // and a row that outlives it is already a review row in Ordering.
    private static readonly string FailSql =
        $"""
        UPDATE shipping.Shipments
        SET
            Attempts      = Attempts + 1,
            LockedUntil   = NULL,
            NextAttemptAt = DATEADD(
                second,
                POWER(2, CASE WHEN Attempts > {OutboxDispatcher.BackoffAttemptCap}
                              THEN {OutboxDispatcher.BackoffAttemptCap}
                              ELSE Attempts END) * {OutboxDispatcher.BackoffBaseSeconds},
                SYSDATETIMEOFFSET())
        WHERE Id = @Id;
        """;

    public async Task<IReadOnlyList<FulfilmentWork>> ClaimAsync(CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        // CommandDefinition, so the token reaches the database command: with
        // the plain overload a shutdown cannot interrupt a blocked claim.
        return [.. await connection.QueryAsync<FulfilmentWork>(new CommandDefinition(ClaimSql, cancellationToken: ct))];
    }

    public async Task FailAsync(Guid id, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(FailSql, new { Id = id }, cancellationToken: ct));
    }
}
```

- [ ] **Step 5: Write the worker**

```csharp
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Fulfilment;

/// <summary>
/// Spec section 4's first worker: it claims a shipment under a lease, reads
/// the address through <see cref="IDeliveryAddressSource"/>, books with
/// <see cref="ICarrierGateway"/>, and services a booked shipment whose
/// cancellation the carrier has not answered yet.
/// </summary>
/// <remarks>
/// The call is made OUTSIDE any unit of work, between the claim and the
/// commit, which is what keeps a third party's latency off a transaction. The
/// crash that doubles the call is the one between the carrier's answer and the
/// commit; the next pass repeats the call under the same key and receives the
/// first answer.
/// </remarks>
public sealed class FulfilmentWorker(IServiceScopeFactory scopes, ILogger<FulfilmentWorker> log) : BackgroundService
{
    /// <summary>
    /// How many rows one claim leases. ONE, and the number is the arithmetic:
    /// a row costs up to both hops' totals, a pass has to fit §15.3's
    /// thirty-second drain, and more throughput is more replicas — which is
    /// what the lease makes safe (spec, section 4).
    /// </summary>
    public const int ClaimBatchSize = 1;

    /// <summary>
    /// How long a claim holds the row it leased. Longer than both hops
    /// together, so a slow pass is not re-claimed underneath itself, and short
    /// enough that a replica killed mid-call releases its row within the
    /// minute.
    /// </summary>
    public const int LeaseSeconds = 60;

    private static readonly Action<ILogger, Guid, Guid, Exception?> PassFailed =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(1, nameof(PassFailed)),
            "Fulfilment pass for shipment {ShipmentId} on order {OrderId} failed; the row backs off.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> Superseded =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Warning,
            new EventId(2, nameof(Superseded)),
            "Shipment {ShipmentId} on order {OrderId} moved while the carrier answered; the booking was handed back.");

    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(ClaimFailed)),
            "Fulfilment claim failed; retrying next tick.");

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(CarrierHop.FulfilmentTick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The filter asks the TOKEN, not the exception's type. No host
                // sets BackgroundServiceExceptionBehavior, so the default turns
                // one escaped exception into a stopped host — and a gateway
                // enforcing its own deadline throws OperationCanceledException
                // while this token is still live (spec, section 4).
                ClaimFailed(log, ex);
            }
        }
    }

    /// <summary>
    /// One claim-and-fulfil pass. Returns the number of rows moved. Public so
    /// tests drive it directly instead of racing a timer (§12.4).
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();

        IReadOnlyList<FulfilmentWork> claimed =
            await claimScope.ServiceProvider.GetRequiredService<FulfilmentClaims>().ClaimAsync(ct);

        int moved = 0;

        foreach (FulfilmentWork work in claimed)
        {
            // A scope per row, not per batch: a handler that throws mid-write
            // would otherwise hand the next row its own tracked, half-mutated
            // state.
            await using AsyncServiceScope row = scopes.CreateAsyncScope();

            try
            {
                if (await FulfilAsync(row.ServiceProvider, work, ct))
                    moved++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Again the token rather than the type, and the address and the
                // carrier are both here: an outage, a refused credential and a
                // defect all back the row off, and only the second is counted.
                await claimScope.ServiceProvider.GetRequiredService<FulfilmentClaims>().FailAsync(work.Id, ct);
                PassFailed(log, work.Id, work.OrderId, ex);
            }
        }

        return moved;
    }

    private async Task<bool> FulfilAsync(IServiceProvider sp, FulfilmentWork work, CancellationToken ct)
    {
        ShipmentId id = new(work.Id);
        OrderId order = new(work.OrderId);

        if (work.Status == nameof(ShipmentStatus.Booked))
        {
            CancellationResult cancellation = await sp.GetRequiredService<ICarrierGateway>()
                .CancelAsync(new CancellationRequest(id, work.CarrierReference!), ct);

            return await CommitAsync(sp, id, (shipment, now) => cancellation switch
            {
                CancellationResult.Cancelled => shipment.CarrierCancelled(now),
                _ => shipment.CarrierRefusedCancellation(now)
            }, ct);
        }

        IDeliveryAddressStore store = sp.GetRequiredService<IDeliveryAddressStore>();
        DeliveryAddress? address = await store.GetAsync(order, ct);

        if (address is null)
        {
            AddressLookup lookup = await sp.GetRequiredService<IDeliveryAddressSource>().GetAsync(order, ct);

            if (lookup is AddressLookup.NoSuchOrder)
            {
                // Terminal, and not retried: ADR-052's fifth row. The order has
                // no address anybody can be shown, so the shipment cannot be
                // fulfilled and no later pass would learn otherwise.
                return await CommitAsync(
                    sp, id, (shipment, now) => shipment.MarkUnfulfillable("no_such_order", now), ct);
            }

            AddressLookup.Found found = (AddressLookup.Found)lookup;
            address = found.Address;

            // Committed on its own, before the booking: the pass that crashes
            // after the carrier has answered repeats from here, and a stored
            // address is one call this service does not make twice (ADR-052).
            await store.SaveAsync(
                order, found.CustomerId, address, sp.GetRequiredService<TimeProvider>().GetUtcNow(), ct);
        }

        BookingResult booking = await sp.GetRequiredService<ICarrierGateway>()
            .BookAsync(new BookingRequest(id, address), ct);

        if (booking is BookingResult.Refused refused)
            return await CommitAsync(sp, id, (shipment, now) => shipment.MarkUnfulfillable(refused.Reason, now), ct);

        BookingResult.Booked booked = (BookingResult.Booked)booking;

        if (await CommitAsync(
            sp, id, (shipment, now) => shipment.Book(booked.Reference, booked.TrackingNumber, now), ct))
            return true;

        // The order was cancelled while the carrier was answering, so the row
        // is Voided and the state machine refuses the booking — spec section 6
        // says a Pending shipment is never booked, and handing the booking back
        // under the same shipment's cancel key is what makes that true. A crash
        // between the two calls leaves a booking the row cannot name; that
        // window is the price of the consumers never waiting on a lease.
        await sp.GetRequiredService<ICarrierGateway>()
            .CancelAsync(new CancellationRequest(id, booked.Reference), ct);
        Superseded(log, work.Id, work.OrderId);

        return false;
    }

    private static async Task<bool> CommitAsync(
        IServiceProvider sp,
        ShipmentId id,
        Func<Shipment, DateTimeOffset, bool> move,
        CancellationToken ct)
    {
        IUnitOfWork unitOfWork = sp.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.ExecuteAsync(
            async inner =>
            {
                Shipment shipment = await sp.GetRequiredService<IShipmentRepository>().GetAsync(id, inner)
                    ?? throw new InvalidOperationException($"Shipment {id} was claimed and is now absent.");

                bool moved = move(shipment, sp.GetRequiredService<TimeProvider>().GetUtcNow());

                // Released whatever the move decided: a row the pass is done
                // with must not hold its lease until it lapses, and a
                // superseded arrival is done with (spec, section 5).
                shipment.ReleaseClaim();
                await unitOfWork.SaveChangesAsync(inner);

                return moved;
            },
            ct);
    }
}
```

Registered in `AddShippingInfrastructure`, beside the outbox dispatcher:

```csharp
        // Spec section 4's first worker. AddHostedService<T> rather than a
        // factory overload, so a suite that drives one pass can find and
        // remove exactly this registration by its implementation type.
        services.AddScoped<FulfilmentClaims>();
        services.AddHostedService<FulfilmentWorker>();
```

and in `ShippingWorkerFactory`, removed and re-registered exactly as Payments'
factory does with `OutboxDispatcher`, so a test drives `RunOnceAsync` rather
than racing a five-second timer:

```csharp
                ServiceDescriptor fulfilment = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(FulfilmentWorker));
                services.Remove(fulfilment);

                services.AddSingleton<FulfilmentWorker>();
```

with `public Task<int> RunFulfilmentPassAsync() => Factory.Services.GetRequiredService<FulfilmentWorker>().RunOnceAsync(TestContext.Current.CancellationToken);`
on `ServiceFixture`.

- [ ] **Step 6: Run and commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Domain.Tests
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~DeliveryAddressSourceTests"
```

Expected: green, 0 warnings.

```bash
git add src/Services/Shipping tests/Shipping.TestSupport tests/Shipping.Domain.Tests \
        tests/Shipping.Worker.Tests
git commit -m "feat(shipping): the fulfilment worker claims a shipment, reads its address and books it"
```

The body argues the batch size of one from the pass's worst case, says why the
address row is committed before the booking, and names the compensating cancel
as what keeps section 6's "never booked" true.

---

### Task 5: The worker over containers

**Files:**
- Modify: `tests/Shipping.TestSupport/ServiceFixture.cs` — the carrier
  simulator, the Ordering stub, the reset and the pass helper
- Create: `tests/Shipping.TestSupport/ShipmentCommitFaults.cs`
- Create: `tests/Shipping.TestSupport/CapturedLogs.cs`
- Test: `tests/Shipping.Worker.Tests/ShipmentFulfilmentTests.cs`
- Modify: `tests/Shipping.Worker.Tests/HostSmokeTests.cs` — the readiness set

**Interfaces:**
- Consumes: Task 1's queue and consumers, Task 2's table, Task 3's stub and
  adapter, Task 4's worker; `SimulatorMappings.Directory()` and the postal-code
  script of PR-2's section 9 table.
- Produces: `ServiceFixture.Carrier`, `.Ordering`, `.FailNextCommit()`,
  `.RunFulfilmentPassAsync()`, `.CapturedLogs`, `.QueueDepthAsync(queue)`,
  `.BindingsAsync(queue)`.

- [ ] **Step 1: Extend the fixture**

`ServiceFixture` starts the carrier simulator in process over
`SimulatorMappings.Directory()`, exactly as Payments' fixture starts its
provider, and a `StubOrdering` beside it; both are reset in `ResetAsync` — the
mappings as well as the log, because a stub a test adds outlives a log reset.
`Factory` becomes
`new ShippingWorkerFactory(ConnectionString, _rabbit.GetConnectionString(), Carrier.Urls[0] + "/", addressSourceBaseUrl: Ordering.Address.ToString())`.

`ShipmentCommitFaults` is `CommitFaultInterceptor` with one change, argued in
the file: the qualifying save is one with a `Shipment` modified, because this
service stages no outbox row yet and firing on an inbox write would make
`Fired` a claim about nothing.

`CapturedLogs` is an `ILoggerProvider` collecting each entry's formatted
message, its state's values and its exception's `ToString()`, registered by
the factory. `RecordingLoggerProvider` in `Common.Web.Tests` records scopes
only, which is a different claim, and §4.3 gives a test helper no licence to
cross an assembly boundary.

`BindingsAsync(string queue)` runs
`rabbitmqctl list_bindings --quiet --no-table-headers source_name destination_name`
through the container, as `QueueDepthAsync` runs `list_queues`, and returns
the source exchanges bound to that destination.

- [ ] **Step 2: Write the failing container tests**

`tests/Shipping.Worker.Tests/ShipmentFulfilmentTests.cs`:

```csharp
[Collection(nameof(IntegrationCollection))]
public sealed class ShipmentFulfilmentTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DeliveryAddress Kazakh =
        new("Абай даңғылы 1, ә ғ қ ң ө ұ ү һ і", "пәтер 12", "Алматы", "050000", "KZ");

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_event_in_the_consumes_column_is_bound_on_the_queue()
    {
        // The half MessagingRegistrationTests cannot make: the harness replaces
        // the UsingRabbitMq callback where ConfigureConsumer is declared, so
        // the binding is provable only against a real broker (§9.5).
        string[] bound = await fixture.BindingsAsync(MessagingRegistration.EventsQueue);

        bound.ShouldContain("Common.Contracts.Ordering.V1:OrderConfirmed");
        bound.ShouldContain("Common.Contracts.Ordering.V1:OrderCancelled");
    }

    [Fact]
    public async Task A_confirmed_order_is_booked_and_the_address_round_trips_to_the_carrier()
    {
        Guid order = await ConfirmAsync(Kazakh);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);

        (await StatusAsync(order)).ShouldBe("Booked");
        (await ReferenceAsync(order)).ShouldBe("crr_SIM-OK");
        BookingBody().ShouldContain("ә ғ қ ң ө ұ ү һ і", Case.Sensitive,
            "nvarchar end to end: a Cyrillic code page anywhere in the path would answer question marks");
        BookingBody().ShouldContain("Алматы");
    }

    [Fact]
    public async Task No_log_line_holds_the_address()
    {
        await ConfirmAsync(Kazakh);

        await fixture.RunFulfilmentPassAsync();

        // Exported, not sampled: §11 says no line holds an address as an
        // attribute or inside an exception's text, and a search over formatted
        // messages alone would miss the structured half.
        fixture.CapturedLogs.Everything.ShouldNotContain(
            line => line.Contains("Абай", StringComparison.Ordinal)
                || line.Contains("Алматы", StringComparison.Ordinal));
        fixture.CapturedLogs.Everything.ShouldNotBeEmpty("a suite that captured nothing would assert nothing");
    }

    [Fact]
    public async Task An_owner_that_refuses_leaves_the_shipment_pending_and_nothing_in_an_error_queue()
    {
        Guid order = await ConfirmAsync(Kazakh);
        fixture.Ordering.Fail(StatusCode.PermissionDenied, StatusCode.PermissionDenied);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0);

        (await StatusAsync(order)).ShouldBe("Pending");
        (await AttemptsAsync(order)).ShouldBe(1);
        (await fixture.QueueDepthAsync($"{MessagingRegistration.EventsQueue}_error")).ShouldBe(
            0, "the consumers made no call, so an outage reaches no queue at all");

        await ClearBackoffAsync(order);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1, "the stub recovered and the row was still there");
        (await StatusAsync(order)).ShouldBe("Booked");
        (await AttemptsAsync(order)).ShouldBe(0, "a released claim resets the backoff");
    }

    [Fact]
    public async Task An_order_the_owner_does_not_know_is_unfulfillable_and_is_not_retried()
    {
        Guid order = await ConfirmAsync(address: null);

        await fixture.RunFulfilmentPassAsync();

        (await StatusAsync(order)).ShouldBe("Unfulfillable");
        (await ReasonAsync(order)).ShouldBe("no_such_order");

        await ClearBackoffAsync(order);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0, "a terminal row is outside the claim");
    }

    [Fact]
    public async Task A_carrier_that_refuses_the_address_makes_the_shipment_unfulfillable()
    {
        Guid order = await ConfirmAsync(Kazakh with { PostalCode = "SIM-REFUSED" });

        await fixture.RunFulfilmentPassAsync();

        (await StatusAsync(order)).ShouldBe("Unfulfillable");
        (await ReasonAsync(order)).ShouldBe("address_not_serviceable");
    }

    [Fact]
    public async Task A_carrier_that_is_down_backs_the_row_off_and_keeps_it()
    {
        Guid order = await ConfirmAsync(Kazakh with { PostalCode = "SIM-DOWN" });

        await fixture.RunFulfilmentPassAsync();

        (await StatusAsync(order)).ShouldBe("Pending");
        (await AttemptsAsync(order)).ShouldBe(1);
        (await NextAttemptDelayAsync(order)).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_crash_between_the_carriers_answer_and_the_commit_books_once_at_the_carrier()
    {
        Guid order = await ConfirmAsync(Kazakh);
        using CommitFault fault = fixture.FailNextCommit();

        await Should.ThrowAsync<Exception>(() => fixture.RunFulfilmentPassAsync());
        await ClearBackoffAsync(order);
        await fixture.RunFulfilmentPassAsync();

        fault.Fired.ShouldBeTrue("the first pass booked and then failed its commit");
        BookingCalls().ShouldBe(2, "the retry repeated the call rather than skipping it");
        BookingKeys().Distinct().ShouldHaveSingleItem().ShouldBe($"book:{await ShipmentIdAsync(order)}");
        (await ReferenceAsync(order)).ShouldBe("crr_SIM-OK");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses WHERE OrderId = {0}", order)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_passes_overlapping_claim_one_row_once()
    {
        Guid order = await ConfirmAsync(Kazakh with { PostalCode = "SIM-SLOW" });

        // Staged, not two passes back to back: the first is inside the
        // carrier's budget when the second claims, which is the only ordering
        // that says anything about READPAST.
        Task<int> first = fixture.RunFulfilmentPassAsync();
        await WaitUntil(() => BookingCalls() >= 1);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0, "the second pass skipped a leased row");
        await Should.ThrowAsync<Exception>(() => first);

        (await StatusAsync(order)).ShouldBe("Pending");
    }

    [Fact]
    public async Task A_lapsed_lease_is_taken_by_another_pass()
    {
        Guid order = await ConfirmAsync(Kazakh);
        await fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, -1, SYSDATETIMEOFFSET()) WHERE OrderId = {0};",
            order);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1, "a lease in the past is no lease");
    }

    [Fact]
    public async Task A_pass_that_throws_leaves_the_host_running()
    {
        // The loop rather than the pass: ExecuteAsync's catch is what stops one
        // escaped exception becoming a stopped host, and the default
        // BackgroundServiceExceptionBehavior is what makes that matter.
        await ConfirmAsync(Kazakh with { PostalCode = "SIM-DOWN" });
        FulfilmentWorker worker = fixture.Factory.Services.GetRequiredService<FulfilmentWorker>();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(CarrierHop.FulfilmentTick * 2, TestContext.Current.CancellationToken);

        worker.ExecuteTask!.IsFaulted.ShouldBeFalse();
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancel_then_despatch_voids_a_pending_shipment_and_never_books_it()
    {
        Guid order = await ConfirmAsync(Kazakh);

        await PublishAsync(Cancelled(order));

        (await StatusAsync(order)).ShouldBe("Voided");
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0);
        BookingCalls().ShouldBe(0, "spec section 6: a Pending shipment is voided at once and is never booked");
    }

    [Fact]
    public async Task Cancel_before_confirm_leaves_a_tombstone_the_confirmation_finds()
    {
        Guid order = Guid.CreateVersion7();
        fixture.Ordering.Addresses[order] = Stub(Kazakh);

        await PublishAsync(Cancelled(order));
        await PublishAsync(Confirmed(order));

        (await StatusAsync(order)).ShouldBe("Voided");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE OrderId = {0}", order)).ShouldBe(1);
    }

    [Fact]
    public async Task A_cancellation_of_a_booked_shipment_asks_the_carrier_and_voids_it()
    {
        Guid order = await ConfirmAsync(Kazakh);
        await fixture.RunFulfilmentPassAsync();

        await PublishAsync(Cancelled(order));
        (await StatusAsync(order)).ShouldBe("Booked", "tracking continues until the carrier answers");

        await fixture.RunFulfilmentPassAsync();

        (await StatusAsync(order)).ShouldBe("Voided");
        CancelKeys().ShouldHaveSingleItem().ShouldBe($"cancel:{await ShipmentIdAsync(order)}");
    }

    [Fact]
    public async Task A_carrier_that_says_it_is_too_late_stamps_the_refusal_and_leaves_it_booked()
    {
        Guid order = await ConfirmAsync(Kazakh with { PostalCode = "SIM-LATE" });
        await fixture.RunFulfilmentPassAsync();
        await PublishAsync(Cancelled(order));

        await fixture.RunFulfilmentPassAsync();

        (await StatusAsync(order)).ShouldBe("Booked");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE OrderId = {0} AND CancellationRefusedAt IS NOT NULL",
            order)).ShouldBe(1, "the saga's review row is the record of the disagreement");
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0, "a refused cancellation is outside the claim");
    }

    [Fact]
    public async Task Despatch_then_cancel_is_a_no_op_and_the_goods_move()
    {
        Guid order = await ConfirmAsync(Kazakh);
        await fixture.RunFulfilmentPassAsync();
        await DespatchAsync(order);

        await PublishAsync(Cancelled(order));

        (await StatusAsync(order)).ShouldBe("Dispatched");
        CancelKeys().ShouldBeEmpty("the carrier is told nothing about a parcel already collected");
    }
}
```

The helpers are Payments' endpoint helpers one service over: `PublishAsync`
onto the bus with a drain, `Confirmed`/`Cancelled` building the contracts,
`StatusAsync`/`ReferenceAsync`/`ReasonAsync`/`AttemptsAsync`/
`NextAttemptDelayAsync`/`ShipmentIdAsync` reading one column with
`fixture.ScalarAsync`, `ClearBackoffAsync` setting `NextAttemptAt` to
`SYSDATETIMEOFFSET()`, `BookingCalls`/`BookingBody`/`BookingKeys`/`CancelKeys`
reading `fixture.Carrier.LogEntries`, `DespatchAsync` recording a `Collected`
tracking event through the repository — PR-6 brings the worker that would —
and `ConfirmAsync` seeding the stub and publishing `OrderConfirmed`.

- [ ] **Step 3: The readiness set**

In `tests/Shipping.Worker.Tests/HostSmokeTests.cs`, Payments' registration
test with one addition, which is the assertion §10 asks for:

```csharp
    [Fact]
    public void Ready_probe_reports_the_sql_and_bus_checks_and_no_shared_dependency()
    {
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // Two, and the count is the assertion rather than a detail of it: the
        // carrier, Ordering and Keycloak are shared by every replica, so one
        // outage would pull every pod and then block the rollout carrying the
        // fix (§13.5).
        options.Registrations.Count.ShouldBe(2);
        options.Registrations.Select(r => r.Name).ShouldBe(["sql", "masstransit-bus"], ignoreOrder: true);
    }
```

- [ ] **Step 4: Run and commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Worker.Tests
```

Expected: green with a running Docker daemon; without one the container half
fails on `Failed to connect to Docker endpoint`, which is the daemon and not
the suite. The `SIM-SLOW` test takes about nineteen seconds by design.

```bash
git add tests/Shipping.TestSupport tests/Shipping.Worker.Tests
git commit -m "test(shipping): the fulfilment worker over SQL Server, RabbitMQ and the simulator"
```

The body lists what each test is the only witness of: the claim's `READPAST`,
the lease that lapses, the idempotency key across a failed commit, both of
section 6's interleavings, and the log export that finds no address.

---

### Task 6: The keys, the Compose unit and the two images

**Files:**
- Modify: `deploy/compose/services/shipping.yml`
- Modify: `deploy/compose/.env.example`
- Modify: `deploy/compose/README.md`
- Modify: `.github/secret-scan/allowed/deploy.txt`
- Modify: `src/Services/Shipping/Shipping.Worker/Dockerfile`,
  `src/Services/Shipping/Shipping.Migrator/Dockerfile`
- Modify: `.github/workflows/ci.yml` — the `shipping` filter
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.4's table
- Modify: `docs/secrets.md` — the local-development exception row

- [ ] **Step 1: The Compose unit**

On `shipping-worker`, replacing the rendered comment that says this service
binds no `Identity__Client__*`:

```yaml
      # ADR-052's read. The host name is the Compose service and the
      # Kubernetes Service alike; the port is Ordering's HTTP/2-only endpoint,
      # because a cleartext Kestrel endpoint cannot serve HTTP/1.1 and h2c at
      # once.
      AddressSource__BaseUrl: "http://ordering-api:8081"
      # Required by ValidateOnStart (§15.4) — this host refuses to boot without
      # all three, and it is the second host in the platform that binds them
      # (ADR-052). Local values only; production mounts a secret.
      Identity__Client__ClientId: "shipping-worker"
      Identity__Client__ClientSecret: "${SHIPPING_CLIENT_SECRET:-local-dev-shipping-secret}"
      # The scope that becomes the audience every service validates (§11.5),
      # and the scope whose mapper writes the permission claim this host holds
      # itself to (ADR-052).
      Identity__Client__Scope: "commerce-api"
```

and, under `depends_on`, `ordering-api: { condition: service_started }` with
the comment that the worker's first pass would otherwise meet a name that does
not resolve and back the row off for nothing.

`.env.example` gains, in the BFF row's shape:

```
# Uncomment only alongside a realm whose shipping-worker client carries the new
# value — changing it here alone leaves the host booting and every address read
# refused at the token endpoint.
# SHIPPING_CLIENT_SECRET=local-dev-shipping-secret
```

Run the secret scan; add the finding it reports against
`deploy/compose/services/shipping.yml` to
`.github/secret-scan/allowed/deploy.txt` with the fingerprint the scanner
computed and a reason naming it as §14.1's local-development default. Take the
gate's digest, never one written from this plan.

- [ ] **Step 2: The two Dockerfiles**

Both images restore `Shipping.Infrastructure.csproj`, which now compiles a
`.proto` by relative path, so both need the file in the build context — the
line `Web.Bff`'s Dockerfile already carries one service over, with its own
comment:

```dockerfile
# THE ONE LINE THAT IS NOT LIKE THE OTHER SERVICE IMAGES, and it is the price
# of the linked .proto (§4.3, and Shipping.Infrastructure's csproj). This
# service compiles Ordering's contract by relative path, so the file has to be
# in the build context even though no Ordering project is — and it is a
# *source* input rather than a restore one, which is why it sits here.
COPY src/Services/Ordering/Ordering.Api/Protos/ src/Services/Ordering/Ordering.Api/Protos/
```

placed after the `COPY src/Services/Shipping/` line in each.

`ci.yml`'s `shipping` filter gains the same path, because a filter that never
fires is what the `images` job cannot close:

```yaml
              - 'src/Services/Ordering/Ordering.Api/Protos/**'
```

- [ ] **Step 3: §15.4 and `docs/secrets.md`**

§15.4's inventory table gains one row, beside `Carrier__BaseUrl`:

```markdown
| `AddressSource__BaseUrl` | Config | Helm `addressSource.baseUrl` | ✓ **for Shipping's worker** — ADR-052's address read, checked at start as the carrier's address is |
```

The three `Identity__Client__*` rows already name Shipping's worker: PR-4
amended them, and nothing here restates a row that is already true.

`docs/secrets.md`'s local-development exception table gains its row beside the
BFF's:

```markdown
| Shipping worker client secret | `${SHIPPING_CLIENT_SECRET:-local-dev-shipping-secret}` |
```

replacing PR-4's placeholder text, which said the seam arrives with the host
that reads it. This is that host.

- [ ] **Step 4: `deploy/compose/README.md`**

The BFF paragraph says it is "the one host that needs more than an authority".
It is not, since ADR-052. Amend that sentence and add Shipping's own host-run
block after Payments':

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Shipping='Server=localhost;Database=Shipping;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://shipping-svc:local-dev-shipping@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export Identity__Client__ClientId='shipping-worker'
export Identity__Client__ClientSecret='local-dev-shipping-secret'
export Identity__Client__Scope='commerce-api'
export Carrier__BaseUrl='http://localhost:5191/'
export Carrier__ApiKey='local-dev-carrier'
export AddressSource__BaseUrl='http://ordering-api:8081'
dotnet run --project src/Services/Shipping/Shipping.Worker
```

with the note the BFF's block already carries one dependency over: the address
leaves the hop pointed at `ordering-api:8081`, which resolves on the Compose
network and nowhere else, so a host-run worker backs every shipment off until
a `hosts` entry maps the name — and a host-run `Ordering.Api` does listen on
8081, because its `appsettings.json` declares both endpoints.

- [ ] **Step 5: Bring the platform up and watch one order ship**

```bash
docker compose -f deploy/compose/docker-compose.yml up --build --wait
```

Place an order through the gateway as the README's own commands do, confirm
it, then read the row and the simulator's journal:

```bash
docker compose -f deploy/compose/docker-compose.yml exec sql sh -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT Status, CarrierReference FROM Shipping.shipping.Shipments"'
curl -s http://localhost:5191/__admin/requests | head -40
```

Expected: one `Booked` row with `crr_SIM-OK`, one booking in the journal
carrying the address and `Idempotency-Key: book:<shipment id>`, and — the
whole point of the postal-code script — the same run with `SIM-REFUSED` in the
address leaving the row `Unfulfillable`. Tear down with `down -v`.

```bash
git add deploy/compose .github/workflows/ci.yml .github/secret-scan/allowed/deploy.txt \
        src/Services/Shipping docs/backend-architecture/15-cicd-deployment.md docs/secrets.md
git commit -m "feat(shipping): the worker's address and client-credential keys, in Compose and the inventory"
```

---

### Task 7: The chapters and the two documents

**Files:**
- Modify: `docs/backend-architecture/03-bounded-contexts.md` — §3.2's Consumes cell
- Modify: `docs/backend-architecture/02-architecture-at-a-glance.md` — §2.2's diagram
- Modify: `docs/backend-architecture/09-messaging.md` — §9.7's sentence
- Modify: `docs/runbooks/latency.md` — the slow-peer branch

- [ ] **Step 1: §3.2's Consumes cell**

Shipping's row becomes:

```markdown
| **Shipping** | Shipment, TrackingEvent | `ShipmentDispatched`, `ShipmentDelivered` | `OrderConfirmed`, `OrderCancelled` | — |
```

Nothing else in that table moves: the Publishes column is already right, and
the two contracts are published by the pull request that promotes a shipment.

- [ ] **Step 2: §2.2's diagram**

Three edges and one node. The carrier joins as its own subgraph, because it is
the first thing in this picture that is not the platform's:

```mermaid
    subgraph External
        CAR[Carrier<br/>simulated in Compose]
    end
```

```mermaid
    BFF -->|gRPC, pricing| CAT

    SHP -->|gRPC, the address read| ORD
    SHP -->|HTTP, book and track| CAR
    SHP -.->|client credentials| IDP
```

The BFF's label loses "the one sync hop", which the second edge makes untrue.
The three bulleted details below the diagram are unchanged and gain no fourth:
§2.3's callout already records ADR-052's departure from Principle 4, and a
second statement of it here would be a copy to correct later.

- [ ] **Step 3: §9.7's sentence**

Replace:

> `Web.Bff`'s pricing hop to Catalog (below) is the platform's one synchronous
> call between its services and Catalog calls nobody, so the deepest chain is
> `Client → Gateway → BFF → Catalog`.

with:

> `Web.Bff`'s pricing hop to Catalog (below) is the platform's one synchronous
> call on a request path, and Catalog calls nobody, so the deepest chain is
> still `Client → Gateway → BFF → Catalog`. There is a second synchronous call
> between services and it is on no request path at all:
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
> has a Shipping worker read a delivery address from Ordering with nothing
> waiting on the answer, so it spends no hop of this budget and §2.3's callout
> records the departure.

Wrap at 80 columns. The paragraph's last sentence about fan-out is unchanged.

- [ ] **Step 4: `docs/runbooks/latency.md`**

Its *A slow peer* branch opens "There is exactly one synchronous hop in this
platform". Replace that sentence with one that keeps the operator's
instruction and corrects the count:

> There is one synchronous hop on a request path — BFF → Catalog for pricing
> ([§9.7](../backend-architecture/09-messaging.md), ADR-017) — and it is the
> only one that can make an HTTP request wait. If the BFF is the service
> alerting, check Catalog's own p99 first, then the resilience handler's
> timeout hierarchy. Shipping's worker calls Ordering for an address
> (ADR-052), and a slow answer there shows up as a shipment that has not
> booked rather than as latency: nothing is waiting on it, and the row backs
> off.

The paragraph below it, "Everything else crosses the broker", keeps its point
and its sentence about a command handler blocking on a message.

- [ ] **Step 5: Run the document checks and commit**

```bash
py -3.12 -m unittest discover -s deploy/observability
py -3.12 deploy/observability/check.py
```

then `/check-links` and `/validate-blueprint`, because three chapters changed.

```bash
git add docs/backend-architecture docs/runbooks/latency.md
git commit -m "docs: §3.2's Consumes cell and §2.2's edges follow Shipping's first work"
```

The body names the second synchronous call, says why it costs no hop of
ADR-017's budget, and points at §2.3's callout as the place that already
records the departure.

---

### Task 8: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings. `TreatWarningsAsErrors` makes
  that the build's own claim, and the one thing to read by eye is that no
  `#pragma` was added anywhere.
- [ ] `dotnet test Platform.slnx` — green, with a Docker daemon running: the
  SQL Server, RabbitMQ and Keycloak suites are `Category=Integration` and are
  never skipped.
- [ ] `py -3.12 -m unittest discover -s deploy/compose/rabbitmq` and
  `py -3.12 deploy/compose/rabbitmq/check_permissions.py` — both exit 0. The
  gate reads `EventsQueue = "shipping-events"` out of the source, so this is
  where a queue outside `shipping-svc`'s grant would be caught.
- [ ] `py -3.12 -m unittest discover -s .github/pipeline-gate`,
  `py -3.12 .github/pipeline-gate/pipeline_gate.py filters` and
  `… images` — all exit 0.
- [ ] `py -3.12 -m unittest discover -s .github/secret-scan` and
  `py -3.12 .github/secret-scan/secret_scan.py` — both exit 0, suite first.
- [ ] `py -3.12 -m unittest discover -s .github/licence-gate` and
  `py -3.12 .github/licence-gate/licence_gate.py` — both exit 0. No pin moved
  and no Appendix B row is owed; the gate is what says so.
- [ ] `git fetch origin main` then
  `py -3.12 .github/comment-gate/comment_gate.py --base origin/main` — exit 0.
- [ ] `docker build -f src/Services/Shipping/Shipping.Worker/Dockerfile .` and
  the same for `Shipping.Migrator` — the two new `COPY` lines are what this
  proves, and Grpc.Tools reporting a missing `.proto` under
  `src/Services/Ordering` is what a missing line looks like.
- [ ] `py -3.12 .github/output-gate/output_gate.py` after a restore and build.
- [ ] `/check-links` and `/validate-blueprint`.
- [ ] PR body: `| Class | A+D+E |` and the touch set from the Global
  Constraints, plus the Compose evidence from Task 6 step 5 — one `Booked` row,
  one booking in the journal under one key — and the sentence that no chart is
  owed until PR-7. Then `/ship`.

## Self-review

**Spec coverage.**

- Section 1, what a cancellation after confirmation does — Shipping consumes
  `OrderCancelled`, no second operator queue opens, and the shipment publishes
  as normal → Tasks 1, 5 and 7.
- Section 3's PR-5 row — `shipping-events` (Task 1), the two consumers (Task
  1), `DeliveryAddresses` (Task 2), the address adapter with `AddressHop`
  (Task 3), the fulfilment worker that reads the address and books (Task 4),
  the void and the carrier cancel (Tasks 4 and 5), §3.2's cell and §2.2's
  edges (Task 7). Class A+D+E, which the locality gate admits today.
- Section 4, where the outbound calls sit — two workers and no consumer (this
  PR brings the first; PR-6 brings the second), the lease under `UPDLOCK,
  READPAST, ROWLOCK`, the `2^min(Attempts, 8) × 5 s` backoff read from
  `OutboxDispatcher`'s own constants, the loop surviving its tick with a filter
  that asks the token, `book:{ShipmentId}` and `cancel:{ShipmentId}`, and the
  two rejected alternatives → Task 4, asserted in Task 5.
- Section 5's state moves — `Pending`→`Booked` (Task 5's booking test),
  `Pending`→`Unfulfillable` (the owner's `NoSuchOrder` and the carrier's
  refusal), `Pending`→`Voided` (cancel then despatch), `Booked`+cancel and the
  carrier's two answers → Tasks 4 and 5. `ReleaseClaim` is Task 4's addition
  to the aggregate, which PR-1 left to the worker that runs it.
- Section 6's two interleavings and the too-late case → Task 5, one test each,
  plus the tombstone at the Application layer in Task 1.
- Section 7 — the `DeliveryAddresses` table, `IDeliveryAddressStore`, the
  `AddDeliveryAddresses` migration, the Kazakh-script round trip through the
  table and on to the simulator, and erasure's `DELETE ... WHERE CustomerId`
  named beside the table → Tasks 2 and 5.
- Section 8 — one `shipping-events` queue binding both contracts, §9.5's
  printed form with the inbox filter outside the in-memory outbox and
  `RetryPolicy.Standard`, no delayed redelivery, the tombstone commute,
  `ShippingIntegrationEventMapper` still empty, and `MessagingRegistrationTests`
  → Task 1, with the binding half in Task 5 for the reason Payments' own
  comment gives: the harness replaces the callback where a binding is declared.
- Section 9 — `IDeliveryAddressSource`, the gRPC adapter with
  `ClientCredentialsHandler` inside the pipeline, `AddressHop` inside §9.7's
  bands, `AddressSourceRefusedException`, `shipping.address.refused`, and the
  `permission` claim held to exactly `orders:delivery-address` → Task 3. The
  §8.1-form table's Ordering row is Task 5's owner-taken-away test and its
  Keycloak row is Task 3's grant-check suite.
- Section 10 — `AddressSource__BaseUrl`, the three `Identity__Client__*` keys,
  and the client secret's Compose and fixture places → Tasks 3 and 6.
- Section 11 — `shipping.address.refused` on the `Shipping.Carrier` meter, and
  no log line holding an address → Tasks 3 and 5. **`shipping.shipments.waiting`
  lands in PR-6, and that is this plan's answer to the question section 11
  leaves open**: the gauge is over rows past their first backoff **by state**,
  and two of the states a row waits in — a booked shipment awaiting its poll,
  and one whose cancellation the carrier has not answered — only become a
  waiting population with PR-6's tracking worker, so a gauge landing here would
  be rewritten there. PR-7 gives the first alert that reads it.
- Section 12 — both consumers against a fake store in both orders (Task 1); the
  mapper registry (Task 1); the owner taken away, the crash between the
  carrier's answer and the commit, two workers overlapping, a pass that throws,
  a lapsed lease, both interleavings and `SIM-LATE`, the Kazakh round trip, the
  log export and the readiness-set assertion (Task 5); the lease inequality
  (Task 4).
- Section 13 — §3.2's Consumes cell and §2.2's diagram → Task 7, with §9.7's
  sentence, `deploy/compose/README.md` and `docs/runbooks/latency.md` beside
  them, which PR-4's *Left to a later PR* assigns here because they describe a
  call no code made until this PR.

**Type consistency.** `IShipmentRepository.GetByOrderAsync/GetAsync/Add`,
`CreateShipmentCommand`, `VoidShipmentCommand`,
`Shipping.Infrastructure.Messaging.DependencyInjection.EventsQueue`,
`IDeliveryAddressStore.SaveAsync/GetAsync`, `AddressLimits`,
`IDeliveryAddressSource.GetAsync`, `AddressLookup.Found(Address, CustomerId)`,
`AddressLookup.NoSuchOrder`, `AddressSourceRefusedException`,
`AddressHop.ClientName` and its five numbers, `AddressMetrics.Refused`,
`GrantCheckedTokenCache`, `DependencyInjection.BaseUrlKey`,
`FulfilmentWork`, `FulfilmentClaims`, `FulfilmentWorker.ClaimBatchSize` and
`.LeaseSeconds` and `.RunOnceAsync`, `Shipment.ReleaseClaim`,
`StubOrdering`/`StubAddress`, and `ShippingWorkerFactory`'s
`addressSourceBaseUrl` and `Tokens` are produced and consumed under those
spellings within this plan. Everything consumed from earlier PRs is spelt as
those plans produce it: `Shipment.For/Book/MarkUnfulfillable/Cancel/
CarrierCancelled/CarrierRefusedCancellation`, `ShipmentId`, `OrderId`,
`ShipmentStatus`, `ICarrierGateway.BookAsync/CancelAsync`, `BookingRequest`,
`BookingResult.Booked/Refused`, `CancellationRequest`,
`CancellationResult.Cancelled/TooLate`, `DeliveryAddress`,
`CarrierHop.FulfilmentTick/TotalRequestTimeout`, `CarrierMetrics.MeterName`,
`SimulatorMappings.Directory()`, `ITokenCache`, `CachingTokenClient.HttpClientName`,
`ClientCredentialsHandler`, `ServiceIdentityOptions.SectionName`,
`AuthorityKeyName`, and PR-4's `GetDeliveryAddressRequest.OrderId` and
`GetDeliveryAddressReply.CustomerId/Line1/Line2/City/PostCode/Country`.

**Deliberately left to a later PR.**

- **The tracking worker, its lease and the monotonic promotion**, the two
  integration events through the outbox, `ShippingJurisdictionOptions`, the
  retention pass over addresses and tracking events, §15.4's "one options type"
  sentence, and the cross-service container test: PR-6. This PR raises no
  domain event and publishes nothing, which is why the mapper's registry is
  asserted empty rather than filled — a test PR-6 deletes, because that is the
  pull request that promotes a shipment. **`FulfilmentClaims` is not
  generalised here and must not be**: PR-6 writes a `TrackingClaims` beside it
  in the same shape, over `NextPollAt` and the pollable population, and the two
  claims are reconciled by shape and by the one `LockedUntil` they share rather
  than by a class parameterised over a `WHERE` clause and a column name.
- **`shipping.shipments.waiting`**: PR-6, argued above.
- **`deploy/helm/shipping`**, the `carrier` and client-credentials
  capabilities, `docs/secrets.md`'s Helm place, the canary row and §13.6's two
  rules: PR-7.
- **§11.7's erasure consumer.** Task 2 designs against it — the customer is
  stored for that statement and nothing else, and a test issues the statement
  by hand — and builds none of it, because that extension is owed whole.
- **Any index on `DeliveryAddresses.CustomerId`**, for the same reason: the
  query it would serve has no code.
- **A second carrier adapter**, and any contract test against a carrier's
  sandbox: there is no carrier.

**Beyond the spec's minimum, with the reason.**

- **The compensating cancel** when a cancellation lands between the carrier's
  answer and the commit. Spec section 6 says a `Pending` shipment "is never
  booked", and section 8 says the consumers never wait — so the window exists
  and the only way to keep the first sentence true is to hand the booking back.
  One branch, one test, and the crash window it cannot close is named in the
  comment rather than left to be discovered.
- **`tests/Shipping.OrderingStub` as a project of its own.** The alternative
  that needs no project is generating both halves into
  `Shipping.Infrastructure`, which is what Ordering and Catalog do on the
  server side; it is refused here because §3.2 gives Shipping no API and an
  abstract gRPC service base in its production assembly is a surface the
  service is defined not to have.

**Where the spec and the tree disagree, and which is wrong.** Spec section 7
says `DeliveryAddresses` "carries `CustomerId`, copied from `OrderConfirmed`".
No code can do that: §7's own `Shipments` column list gives the consumer
nowhere to keep a customer between the confirmation and the address read, and
ADR-052 puts `customer_id` on `GetDeliveryAddressReply` precisely so the reader
has it — PR-4's proto carries the field with that comment. The blueprint wins,
so this plan takes the value from the reply, and the value is the same
customer either way: the reply answers for the order the event confirmed. The
spec's sentence is the one that is wrong, and it is a wording correction rather
than a decision to reopen.
