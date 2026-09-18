# Payments PR-4 — void on cancellation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When `OrderCancelled` meets an authorised intent, void it at the
provider under `void:{OrderId}`, record a `Refund`, and publish
`PaymentRefunded` once; and give Payments the outbox gauges every
dispatcher-hosting service owes, so PR-1's exemption can go.

**Architecture:** PR-1's `RecordOrderCancelledHandler` gains the void. It
stamps the record first — the stamp's `UPDLOCK, SERIALIZABLE` is the lock
that serialises it against `AuthorisePayment`'s — then reads the intent, and
only an `Authorised` intent with no refund calls the provider. The `Refund` is
the one tracked aggregate, so §6.3's check holds. The outbox gauges are
Ordering's four files under Payments' namespace.

**Tech Stack:** EF Core, the PR-2 provider port, MassTransit, WireMock.Net in
process, `System.Diagnostics.Metrics`.

**Spec:** `docs/superpowers/specs/2026-09-18-payments-service-design.md`,
sections 5 (`Refund`), 6 (the `OrderCancelled` table and the race), 7
(`Refunds`), 12 (the outbox gauges) and 13 (the race test).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Payments/**`, `tests/Payments.*`,
  `Payments.Infrastructure.csproj` (E: the package the copied `OutboxStats`
  needs, no `Version=`), `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`
  and `tests/Common.Web.Tests/ObservabilityTests.cs` (B: one `AddMeter`
  line, inside A's `src/BuildingBlocks/**`), `deploy/observability/check.py`
  (D: the exemption deleted).
- **Three classes, which the locality gate does not yet admit.** A service's
  arrival spans its code (A), its projects (E) and its deployment or harness
  tree (D); `docs/change-locality.md` names at most two and
  `.github/locality-gate` refuses a third letter. This PR cannot merge until
  the contract and the gate admit that case — a Class D change of its own,
  owed before Payments' PR-1, and met first by Inventory's plans, which
  declare the same shape.
- Depends on PR-3 having merged.
- `PaymentRefunded` is published only when the provider voided money. A
  cancellation of an order with no authorised intent publishes nothing
  (ADR-047).
- The provider is called only after the record's stamp has taken its lock.
- Every step that adds behaviour writes its test first; container tests are
  never skipped.

---

### Task 1: `Refund` and its event

**Files:**
- Create: `src/Services/Payments/Payments.Domain/Refunds/Refund.cs`
- Create: `src/Services/Payments/Payments.Domain/Refunds/Events/PaymentRefundedDomainEvent.cs`
- Create: `src/Services/Payments/Payments.Domain/Refunds/IRefundRepository.cs`
- Test: `tests/Payments.Domain.Tests/RefundTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Payments.Domain.Refunds;

public sealed class Refund : AggregateRoot<OrderId>
{
    public string Reference { get; }
    public decimal Amount { get; }
    public string Currency { get; }
    public DateTimeOffset VoidedAt { get; }

    public static Refund Voided(PaymentIntent authorised, DateTimeOffset now);
}

public sealed record PaymentRefundedDomainEvent(OrderId OrderId, string Reference, decimal Amount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

public interface IRefundRepository
{
    Task<bool> ExistsAsync(OrderId id, CancellationToken ct);
    void Add(Refund refund);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using Common.Domain;
using Payments.Domain.Intents;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;
using Payments.Domain.Refunds.Events;
using Shouldly;
using Xunit;

namespace Payments.Domain.Tests;

public class RefundTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_void_of_an_authorised_intent_carries_its_reference_and_money_and_raises_the_refund()
    {
        OrderId order = OrderId.New();
        PaymentIntent intent = PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-5));

        Refund refund = Refund.Voided(intent, Now);

        refund.Id.ShouldBe(order);
        refund.Reference.ShouldBe("psp_1");
        refund.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new PaymentRefundedDomainEvent(order, "psp_1", 42.10m, "EUR", Now));
    }

    [Fact]
    public void A_declined_intent_has_nothing_to_refund()
    {
        PaymentIntent intent = PaymentIntent.Decline(OrderId.New(), 42.10m, "EUR", "card_declined", Now);

        Should.Throw<DomainException>(() => Refund.Voided(intent, Now),
            "ADR-047: PaymentRefunded means money moved back");
    }
}
```

- [ ] **Step 2: Run to see them fail; write the domain**

```csharp
using Common.Domain;
using Payments.Domain.Intents;
using Payments.Domain.Orders;
using Payments.Domain.Refunds.Events;

namespace Payments.Domain.Refunds;

/// <summary>
/// §3.2's <c>Refund</c>: money voided back for a cancelled order. Created only
/// from an authorised intent, because the event it raises reports an act and
/// not a postcondition (ADR-047).
/// </summary>
public sealed class Refund : AggregateRoot<OrderId>
{
    public string Reference { get; private set; } = "";
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public DateTimeOffset VoidedAt { get; private set; }

    private Refund() { }

    public static Refund Voided(PaymentIntent authorised, DateTimeOffset now)
    {
        if (authorised.Status != PaymentIntentStatus.Authorised || authorised.Reference is null)
            throw new DomainException("Only an authorised payment can be refunded.");

        Refund refund = new()
        {
            Id = authorised.Id,
            Reference = authorised.Reference,
            Amount = authorised.Amount,
            Currency = authorised.Currency,
            VoidedAt = now
        };
        refund.Raise(new PaymentRefundedDomainEvent(refund.Id, refund.Reference, refund.Amount, refund.Currency, now));
        return refund;
    }
}
```

`Entity<TId>.Id` has a protected setter, so `Refund`'s own factory may set it
in the initialiser.

- [ ] **Step 3: Run; commit**

```bash
dotnet test tests/Payments.Domain.Tests
git add src/Services/Payments/Payments.Domain tests/Payments.Domain.Tests
git commit -m "feat(payments): Refund, created only from an authorised intent"
```

---

### Task 2: Persistence and the third mapping

**Files:**
- Create: `Payments.Infrastructure/Persistence/RefundConfiguration.cs`
- Create: `Payments.Infrastructure/Persistence/RefundRepository.cs`
- Modify: `PaymentsDbContext.cs` (`DbSet<Refund> Refunds`), `DependencyInjection.cs`
  (register the repository)
- Modify: `Payments.Application/Integration/PaymentsIntegrationEventMapper.cs`
  (the third entry)
- Create (generated): `<ts>_AddRefunds.cs`
- Test: `tests/Payments.Application.Tests/PaymentsIntegrationEventMapperTests.cs` (extend)
- Test: `tests/Payments.Application.Tests/OutboxSerialisationTests.cs` (the
  third sample; the stageable set becomes the three types)
- Test: `tests/Payments.Api.Tests/DatabaseSmokeTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_refund_becomes_PaymentRefunded_correlated_on_the_order()
{
    OrderId order = OrderId.New();

    PaymentRefunded contract = Mapper()
        .Map([new PaymentRefundedDomainEvent(order, "psp_1", 42.10m, "EUR", Raised)])
        .ShouldHaveSingleItem().ShouldBeOfType<PaymentRefunded>();

    contract.OrderId.ShouldBe(order.Value);
    contract.CorrelationId.ShouldBe(order.Value);
    contract.Reference.ShouldBe("psp_1");
    contract.Amount.ShouldBe(42.10m);
    contract.Currency.ShouldBe("EUR");
}

[Fact]
public void The_registry_is_the_publishes_column_and_exactly_it()
{
    PaymentsIntegrationEventMapper.RegisteredEvents.ShouldBe(
        [typeof(PaymentAuthorisedDomainEvent), typeof(PaymentDeclinedDomainEvent), typeof(PaymentRefundedDomainEvent)],
        ignoreOrder: true,
        "§3.2's Publishes column, one domain event per contract");
}
```

`RegisteredEvents` is `public static IReadOnlyCollection<Type> RegisteredEvents
=> Registry.Keys;`, added to the mapper by this task. Each key maps to one
contract, which the three mapping tests above pin, so the keys are the column.

Smoke: `payments.Refunds` exists with `Reference`, `Amount`, `Currency`,
`VoidedAt` and no `RowVersion` — the record's lock is what serialises its
writes.

- [ ] **Step 2: Write the configuration, repository and entry**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Application.Provider;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;

namespace Payments.Infrastructure.Persistence;

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("Refunds", "payments");

        builder.HasKey(r => r.Id);
        builder
            .Property(r => r.Id)
            .HasColumnName("OrderId")
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();

        // The intent's width, from the same constant: a refund carries the
        // intent's reference, so the two columns cannot be allowed to differ.
        builder.Property(r => r.Reference).HasMaxLength(ProviderLimits.MaxReferenceLength).IsRequired();
        builder.Property(r => r.Amount).HasPrecision(18, 2);
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(r => r.VoidedAt).IsRequired();

        // No rowversion: one refund per order, inserted once, never updated,
        // and the order record's lock serialises the insert (spec, section 6).
        builder.Ignore(r => r.Version);
        builder.Ignore(r => r.DomainEvents);
    }
}
```

```csharp
internal sealed class RefundRepository(PaymentsDbContext db) : IRefundRepository
{
    public Task<bool> ExistsAsync(OrderId id, CancellationToken ct) => db.Refunds.AnyAsync(r => r.Id == id, ct);

    public void Add(Refund refund) => db.Add(refund);
}
```

The registry's third entry, in the shape of the other two:

```csharp
[typeof(PaymentRefundedDomainEvent)] = e => ToContract((PaymentRefundedDomainEvent)e)

private static PaymentRefunded ToContract(PaymentRefundedDomainEvent e) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = e.OrderId.Value,
    OccurredAt = e.OccurredAt,
    OrderId = e.OrderId.Value,
    Reference = e.Reference,
    Amount = e.Amount,
    Currency = e.Currency
};
```

Generate `AddRefunds` with `dotnet ef migrations add` as PR-3 did and confirm
it creates exactly `payments.Refunds`.

- [ ] **Step 3: Run; commit**

```bash
dotnet test tests/Payments.Application.Tests tests/Payments.Api.Tests
git add src/Services/Payments tests/Payments.*
git commit -m "feat(payments): map Refunds and translate PaymentRefunded"
```

---

### Task 3: The void on `OrderCancelled`

**Files:**
- Modify: `Payments.Application/Orders/RecordOrderCancelled/RecordOrderCancelledHandler.cs`
- Modify: `.../RecordOrderCancelled/OrderCancelledHandler.cs` (summary only)
- Test: `tests/Payments.Application.Tests/RecordOrderCancelledHandlerTests.cs`

**Interfaces:**
- Consumes: `IPaymentOrderStore.RecordCancelledAsync`, `IPaymentIntentRepository`,
  `IRefundRepository`, `IPaymentProvider.VoidAsync`, `Refund.Voided`.

- [ ] **Step 1: Write the failing handler tests**

With PR-3's fakes (copied into this file, not shared), plus `FakeRefunds :
IRefundRepository` and a `FakeProvider.Voids` list:

```csharp
public class RecordOrderCancelledHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeOrderStore _orders = new();
    private readonly FakeIntents _intents = new();
    private readonly FakeRefunds _refunds = new();
    private readonly FakeProvider _provider = new();

    private RecordOrderCancelledHandler Handler() => new(_orders, _intents, _refunds, _provider, new FixedClock(Now));

    [Fact]
    public async Task An_authorised_intent_is_voided_under_the_void_key_and_refunded()
    {
        OrderId order = OrderId.New();
        _intents.Seed(PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1)));

        await Handler().HandleAsync(new RecordOrderCancelledCommand(order.Value, Now), default);

        _orders.Cancelled.ShouldBe([(order, Now)]);
        _provider.Voids.ShouldHaveSingleItem().ShouldBe(new VoidRequest(order, "psp_1"));
        _refunds.Added.ShouldHaveSingleItem().Reference.ShouldBe("psp_1");
    }

    [Fact]
    public async Task An_intent_already_refunded_is_not_voided_twice()
    {
        OrderId order = OrderId.New();
        _intents.Seed(PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1)));
        _refunds.Existing.Add(order);

        await Handler().HandleAsync(new RecordOrderCancelledCommand(order.Value, Now), default);

        _provider.Voids.ShouldBeEmpty();
        _refunds.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_declined_intent_is_stamped_and_nothing_is_voided()
    {
        OrderId order = OrderId.New();
        _intents.Seed(PaymentIntent.Decline(order, 42.10m, "EUR", "card_declined", Now));

        await Handler().HandleAsync(new RecordOrderCancelledCommand(order.Value, Now), default);

        _orders.Cancelled.ShouldHaveSingleItem();
        _provider.Voids.ShouldBeEmpty();
        _refunds.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_intent_is_stamped_and_nothing_is_voided()
    {
        OrderId order = OrderId.New();

        await Handler().HandleAsync(new RecordOrderCancelledCommand(order.Value, Now), default);

        _orders.Cancelled.ShouldHaveSingleItem();
        _provider.Voids.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_stamp_is_taken_before_the_intent_is_read()
    {
        OrderId order = OrderId.New();

        await Handler().HandleAsync(new RecordOrderCancelledCommand(order.Value, Now), default);

        _intents.GetCallsBeforeStamp.ShouldBe(0, "the stamp's lock is what serialises this against AuthorisePayment");
    }

    [Fact]
    public async Task A_provider_that_cannot_void_throws_and_records_no_refund()
    {
        OrderId order = OrderId.New();
        _intents.Seed(PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1)));
        _provider.VoidFault = new PaymentProviderUnavailableException("down");

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Handler().HandleAsync(new RecordOrderCancelledCommand(order.Value, Now), default));
        _refunds.Added.ShouldBeEmpty("the unit rolls back and §9.8 retries it whole");
    }
}
```

`FakeOrderStore.Cancelled` records `(OrderId, DateTimeOffset)` pairs;
`FakeIntents.GetCallsBeforeStamp` counts `GetAsync` calls while the store's
`Cancelled` list is empty.

- [ ] **Step 2: Run to see them fail; extend the handler**

```csharp
using Common.Application;
using Payments.Application.Provider;
using Payments.Domain.Intents;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;

namespace Payments.Application.Orders.RecordOrderCancelled;

/// <summary>
/// Records the cancellation and voids an authorisation it finds (§3.2, §9.6).
/// </summary>
/// <remarks>
/// The stamp goes first, and its lock is held to commit: an
/// <c>AuthorisePayment</c> for the same order locks the same row, so whichever
/// commits second sees the first's work — a decline with nothing to void, or
/// an authorisation this voids (spec, section 6; ADR-047).
/// </remarks>
public sealed class RecordOrderCancelledHandler(
    IPaymentOrderStore orders,
    IPaymentIntentRepository intents,
    IRefundRepository refunds,
    IPaymentProvider provider,
    TimeProvider clock)
    : ICommandHandler<RecordOrderCancelledCommand, Result>
{
    public async Task<Result> HandleAsync(RecordOrderCancelledCommand command, CancellationToken ct)
    {
        OrderId order = new(command.OrderId);

        await orders.RecordCancelledAsync(order, command.CancelledAt, ct);

        PaymentIntent? intent = await intents.GetAsync(order, ct);
        if (intent is not { Status: PaymentIntentStatus.Authorised } || await refunds.ExistsAsync(order, ct))
            return Result.Success();

        // The void key makes a retry of this whole unit a replay at the
        // provider rather than a second void (spec, section 4).
        await provider.VoidAsync(new VoidRequest(order, intent.Reference!), ct);
        refunds.Add(Refund.Voided(intent, clock.GetUtcNow()));

        return Result.Success();
    }
}
```

`OrderCancelledHandler`'s summary gains: "and voids an authorisation taken
for it (§9.6)".

- [ ] **Step 3: Run; commit**

```bash
dotnet test tests/Payments.Application.Tests
git add src/Services/Payments/Payments.Application tests/Payments.Application.Tests
git commit -m "feat(payments): a cancellation voids an authorised payment and records the refund"
```

---

### Task 4: The void over containers, and the race

**Files:**
- Modify: `src/Services/Payments/Payments.Infrastructure/Messaging/DependencyInjection.cs`
  — `payments-events` takes `RetryPolicy.Standard` bare since PR-1, when
  neither of its handlers could raise a terminal fault; the void can, so the
  endpoint's retry becomes
  `e.UseMessageRetry(r => { r.Ignore<PaymentMismatchException>(); RetryPolicy.Standard(r); });`
  with the comment "A provider's 409 on the void key is terminal: the same key
  and different figures is a defect no retry fixes (§9.8)."
- Test: `tests/Payments.Api.Tests/VoidOnCancellationTests.cs`

The helpers are PR-3's command-endpoint helpers, copied: `SendAsync`,
`PublishAsync`, `Placed`, `Cancelled`, `StatusAsync`, `StagedAsync`,
`Eventually`, and:

```csharp
private int VoidCalls() =>
    fixture.Provider.LogEntries.Count(e => e.RequestMessage.Path.EndsWith("/void", StringComparison.Ordinal));

private int AuthoriseCalls() =>
    fixture.Provider.LogEntries.Count(e => e.RequestMessage.Path == "/v1/authorisations");

private Task<int> RefundCount(Guid order) =>
    fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.Refunds WHERE OrderId = {0}", order);
```

- [ ] **Step 1: Write the tests**

```csharp
[Fact]
public async Task A_cancellation_after_an_authorisation_voids_it_once_and_publishes_PaymentRefunded()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));
    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

    await PublishAsync(Cancelled(order));

    (await RefundCount(order)).ShouldBe(1);
    VoidCalls().ShouldBe(1);
    (await StagedAsync("PaymentRefunded")).ShouldBe(1);
}

[Fact]
public async Task A_second_cancellation_under_a_fresh_id_refunds_nothing_more()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));
    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));
    await PublishAsync(Cancelled(order));

    await PublishAsync(Cancelled(order));

    (await RefundCount(order)).ShouldBe(1);
    VoidCalls().ShouldBe(1);
    (await StagedAsync("PaymentRefunded")).ShouldBe(1, "the refund was published; a repeat is not a second refund");
}

[Fact]
public async Task A_cancellation_of_a_declined_payment_publishes_nothing()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 10.01m));
    await SendAsync(new AuthorisePayment(order, 10.01m, "EUR"));

    await PublishAsync(Cancelled(order));

    (await RefundCount(order)).ShouldBe(0);
    VoidCalls().ShouldBe(0);
    (await StagedAsync("PaymentRefunded")).ShouldBe(0, "ADR-047: PaymentRefunded means money moved back");
}

[Fact]
public async Task A_cancellation_arriving_mid_authorisation_waits_for_it_and_then_voids_it()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));
    Guid command = Guid.CreateVersion7();
    OrderCancelled cancelled = Cancelled(order);
    using ProviderGate gate = fixture.PauseNextAuthorisation();

    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"), drain: false, messageId: command);
    await gate.Reached.WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken);
    await PublishAsync(cancelled, drain: false);
    await fixture.Orders.Stamping(order).WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken);
    await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

    (await fixture.InboxAsync(cancelled.MessageId)).ShouldBeEmpty(
        "the cancellation entered its stamp and is waiting on the record lock the paused authorisation holds");

    gate.Release();
    await Eventually(
        async () => (await fixture.InboxAsync(command)).Count + (await fixture.InboxAsync(cancelled.MessageId)).Count,
        expected: 2,
        because: "both deliveries are consumed, the cancellation after the authorisation commits");

    (await StatusAsync(order)).ShouldBe("Authorised");
    (await RefundCount(order)).ShouldBe(1, "the cancellation saw the committed authorisation and voided it");
    AuthoriseCalls().ShouldBe(1);
    VoidCalls().ShouldBe(1);
}
```

The gate makes the interleaving the test's rather than the scheduler's, and
`Stamping` proves the cancellation arrived inside it — the one-second hold
after that is a wait on a consumer known to be blocked, not a guess that it
started: the authorisation is held after the provider answered and before
its unit commits, which is the window an unlocked read would let the cancellation
through — it would find no intent, void nothing, and leave money held on a
cancelled order. The other order, the cancellation committing first, is
PR-3's `order_cancelled` test.

`ServiceFixture.PauseNextAuthorisation()` is this task's: a test-only
`IPaymentProvider` decorator the fixture's factory registers through the
same `ConfigureTestServices` hook as PR-3's two seams. Armed, the next
`AuthoriseAsync` awaits the inner call, completes `ProviderGate.Reached`, then waits on the
gate until `Release()` or disposal. `ProviderGate` exposes `Task Reached` and
`void Release()`, and disposing it releases, so a failing test cannot leave
a consumer parked.

The void has the authorisation's hazard: money moves at the provider inside
a unit that may still roll back. So it gets the same test, with PR-3's
`FailNextCommit()` armed for the cancellation's unit only:

```csharp
[Fact]
public async Task A_void_whose_commit_fails_is_replayed_under_the_same_key_and_refunds_once()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));
    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));
    using CommitFault fault = fixture.FailNextCommit();

    await PublishAsync(Cancelled(order));

    fault.Fired.ShouldBeTrue("the first unit voided and staged the refund before its commit failed");
    (await RefundCount(order)).ShouldBe(1);
    (await StagedAsync("PaymentRefunded")).ShouldBe(1, "the rolled-back unit's refund event went with it");
    VoidCalls().ShouldBe(2, "the retry replayed the void rather than skipping it");
    fixture.Provider.LogEntries
        .Where(e => e.RequestMessage.Path.EndsWith("/void", StringComparison.Ordinal))
        .Select(e => e.RequestMessage.Headers!["Idempotency-Key"].Single())
        .Distinct().ShouldHaveSingleItem().ShouldBe($"void:{order}");
}
```

And the void's terminal fault reaches the error queue on one attempt:

```csharp
[Fact]
public async Task A_void_the_provider_refuses_as_a_mismatch_is_not_retried()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));
    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));
    fixture.Provider.Given(Request.Create().WithPath("/v1/authorisations/*/void").UsingPost())
        .AtPriority(0)
        .RespondWith(Response.Create().WithStatusCode(409));

    await PublishAsync(Cancelled(order), drain: false);

    await Eventually(
        () => fixture.QueueDepthAsync($"{MessagingRegistration.EventsQueue}_error"),
        expected: 1,
        because: "a 409 on the void key is excluded from retry and faults straight to the error queue");
    VoidCalls().ShouldBe(1, "one consumer attempt, and the provider was asked once");
    (await RefundCount(order)).ShouldBe(0);
}
```

The file's usings gain `WireMock.RequestBuilders` and
`WireMock.ResponseBuilders`.

- [ ] **Step 2: Run; commit**

```bash
dotnet test tests/Payments.Api.Tests --filter VoidOnCancellationTests
git add src/Services/Payments/Payments.Infrastructure tests/Payments.Api.Tests
git commit -m "test(payments): the void over the broker, and the race that must never leave money held"
```

---

### Task 5: The outbox gauges and the provider counter at startup

**Files:**
- Create: `src/Services/Payments/Payments.Infrastructure/Observability/OutboxMetrics.cs`,
  `OutboxStats.cs`, `IOutboxStats.cs`, `MetricsInitialiser.cs` — Ordering's
  four files under Payments' namespace, the meter renamed `Payments.Outbox`,
  and `MetricsInitialiser` taking `ProviderMetrics` as well, so the counter
  exists at startup and a quiet service reports zero rather than nothing
  (§13.6)
- Modify: `Payments.Infrastructure/DependencyInjection.cs` — the stats,
  metrics and initialiser registrations exactly as Ordering's, including
  `IOutboxStats` over a `SqlConnectionFactory` on the `Payments` connection
  string with `OutboxStats.ConnectTimeoutSeconds`
- Modify: `Payments.Infrastructure.csproj` —
  `<PackageReference Include="Microsoft.Extensions.Caching.Memory" />`, which
  the copied `OutboxStats` needs, as Ordering's project declares it
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` —
  `.AddMeter("Payments.Outbox")  // §13.6 per-lane` beside `Payments.Provider`
