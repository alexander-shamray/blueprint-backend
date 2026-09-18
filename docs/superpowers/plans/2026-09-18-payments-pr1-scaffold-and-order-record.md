# Payments PR-1 — fourth service from the scaffold — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land `src/Services/Payments` from the scaffold with no Redis,
Payments' own record of each order built from `OrderPlaced` and `OrderCancelled`
on a `payments-events` queue, and everything the platform needs to build and run
it locally: the Compose pair, the broker grant a receive endpoint needs, CI's
filter and image matrix, and the observability gate's exemption.

**Architecture:** `py -3.12 tools/new-service/new_service.py Payments --port
5104` renders the five projects §4.1 names and the shared-file edits. This PR
removes `AddRedisConnections` and everything that exists only to feed it, and
amends §2's sentence to match. It adds `PaymentOrders` — a table with no
aggregate, mapped by an `IEntityTypeConfiguration` so the migration is the
model's, written and locked through `IPaymentOrderStore`, a port whose SQL runs
on the unit of work's own transaction — and two event handlers that dispatch
one command each.

**Tech Stack:** .NET at `global.json`'s pin, EF Core with SQL Server, Dapper
through the port, MassTransit `IntegrationEventConsumer<T>`, xUnit with
Shouldly and Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-18-payments-service-design.md`,
sections 1, 2 (the §2 half), 3, 5 (`PaymentOrder`), 6 (the `CancelledAt` stamp
only), 7 (`PaymentOrders`), 8 (`payments-events` and the broker grant), 11 and
12 (the exemption).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Payments/**`, `tests/Payments.*`,
  `Platform.slnx` and the rendered `*.csproj` files (E), `deploy/compose/**`,
  `.github/secret-scan/allowed/**`, `.github/workflows/ci.yml`,
  `deploy/observability/check.py`, and
  `docs/backend-architecture/02-architecture-at-a-glance.md` (§2's one
  sentence, inside D's `docs/**`).
- **Three classes, which the locality gate does not yet admit.** A service's
  arrival spans its code (A), its projects (E) and its deployment or harness
  tree (D); `docs/change-locality.md` names at most two and
  `.github/locality-gate` refuses a third letter. This PR cannot merge until
  the contract and the gate admit that case — a Class D change of its own,
  owed before Payments' PR-1, and met first by Inventory's plans, which
  declare the same shape.
- Depends on Inventory's PR-1 having merged, so port 5103 is taken and the
  scaffold's own refusal proves 5104 free; nothing else of Inventory's.
- No new package: no `Directory.Packages.props` change, no Appendix B row.
- Payments reads **no** Redis key and registers no `IConnectionMultiplexer`
  (spec, section 2). A rendered line that exists only to feed Redis is cut, not
  commented out.
- `PaymentOrders` is written only by statement, through `IPaymentOrderStore`,
  never through a `DbSet`; its configuration exists so `migrations add` emits
  the table.
- Comments say why and cite the owner. No history, no PR names.
- Explicit local types, file-scoped namespaces, 120 columns.
- `py -3.12`, never `python`, for anything Python.
- Container tests are `[Collection(nameof(IntegrationCollection))]` and never
  skipped: without a daemon they fail.
- Every step that adds behaviour writes its test first.

---

### Task 1: Run the scaffold and prove the empty service

**Files:**
- Create (by the script): `src/Services/Payments/**`, `tests/Payments.*/**`,
  `deploy/compose/services/payments.yml`
- Modify (by the script): `Platform.slnx`, `deploy/compose/docker-compose.yml`,
  `deploy/compose/docker-compose.infra-only.yml`,
  `deploy/compose/.env.example`, `deploy/compose/README.md`,
  `deploy/compose/rabbitmq/definitions.json`,
  `.github/secret-scan/allowed/*.txt`

**Interfaces:**
- Produces: `Payments.Api`, `Payments.Application`, `Payments.Domain`,
  `Payments.Infrastructure`, `Payments.Migrator`; `PaymentsDbContext` with
  default schema `payments`; `AddPaymentsApplication()`,
  `AddPaymentsInfrastructure(IConfiguration)`; `PaymentsPermissions`; the
  fixtures `ServiceFixture`, `PaymentsApiFactory`, `TestAuthHandler` in
  `tests/Payments.TestSupport`.

- [ ] **Step 1: Confirm the tree is clean and the port is free**

Run: `git status --short` (expect empty) and
`grep -n "5104" deploy/compose/README.md deploy/compose/services/*.yml`
(expect no match).

- [ ] **Step 2: Run the scaffold**

```bash
py -3.12 tools/new-service/new_service.py Payments --port 5104
```

Expected: the script lists what it wrote and ends with
`Next: dotnet restore Platform.slnx && dotnet build Platform.slnx`.

- [ ] **Step 3: Build and run the rendered suites**

```bash
dotnet restore Platform.slnx && dotnet build Platform.slnx
dotnet test tests/Payments.Domain.Tests tests/Payments.Application.Tests tests/Payments.Api.Tests
```

Expected: 0 warnings, 0 errors, every test green. A failure on `Failed to
connect to Docker endpoint` is the daemon, not the scaffold.

- [ ] **Step 4: Confirm the secret scan accepts the rendered tree**

```bash
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: both exit 0, suite first (`docs/testing.md`).

- [ ] **Step 5: Commit the scaffold output alone**

```bash
git add -A
git commit -m "feat(payments): fourth service from the scaffold"
```

The body says this is the scaffold's third dogfood, names port 5104, and says
the next commit removes Redis because §2 gives Payments none.

---

### Task 2: Payments has no Redis

**Files:**
- Modify: `src/Services/Payments/Payments.Infrastructure/DependencyInjection.cs`
  — cut `services.AddRedisConnections(configuration);` and its comment block
- Modify: `deploy/compose/services/payments.yml` — cut both
  `ConnectionStrings__Redis*` variables, their comment, and the two
  `redis-*` entries under the API's `depends_on`
- Modify: `tests/Payments.TestSupport/PaymentsApiFactory.cs` — cut the two Redis
  constructor parameters, `UnreachableRedis` and its remarks, and the two
  `UseSetting` calls that feed them
- Modify: `tests/Payments.TestSupport/ServiceFixture.cs` — cut `_redisCache`,
  `_redisCoordination`, their starts, their disposal and the two factory
  arguments
- Modify: `tests/Payments.TestSupport/Payments.TestSupport.csproj` — cut the
  `Testcontainers.Redis` reference if nothing else in the project uses it
- Modify: any rendered test the grep in Step 1 finds asserting a Redis
  registration, readiness entry or HybridCache behaviour — deleted, since the
  subject it guards no longer exists here
- Test: `tests/Payments.Api.Tests/NoRedisTests.cs`

**Interfaces:**
- Produces: `new PaymentsApiFactory(string connectionString, string
  rabbitConnectionString)` — two parameters, used by every later plan.

- [ ] **Step 1: Inventory every rendered Redis mention**

```bash
grep -rn -i "redis\|HybridCache\|IConnectionMultiplexer" src/Services/Payments tests/Payments.* deploy/compose/services/payments.yml
```

Every hit is one of: the `AddRedisConnections` call and its comment; the
Compose unit's two variables, their comment and two `depends_on` entries; the
factory's parameters, constant and settings; the fixture's two containers; a
`using` for `Common.Infrastructure.Redis` or `Testcontainers.Redis`; a test
whose subject is Redis. A hit of any other kind is a stop: record it and ask,
rather than cut something the design did not name.

- [ ] **Step 2: Write the failing test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §2: Payments reaches neither Redis instance. It takes no §8.5 key, because
/// it has no HTTP write command, and caches nothing.
/// </summary>
public sealed class NoRedisTests
{
    [Fact]
    public void The_host_starts_with_no_redis_key_and_registers_no_connection()
    {
        using PaymentsApiFactory factory = new(
            "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://payments-svc:x@rabbit.invalid:5672");

        IServiceProvider services = factory.Services;

        // By name, not by a package reference: a test proving the service has no
        // Redis should not be the thing that gives its project one. The type is
        // still loadable, because Common.Infrastructure carries the package.
        Type multiplexer = Type.GetType("StackExchange.Redis.IConnectionMultiplexer, StackExchange.Redis")
            ?? throw new InvalidOperationException("StackExchange.Redis did not load; the assertions below would prove nothing.");
        IKeyedServiceProvider keyed = (IKeyedServiceProvider)services;

        services.GetService(multiplexer).ShouldBeNull();
        keyed.GetKeyedService(multiplexer, RedisConnections.Cache).ShouldBeNull();
        keyed.GetKeyedService(multiplexer, RedisConnections.Coordination).ShouldBeNull();
    }
}
```

`RedisConnections` is `Common.Infrastructure.Redis`'s, reached through
`Payments.Infrastructure`'s reference; add that `using`. The type is named as
a string so the test project takes no `StackExchange.Redis` reference of its
own, which the repository would otherwise require of any project naming a
package's type.

- [ ] **Step 3: Run it to see it fail**

Run: `dotnet test tests/Payments.Api.Tests --filter NoRedisTests`
Expected: FAIL — a compile error on the two-argument constructor, or, once
the factory is cut first, a host that throws naming a missing Redis key.

- [ ] **Step 4: Cut every hit Step 1 listed**

In `DependencyInjection.cs`, delete the `AddRedisConnections` line and the
comment above it. `IIdempotencyMarkerStore` stays: it is the durable marker's
EF half, the scaffold's migrations create its table, and the purge covers it;
`IdempotencyBehavior` is constrained to `IIdempotentCommand`, so a service
with no such command never resolves the Redis store it would need.

In the factory, the constructor becomes
`PaymentsApiFactory(string connectionString, string rabbitConnectionString)`.
In the fixture, `Factory = new PaymentsApiFactory(ConnectionString,
_rabbit.GetConnectionString());`, and the `Task.WhenAll` starts two containers.

- [ ] **Step 5: Run the Payments suites**

```bash
dotnet build Platform.slnx
dotnet test tests/Payments.Domain.Tests tests/Payments.Application.Tests tests/Payments.Api.Tests
```

Expected: 0 warnings, all green, `NoRedisTests` included.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Payments tests/Payments.* deploy/compose/services/payments.yml
git commit -m "feat(payments): no Redis, because Payments takes no idempotency key and caches nothing"
```

---

### Task 3: §2's sentence

**Files:**
- Modify: `docs/backend-architecture/02-architecture-at-a-glance.md` — the
  bullet "Two Redis instances, not one", its last sentence

- [ ] **Step 1: Rewrite the sentence**

Replace "Payments reaches only the coordination instance: it takes idempotency
keys (§8.5) and caches nothing." with:

"Payments reaches neither: it caches nothing, and §8.5's keys belong to HTTP
write commands, which it does not have — its idempotency is the payment
provider's key and its own rows."

Wrap at 80 columns.

- [ ] **Step 2: Audit**

Run `/check-links` and `/validate-blueprint`. `docs/change-locality.md`'s
procedure owes the audit after any chapter edit. A finding is fixed here.

- [ ] **Step 3: Commit**

```bash
git add docs/backend-architecture/02-architecture-at-a-glance.md
git commit -m "docs: §2 says Payments reaches neither Redis instance"
```

---

### Task 4: `OrderId`, the record and its store

**Files:**
- Create: `src/Services/Payments/Payments.Domain/Orders/OrderId.cs`
- Create: `src/Services/Payments/Payments.Application/Orders/PaymentOrderRecord.cs`
- Create: `src/Services/Payments/Payments.Application/Orders/IPaymentOrderStore.cs`
- Create: `src/Services/Payments/Payments.Infrastructure/Persistence/PaymentOrderRow.cs`
- Create: `src/Services/Payments/Payments.Infrastructure/Persistence/PaymentOrderRowConfiguration.cs`
- Create: `src/Services/Payments/Payments.Infrastructure/Persistence/SqlPaymentOrderStore.cs`
- Modify: `Payments.Infrastructure/DependencyInjection.cs` (register the store
  scoped, beside `IUnitOfWork`)
- Create (generated): `Payments.Infrastructure/Persistence/Migrations/<ts>_AddPaymentOrders.cs`
- Test: `tests/Payments.Api.Tests/PaymentOrderStoreTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Payments.Domain.Orders;
public readonly record struct OrderId(Guid Value) { public static OrderId New(); }

namespace Payments.Application.Orders;
public sealed record PaymentOrderRecord(
    OrderId OrderId,
    Guid? CustomerId,
    decimal? TotalAmount,
    string? Currency,
    DateTimeOffset? PlacedAt,
    DateTimeOffset? CancelledAt)
{
    public bool IsPlaced => PlacedAt is not null;
    public bool IsCancelled => CancelledAt is not null;
}

public interface IPaymentOrderStore
{
    Task RecordPlacedAsync(OrderId id, Guid customerId, decimal total, string currency, DateTimeOffset placedAt, CancellationToken ct);
    Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct);
    Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct);
}
```

- Table `payments.PaymentOrders(OrderId uniqueidentifier PK, CustomerId
  uniqueidentifier NULL, TotalAmount decimal(18,2) NULL, Currency char(3) NULL,
  PlacedAt datetimeoffset NULL, CancelledAt datetimeoffset NULL)`.

- [ ] **Step 1: Write the failing store tests**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Orders;
using Payments.Domain.Orders;
using Payments.Infrastructure.Persistence;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class PaymentOrderStoreTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Placed = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cancelled = Placed.AddMinutes(3);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<T> InTransaction<T>(Func<IPaymentOrderStore, Task<T>> act)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        IPaymentOrderStore store = scope.ServiceProvider.GetRequiredService<IPaymentOrderStore>();
        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        T result = await act(store);
        await tx.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private Task InTransaction(Func<IPaymentOrderStore, Task> act) =>
        InTransaction(async s => { await act(s); return 0; });

    [Fact]
    public async Task Placed_then_cancelled_and_cancelled_then_placed_leave_the_same_row()
    {
        OrderId first = OrderId.New();
        OrderId second = OrderId.New();
        Guid customer = Guid.CreateVersion7();

        await InTransaction(s => s.RecordPlacedAsync(first, customer, 25.50m, "EUR", Placed, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordCancelledAsync(first, Cancelled, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordCancelledAsync(second, Cancelled, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordPlacedAsync(second, customer, 25.50m, "EUR", Placed, TestContext.Current.CancellationToken));

        PaymentOrderRecord? a = await InTransaction(s => s.LockAsync(first, TestContext.Current.CancellationToken));
        PaymentOrderRecord? b = await InTransaction(s => s.LockAsync(second, TestContext.Current.CancellationToken));

        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
        (b with { OrderId = a.OrderId }).ShouldBe(a, "the two events commute: each fills only its own columns");
        a.IsPlaced.ShouldBeTrue();
        a.IsCancelled.ShouldBeTrue();
        a.CustomerId.ShouldBe(customer);
        a.TotalAmount.ShouldBe(25.50m);
        a.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task A_cancellation_with_no_placement_is_a_tombstone_that_is_not_placed()
    {
        OrderId order = OrderId.New();

        await InTransaction(s => s.RecordCancelledAsync(order, Cancelled, TestContext.Current.CancellationToken));
        PaymentOrderRecord? record = await InTransaction(s => s.LockAsync(order, TestContext.Current.CancellationToken));

        record.ShouldNotBeNull();
        record.IsPlaced.ShouldBeFalse();
        record.IsCancelled.ShouldBeTrue();
        record.CustomerId.ShouldBeNull();
    }

    [Fact]
    public async Task A_redelivered_placement_writes_the_same_values_and_one_row()
    {
        OrderId order = OrderId.New();
        Guid customer = Guid.CreateVersion7();

        await InTransaction(s => s.RecordPlacedAsync(order, customer, 10m, "EUR", Placed, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordPlacedAsync(order, customer, 10m, "EUR", Placed, TestContext.Current.CancellationToken));

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0}", order.Value))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_second_cancellation_keeps_the_first_instant()
    {
        OrderId order = OrderId.New();

        await InTransaction(s => s.RecordCancelledAsync(order, Cancelled, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordCancelledAsync(order, Cancelled.AddHours(1), TestContext.Current.CancellationToken));
        PaymentOrderRecord? record = await InTransaction(s => s.LockAsync(order, TestContext.Current.CancellationToken));

        record!.CancelledAt.ShouldBe(Cancelled, "the order was cancelled once; a redelivery does not move when");
    }

    [Fact]
    public async Task An_unknown_order_locks_nothing_and_reads_null()
    {
        (await InTransaction(s => s.LockAsync(OrderId.New(), TestContext.Current.CancellationToken))).ShouldBeNull();
    }

    [Fact]
    public async Task The_store_refuses_to_run_outside_a_transaction()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IPaymentOrderStore store = scope.ServiceProvider.GetRequiredService<IPaymentOrderStore>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.LockAsync(OrderId.New(), TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Payments.Api.Tests --filter PaymentOrderStoreTests`
