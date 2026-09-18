# Payments PR-3 — authorise — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer `AuthorisePayment` on `payments-commands` with
`PaymentAuthorised` or `PaymentDeclined` through the outbox: charged to the
payer Payments' own record names, refused as a fault on a mismatch, answered
`order_cancelled` for a cancelled order (ADR-047), and redelivered on a delay
while the record has not arrived.

**Architecture:** `PaymentIntent` is created in a terminal state inside one
unit of work that locks the order record, calls the provider under
`authorise:{OrderId}` and stages the event; a retry of the whole unit is a
replay at the provider. The endpoint layers §9.8's retry under a delayed
redelivery scoped to one exception, whose ladder reaches §9.6's payment
timeout — asserted across the two services in `Platform.IntegrationTests`.

**Tech Stack:** EF Core, MassTransit `CommandConsumer<,>`,
`UseDelayedRedelivery` over ADR-021's delayed exchange, WireMock.Net in process,
xUnit with Shouldly and Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-18-payments-service-design.md`,
sections 2 (ADR-047), 4, 5 (`PaymentIntent`), 6 (the `AuthorisePayment`
table), 7 (`PaymentIntents`), 8 (`payments-commands` and the ladder) and 14.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class C+E.** C because the PR adds ADR-047 (`docs/change-locality.md`
  §3). Touch set: `src/Services/Payments/**`, `tests/Payments.*`,
  `src/BuildingBlocks/Common.Contracts/Payments/V1/PaymentEvents.cs` (the
  `Reason` remark ADR-047 makes false),
  `tests/Platform.IntegrationTests/**` and its `*.csproj` (E: two project
  references), `docs/backend-architecture/03-bounded-contexts.md` (one
  sentence), `docs/backend-architecture/adr/ADR-047-*.md` and
  `docs/backend-architecture/appendix-a-*.md` (its row).
- Depends on PR-2 having merged.
- No new package, no `Directory.Packages.props` change.
- The handler reads the record under `IPaymentOrderStore.LockAsync` before
  anything else, and holds that lock across the provider call (spec, section
  6). Nothing reads the record unlocked on this path.
- The payer is `PaymentOrderRecord.CustomerId`. No code path reads a payer
  from the command, which has none (ADR-028).
- `PaymentDeclined.Reason` is `order_cancelled` for ADR-047's refusal and the
  provider's code otherwise, and nothing branches on it.
- Every step that adds behaviour writes its test first; container tests are
  never skipped.

---

### Task 1: `PaymentIntent` and its events

**Files:**
- Create: `src/Services/Payments/Payments.Domain/Intents/PaymentIntentStatus.cs`
- Create: `src/Services/Payments/Payments.Domain/Intents/PaymentIntent.cs`
- Create: `src/Services/Payments/Payments.Domain/Intents/Events/PaymentIntentEvents.cs`
- Create: `src/Services/Payments/Payments.Domain/Intents/IPaymentIntentRepository.cs`
- Create: `src/Services/Payments/Payments.Domain/Intents/DeclineReasons.cs`
- Delete: `src/Services/Payments/Payments.Domain/AssemblyMarker.cs`
- Modify: every file the render left naming `typeof(AssemblyMarker)` — the
  scaffold anchors `MessageTypeSource` in `Payments.Infrastructure/DependencyInjection.cs`,
  and tests in `Payments.Domain.Tests`, `Payments.Application.Tests` and
  `Payments.Api.Tests`, on it; each becomes `typeof(PaymentIntent)`, which
  lives in the same assembly, so what each anchors is unchanged
- Test: `tests/Payments.Domain.Tests/PaymentIntentTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Payments.Domain.Intents;

public enum PaymentIntentStatus { Authorised, Declined }

public static class DeclineReasons
{
    public const string OrderCancelled = "order_cancelled";
}

public sealed class PaymentIntent : AggregateRoot<OrderId>
{
    public PaymentIntentStatus Status { get; }
    public decimal Amount { get; }
    public string Currency { get; }
    public string? Reference { get; }
    public string? DeclineReason { get; }
    public DateTimeOffset CreatedAt { get; }

    public static PaymentIntent Authorise(OrderId id, decimal amount, string currency, string reference, DateTimeOffset now);
    public static PaymentIntent Decline(OrderId id, decimal amount, string currency, string reason, DateTimeOffset now);
}

// Events
public sealed record PaymentAuthorisedDomainEvent(OrderId OrderId, string Reference, decimal Amount, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record PaymentDeclinedDomainEvent(OrderId OrderId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public interface IPaymentIntentRepository
{
    Task<PaymentIntent?> GetAsync(OrderId id, CancellationToken ct);
    void Add(PaymentIntent intent);
}
```

- [ ] **Step 1: Write the failing domain tests**

```csharp
using Common.Domain;
using Payments.Domain.Intents;
using Payments.Domain.Intents.Events;
using Payments.Domain.Orders;
using Shouldly;
using Xunit;

namespace Payments.Domain.Tests;

public class PaymentIntentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Authorise_records_the_reference_and_raises_the_authorisation()
    {
        OrderId order = OrderId.New();

        PaymentIntent intent = PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now);

        intent.Status.ShouldBe(PaymentIntentStatus.Authorised);
        intent.Reference.ShouldBe("psp_1");
        intent.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new PaymentAuthorisedDomainEvent(order, "psp_1", 42.10m, "EUR", Now));
    }

    [Fact]
    public void Decline_records_the_reason_and_raises_the_decline()
    {
        OrderId order = OrderId.New();

        PaymentIntent intent = PaymentIntent.Decline(order, 42.10m, "EUR", "card_declined", Now);

        intent.Status.ShouldBe(PaymentIntentStatus.Declined);
        intent.Reference.ShouldBeNull();
        intent.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new PaymentDeclinedDomainEvent(order, "card_declined", Now));
    }

    [Fact]
    public void An_authorisation_needs_a_reference_and_a_decline_a_reason()
    {
        Should.Throw<DomainException>(() => PaymentIntent.Authorise(OrderId.New(), 1m, "EUR", " ", Now));
        Should.Throw<DomainException>(() => PaymentIntent.Decline(OrderId.New(), 1m, "EUR", "", Now));
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Payments.Domain.Tests --filter PaymentIntentTests`
Expected: compile failure.

- [ ] **Step 3: Write the domain**

```csharp
using Common.Domain;
using Payments.Domain.Intents.Events;
using Payments.Domain.Orders;

namespace Payments.Domain.Intents;

/// <summary>
/// §3.2's <c>PaymentIntent</c>: the provider's verdict on one order. Created in
/// a terminal state, because the provider answers inside the unit that creates
/// it (spec, section 4); there is no pending row.
/// </summary>
public sealed class PaymentIntent : AggregateRoot<OrderId>
{
    public PaymentIntentStatus Status { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public string? Reference { get; private set; }
    public string? DeclineReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private PaymentIntent() { }

    private PaymentIntent(OrderId id, PaymentIntentStatus status, decimal amount, string currency, DateTimeOffset now)
    {
        Id = id;
        Status = status;
        Amount = amount;
        Currency = currency;
        CreatedAt = now;
    }

    public static PaymentIntent Authorise(OrderId id, decimal amount, string currency, string reference, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new DomainException("An authorisation needs the provider's reference.");

        PaymentIntent intent = new(id, PaymentIntentStatus.Authorised, amount, currency, now) { Reference = reference };
        intent.Raise(new PaymentAuthorisedDomainEvent(id, reference, amount, currency, now));
        return intent;
    }

    public static PaymentIntent Decline(OrderId id, decimal amount, string currency, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("A decline needs a reason.");

        PaymentIntent intent = new(id, PaymentIntentStatus.Declined, amount, currency, now) { DeclineReason = reason };
        intent.Raise(new PaymentDeclinedDomainEvent(id, reason, now));
        return intent;
    }
}
```

`PaymentIntentStatus.cs`, `DeclineReasons.cs` (its summary: "ADR-047's one
reason of Payments' own; every other decline carries the provider's code"),
the two events and the repository interface as in Interfaces. Delete
`AssemblyMarker.cs`, then
`grep -rn "AssemblyMarker" src/Services/Payments tests/Payments.*` and
replace every hit with `typeof(PaymentIntent)`; the build refuses a missed
one, so a green build is the proof the list was whole.

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Payments.Domain.Tests
dotnet test tests/Payments.Application.Tests --filter ArchitectureTests
git add src/Services/Payments tests/Payments.*
git commit -m "feat(payments): PaymentIntent, created in the provider's verdict"
```

---

### Task 2: Persistence and the outbox mapping

**Files:**
- Create: `Payments.Infrastructure/Persistence/PaymentIntentConfiguration.cs`
- Create: `Payments.Infrastructure/Persistence/PaymentIntentRepository.cs`
- Modify: `Payments.Infrastructure/Persistence/PaymentsDbContext.cs`
  (`DbSet<PaymentIntent> PaymentIntents`)
- Modify: `Payments.Infrastructure/DependencyInjection.cs` (register the
  repository; `MessageTypeSource` already names the Domain assembly through
  `typeof(PaymentIntent)` since Task 1)
- Modify: `Payments.Application/Integration/PaymentsIntegrationEventMapper.cs`
  (the registry's first two entries)
- Create (generated): `<ts>_AddPaymentIntents.cs`
- Test: `tests/Payments.Api.Tests/DatabaseSmokeTests.cs` (extend)
- Test: `tests/Payments.Application.Tests/PaymentsIntegrationEventMapperTests.cs`
- Test: `tests/Payments.Application.Tests/OutboxSerialisationTests.cs` — the
  scaffold omits it because a rendered service raises no event; this is the
  first, so it comes back in Ordering's shape, with a
  `DomainEventSamples.Create(Type)` entry per event and the stageable set
  asserted as exactly the two types (PR-4 adds the third)

**Interfaces:**
- Produces: table `payments.PaymentIntents(OrderId uniqueidentifier PK, Status
  nvarchar(16), Amount decimal(19,4), Currency char(3), Reference
  nvarchar(100) NULL, DeclineReason nvarchar(100) NULL, CreatedAt
  datetimeoffset, RowVersion rowversion)`; the mapper's two entries, each with
  `CorrelationId = OrderId`.

- [ ] **Step 1: Write the failing tests**

Smoke:

```csharp
[Fact]
public async Task The_migrator_creates_the_intent_table_with_its_rowversion()
{
    (await fixture.ScalarAsync<int>(
        "SELECT Value = COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('payments.PaymentIntents') " +
        "AND name = 'RowVersion' AND system_type_id = TYPE_ID('timestamp')"))
        .ShouldBe(1);
}
```

Mapper:

```csharp
using Common.Application;
using Common.Contracts.Payments.V1;
using Microsoft.Extensions.DependencyInjection;
using Payments.Domain.Intents.Events;
using Payments.Domain.Orders;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class PaymentsIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddPaymentsApplication();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IIntegrationEventMapper>();
    }

    [Fact]
    public void An_authorisation_becomes_PaymentAuthorised_correlated_on_the_order()
    {
        OrderId order = OrderId.New();

        PaymentAuthorised contract = Mapper()
            .Map([new PaymentAuthorisedDomainEvent(order, "psp_1", 42.10m, "EUR", Raised)])
            .ShouldHaveSingleItem().ShouldBeOfType<PaymentAuthorised>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value, "§9.6's saga correlates on the order");
        contract.Reference.ShouldBe("psp_1");
        contract.Amount.ShouldBe(42.10m);
        contract.Currency.ShouldBe("EUR");
        contract.OccurredAt.ShouldBe(Raised);
        contract.MessageId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void A_decline_becomes_PaymentDeclined_with_its_reason()
    {
        OrderId order = OrderId.New();

        PaymentDeclined contract = Mapper()
            .Map([new PaymentDeclinedDomainEvent(order, "order_cancelled", Raised)])
            .ShouldHaveSingleItem().ShouldBeOfType<PaymentDeclined>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value);
        contract.Reason.ShouldBe("order_cancelled");
    }
}
```

- [ ] **Step 2: Run to see them fail**

Expected: the smoke test counts 0; the mapper tests find no entry.

- [ ] **Step 3: Write the configuration, repository and registry**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Application;
using Payments.Application.Provider;
using Payments.Domain.Intents;
using Payments.Domain.Orders;

namespace Payments.Infrastructure.Persistence;

internal sealed class PaymentIntentConfiguration : IEntityTypeConfiguration<PaymentIntent>
{
    public void Configure(EntityTypeBuilder<PaymentIntent> builder)
    {
        builder.ToTable("PaymentIntents", "payments");

        builder.HasKey(i => i.Id);
        builder
            .Property(i => i.Id)
            .HasColumnName("OrderId")
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();

        // §7.2: an enum a reader of the database should be able to name.
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(16);

        builder.Property(i => i.Amount).HasPrecision(PaymentAmounts.Precision, PaymentAmounts.Scale);
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);

        // ProviderLimits, which the adapter enforces before a verdict exists,
        // so nothing this column refuses can reach it.
        builder.Property(i => i.Reference).HasMaxLength(ProviderLimits.MaxReferenceLength);
        builder.Property(i => i.DeclineReason).HasMaxLength(ProviderLimits.MaxReasonLength);

        builder.Property(i => i.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(i => i.DomainEvents);
    }
}
```

```csharp
using Microsoft.EntityFrameworkCore;
using Payments.Domain.Intents;
using Payments.Domain.Orders;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// A plain read: the handler has already locked the order's record, which is
/// the lock that serialises every write for the order (spec, section 6).
/// </summary>
internal sealed class PaymentIntentRepository(PaymentsDbContext db) : IPaymentIntentRepository
{
    public Task<PaymentIntent?> GetAsync(OrderId id, CancellationToken ct) =>
        db.PaymentIntents.SingleOrDefaultAsync(i => i.Id == id, ct);

    public void Add(PaymentIntent intent) => db.Add(intent);
}
```

Registry, in the scaffolded mapper:

```csharp
private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
{
    [typeof(PaymentAuthorisedDomainEvent)] = e => ToContract((PaymentAuthorisedDomainEvent)e),
    [typeof(PaymentDeclinedDomainEvent)] = e => ToContract((PaymentDeclinedDomainEvent)e)
};

// The correlation is the ORDER: §9.6's saga correlates every payment event on it.
private static PaymentAuthorised ToContract(PaymentAuthorisedDomainEvent e) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = e.OrderId.Value,
    OccurredAt = e.OccurredAt,
    OrderId = e.OrderId.Value,
    Reference = e.Reference,
    Amount = e.Amount,
    Currency = e.Currency
};

private static PaymentDeclined ToContract(PaymentDeclinedDomainEvent e) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = e.OrderId.Value,
    OccurredAt = e.OccurredAt,
    OrderId = e.OrderId.Value,
    Reason = e.Reason
};
```

`PaymentsDbContext` gains `public DbSet<PaymentIntent> PaymentIntents =>
Set<PaymentIntent>();`; register `services.AddScoped<IPaymentIntentRepository,
PaymentIntentRepository>();`. The two events carry an `OrderId`, a value
object with a public record constructor that `System.Text.Json` rebuilds, but
`OutboxSerialisationTests` is what proves it for the `Local` lane — if the
round trip loses the `Guid`, add an `OrderIdJsonConverter` in
`Payments.Infrastructure/Persistence` on Ordering's `ReferenceJsonConverters`
pattern and register it as a `JsonConverter` singleton.

Generate:

```bash
dotnet ef migrations add AddPaymentIntents \
    --project src/Services/Payments/Payments.Infrastructure \
    --startup-project src/Services/Payments/Payments.Migrator \
    --output-dir Persistence/Migrations
```

and confirm it creates exactly `payments.PaymentIntents`.

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Payments.Application.Tests tests/Payments.Api.Tests
git add src/Services/Payments tests/Payments.*
git commit -m "feat(payments): map PaymentIntents and translate its two events"
```

---

### Task 3: `AuthorisePaymentCommand`

**Files:**
- Create: `Payments.Application/Intents/AuthorisePayment/AuthorisePaymentCommand.cs`
- Create: `.../AuthorisePayment/AuthorisePaymentHandler.cs`
- Create: `Payments.Application/Intents/PaymentOrderNotYetKnownException.cs`
- Test: `tests/Payments.Application.Tests/AuthorisePaymentHandlerTests.cs`

**Interfaces:**
- Consumes: `IPaymentOrderStore.LockAsync`, `IPaymentIntentRepository`,
  `IPaymentProvider`, `PaymentMismatchException`, `DeclineReasons.OrderCancelled`.
- Produces: `record AuthorisePaymentCommand(Guid OrderId, decimal Amount,
  string Currency) : ICommand<Result>`; `PaymentOrderNotYetKnownException`
  (three standard constructors).

The order of the checks is the spec's table with one move, argued: **an
existing intent is checked first**. A command resent after a cancellation is
compared with that intent's money and then acknowledged without publishing —
its verdict was staged with the intent — rather than trying to create a
second intent under the same key.

- [ ] **Step 1: Write the failing handler tests**

Fakes, in the test file: `FakeOrderStore : IPaymentOrderStore` holding one
`PaymentOrderRecord?` and counting `LockAsync` calls; `FakeIntents :
IPaymentIntentRepository` over a dictionary; `FakeProvider : IPaymentProvider`
returning a configured `AuthorisationResult` and recording every request;
and `FixedClock(DateTimeOffset now) : TimeProvider`, overriding `GetUtcNow()`
to return `now` — one line, so no testing package is added for it.
The file's usings include `Microsoft.Extensions.Logging.Abstractions` for
`NullLogger<T>`, and `Payments.Application.Tests.csproj` gains
`<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />`,
no `Version=` — in this repository a project that names a package's type
references the package directly, as `Ordering.Application.Tests` does,
rather than leaning on what `Common.Application` happens to carry.

```csharp
public class AuthorisePaymentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Payer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly FakeOrderStore _orders = new();
    private readonly FakeIntents _intents = new();
    private readonly FakeProvider _provider = new();

    private AuthorisePaymentHandler Handler() =>
        new(_orders, _intents, _provider, new FixedClock(Now), NullLogger<AuthorisePaymentHandler>.Instance);

    private static PaymentOrderRecord Placed(OrderId id, decimal total = 42.10m, DateTimeOffset? cancelledAt = null) =>
        new(id, Payer, total, "EUR", Now.AddMinutes(-1), cancelledAt);

    [Fact]
    public async Task A_placed_order_is_authorised_at_the_provider_as_its_recorded_payer()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order);
        _provider.Answer = new AuthorisationResult.Authorised("psp_1");

        Result result = await Handler().HandleAsync(new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), default);

        result.IsSuccess.ShouldBeTrue();
        _provider.Requests.ShouldHaveSingleItem().ShouldBe(new AuthorisationRequest(order, Payer, 42.10m, "EUR"));
        _intents.Added.ShouldHaveSingleItem().Status.ShouldBe(PaymentIntentStatus.Authorised);
    }

    [Fact]
    public async Task A_provider_decline_is_recorded_as_a_declined_intent()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order);
        _provider.Answer = new AuthorisationResult.Declined("card_declined");

        await Handler().HandleAsync(new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), default);

        PaymentIntent intent = _intents.Added.ShouldHaveSingleItem();
        intent.Status.ShouldBe(PaymentIntentStatus.Declined);
        intent.DeclineReason.ShouldBe("card_declined");
    }

    [Fact]
    public async Task No_record_is_a_wait_and_calls_nobody()
    {
        _orders.Record = null;

        await Should.ThrowAsync<PaymentOrderNotYetKnownException>(() =>
            Handler().HandleAsync(new AuthorisePaymentCommand(Guid.CreateVersion7(), 1m, "EUR"), default));
        _provider.Requests.ShouldBeEmpty();
        _intents.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancelled_order_is_declined_order_cancelled_without_calling_the_provider()
    {
        OrderId order = OrderId.New();
        _orders.Record = new PaymentOrderRecord(order, null, null, null, null, Now.AddMinutes(-2));

        await Handler().HandleAsync(new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), default);

        _provider.Requests.ShouldBeEmpty("ADR-047: a cancelled order is never charged");
        PaymentIntent intent = _intents.Added.ShouldHaveSingleItem();
        intent.Status.ShouldBe(PaymentIntentStatus.Declined);
        intent.DeclineReason.ShouldBe(DeclineReasons.OrderCancelled);
    }

    [Fact]
    public async Task A_placed_then_cancelled_order_is_declined_order_cancelled_too()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order, cancelledAt: Now.AddSeconds(-5));

        await Handler().HandleAsync(new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), default);

        _provider.Requests.ShouldBeEmpty();
        _intents.Added.ShouldHaveSingleItem().DeclineReason.ShouldBe(DeclineReasons.OrderCancelled);
    }

    [Fact]
    public async Task A_placed_then_cancelled_order_with_other_money_is_a_mismatch_not_a_decline()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order, cancelledAt: Now.AddSeconds(-5));

        await Should.ThrowAsync<PaymentMismatchException>(() =>
            Handler().HandleAsync(new AuthorisePaymentCommand(order.Value, 99.99m, "USD"), default));
        _intents.Added.ShouldBeEmpty("no customer-facing decline is published for a command that disagrees with the order");
    }

    [Theory]
    [InlineData(42.11, "EUR")]
    [InlineData(42.10, "USD")]
    public async Task A_mismatch_is_a_fault_and_charges_nothing(decimal amount, string currency)
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order);

        await Should.ThrowAsync<PaymentMismatchException>(() =>
            Handler().HandleAsync(new AuthorisePaymentCommand(order.Value, amount, currency), default));
        _provider.Requests.ShouldBeEmpty();
        _intents.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_resend_with_other_money_is_a_mismatch_even_when_an_intent_exists()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order);
        _intents.Seed(PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1)));

        await Should.ThrowAsync<PaymentMismatchException>(() =>
            Handler().HandleAsync(new AuthorisePaymentCommand(order.Value, 99.99m, "EUR"), default));
        _provider.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_existing_intent_is_acknowledged_without_a_second_verdict_even_after_a_cancellation()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order, cancelledAt: Now);
        PaymentIntent existing = PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1));
        existing.ClearDomainEvents();
        _intents.Seed(existing);

        Result result = await Handler().HandleAsync(new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), default);

        result.IsSuccess.ShouldBeTrue();
        _provider.Requests.ShouldBeEmpty();
        _intents.Added.ShouldBeEmpty();
        existing.DomainEvents.ShouldBeEmpty("the first verdict is already staged, and a second is not idempotent downstream");
    }

    [Fact]
    public async Task The_record_is_locked_before_anything_else_is_read()
    {
        _orders.Record = null;

        await Should.ThrowAsync<PaymentOrderNotYetKnownException>(() =>
            Handler().HandleAsync(new AuthorisePaymentCommand(Guid.CreateVersion7(), 1m, "EUR"), default));
        _orders.LockCalls.ShouldBe(1);
        _intents.GetCallsBeforeLock.ShouldBe(0);
    }
}
```

`FakeIntents.GetCallsBeforeLock` counts `GetAsync` calls made while the
store's `LockCalls` was zero; give the fake a reference to the store.

- [ ] **Step 2: Run to see them fail**

Expected: compile failure.

- [ ] **Step 3: Write the command and handler**

```csharp
using Common.Application;

namespace Payments.Application.Intents.AuthorisePayment;

/// <summary>
/// §3.2's Accepts column. No payer: it is derived from the record (ADR-028).
/// The amount and currency are the instruction, checked against the record.
/// </summary>
public sealed record AuthorisePaymentCommand(Guid OrderId, decimal Amount, string Currency) : ICommand<Result>;
```

```csharp
using Common.Application;
using Microsoft.Extensions.Logging;
using Payments.Application.Orders;
using Payments.Application.Provider;
using Payments.Domain.Intents;
using Payments.Domain.Orders;

namespace Payments.Application.Intents.AuthorisePayment;

public sealed class AuthorisePaymentHandler(
    IPaymentOrderStore orders,
    IPaymentIntentRepository intents,
    IPaymentProvider provider,
    TimeProvider clock,
    ILogger<AuthorisePaymentHandler> log)
    : ICommandHandler<AuthorisePaymentCommand, Result>
{
    // §13.4's own example of an Information line: a business event worth an
    // audit trail. Compiled once (CA1848, ADR-019). The order and the
    // provider's reference, never the payer: the subject is not the audit's.
    private static readonly Action<ILogger, Guid, string, Exception?> Authorised =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(1, nameof(Authorised)),
            "Payment authorised for order {OrderId} as {Reference}.");

    public async Task<Result> HandleAsync(AuthorisePaymentCommand command, CancellationToken ct)
    {
        OrderId order = new(command.OrderId);

        // First, and held to commit: the cancellation's stamp waits behind this
        // lock, so it cannot land between the check below and the charge
        // (spec, section 6; ADR-047).
        PaymentOrderRecord? record = await orders.LockAsync(order, ct);

        PaymentIntent? existing = await intents.GetAsync(order, ct);
        if (existing is not null)
        {
            // A resend is answered only when it asks for what was decided: a
            // fresh command with other money must not inherit an authorisation.
            if (command.Amount != existing.Amount || !string.Equals(command.Currency, existing.Currency, StringComparison.Ordinal))
                throw new PaymentMismatchException(Mismatch(order, command, existing.Amount, existing.Currency, "the recorded payment"));

            // Acknowledged, not answered again: the verdict was staged with the
            // intent and reaches the saga regardless, and a second
            // PaymentAuthorised is not idempotent there (spec, section 6).
            return Result.Success();
        }

        // Money first, whenever there are figures to compare: a cancelled order
        // that was placed still holds its total, and a command disagreeing
        // with it is a fault, not a customer-facing decline. A tombstone has no
        // figures, so it has nothing to disagree with.
        if (record is { IsPlaced: true }
            && (command.Amount != record.TotalAmount || !string.Equals(command.Currency, record.Currency, StringComparison.Ordinal)))
        {
            throw new PaymentMismatchException(Mismatch(order, command, record.TotalAmount, record.Currency, "the placed order"));
        }

        if (record is { IsCancelled: true })
        {
            intents.Add(PaymentIntent.Decline(
                order, command.Amount, command.Currency, DeclineReasons.OrderCancelled, clock.GetUtcNow()));
            return Result.Success();
        }

        // §3.2: a missing record is a wait, not a decline. Thrown, so the
        // endpoint's delayed redelivery takes it (spec, section 8).
        if (record is not { IsPlaced: true })
            throw new PaymentOrderNotYetKnownException($"No OrderPlaced has reached Payments for {order}.");

        AuthorisationResult verdict = await provider.AuthoriseAsync(
            new AuthorisationRequest(order, record.CustomerId!.Value, command.Amount, command.Currency), ct);

        if (verdict is AuthorisationResult.Authorised authorised)
            Authorised(log, order.Value, authorised.Reference, null);

        DateTimeOffset now = clock.GetUtcNow();
        intents.Add(verdict switch
        {
            AuthorisationResult.Authorised a => PaymentIntent.Authorise(order, command.Amount, command.Currency, a.Reference, now),
            AuthorisationResult.Declined d => PaymentIntent.Decline(order, command.Amount, command.Currency, d.Reason, now),
            _ => throw new InvalidOperationException($"Unknown verdict {verdict.GetType().Name}.")
        });

        return Result.Success();
    }

    // Both fields, both sides: the error queue is read by a person deciding
    // whether the sender or the record is wrong.
    private static string Mismatch(OrderId order, AuthorisePaymentCommand command, decimal? amount, string? currency, string against) =>
        $"AuthorisePayment for {order} asks for {command.Amount} {command.Currency}; {against} holds {amount} {currency}.";
}
```

No validator: the mapper refuses a malformed contract (Task 4), and there is
no HTTP route to this command.

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Payments.Application.Tests
git add src/Services/Payments/Payments.Application tests/Payments.Application.Tests
git commit -m "feat(payments): AuthorisePayment charges the recorded payer, or declines a cancelled order"
```

---

### Task 4: `payments-commands` and its delayed redelivery

**Files:**
- Create: `Payments.Infrastructure/Messaging/CommandMappers.cs`
- Create: `Payments.Infrastructure/Messaging/RedeliveryLadder.cs`
- Modify: `Payments.Infrastructure/Messaging/DependencyInjection.cs`
- Modify: `tests/Payments.TestSupport/ServiceFixture.cs` — build the broker
  image from `deploy/compose/rabbitmq`, as Ordering's fixture does, and start
  an in-process `WireMockServer` over `SimulatorMappings.Directory()`,
  exposing it as `Provider` and passing its URL as the factory's third
  argument
- Test: `tests/Payments.Api.Tests/MessagingRegistrationTests.cs` (extend)
- Test: `tests/Payments.Api.Tests/AuthorisePaymentMapperTests.cs`
- Test: `tests/Payments.Api.Tests/PaymentsCommandEndpointTests.cs`

**Interfaces:**
- Produces: `public const string CommandsQueue = "payments-commands"`;
  `public static class RedeliveryLadder { public static IReadOnlyList<TimeSpan>
  Intervals; public static TimeSpan Total; }` in
  `Payments.Infrastructure.Messaging`; `AuthorisePaymentMapper :
  ICommandMessageMapper<AuthorisePayment, AuthorisePaymentCommand>`;
  `ServiceFixture.Provider` (`WireMockServer`).

- [ ] **Step 1: Write the failing mapper and registration tests**

```csharp
public class AuthorisePaymentMapperTests
{
    [Theory]
    [InlineData(-1, "EUR")]
    [InlineData(1.001, "EUR")]
    [InlineData(1e15, "EUR")]
    [InlineData(1, "")]
    [InlineData(1, "EU")]
    [InlineData(1, "eur")]
    public void A_contract_no_order_could_have_produced_is_refused_before_the_handler(decimal amount, string currency)
    {
        Should.Throw<ContractMappingException>(() =>
            new AuthorisePaymentMapper().Map(new AuthorisePayment(Guid.CreateVersion7(), amount, currency)));
    }

    [Fact]
    public void A_zero_total_is_a_contract_an_order_can_produce()
    {
        Guid order = Guid.CreateVersion7();

        new AuthorisePaymentMapper().Map(new AuthorisePayment(order, 0m, "EUR"))
            .ShouldBe(new AuthorisePaymentCommand(order, 0m, "EUR"),
                "Money.Zero is a valid total in Catalog and Ordering, and the saga forwards it");
    }

    [Fact]
    public void An_empty_order_id_is_refused_rather_than_waited_for()
    {
        Should.Throw<ContractMappingException>(() =>
            new AuthorisePaymentMapper().Map(new AuthorisePayment(Guid.Empty, 42.10m, "EUR")));
    }

    [Fact]
    public void A_well_formed_contract_maps_field_for_field()
    {
        Guid order = Guid.CreateVersion7();

        new AuthorisePaymentMapper().Map(new AuthorisePayment(order, 42.10m, "EUR"))
            .ShouldBe(new AuthorisePaymentCommand(order, 42.10m, "EUR"));
    }
}
```

The file is `tests/Payments.Api.Tests/AuthorisePaymentMapperTests.cs`, not
the Application suite: the mapper lives in `Payments.Infrastructure`, which
§4.2 keeps out of `Payments.Application.Tests`' references.

Registration, beside PR-1's test:

```csharp
[Fact]
public void Every_command_in_the_accepts_column_is_registered()
{
    ServiceCollection services = new();

    services.AddMassTransitMessaging(Configuration());

    services.ShouldContain(
        d => d.ImplementationType == typeof(CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>)
             || d.ServiceType == typeof(CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>),
        "AuthorisePayment is §3.2's Accepts column and has no AddConsumer");
}

[Fact]
public void The_ladder_is_non_decreasing_and_starts_under_a_minute()
{
    RedeliveryLadder.Intervals.ShouldBe(RedeliveryLadder.Intervals.Order());
    RedeliveryLadder.Intervals[0].ShouldBeLessThan(TimeSpan.FromMinutes(1),
        "the routine reorder is milliseconds; the first wait should not cost the saga minutes");
    RedeliveryLadder.Total.ShouldBe(RedeliveryLadder.Intervals.Aggregate(TimeSpan.Zero, (a, b) => a + b));
}
```

- [ ] **Step 2: Write the failing endpoint tests**

`PaymentsCommandEndpointTests`, in PR-1's event-test shape, with
`SendAsync(AuthorisePayment message, bool drain = true, Guid? messageId =
null)` setting the transport `MessageId` (a fresh one when none is given),
sending to `queue:payments-commands` through `ISendEndpointProvider`, and,
when draining, waiting for the inbox row under that id — the inbox keys on
the transport id, and PR-4's race test names it — a `PublishAsync` reused
from PR-1's test file (copied, not shared), and:

```csharp
private Task<string?> StatusAsync(Guid order) =>
    fixture.ScalarAsync<string?>("SELECT Value = Status FROM payments.PaymentIntents WHERE OrderId = {0}", order);

private async Task<int> StagedAsync(string type) =>
    (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains(type, StringComparison.Ordinal));

private int ProviderCalls() =>
    fixture.Provider.LogEntries.Count(e => e.RequestMessage.Path == "/v1/authorisations");
```

`ResetAsync` also calls `fixture.Provider.ResetLogEntries()` so a test counts
only its own calls. The tests:

```csharp
[Fact]
public async Task A_placed_order_is_authorised_and_PaymentAuthorised_is_staged()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));

    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

    (await StatusAsync(order)).ShouldBe("Authorised");
    (await StagedAsync("PaymentAuthorised")).ShouldBe(1);
    ProviderCalls().ShouldBe(1);
}

[Fact]
public async Task A_scripted_decline_stages_PaymentDeclined()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 10.01m));

    await SendAsync(new AuthorisePayment(order, 10.01m, "EUR"));

    (await StatusAsync(order)).ShouldBe("Declined");
    (await StagedAsync("PaymentDeclined")).ShouldBe(1);
}

[Fact]
public async Task An_authorisation_before_its_order_waits_and_succeeds_when_the_order_lands()
{
    Guid order = Guid.CreateVersion7();

    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"), drain: false);
    await fixture.Orders.Locked(order).WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken);
    (await StatusAsync(order)).ShouldBeNull("§3.2: a missing record is a wait, not a decline");
    (await StagedAsync("PaymentDeclined")).ShouldBe(0);

    await PublishAsync(Placed(order, 42.10m));

    await Eventually(
        async () => await StatusAsync(order) == "Authorised" ? 1 : 0,
        expected: 1,
        because: "the first redelivery, RedeliveryLadder.Intervals[0] later, finds the record",
        budget: RedeliveryLadder.Intervals[0] + TimeSpan.FromSeconds(30));
}

[Fact]
public async Task A_cancellation_before_the_authorisation_answers_order_cancelled_and_calls_nobody()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));
    await PublishAsync(Cancelled(order));

    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

    (await StatusAsync(order)).ShouldBe("Declined");
    (await fixture.ScalarAsync<string>("SELECT Value = DeclineReason FROM payments.PaymentIntents WHERE OrderId = {0}", order))
        .ShouldBe("order_cancelled");
    ProviderCalls().ShouldBe(0, "ADR-047");
}

[Fact]
public async Task A_mismatch_charges_nothing_and_is_not_retried()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));

    await SendAsync(new AuthorisePayment(order, 99.99m, "EUR"), drain: false);

    await Eventually(
        () => fixture.QueueDepthAsync($"{MessagingRegistration.CommandsQueue}_error"),
        expected: 1,
        because: "a mismatch is excluded from retry and faults straight to the error queue §13.6 pages on");
    (await StatusAsync(order)).ShouldBeNull();
    ProviderCalls().ShouldBe(0);
    (await fixture.InboxAsync()).ShouldNotContain(m => m.Endpoint == MessagingRegistration.CommandsQueue,
        "a fault is not consumed, so no inbox row is written");
}

[Fact]
public async Task A_resend_under_a_fresh_id_is_acknowledged_without_a_second_charge_or_verdict()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));
    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

    (await StagedAsync("PaymentAuthorised")).ShouldBe(1, "the first verdict is the only one");
    ProviderCalls().ShouldBe(1, "one charge");
}

[Fact]
public async Task A_commit_that_fails_after_the_verdict_is_staged_rolls_back_and_the_retry_charges_once()
{
    Guid order = Guid.CreateVersion7();
    await PublishAsync(Placed(order, 42.10m));
    using CommitFault fault = fixture.FailNextCommit();

    await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

    fault.Fired.ShouldBeTrue("the first unit reached its commit with the intent and the outbox row staged");
    (await StatusAsync(order)).ShouldBe("Authorised");
    (await StagedAsync("PaymentAuthorised")).ShouldBe(1, "the rolled-back unit's outbox row went with it");
    fixture.Provider.LogEntries.Count(e => e.RequestMessage.Path == "/v1/authorisations").ShouldBe(2,
        "the retry replayed the provider call under the same key");
    fixture.Provider.LogEntries.Select(e => e.RequestMessage.Headers!["Idempotency-Key"].Single()).Distinct()
        .ShouldHaveSingleItem();
}
```

`Eventually` gains an optional `budget` parameter defaulting to
`DeliveryBudget`. `ServiceFixture.QueueDepthAsync(string queue)` is added by
this task: it runs `rabbitmqctl list_queues name messages` in the broker
container through the same `ExecAsync` the harness widening uses, and returns
the named queue's count, or zero when the queue does not exist yet — MassTransit
declares `_error` on the first fault. The test polls it, because a fault's
arrival there is the terminal outcome the spec names, and no other assertion
distinguishes it from a message still queued or retrying.
`MessagingRegistration.CommandsQueue` is the constant on
`Payments.Infrastructure.Messaging.DependencyInjection`; alias the class as
Inventory's plans do if its name collides with the root one.

**The two test seams, both test-support code that never ships.** Ordering's
`TransientFaultInjection` is the precedent for a test-only fault; read it
before writing these.

- **`FailNextCommit()`** arms a singleton `SaveChangesInterceptor` the
  fixture's factory adds with
  `services.ConfigureDbContext<PaymentsDbContext>(o => o.AddInterceptors(...))`
  inside `ConfigureTestServices`. Armed, its `SavingChangesAsync` disarms,
  sets `Fired` and throws `TimeoutException` — a fault §9.8's policy retries —
  after the handler has added the intent and the collector has staged the
  outbox row, which is the rollback the test is for. Disposing the returned
  `CommitFault` disarms it.
- **`fixture.Orders`** is an `ObservedOrderStore`, a decorator over
  `IPaymentOrderStore` registered the same way. `Locked(Guid order)` and
  `Stamping(Guid order)` return tasks that complete when `LockAsync` or
  `RecordCancelledAsync` is entered for that order — before its SQL runs, so
  a caller can prove a consumer reached the store and is waiting on a lock,
  rather than infer it from a sleep. PR-4's race test uses the second.

- [ ] **Step 3: Run to see them fail**

Expected: registration fails; endpoint tests time out.

- [ ] **Step 4: Write the mapper, ladder and endpoint**

`CommandMappers.cs`:

```csharp
using Common.Application;
using Common.Contracts.Payments.V1;
using Payments.Application.Intents.AuthorisePayment;

namespace Payments.Infrastructure.Messaging;

/// <summary>
/// The wire-to-command boundary for §3.2's one accepted command. A contract no
/// placed order could have produced is a defect in the sender, refused before
/// the handler and excluded from retry (§9.8).
/// </summary>
public sealed class AuthorisePaymentMapper : ICommandMessageMapper<AuthorisePayment, AuthorisePaymentCommand>
{
    public AuthorisePaymentCommand Map(AuthorisePayment message)
    {
        // Refused here, not left to the handler: no record will ever exist for
        // it, so it would ride the whole redelivery ladder to the error queue.
        if (message.OrderId == Guid.Empty)
            throw new ContractMappingException($"An empty order id on {nameof(AuthorisePayment)}.");

        // Zero is an order's total like any other — Money.Zero is valid in
        // Catalog and Ordering — and goes to the provider, whose answer decides.
        if (message.Amount < 0)
            throw new ContractMappingException($"A negative amount on {nameof(AuthorisePayment)}.");

        // Refused here, before any branch writes it: the cancelled-order path
        // records the amount without ever converting it to minor units, so a
        // bound checked only in the adapter would be a bound one branch skips.
        if (message.Amount >= PaymentAmounts.Ceiling
            || decimal.Round(message.Amount, PaymentAmounts.MinorUnitPlaces) != message.Amount)
        {
            throw new ContractMappingException(
                $"An amount beyond what Payments can record or send on {nameof(AuthorisePayment)}.");
        }

        if (message.Currency is not { Length: 3 } || !message.Currency.All(char.IsAsciiLetterUpper))
            throw new ContractMappingException($"A currency that is not three upper-case letters on {nameof(AuthorisePayment)}.");

        return new AuthorisePaymentCommand(message.OrderId, message.Amount, message.Currency);
    }
}
```

`RedeliveryLadder.cs`:

```csharp
namespace Payments.Infrastructure.Messaging;

/// <summary>
/// §3.2's wait for an <c>OrderPlaced</c> that has not arrived: redelivery, not
/// retry, so the message is released between attempts rather than holding an
/// endpoint slot for minutes.
/// </summary>
/// <remarks>
/// Its total must reach the saga's payment timeout, so an order whose
/// <c>OrderPlaced</c> never arrives compensates on that timeout rather than
/// paging first. The timeout is Ordering's to set (§9.6) and §4.2 forbids
/// this assembly to read it, so the two are held together outside both
/// services rather than by a literal here restating it.
/// </remarks>
public static class RedeliveryLadder
{
    public static IReadOnlyList<TimeSpan> Intervals { get; } =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8)
    ];

    public static TimeSpan Total { get; } = Intervals.Aggregate(TimeSpan.Zero, (sum, next) => sum + next);
}
```

In `AddMassTransitMessaging`: the constant `CommandsQueue =
"payments-commands"` with a summary citing `Endpoints.PaymentsQueue` in
Ordering as the address that must match; `x.AddConsumer<CommandConsumer<
AuthorisePayment, AuthorisePaymentCommand>>();`; `x.AddDelayedMessageScheduler();`
beside it, and `cfg.UseDelayedMessageScheduler();` first inside
`UsingRabbitMq` — ADR-021's two halves, both named because either alone
leaves the redelivery unable to schedule. Then:

```csharp
cfg.ReceiveEndpoint(
    CommandsQueue,
    e =>
    {
        // Outermost: a record that has not arrived is a wait (§3.2), so the
        // message is released and delivered again later rather than held.
        e.UseDelayedRedelivery(r =>
        {
            r.Handle<PaymentOrderNotYetKnownException>();
            r.Intervals([.. RedeliveryLadder.Intervals]);
        });

        e.UseMessageRetry(r =>
        {
            // Terminal: a malformed contract, a mismatch and a wait each get
            // nothing from an immediate retry — the first two never will, and
            // the third is the redelivery's above.
            r.Ignore<ContractMappingException>();
            r.Ignore<PaymentMismatchException>();
            r.Ignore<PaymentOrderNotYetKnownException>();

            RetryPolicy.Standard(r);
        });

        e.UseConsumeFilter(typeof(InboxFilter<>), context);
        e.UseInMemoryOutbox(context);

        e.ConfigureConsumer<CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>>(context);
    });
```

The mapper is found by Infrastructure's `AddPluggableFrom` scan.

**The broker.** PR-1's `payments-` prefix admits this queue; confirm with
`py -3.12 deploy/compose/rabbitmq/check_permissions.py`. The delayed exchange
is the plugin `deploy/compose/rabbitmq`'s image builds in, so the fixture
builds that image as Ordering's does — the stock image the scaffold rendered
declares the exchange and hangs, which is ADR-021's warning.

- [ ] **Step 5: Run the API suite; commit**

```bash
dotnet test tests/Payments.Api.Tests
git add src/Services/Payments tests/Payments.*
git commit -m "feat(payments): the payments-commands endpoint, with delayed redelivery while the order is unknown"
```

---

### Task 5: The ladder held to the saga's timeout

**Files:**
- Modify: `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` —
  project references to `Ordering.Infrastructure` and `Payments.Infrastructure`
- Create: `tests/Platform.IntegrationTests/PaymentWaitTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using Ordering.Infrastructure.Messaging;
using Payments.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>
/// The one coupling between two services' timings, pinned where a suite holds
/// two services to each other: §3.2 requires Payments' wait for a missing
/// record to reach §9.6's payment timeout, so an order whose OrderPlaced never
/// arrives compensates on that timeout rather than paging first.
/// </summary>
public sealed class PaymentWaitTests
{
    [Fact]
    public void Payments_waits_at_least_as_long_as_the_saga_waits_for_it()
    {
        RedeliveryLadder.Total.ShouldBeGreaterThanOrEqualTo(OrderFulfilmentSaga.PaymentTimeoutDelay);
    }
}
```

If either project's `ArchitectureTests` or a solution-level gate refuses a
test project referencing two services' Infrastructure, stop and report:
the spec's section 8 chose this suite on the premise that it may.

- [ ] **Step 2: Run; commit**

```bash
dotnet test tests/Platform.IntegrationTests --filter PaymentWaitTests
git add tests/Platform.IntegrationTests
git commit -m "test: Payments' wait for a missing order reaches the saga's payment timeout"
```

Run it once with the last interval cut to 4 minutes and see it fail, then
restore; a test that cannot fail proves nothing.

---

### Task 6: ADR-047 and §3.2's sentence

**Files:**
- Create (through `/new-adr`): `docs/backend-architecture/adr/ADR-047-a-cancellation-payments-has-recorded-declines-the-authorisation-that-follows.md`
  and its Appendix A row
- Modify: `docs/backend-architecture/03-bounded-contexts.md` — one sentence
  after the callout that ends "rather than paging long before it."
- Modify: `src/BuildingBlocks/Common.Contracts/Payments/V1/PaymentEvents.cs` —
  `PaymentDeclined`'s remark says `Reason` is the provider's, which ADR-047
  makes false for one value. It becomes: "<see cref="Reason"/> is the
  provider's code, or Payments' own <c>order_cancelled</c> when a
  cancellation it recorded refused the authorisation (ADR-047), and
  deliberately not a closed vocabulary: a PSP's release could break one
  pinned here. It is for a human, never branched on and never a metric
  dimension (§9.8)." No member moves, so no consumer does

- [ ] **Step 1: Write the ADR**

Run `/new-adr` with the title "A cancellation Payments has recorded declines
the authorisation that follows". The decision, in the ADR's established
form:

- **Decision.** Payments records `OrderCancelled` on its record of the order,
  creating a tombstone when `OrderPlaced` has not arrived. An
  `AuthorisePayment` for a cancelled order calls no provider and publishes
  `PaymentDeclined` with reason `order_cancelled`. `PaymentRefunded` is
  published only when money moved back.
- **Why.** §9.4 orders nothing between the cancellation and the command, and
  a late command would charge an order whose saga is in `Compensating`,
  where an authorisation escalates to a person. A decline settles the saga's
  payment half at once; silence would hold it for the payment timeout.
- **Why a decline and not a postcondition event.** ADR-024 answers a refused
  reserve with `StockReleased` because that event states a postcondition;
  Payments' three events state acts, and `PaymentDeclined.Reason` is for a
  human and never branched on (§9.8), so a reason Payments owns is the
  cheapest honest answer and moves no contract.
- **Why the refund is not symmetric.** Nobody waits on `PaymentRefunded`
  but Notifications; a refund event for money never taken would tell a
  customer about a refund that did not happen.
- **Consequences.** The lock on the record serialises the authorisation and
  the cancellation for one order; the provider call is inside that lock.

Link §3.2, §9.4, §9.6, ADR-024 and ADR-028. The ADR argues; it does not
restate the spec's tables.

- [ ] **Step 2: §3.2's sentence**

After the callout, a paragraph:

"**A cancellation can reach Payments before the `AuthorisePayment` it
precedes, too**, and Payments records it rather than racing it: the
authorisation that follows is declined with reason `order_cancelled` and
charges nothing, and `PaymentRefunded` is published only when money moved
back — [ADR-047](adr/ADR-047-….md)."

With the ADR's real file name. Wrap at 80 columns.

- [ ] **Step 3: Audit and commit**

Run `/check-links` and `/validate-blueprint`; fix any finding here.

```bash
git add docs/backend-architecture src/BuildingBlocks/Common.Contracts/Payments/V1/PaymentEvents.cs
git commit -m "docs: ADR-047, a cancellation Payments has recorded declines the authorisation that follows"
```

---

### Task 7: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green.
- [ ] `py -3.12 deploy/compose/rabbitmq/check_permissions.py` — exit 0.
- [ ] PR body: `| Class | C+E |`, touch set from the Global Constraints.
  Then `/ship`.

## Self-review

- Spec coverage: section 6's `AuthorisePayment` table → Task 3, a test per
  row, and Task 4 over containers; section 4's replay → Task 4's retried-unit
  test; section 8's endpoint, ladder and cross-service assertion → Tasks 4, 5;
  section 2's ADR and §3.2 sentence → Task 6; section 7's `PaymentIntents` →
  Task 2.
- The race between authorise and cancel is PR-4's to assert end to end, since
  its only safe outcome is an authorisation with a refund; this PR's lock is
  what that test proves.
- Types: `PaymentIntent.Authorise/Decline`, `PaymentIntentStatus`,
  `DeclineReasons.OrderCancelled`, the two domain events,
  `IPaymentIntentRepository`, `AuthorisePaymentCommand`,
  `PaymentOrderNotYetKnownException`, `RedeliveryLadder`, `CommandsQueue` and
  `ServiceFixture.Provider` match PR-4 and PR-5.