- Modify: `tests/Common.Web.Tests/ObservabilityTests.cs` — the name joins the
  list; write it first and see it fail
- Modify: `deploy/observability/check.py` — delete the `"Payments"` entry in
  `OUTBOX_METRICS_EXEMPT`
- Test: `tests/Payments.Api.Tests/OutboxStatsTests.cs` and
  `MetricsRegistrationTests.cs` — Ordering's, with the namespace and meter
  names changed

- [ ] **Step 1: Write the failing tests**

Copy Ordering's two test files and rename. `MetricsRegistrationTests`
collects every registered service type whose name ends in `Metrics` from both
`AddPaymentsApplication()` and `AddPaymentsInfrastructure(configuration)`, and
asserts each is a parameter of `MetricsInitialiser`'s constructor or has a
reason in `NotForced`. `ProviderMetrics`, registered since PR-2, is therefore
caught with no test of its own: the copied test fails until the initialiser
takes it, which is the startup guarantee §13.6 asks for. Its
`BuildServices()` helper needs `PaymentProvider:BaseUrl` in the in-memory
configuration beside the connection strings, because `AddPaymentProvider`
reads it eagerly.

Add `"Payments.Outbox"` to `ObservabilityTests`' list. Run both and see them
fail.

- [ ] **Step 2: Copy, rename, register, delete the exemption**