Expected: compile failure on the missing types.

- [ ] **Step 3: Write the types**

`OrderId.cs`:

```csharp
namespace Payments.Domain.Orders;

/// <summary>§5.2's typed identifier for the order a payment answers for.</summary>
public readonly record struct OrderId(Guid Value)
{
    public static OrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
```

`PaymentOrderRecord.cs` and `IPaymentOrderStore.cs` as in Interfaces above.
The interface's summary:

```csharp
/// <summary>
/// Payments' record of the order (§3.2): the payer, the total and the currency
/// from <c>OrderPlaced</c>, and the instant of <c>OrderCancelled</c>. Raw
/// statements on the unit of work's transaction, following
/// <c>IUnitOfWork.ExecuteRawAsync</c>'s rule for a table with no aggregate; a
/// port rather than that member because <see cref="LockAsync"/> returns the row
/// it locked.
/// </summary>
```

`PaymentOrderRow.cs`:

```csharp
namespace Payments.Infrastructure.Persistence;

/// <summary>
/// The shape of <c>payments.PaymentOrders</c>, mapped only so that
/// <c>migrations add</c> emits the table. Nothing loads or saves it through EF:
/// <see cref="SqlPaymentOrderStore"/> is the only reader and writer.
/// </summary>
internal sealed class PaymentOrderRow
{
    public Guid OrderId { get; set; }
    public Guid? CustomerId { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? Currency { get; set; }
    public DateTimeOffset? PlacedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}
```

`PaymentOrderRowConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// Maps the record's table on the terms Ordering's <c>ProductPriceConfiguration</c>
/// states for a read model: no <c>DbSet</c>, and the columns are nullable
/// because either event can arrive first (§9.4).
/// </summary>
internal sealed class PaymentOrderRowConfiguration : IEntityTypeConfiguration<PaymentOrderRow>
{
    public void Configure(EntityTypeBuilder<PaymentOrderRow> builder)
    {
        builder.ToTable("PaymentOrders", "payments");

        builder.HasKey(r => r.OrderId);
        builder.Property(r => r.OrderId).ValueGeneratedNever();

        builder.Property(r => r.TotalAmount).HasPrecision(18, 2);

        // Three ASCII letters by contract; IsFixedLength plus IsUnicode(false)
        // is what emits char(3) rather than nvarchar(3).
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
    }
}
```

`SqlPaymentOrderStore.cs`:

```csharp
using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Payments.Application.Orders;
using Payments.Domain.Orders;

namespace Payments.Infrastructure.Persistence;

internal sealed class SqlPaymentOrderStore(PaymentsDbContext db) : IPaymentOrderStore
{
    // UPDLOCK with SERIALIZABLE on the update: two first writes for one order
    // meet on the key-range lock rather than on the primary key, so the loser
    // updates the winner's row instead of failing its insert.
    private const string PlacedSql =
        """
        UPDATE payments.PaymentOrders WITH (UPDLOCK, SERIALIZABLE)
        SET CustomerId = @CustomerId, TotalAmount = @TotalAmount, Currency = @Currency, PlacedAt = @PlacedAt
        WHERE OrderId = @OrderId;

        IF @@ROWCOUNT = 0
            INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt)
            VALUES (@OrderId, @CustomerId, @TotalAmount, @Currency, @PlacedAt);
        """;

    // COALESCE keeps the first cancellation's instant: a redelivery says the
    // order was cancelled, not that it was cancelled again later.
    private const string CancelledSql =
        """
        UPDATE payments.PaymentOrders WITH (UPDLOCK, SERIALIZABLE)
        SET CancelledAt = COALESCE(CancelledAt, @CancelledAt)
        WHERE OrderId = @OrderId;

        IF @@ROWCOUNT = 0
            INSERT INTO payments.PaymentOrders (OrderId, CancelledAt)
            VALUES (@OrderId, @CancelledAt);
        """;

    // HOLDLOCK takes a key-range lock when the row is absent, so a
    // cancellation's first insert waits behind this read as an update would.
    private const string LockSql =
        """
        SELECT OrderId, CustomerId, TotalAmount, Currency, PlacedAt, CancelledAt
        FROM payments.PaymentOrders WITH (UPDLOCK, HOLDLOCK)
        WHERE OrderId = @OrderId;
        """;

    private sealed record Row(
        Guid OrderId,
        Guid? CustomerId,
        decimal? TotalAmount,
        string? Currency,
        DateTimeOffset? PlacedAt,
        DateTimeOffset? CancelledAt);

    public async Task RecordPlacedAsync(
        OrderId id,
        Guid customerId,
        decimal total,
        string currency,
        DateTimeOffset placedAt,
        CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        await connection.ExecuteAsync(new CommandDefinition(
            PlacedSql,
            new { OrderId = id.Value, CustomerId = customerId, TotalAmount = total, Currency = currency, PlacedAt = placedAt },
            transaction,
            cancellationToken: ct));
    }

    public async Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        await connection.ExecuteAsync(new CommandDefinition(
            CancelledSql, new { OrderId = id.Value, CancelledAt = cancelledAt }, transaction, cancellationToken: ct));
    }

    public async Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            LockSql, new { OrderId = id.Value }, transaction, cancellationToken: ct));

        return row is null
            ? null
            : new PaymentOrderRecord(
                new OrderId(row.OrderId),
                row.CustomerId,
                row.TotalAmount,
                row.Currency?.Trim(),
                row.PlacedAt,
                row.CancelledAt);
    }

    private (DbConnection, DbTransaction) Current()
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // The refusal EfUnitOfWork.ExecuteRawAsync makes: a statement with no
        // transaction autocommits outside the unit the caller believes it is in.
        if (current is null)
            throw new InvalidOperationException("The order record is written only inside the unit of work's transaction (§6.3).");

        return (db.Database.GetDbConnection(), current.GetDbTransaction());
    }
}
```