Copy the four files from `src/Services/Ordering/Ordering.Infrastructure/Observability/`,
change the namespace to `Payments.Infrastructure.Observability`, the meter to
`Payments.Outbox`, and the schema-bound statements to `payments`. Add
`ProviderMetrics provider` to `MetricsInitialiser`'s constructor beside the
outbox metrics, with `ArgumentNullException.ThrowIfNull(provider);` beside
the copied guards — an unread parameter is CS9113, an error under ADR-019,
which is why the copied constructor guards every one. Register as Ordering
does. Add the `AddMeter` line. Delete
the exemption, then:

```bash
py -3.12 deploy/observability/check.py
```

Expected: exit 0 with Payments instrumented. The four outbox alerts group by
service name and read these gauges from now on.

- [ ] **Step 3: Run; commit**

```bash
dotnet test tests/Payments.Api.Tests tests/Common.Web.Tests
git add src/Services/Payments tests/Payments.Api.Tests src/BuildingBlocks/Common.Web tests/Common.Web.Tests deploy/observability/check.py
git commit -m "feat(payments): outbox gauges and the metrics initialiser, and the exemption goes"
```

---

### Task 6: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green.
- [ ] `py -3.12 deploy/observability/check.py` — exit 0.
- [ ] Under Compose, place an order, cancel it, and read
  `payments.Refunds` and the simulator's journal at
  `http://localhost:5190/__admin/requests`: one authorisation, one void.
- [ ] Neither `/validate-blueprint` nor `/check-links` is owed: no chapter
  moved.
- [ ] PR body: `| Class | A+D+E |`, touch set from the Global Constraints.
  Then `/ship`.

## Self-review

- Spec coverage: section 5's `Refund` → Task 1; section 6's `OrderCancelled`
  table → Task 3, a test per row, and Task 4 over containers; the race →
  Task 4's last test; section 7's `Refunds` and `AddRefunds` → Task 2;
  section 12's gauges, the provider counter at startup and the exemption →
  Task 5.
- Types: `Refund.Voided`, `PaymentRefundedDomainEvent`, `IRefundRepository.
  ExistsAsync/Add`, the extended `RecordOrderCancelledHandler` constructor and
  `ProviderMetrics.MeterName` agree with PR-2 and PR-3; PR-5's query reads
  `payments.Refunds` by these column names.