If `Payments.Infrastructure.csproj` carries no `Dapper` reference, add
`<PackageReference Include="Dapper" />` beside its other package references,
with no `Version=`.

Register `services.AddScoped<IPaymentOrderStore, SqlPaymentOrderStore>();` on
the line after `IUnitOfWork`.

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddPaymentOrders \
    --project src/Services/Payments/Payments.Infrastructure \
    --startup-project src/Services/Payments/Payments.Migrator \
    --output-dir Persistence/Migrations
```

Open it: exactly `payments.PaymentOrders` with the six columns in Interfaces,
`Currency` as `char(3)`, `TotalAmount` as `decimal(18,2)`, and nothing else.

- [ ] **Step 5: Run the store tests and the suite**

Run: `dotnet test tests/Payments.Api.Tests`
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Payments tests/Payments.Api.Tests
git commit -m "feat(payments): the order record and the store that writes it on the unit's transaction"
```

---

### Task 5: The two commands and their event handlers

**Files:**
- Modify: `src/Services/Payments/Payments.Application/Payments.Application.csproj`
  — restore the `Common.Contracts` project reference the scaffold stripped,
  cut the comment that stood in its place
- Create: `Payments.Application/Orders/RecordOrderPlaced/RecordOrderPlacedCommand.cs`
- Create: `.../RecordOrderPlaced/RecordOrderPlacedHandler.cs`
- Create: `.../RecordOrderPlaced/OrderPlacedHandler.cs`
- Create: `Payments.Application/Orders/RecordOrderCancelled/RecordOrderCancelledCommand.cs`
- Create: `.../RecordOrderCancelled/RecordOrderCancelledHandler.cs`
- Create: `.../RecordOrderCancelled/OrderCancelledHandler.cs`
- Modify: `tests/Payments.Application.Tests/Payments.Application.Tests.csproj`
  and the scaffold's registration test — the first handler brings the
  container wiring and the scan's registration test back, per the scaffold's
  "What you do next", step 3
- Test: `tests/Payments.Application.Tests/OrderEventHandlerTests.cs`

**Interfaces:**
- Produces: `record RecordOrderPlacedCommand(Guid OrderId, Guid CustomerId,
  decimal TotalAmount, string Currency, DateTimeOffset PlacedAt) :
  ICommand<Result>`; `record RecordOrderCancelledCommand(Guid OrderId,
  DateTimeOffset CancelledAt) : ICommand<Result>` — PR-4 extends
  `RecordOrderCancelledHandler` with the void; `OrderPlacedHandler :
  IIntegrationEventHandler<OrderPlaced>`; `OrderCancelledHandler :
  IIntegrationEventHandler<OrderCancelled>`.

- [ ] **Step 1: Write the failing handler tests**

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;
using Payments.Application.Orders.RecordOrderCancelled;
using Payments.Application.Orders.RecordOrderPlaced;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class OrderEventHandlerTests
{
    private static readonly DateTimeOffset Occurred = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Sent { get; } = [];

        public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct)
        {
            Sent.Add(command);
            return Task.FromResult((TResult)(object)Result.Success());
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task OrderPlaced_records_the_payer_the_total_and_the_currency_at_the_events_instant()
    {
        RecordingDispatcher dispatcher = new();
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await new OrderPlacedHandler(dispatcher).HandleAsync(
            new OrderPlaced
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = order,
                OccurredAt = Occurred,
                OrderId = order,
                CustomerId = customer,
                TotalAmount = 42.10m,
                Currency = "EUR",
                Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 42.10m)]
            },
            TestContext.Current.CancellationToken);

        dispatcher.Sent.ShouldHaveSingleItem()
            .ShouldBe(new RecordOrderPlacedCommand(order, customer, 42.10m, "EUR", Occurred));
    }

    [Fact]
    public async Task OrderCancelled_records_the_cancellation_at_the_events_instant()
    {
        RecordingDispatcher dispatcher = new();
        Guid order = Guid.CreateVersion7();

        await new OrderCancelledHandler(dispatcher).HandleAsync(
            new OrderCancelled
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = order,
                OccurredAt = Occurred,
                OrderId = order,
                CustomerId = Guid.CreateVersion7(),
                Reason = CancelReasons.CustomerRequest
            },
            TestContext.Current.CancellationToken);

        dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new RecordOrderCancelledCommand(order, Occurred));
    }
}
```

The fake implements `IDispatcher` as `Common.Application` declares it; its
`= default` on each token is the interface's and need not be repeated.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Payments.Application.Tests --filter OrderEventHandlerTests`
Expected: compile failure.

- [ ] **Step 3: Write the commands, handlers and event handlers**

```csharp
using Common.Application;

namespace Payments.Application.Orders.RecordOrderPlaced;

public sealed record RecordOrderPlacedCommand(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset PlacedAt) : ICommand<Result>;
```

```csharp
using Common.Application;
using Payments.Domain.Orders;

namespace Payments.Application.Orders.RecordOrderPlaced;

public sealed class RecordOrderPlacedHandler(IPaymentOrderStore orders)
    : ICommandHandler<RecordOrderPlacedCommand, Result>
{
    public async Task<Result> HandleAsync(RecordOrderPlacedCommand command, CancellationToken ct)
    {
        await orders.RecordPlacedAsync(
            new OrderId(command.OrderId),
            command.CustomerId,
            command.TotalAmount,
            command.Currency,
            command.PlacedAt,
            ct);

        return Result.Success();
    }
}
```

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;

namespace Payments.Application.Orders.RecordOrderPlaced;

/// <summary>
/// §3.2's subscription: the payer is bound from a real principal at Ordering's
/// endpoint (§11.4) and reaches Payments here, never on <c>AuthorisePayment</c>
/// (ADR-028).
/// </summary>
/// <remarks>
/// It dispatches rather than writing, so the write runs inside the command
/// pipeline's transaction, which is where the store refuses to run without.
/// </remarks>
public sealed class OrderPlacedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordOrderPlacedCommand(
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                integrationEvent.TotalAmount,
                integrationEvent.Currency,
                integrationEvent.OccurredAt),
            ct);
}
```

`RecordOrderCancelledCommand(Guid OrderId, DateTimeOffset CancelledAt)`,
its handler calling `orders.RecordCancelledAsync(new OrderId(command.OrderId),
command.CancelledAt, ct)` and returning `Result.Success()`, and
`OrderCancelledHandler` dispatching it from `OrderId` and `OccurredAt`, with
this summary:

```csharp
/// <summary>
/// Records the cancellation on Payments' record of the order, creating the
/// record when <c>OrderPlaced</c> has not arrived (§9.4 orders nothing). The
/// stamp is what refuses a later <c>AuthorisePayment</c> (ADR-047).
/// </summary>
```

The Result is returned rather than thrown on in both event handlers: neither
command has a failure branch, and a thrown fault is what the endpoint retries.

Restore `<ProjectReference
Include="..\..\..\BuildingBlocks\Common.Contracts\Common.Contracts.csproj" />`
in `Payments.Application.csproj` and cut the scaffold's comment in its place.
Restore the Application test project's container wiring and the scan's
registration test the scaffold names, asserting both handlers resolve.

- [ ] **Step 4: Run the application suite**

Run: `dotnet test tests/Payments.Application.Tests`
Expected: green, including the architecture tests.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Payments/Payments.Application tests/Payments.Application.Tests
git commit -m "feat(payments): OrderPlaced and OrderCancelled record the order through two commands"
```

---

### Task 6: The `payments-events` queue and the broker grant

**Files:**
- Create: `src/Services/Payments/Payments.Infrastructure/Messaging/RetryPolicy.cs`
  — Ordering's file with the namespace changed; §9.8 prints the ladder per
  service, and the scaffold, having no endpoint, carries none
- Modify: `src/Services/Payments/Payments.Infrastructure/Messaging/DependencyInjection.cs`
- Modify: `deploy/compose/rabbitmq/definitions.json` — `payments-svc`'s three
  patterns
- Modify: `tests/Payments.TestSupport/ServiceFixture.cs` — the harness-only
  write widening
- Test: `tests/Payments.Api.Tests/MessagingRegistrationTests.cs` (extend)
- Test: `tests/Payments.Api.Tests/PaymentsEventEndpointTests.cs`

**Interfaces:**
- Produces: `public const string EventsQueue = "payments-events"` on
  `Payments.Infrastructure.Messaging.DependencyInjection`; `RetryPolicy.Standard`.

- [ ] **Step 1: Write the failing registration test**

In the scaffolded `MessagingRegistrationTests`, with `using
Common.Contracts.Ordering.V1;` and `using Common.Infrastructure.Messaging;`:

```csharp
[Fact]
public void Every_event_in_the_consumes_column_is_registered()
{
    ServiceCollection services = new();

    services.AddMassTransitMessaging(Configuration());

    foreach (Type consumer in new[]
             {
                 typeof(IntegrationEventConsumer<OrderPlaced>),
                 typeof(IntegrationEventConsumer<OrderCancelled>)
             })
    {
        services.ShouldContain(
            d => d.ImplementationType == consumer || d.ServiceType == consumer,
            $"{consumer.Name} is in §3.2's Consumes column and has no AddConsumer");
    }
}
```

- [ ] **Step 2: Write the failing endpoint tests**

`PaymentsEventEndpointTests` in the shape of Ordering's
`CatalogEventEndpointTests` — its usings are `Common.Contracts` (for
`IIntegrationEvent`), `Common.Contracts.Ordering.V1`, `MassTransit`,
`Microsoft.Extensions.DependencyInjection`, `Payments.TestSupport`,
`Shouldly` and `Xunit` — with a 30-second `DeliveryBudget`, an `Eventually`
that polls and fails with the last value it saw, and a `PublishAsync<T>` that
publishes through `IBus` setting both transport headers from the contract:

```csharp
private async Task PublishAsync<T>(T message, bool drain = true)
    where T : class, IIntegrationEvent
{
    await fixture.Factory.Services.GetRequiredService<IBus>().Publish(
        message,
        c =>
        {
            c.MessageId = message.MessageId;
            c.CorrelationId = message.CorrelationId;
        },
        TestContext.Current.CancellationToken);

    if (drain)
    {
        await Eventually(
            async () => (await fixture.InboxAsync(message.MessageId)).Count,
            expected: 1,
            because: "the inbox row is written when the handler's transaction commits (§9.5)");
    }
}

private static OrderPlaced Placed(Guid order, decimal total = 42.10m) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = order,
    OccurredAt = DateTimeOffset.UtcNow,
    OrderId = order,
    CustomerId = Guid.CreateVersion7(),
    TotalAmount = total,
    Currency = "EUR",
    Lines = [new PlacedLine(Guid.CreateVersion7(), 1, total)]
};

private static OrderCancelled Cancelled(Guid order) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = order,
    OccurredAt = DateTimeOffset.UtcNow,
    OrderId = order,
    CustomerId = Guid.CreateVersion7(),
    Reason = CancelReasons.CustomerRequest
};

private Task<int> PlacedCount(Guid order) =>
    fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0} AND PlacedAt IS NOT NULL", order);

private Task<int> CancelledCount(Guid order) =>
    fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0} AND CancelledAt IS NOT NULL", order);
```

The tests:

```csharp
[Fact]
public async Task OrderPlaced_over_the_queue_records_the_order()
{
    Guid order = Guid.CreateVersion7();

    await PublishAsync(Placed(order));

    (await PlacedCount(order)).ShouldBe(1);
    (await fixture.ScalarAsync<decimal>("SELECT Value = TotalAmount FROM payments.PaymentOrders WHERE OrderId = {0}", order))
        .ShouldBe(42.10m);
}

[Fact]
public async Task A_cancellation_before_its_placement_leaves_a_tombstone_the_placement_completes()
{
    Guid order = Guid.CreateVersion7();

    await PublishAsync(Cancelled(order));
    (await CancelledCount(order)).ShouldBe(1);
    (await PlacedCount(order)).ShouldBe(0, "a tombstone: cancelled, never placed");

    await PublishAsync(Placed(order));

    (await PlacedCount(order)).ShouldBe(1);
    (await CancelledCount(order)).ShouldBe(1, "the placement fills its own columns and leaves the stamp");
}

[Fact]
public async Task The_same_placement_delivered_twice_is_consumed_once()
{
    Guid order = Guid.CreateVersion7();
    OrderPlaced placed = Placed(order);

    await PublishAsync(placed);
    await PublishAsync(placed, drain: false);
    await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

    (await fixture.InboxAsync(placed.MessageId)).Count.ShouldBe(1, "§9.5's inbox dropped the redelivery");
    (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0}", order))
        .ShouldBe(1);
}

[Fact]
public async Task A_placement_and_a_cancellation_arriving_together_both_land_on_one_row()
{
    Guid order = Guid.CreateVersion7();

    await Task.WhenAll(PublishAsync(Placed(order), drain: false), PublishAsync(Cancelled(order), drain: false));

    await Eventually(() => PlacedCount(order), expected: 1, because: "the placement landed");
    await Eventually(() => CancelledCount(order), expected: 1, because: "the cancellation landed on the same row");
    (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0}", order))
        .ShouldBe(1, "the loser of the first insert updated the winner's row rather than failing on the key");
}
```

- [ ] **Step 3: Run to see them fail**

Run: `dotnet test tests/Payments.Api.Tests --filter "MessagingRegistrationTests|PaymentsEventEndpointTests"`
Expected: the registration test fails on missing consumers; the endpoint tests
time out.

- [ ] **Step 4: Write the endpoint**

`RetryPolicy.cs`: copy `src/Services/Ordering/Ordering.Infrastructure/Messaging/RetryPolicy.cs`
and change the namespace to `Payments.Infrastructure.Messaging`.

In `AddMassTransitMessaging`, the constant beside the class's other members:

```csharp
/// <summary>
/// §3.2's Consumes column for Payments. One queue for both events: each
/// dispatches a command with no failure branch, so they share one retry
/// vocabulary.
/// </summary>
public const string EventsQueue = "payments-events";
```

Inside `AddMassTransit`:

```csharp
// §3.2's Consumes column. Registering and binding are two statements and both
// are needed; a consumer registered and never bound receives nothing.
x.AddConsumer<IntegrationEventConsumer<OrderPlaced>>();
x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();
```

Inside `UsingRabbitMq`, after `cfg.Host(...)`:

```csharp
cfg.ReceiveEndpoint(
    EventsQueue,
    e =>
    {
        e.UseMessageRetry(RetryPolicy.Standard);

        // Inbox outside the in-memory outbox (§9.8): the other nesting commits
        // the inbox row before the buffered sends have flushed.
        e.UseConsumeFilter(typeof(InboxFilter<>), context);
        e.UseInMemoryOutbox(context);

        e.ConfigureConsumer<IntegrationEventConsumer<OrderPlaced>>(context);
        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
    });
```

Match the scaffold's `ConfigureEndpoints` stance: if the rendered registration
carries Ordering's "No ConfigureEndpoints, deliberately" comment, keep it;
nothing here calls it.

**The broker grant.** The scaffold copied `catalog-svc`'s publisher-only
patterns, which admit no queue. In `deploy/compose/rabbitmq/definitions.json`,
`payments-svc`'s entry becomes `ordering-svc`'s shape with Payments' names:

```json
"configure": "^(payments-|Common\\.Contracts|Payments\\.Infrastructure\\.Messaging:|MassTransit:)",
"write": "^(payments-|Common\\.Contracts(\\.Payments\\.V1:|:)|Payments\\.Infrastructure\\.Messaging:|MassTransit:)",
"read": "^(payments-|Common\\.Contracts|Payments\\.Infrastructure\\.Messaging:|MassTransit:)"
```

`payments-` admits `payments-events` now and `payments-commands` when PR-3
declares it. `write` admits Payments' own contract exchanges and the bare
`Common.Contracts:` fault exchange and no other context's.

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

Suite first, then the gate. Expected: both exit 0.

**The harness widening.** These tests publish `OrderPlaced` and
`OrderCancelled` as `payments-svc`, and `write` on Ordering's exchanges is
exactly what the production grant refuses. `ServiceFixture` gains Ordering's
`WidenWriteForTheHarnessAsync`, with `payments-svc` and the scope
`^(payments-|Common\.Contracts|Payments\.Infrastructure\.Messaging:|MassTransit:)`,
throwing with stdout and stderr on a non-zero exit, called from
`InitializeAsync` after the broker starts and before the factory is built.
The production file does not move for it.

- [ ] **Step 5: Run the API suite**

Run: `dotnet test tests/Payments.Api.Tests`
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Payments/Payments.Infrastructure tests/Payments.* deploy/compose/rabbitmq/definitions.json
git commit -m "feat(payments): the payments-events endpoint, and the broker grant a receive endpoint needs"
```

---

### Task 7: CI's filter and image matrix, and the observability exemption

**Files:**
- Modify: `.github/workflows/ci.yml` — the `changes` job's `outputs` and
  `filters`, the `images` job's `if` and its `matrix.include`
- Modify: `deploy/observability/check.py` — `OUTBOX_METRICS_EXEMPT` gains
  `"Payments"`

- [ ] **Step 1: Run both gates to see them fail**

```bash
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
py -3.12 deploy/observability/check.py
```

Expected: `filters` refuses `src/Services/Payments`, `images` refuses its two
Dockerfiles, and the observability check names Payments as hosting the
dispatcher with neither gauges nor an exemption.

- [ ] **Step 2: Edit**

`ci.yml` outputs, after `inventory`:

```yaml
      payments: ${{ steps.changes.outputs.payments }}
```

Filters, after the `inventory` block:

```yaml
            payments:
              - *shared
              - 'src/Services/Payments/**'
              - 'tests/Payments.*/**'
```

The `images` job's `if` gains `|| needs.changes.outputs.payments == 'true'`.
Matrix, after the two `inventory` entries:

```yaml
          - filter: payments
            image: payments-api
            dockerfile: src/Services/Payments/Payments.Api/Dockerfile
          - filter: payments
            image: payments-migrator
            dockerfile: src/Services/Payments/Payments.Migrator/Dockerfile
```

`check.py`: add `"Payments"` to `OUTBOX_METRICS_EXEMPT` with the reason
"Rendered from Catalog and inherits its gap; removed when Payments registers
OutboxMetrics."

- [ ] **Step 3: Run the gates with their suites**

```bash
py -3.12 -m unittest discover -s .github/pipeline-gate
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
py -3.12 deploy/observability/check.py
```

Expected: all exit 0.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml deploy/observability/check.py
git commit -m "ci: build and filter Payments' two images, and exempt its outbox gauges until it has them"
```

---

### Task 8: The platform up, and whole-solution verification

- [ ] **Step 1: Bring the platform up**

```bash
docker compose -f deploy/compose/docker-compose.yml up --build --wait
```

Expected: every service healthy, `payments-api` included, and
`docker compose -f deploy/compose/docker-compose.yml config` shows no Redis
variable and no Redis dependency for it. Place an order as `demo` per
`deploy/compose/README.md`, then:

```bash
docker compose -f deploy/compose/docker-compose.yml exec sql sh -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT OrderId, PlacedAt FROM Payments.payments.PaymentOrders"'
```

`sql` is the service `deploy/compose/infrastructure.yml` declares and
`MSSQL_SA_PASSWORD` the variable it sets inside that container; the single
quotes keep the host shell from expanding it. Expected: the order's row, with
`PlacedAt` set. Tear down with
`docker compose -f deploy/compose/docker-compose.yml down -v` — from the
repository root Compose has no default file, so the `-f` is what makes the
teardown reach the stack the `up` started.

- [ ] **Step 2: Build and test everything**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx
py -3.12 -m unittest discover -s tools/new-service
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: 0 warnings; every suite green; both exit 0.

- [ ] **Step 3: The PR body**

`| Class | A+D+E |` and the touch set from the Global Constraints, the
dogfood evidence (the three rendered suites' counts before Task 2), then
`/ship`.

## Self-review

- Spec coverage: section 1's port and the no-Redis answer → Tasks 1, 2;
  section 2's §2 sentence → Task 3; section 5's `PaymentOrder` and section 7's
  `PaymentOrders` and `AddPaymentOrders` → Task 4; section 6's `CancelledAt`
  stamp and the commuting writes → Tasks 4, 5, 6; section 8's
  `payments-events`, registration test and broker grant → Task 6; section 11's
  keys (the scaffold's, less Redis) → Tasks 1, 2; section 12's exemption →
  Task 7; the CI half of section 3 → Task 7.
- Not in this PR by design: the provider (PR-2), `PaymentIntent` and
  `payments-commands` (PR-3), the void (PR-4), the admin read (PR-5), Helm
  (PR-6).
- Types: `OrderId`, `PaymentOrderRecord`, `IPaymentOrderStore.RecordPlacedAsync/
  RecordCancelledAsync/LockAsync`, `RecordOrderPlacedCommand`,
  `RecordOrderCancelledCommand`, `EventsQueue`, `RetryPolicy.Standard` and the
  two-argument `PaymentsApiFactory` are named identically in every later plan.
