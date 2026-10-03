# BFF order read PR-2 — the eight consumers that keep the projection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Web.Bff` receive. `bff-order-events` binds the eight events
ADR-051 names; seven handlers keep one `bff.Orders` row per order and its
`bff.OrderLines`, an eighth keeps `bff.Products`' names; every statement
inserts a missing row and fills only columns that are still null, so arrival
order and redelivery never move what the row says. §10.7's cancellation map
is decided once, at write, and stored; §10.7's rank is a pure function of
which step timestamps are set. `bff.orders.unattributed` reports how long the
oldest row with no owner has waited for one. The broker account `bff-svc`
joins `definitions.json` and the permission gate reads the BFF's `Messaging`
directory by selector. The chart's `broker` block and the descriptor's
`consume` signal land with the consumers, because `smoke.sh` and `canary.py`
read the new code the day it lands and refuse a host that reads a broker its
chart does not name or registers a consumer its descriptor does not judge.
Nothing reads the rows yet: the routes are PR-3's.

**Architecture:** the handlers live in `Web.Bff.Orders` — `OrderProjection`
implementing seven `IIntegrationEventHandler<T>`s and `ProductNameProjection`
the eighth — registered by §6.2's scan, and each event's write is one Dapper
batch over PR-1's `IDbConnectionFactory`, in `ProductPriceProjection`'s form
(§6.6): `MERGE ... WITH (HOLDLOCK)` inserting on a missing row and setting
each column through `COALESCE(target.c, @c)` behind a guard on the event's
own step column. The two line-carrying Ordering events add the lines in the
same transaction when none exist, read through `OPENJSON` from one parameter.
The three Ordering events read the row's owner back and log, never move, a
disagreeing customer. `Web.Bff.Messaging` is Notifications' registration with
the BFF's names: one receive endpoint, the inbox filter outside the in-memory
outbox, a bare `RetryPolicy.Standard` and no redelivery. The gauge is
Shipping's `ShipmentMetrics` shape over an internal stats reader with a
five-second cache. The gate change is a second glob in
`check_permissions.py`'s `messaging_dirs`, keyed by the host tree's name.

**Tech Stack:** MassTransit 8.5.3 over RabbitMQ, Dapper over SQL Server
2022 (`MERGE`, `OPENJSON`), EF Core migrations for the one index, xUnit v3
with Shouldly and Testcontainers, `System.Diagnostics.Metrics`, stdlib
Python 3.12 for the gates, Helm 3 at `helm.yml`'s pin for `smoke.sh`.

**Spec:** `docs/superpowers/specs/2026-10-03-bff-order-read-design.md`,
sections 1 (the names, how rank is stored, the cancellation member, the
lines, product names, monitoring), 3 (the broker account, every gate told by
selector, `BffFactory`'s placeholder keys), 4 (PR-2's row, and why each
chart half rides the PR that makes the host need it), 5 (the tables,
`PaymentCurrency`, `CustomerId` written once, `IndexUnattributedOrders`), 6
(messaging), 9 (PR-2's key, the chart's broker half, no printed defaults),
10 (the gauge), 11 (the rows this PR owns) and 12 (the rows taken by 2).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set:

  `src/BFF/Web.Bff/**`, `src/BFF/Web.Bff.Persistence/**`, `tests/Web.Bff.Tests/**`, `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`, `tests/Common.Web.Tests/ObservabilityTests.cs`, `deploy/compose/rabbitmq/**`, `deploy/compose/services/web-bff.yml`, `deploy/helm/web-bff/values.yaml`, `deploy/canary/deployables/web-bff.json`, `.github/workflows/broker-permissions.yml`, `.github/secret-scan/allowed/deploy.txt`, `.github/secret-scan/allowed/docs.txt`, `docs/secrets.md`, `docs/backend-architecture/02-architecture-at-a-glance.md`, `docs/backend-architecture/03-bounded-contexts.md`, `docs/backend-architecture/12-test-strategy.md`, `docs/backend-architecture/14-local-development.md`, `docs/backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md`

  Why each, since the row is paths only. A is the BFF's tree — the handlers,
  the bus, the gauge and the host's two new lines in `Web.Bff`, the index and
  its migration in `Web.Bff.Persistence`, and its one suite — plus one
  building block and its test: `ObservabilityExtensions.cs` gains the
  `AddMeter` line §13.2's export needs and `ObservabilityTests` its copy of
  the list. D is the broker's tree (`definitions.json`, the gate and its
  suite), the BFF's Compose unit, the chart's broker block and the
  descriptor's `consume` signal (spec section 4: `smoke.sh` and `canary.py`
  refuse the host without them), the gate's workflow (its filter must cover
  every input the gate reads, which the gate asserts), the secret scan's two
  allow files for the local defaults this PR prints into `deploy/` and
  `docs/`, `docs/secrets.md`'s broker rows, and the chapters and the ADR spec
  section 12 gives PR 2. E is `Web.Bff.csproj`, inside the A paths.
- **Mutexes**: `src/BFF/Web.Bff/Program.cs` (the composition root), the BFF's
  `DbContext` model snapshot (Task 7 adds a migration), and `Web.Bff.csproj`.
  No repo-wide mutex surface: no pin moves, no project is added, so
  `Directory.Packages.props` and `Platform.slnx` are untouched.
- **Appendix B gains no row.** `MassTransit.RabbitMQ` and `Dapper` are
  pinned and listed today, and this PR adds two references to them.
- **Depends on PR-1 having merged**, and consumes the names its plan's
  *Interfaces for PR-2* section lists, as written there; Task 1 reads each on
  disk and stops on a miss rather than guessing:
  - `src/BFF/Web.Bff.Persistence` (namespace `Web.Bff.Persistence`) with
    `BffDbContext`, `BffSchema.Name`, the row types `OrderRow`,
    `OrderLineRow` and `ProductRow`, and the internal
    `Configurations.OrderRowConfiguration`; `bff.Orders` with spec section
    5's columns, `PaymentCurrency` among them; the migration
    `AddOrderProjection`, the only one; `src/BFF/Web.Bff.Migrator`.
  - The widths `ProjectionLimits.CurrencyLength`, `ProductNameMaxLength`,
    `TrackingNumberMaxLength` and `CancelOutcomeMaxLength`, and the
    vocabulary `CancelOutcomes.Cancelled`, `OutOfStock` and `Declined`.
  - The constraints every statement below respects:
    `CK_Orders_Total` (`Currency` with `TotalAmount`),
    `CK_Orders_Cancellation` (`CancelledAt` with `CancelOutcome`),
    `CK_Orders_Authorisation` (`AuthorisedAt` with `AuthorisedAmount`),
    `CK_Orders_Refund` (`RefundedAt` with `RefundedAmount`),
    `CK_Orders_CancelOutcome` (one of `CancelOutcomes`' three), the one
    holding `PaymentCurrency` present exactly when an amount is, and
    `FK_OrderLines_Orders_OrderId`, cascading, so an order's row is written
    before its lines in the same transaction.
  - `Web.Bff.BffPersistence.AddBffPersistence`, registering `BffDbContext`,
    an `IDbConnectionFactory` singleton over the internal
    `Web.Bff.SqlConnectionFactory`, `InboxTable("bff")`, `RetentionPolicy`,
    the hosted purge and the readiness check `sql`; **not** the `DbContext`
    alias `InboxFilter<>` resolves, which Task 6 adds beside the filter.
  - `PersistenceRegistrationTests.The_readiness_set_is_the_projections_sql_check`
    asserting exactly `["sql"]`, which Task 6 changes in the same commit that
    adds the bus.
  - `tests/Web.Bff.Tests/BffServiceFixture.cs` (named `Bff`, so the account
    `bff-svc` and its local password), overriding
    `ServiceFixture<,,>.BrokerAccountGranted` to `false` because
    `definitions.json` grants `bff-svc` nothing in PR-1 — Task 5 deletes that
    override in the commit that adds the account; `BffIntegrationCollection`,
    `BffFactory.DatabaseConnectionString`, `BffFactory.UnreachableDatabase`,
    `OptionsValidationTests.MissingSettingFactory` and
    `PersistenceRegistrationTests.NoDatabaseFactory`.
  - `deploy/compose/services/web-bff.yml` with the SQL key, the migrator
    unit and the host's `depends_on` on it; §14.1's fence and §14.2's sample
    showing the database and the migrator; the chart with `database.enabled:
    true` and `broker.enabled: false`; the descriptor with `migrator: true`
    and `signals: ["http"]`.
- **This plan prints no credential and no connection-string default**, because
  the secret scan reads `docs/superpowers/` too (spec section 9). Where PR-2
  writes one — `definitions.json`'s hash, the Compose key, §14.1's fence —
  the step names the existing line to copy and the renaming to apply, and the
  scan's own output supplies the allow entry's fingerprint, as
  `tools/new-service` takes it.
- **The account's password scheme is `tools/new-service`'s**
  (`scaffold/render.py`'s broker-account function): a four-byte salt from
  `sha256` of the account name, a `sha256` digest of the salt and the local
  password, base64 of the two. The shared fixture's broker logs in with the
  name-derived local password (ADR-056), so the hash must verify against it.
- **Comments obey `docs/style-guide.md`'s *Comments* budget**: a summary is
  one sentence, a `<remarks>` cites and is four lines, a block is five. The
  code below is written to it; run the comment gate before pushing.

## Task 1: PR-1's names, verified on disk

**Files:** none changed.

- [ ] **Step 1: Read each name this plan consumes**

```bash
git fetch origin main
git log --oneline -1 origin/main
grep -rn "class BffDbContext\|class BffSchema\|class ProjectionLimits\|class CancelOutcomes" src/BFF/Web.Bff.Persistence
grep -rn "PaymentCurrency" src/BFF/Web.Bff.Persistence/OrderRow.cs
grep -rn "class OrderRowConfiguration" src/BFF/Web.Bff.Persistence/Configurations
grep -rln "AddOrderProjection" src/BFF/Web.Bff.Persistence/Migrations
grep -rn "CK_Orders_\|FK_OrderLines_Orders_OrderId" src/BFF/Web.Bff.Persistence/Migrations
grep -n "AddBffPersistence\|AddScoped<DbContext>\|MessagingMetrics" src/BFF/Web.Bff/Program.cs src/BFF/Web.Bff/BffPersistence.cs
grep -rn "class SqlConnectionFactory" src/BFF/Web.Bff
grep -rn "The_readiness_set_is_the_projections_sql_check\|class NoDatabaseFactory\|class MissingSettingFactory" tests/Web.Bff.Tests
grep -rn "class BffServiceFixture\|class BffIntegrationCollection\|DatabaseConnectionString" tests/Web.Bff.Tests
grep -n "enabled" deploy/helm/web-bff/values.yaml
grep -n "signals\|migrator" deploy/canary/deployables/web-bff.json
```

Expected: every line prints at least one match except that `AddScoped<DbContext>`
and `MessagingMetrics` match nothing in either `Web.Bff` file; the chart
reads `database` on and `broker` off; the descriptor reads `migrator: true`
and `["http"]`.

- [ ] **Step 2: Reconcile or stop**

A missing name is PR-1 unfinished: stop and report it, because every task
below writes against it.

## Task 2: §10.7's vocabulary and its cancellation map

**Files:**
- Create: `src/BFF/Web.Bff/Orders/BuyerStatuses.cs`
- Create: `src/BFF/Web.Bff/Orders/CancellationOutcome.cs`
- Create: `tests/Web.Bff.Tests/CancellationOutcomeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Web.Bff.Tests/CancellationOutcomeTests.cs
using Common.Contracts.Ordering.V1;
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§10.7's table, a row per line, keyed on <c>Origin</c> before <c>Reason</c>.</summary>
public sealed class CancellationOutcomeTests
{
    [Theory]
    [InlineData(CancelOrigins.User, CancelReasons.OutOfStock, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.User, CancelReasons.StockTimeout, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.User, CancelReasons.PaymentDeclined, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.User, CancelReasons.PaymentTimeout, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.User, CancelReasons.CustomerRequest, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.OutOfStock, BuyerStatuses.OutOfStock)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.StockTimeout, BuyerStatuses.OutOfStock)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.PaymentDeclined, BuyerStatuses.Declined)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.PaymentTimeout, BuyerStatuses.Declined)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.CustomerRequest, BuyerStatuses.Cancelled)]
    [InlineData(null, CancelReasons.OutOfStock, BuyerStatuses.Cancelled)]
    [InlineData(null, CancelReasons.PaymentDeclined, BuyerStatuses.Cancelled)]
    [InlineData(null, CancelReasons.CustomerRequest, BuyerStatuses.Cancelled)]
    public void Each_line_of_the_table_maps_as_it_reads(string? origin, string reason, string expected) =>
        CancellationOutcome.Of(origin, reason).ShouldBe(expected);

    [Theory]
    [InlineData(CancelOrigins.User)]
    [InlineData(CancelOrigins.Workflow)]
    [InlineData(null)]
    [InlineData("operator")]
    public void A_reason_outside_the_vocabulary_claims_least_under_every_origin(string? origin) =>
        CancellationOutcome.Of(origin, "fraud_suspected").ShouldBe(
            BuyerStatuses.Cancelled,
            "a code a newer publisher adds is no evidence the platform refused anything (§10.7)");

    [Theory]
    [InlineData(CancelReasons.OutOfStock)]
    [InlineData(CancelReasons.PaymentDeclined)]
    public void An_origin_outside_the_vocabulary_claims_least_too(string reason) =>
        CancellationOutcome.Of("operator", reason).ShouldBe(BuyerStatuses.Cancelled);

    [Fact]
    public void A_buyer_who_typed_the_saga_s_reason_is_not_told_their_card_was_refused() =>
        CancellationOutcome.Of(CancelOrigins.User, CancelReasons.PaymentDeclined).ShouldBe(
            BuyerStatuses.Cancelled,
            "the cancel endpoint accepts all five codes, so Reason alone answers the wrong question (§10.7)");
}
```

- [ ] **Step 2: Run it to see it fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~CancellationOutcomeTests"
```

Expected: build failure, `The type or namespace name 'Orders' does not exist
in the namespace 'Web.Bff'`.

- [ ] **Step 3: Write the vocabulary and the map**

```csharp
// src/BFF/Web.Bff/Orders/BuyerStatuses.cs
using Web.Bff.Persistence;

namespace Web.Bff.Orders;

/// <summary>§10.7's closed status vocabulary, as the wire spells it.</summary>
/// <remarks>The three cancellation members are <see cref="CancelOutcomes"/>', which the schema's check holds.</remarks>
public static class BuyerStatuses
{
    public const string Placed = "placed";
    public const string Confirmed = "confirmed";
    public const string Dispatched = "dispatched";
    public const string Delivered = "delivered";
    public const string Cancelled = CancelOutcomes.Cancelled;
    public const string OutOfStock = CancelOutcomes.OutOfStock;
    public const string Declined = CancelOutcomes.Declined;
}
```

```csharp
// src/BFF/Web.Bff/Orders/CancellationOutcome.cs
using Common.Contracts.Ordering.V1;

namespace Web.Bff.Orders;

/// <summary>§10.7's map from an <c>OrderCancelled</c> to the member a buyer is shown, decided once at write.</summary>
public static class CancellationOutcome
{
    public static string Of(string? origin, string? reason) =>
        origin switch
        {
            CancelOrigins.Workflow => reason switch
            {
                CancelReasons.OutOfStock or CancelReasons.StockTimeout => BuyerStatuses.OutOfStock,
                CancelReasons.PaymentDeclined or CancelReasons.PaymentTimeout => BuyerStatuses.Declined,

                // customer_request, and any code outside CancelReasons: the member that claims least (§10.7).
                _ => BuyerStatuses.Cancelled
            },

            // user, an absent origin and one outside CancelOrigins alike (§10.7).
            _ => BuyerStatuses.Cancelled
        };
}
```

- [ ] **Step 4: Run it to see it pass**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~CancellationOutcomeTests"
```

Expected: 20 passed.

- [ ] **Step 5: Commit**

```bash
git add src/BFF/Web.Bff/Orders tests/Web.Bff.Tests/CancellationOutcomeTests.cs
git commit -m "feat(bff): BuyerStatuses and CancellationOutcome, §10.7's vocabulary and its map"
```

## Task 3: §10.7's rank as a pure function of the steps

**Files:**
- Create: `src/BFF/Web.Bff/Orders/OrderSteps.cs`
- Create: `src/BFF/Web.Bff/Orders/BuyerStatus.cs`
- Create: `tests/Web.Bff.Tests/BuyerStatusTests.cs`

The status is computed on read (spec section 1), so PR-3's reader calls this;
it lands here because the rank is what the set-once storage below is for.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Web.Bff.Tests/BuyerStatusTests.cs
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§10.7's rank over every subset of the five steps: the highest absorbed, never the latest.</summary>
public sealed class BuyerStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The rank as a list, highest first: a second spelling of the order for the oracle below.</summary>
    private static readonly string[] Rank =
    [
        BuyerStatuses.Delivered,
        BuyerStatuses.Cancelled,
        BuyerStatuses.Dispatched,
        BuyerStatuses.Confirmed,
        BuyerStatuses.Placed
    ];

    public static TheoryData<int> EverySubset()
    {
        TheoryData<int> subsets = [];
        for (int mask = 0; mask < 1 << 5; mask++)
            subsets.Add(mask);
        return subsets;
    }

    [Theory]
    [MemberData(nameof(EverySubset))]
    public void The_status_is_the_highest_step_present(int mask)
    {
        // Bit i set means Rank[i]'s step was absorbed; a higher step gets an earlier instant, so latest is not highest.
        DateTimeOffset? Step(int bit) => (mask & (1 << bit)) != 0 ? At.AddMinutes(bit) : null;

        OrderSteps steps = new(
            PlacedAt: Step(4),
            ConfirmedAt: Step(3),
            DispatchedAt: Step(2),
            DeliveredAt: Step(0),
            CancelledAt: Step(1),
            CancelOutcome: Step(1) is null ? null : BuyerStatuses.Cancelled);

        string? expected = Enumerable.Range(0, 5).Where(bit => Step(bit) is not null).Select(bit => Rank[bit])
            .FirstOrDefault();

        BuyerStatus.Of(steps).ShouldBe(expected);
    }

    [Theory]
    [InlineData(BuyerStatuses.Cancelled)]
    [InlineData(BuyerStatuses.OutOfStock)]
    [InlineData(BuyerStatuses.Declined)]
    public void A_cancellation_outranks_a_despatch_and_reads_as_its_stored_member(string member) =>
        BuyerStatus.Of(new OrderSteps(At, At, At.AddHours(1), null, At.AddMinutes(30), member)).ShouldBe(
            member,
            "Order.Cancel refuses a shipped order, so a cancellation on the wire preceded the despatch (§10.7)");

    [Fact]
    public void Delivery_outranks_a_cancellation() =>
        BuyerStatus.Of(new OrderSteps(At, At, At, At.AddDays(1), At, BuyerStatuses.Declined))
            .ShouldBe(BuyerStatuses.Delivered);

    [Fact]
    public void A_cancellation_with_no_stored_member_reads_as_the_member_that_claims_least() =>
        BuyerStatus.Of(new OrderSteps(null, null, null, null, At, null)).ShouldBe(BuyerStatuses.Cancelled);

    [Fact]
    public void A_row_no_step_has_reached_has_no_status() =>
        BuyerStatus.Of(new OrderSteps(null, null, null, null, null, null)).ShouldBeNull();
}
```

- [ ] **Step 2: Run it to see it fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~BuyerStatusTests"
```

Expected: build failure, `The type or namespace name 'OrderSteps' could not
be found`.

- [ ] **Step 3: Write the steps and the rank**

```csharp
// src/BFF/Web.Bff/Orders/OrderSteps.cs
namespace Web.Bff.Orders;

/// <summary>The five step instants of one <c>bff.Orders</c> row and the member its cancellation mapped to.</summary>
public readonly record struct OrderSteps(
    DateTimeOffset? PlacedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? CancelledAt,
    string? CancelOutcome);
```

```csharp
// src/BFF/Web.Bff/Orders/BuyerStatus.cs
namespace Web.Bff.Orders;

/// <summary>§10.7's rank: the highest step a row has absorbed, which needs no clock and so no ordering.</summary>
public static class BuyerStatus
{
    /// <summary>Null only for a row no step has reached, which no owned row is.</summary>
    public static string? Of(OrderSteps steps)
    {
        // Above a cancellation: §10.7 makes it highest, and goods that reached the buyer did reach them.
        if (steps.DeliveredAt is not null)
            return BuyerStatuses.Delivered;

        if (steps.CancelledAt is not null)
            return steps.CancelOutcome ?? BuyerStatuses.Cancelled;

        if (steps.DispatchedAt is not null)
            return BuyerStatuses.Dispatched;

        if (steps.ConfirmedAt is not null)
            return BuyerStatuses.Confirmed;

        return steps.PlacedAt is not null ? BuyerStatuses.Placed : null;
    }
}
```

- [ ] **Step 4: Run it to see it pass**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~BuyerStatusTests"
```

Expected: 38 passed.

- [ ] **Step 5: Commit**

```bash
git add src/BFF/Web.Bff/Orders tests/Web.Bff.Tests/BuyerStatusTests.cs
git commit -m "feat(bff): BuyerStatus, §10.7's rank over which steps a row has absorbed"
```

## Task 4: The permission gate reads a host's Messaging directory

**Files:**
- Modify: `deploy/compose/rabbitmq/check_permissions.py`
- Modify: `deploy/compose/rabbitmq/test_check_permissions.py`
- Modify: `.github/workflows/broker-permissions.yml`

The selector first, over a tree of the test's own, so it is proved before the
BFF's directory exists; Task 5 adds that directory and the account together,
because either alone fails the gate.

- [ ] **Step 1: Write the failing cases**

Append to `test_check_permissions.py`, after `AServiceThatPublishesNothing`:

```python
class AHostsMessagingIsReadToo(unittest.TestCase):
    """The second glob in `messaging_dirs`, over a host tree of its own (ADR-051)."""

    def found_over(self, layout: str) -> dict:
        with tempfile.TemporaryDirectory() as directory:
            hosts = Path(directory) / "Edge"
            (hosts / layout).mkdir(parents=True)
            original = gate.HOSTS
            gate.HOSTS = hosts
            try:
                return gate.messaging_dirs()
            finally:
                gate.HOSTS = original

    def test_a_host_s_messaging_directory_is_keyed_by_its_tree(self):
        # Keyed as a service's is, by the directory above the project, so the BFF's account is bff-svc.
        self.assertIn("Edge", self.found_over("Edge.Api/Messaging"))

    def test_a_messaging_directory_deeper_in_a_host_is_not_read(self):
        # A host has no Infrastructure project (§10.1), so only its own project's root is the selector.
        self.assertNotIn("Edge", self.found_over("Edge.Api/Orders/Messaging"))

    def test_the_services_are_still_read_beside_a_host(self):
        self.assertIn("Catalog", self.found_over("Edge.Api/Messaging"))

    def test_the_hosts_tree_is_one_of_the_inputs_the_workflow_watches(self):
        self.assertIn("src/BFF", gate.SOURCE_INPUTS)
```

- [ ] **Step 2: Run them to see them fail**

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq -k AHostsMessagingIsReadToo
```

Expected: `AttributeError: module 'check_permissions' has no attribute
'HOSTS'` on the first three, and `AssertionError: 'src/BFF' not found` on the
fourth.

- [ ] **Step 3: Widen the gate**

In `check_permissions.py`, below `SERVICES = ROOT / "src" / "Services"`:

```python
# The hosts' tree (§4.1). A host has no Infrastructure project (§10.1), so a
# consuming host keeps its Messaging directory at its own project's root.
HOSTS = ROOT / "src" / "BFF"
```

`SOURCE_INPUTS` becomes:

```python
SOURCE_INPUTS = [
    "src/Services",
    "src/BFF",
    "src/BuildingBlocks/Common.Contracts",
    "tests",
]
```

`messaging_dirs` becomes:

```python
def messaging_dirs() -> dict[str, Path]:
    """Every service's and consuming host's Messaging directory, keyed by its tree's name.

    Globbed rather than listed, so a service §4.5's scaffold renders tomorrow
    is read by this gate on the day it lands. A host is keyed by the tree
    above its project, as a service is, which makes the BFF's account bff-svc.
    """
    found = {}
    for path in sorted(SERVICES.glob("*/*.Infrastructure/Messaging")):
        if path.is_dir():
            found[path.parents[1].name] = path
    for path in sorted(HOSTS.glob("*/Messaging")):
        if path.is_dir():
            found[path.parents[1].name] = path
    return found
```

In `main()`, the existence check reads the hosts' tree too:

```python
    for path in (DEFINITIONS, WORKFLOW, SERVICES, HOSTS, CONTRACTS, DOCKERFILE, TESTS):
```

and the orphan-account message names both trees:

```python
            fail(f"{user}: has broker permissions and no messaging source under "
                 f"src/Services or src/BFF. Delete the account or restore the source")
```

`publishes` is unchanged: it asks for `src/Services/<name>/<name>.Domain`,
which no host has, so a host is owed no contract write by the selector that
already decides it for Notifications.

- [ ] **Step 4: Widen the workflow's filters**

In `.github/workflows/broker-permissions.yml`, add `- 'src/BFF/**'` after
`- 'src/Services/**'` under both `pull_request.paths` and `push.paths`.

- [ ] **Step 5: Run the suite, then the gate**

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

Expected: every case passes, the existing `test_the_real_definitions_pass`
among them — it runs `check_workflow_covers_inputs`, which fails on
`src/BFF` until Step 4 lands — and the gate prints `broker permission gate:
OK`. No BFF directory exists yet, so nothing new is required of
`definitions.json`.

- [ ] **Step 6: Commit**

```bash
git add deploy/compose/rabbitmq/check_permissions.py deploy/compose/rabbitmq/test_check_permissions.py .github/workflows/broker-permissions.yml
git commit -m "feat(broker): check_permissions.py reads a host's Messaging directory, keyed by its tree"
```

## Task 5: `bff-order-events` and the account `bff-svc`

**Files:**
- Create: `src/BFF/Web.Bff/Messaging/DependencyInjection.cs`
- Create: `src/BFF/Web.Bff/Messaging/RetryPolicy.cs`
- Modify: `src/BFF/Web.Bff/Web.Bff.csproj`
- Modify: `deploy/compose/rabbitmq/definitions.json`
- Modify: `deploy/compose/rabbitmq/test_check_permissions.py`
- Modify: `tests/Web.Bff.Tests/BffServiceFixture.cs` — the
  `BrokerAccountGranted` override goes
- Modify: `deploy/helm/web-bff/values.yaml`
- Modify: `deploy/canary/deployables/web-bff.json`
- Modify: `.github/secret-scan/allowed/deploy.txt`
- Create: `tests/Web.Bff.Tests/MessagingRegistrationTests.cs`
- Modify: `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`

- [ ] **Step 1: Write the failing registration test**

```csharp
// tests/Web.Bff.Tests/MessagingRegistrationTests.cs
using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Common.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Web.Bff.Messaging;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The production helper composes under the in-memory transport the harness swaps in.</summary>
public sealed class MessagingRegistrationTests
{
    /// <summary>Unresolvable and credential-free, so a test reaching for the real transport fails (§12.4).</summary>
    private const string UnreachableBroker = "amqp://bff-rabbit.invalid:5672";

    /// <summary>ADR-051's eight events, the BFF's row in §3.2's Consumes column.</summary>
    public static readonly Type[] Consumed =
    [
        typeof(OrderPlaced),
        typeof(OrderConfirmed),
        typeof(OrderCancelled),
        typeof(PaymentAuthorised),
        typeof(PaymentRefunded),
        typeof(ShipmentDispatched),
        typeof(ShipmentDelivered),
        typeof(ProductPublished)
    ];

    private static readonly TimeSpan HarnessInactivityTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan HarnessTestTimeout = TimeSpan.FromSeconds(60);

    private static IConfiguration Configuration(string? rabbitConnectionString = UnreachableBroker) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(rabbitConnectionString is null
                ? []
                : [new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", rabbitConnectionString)])
            .Build();

    public sealed record ProbeMessage(Guid Id);

    public sealed class ProbeConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask;
    }

    [Fact]
    public async Task Publish_reaches_a_consumer_with_the_transport_swapped_for_in_memory()
    {
        ServiceCollection services = new();
        services.AddMassTransitMessaging(Configuration());
        services.AddMassTransitTestHarness(x => x
            .SetTestTimeouts(HarnessTestTimeout, HarnessInactivityTimeout)
            .AddConsumer<ProbeConsumer>());

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        await harness.Bus.Publish(new ProbeMessage(id), TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeMessage>(
            m => m.Context.Message.Id == id,
            TestContext.Current.CancellationToken)).ShouldBeTrue(
            "the helper's registrations did not compose with the consumer bindings");
    }

    [Fact]
    public void Registration_adds_the_bus_and_its_hosted_service()
    {
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(d => d.ServiceType == typeof(IBus));
        services.ShouldContain(
            d => d.ServiceType == typeof(IHostedService),
            "MassTransit starts the bus from a hosted service; without it the registration is inert");
    }

    [Fact]
    public void Every_event_in_the_bff_row_is_registered()
    {
        // Registered only; the binding is a separate claim, provable only against a real queue.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        foreach (Type consumer in Consumed.Select(e => typeof(IntegrationEventConsumer<>).MakeGenericType(e)))
        {
            services.ShouldContain(
                d => d.ImplementationType == consumer || d.ServiceType == consumer,
                $"{consumer.Name} is in the BFF's Consumes cell and has no AddConsumer");
        }
    }

    [Fact]
    public void Usage_telemetry_is_disabled_by_the_production_registration_alone()
    {
        ServiceCollection services = new();
        services.AddMassTransitMessaging(Configuration());

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        provider
            .GetRequiredService<IOptions<UsageTelemetryOptions>>()
            .Value.Enabled.ShouldBeFalse("§13.2 owns this platform's telemetry, and none of it leaves silently");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_connection_string_fails_at_registration_naming_the_key(string? value)
    {
        ServiceCollection services = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            services.AddMassTransitMessaging(Configuration(rabbitConnectionString: value)));

        exception.Message.ShouldContain("ConnectionStrings:RabbitMq");
    }
}
```

In `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, beside the other package
references:

```xml
    <!-- The in-memory harness; core, not MassTransit.RabbitMQ, since the harness swaps the transport out. -->
    <PackageReference Include="MassTransit" />
```

- [ ] **Step 2: Run it to see it fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~MessagingRegistrationTests"
```

Expected: build failure, `The type or namespace name 'Messaging' does not
exist in the namespace 'Web.Bff'`.

- [ ] **Step 3: Write the retry policy and the registration**

```csharp
// src/BFF/Web.Bff/Messaging/RetryPolicy.cs
using MassTransit;

namespace Web.Bff.Messaging;

/// <summary>§9.8's retry policy, the ladder every receive endpoint applies unless it says otherwise.</summary>
/// <remarks>In-memory retries of one delivery, not redeliveries, so the delivery stays locked (§9.8).</remarks>
internal static class RetryPolicy
{
    /// <summary>Retries after the first attempt, so the endpoint makes one more attempt than this.</summary>
    public const int RetryLimit = 5;

    /// <summary>The first interval, before any doubling.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>The ceiling the ladder climbs towards, not reached within <see cref="RetryLimit"/> retries.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(1);

    /// <summary>What each interval adds on top of the doubling.</summary>
    public static readonly TimeSpan IntervalDelta = TimeSpan.FromSeconds(2);

    /// <summary>Applies the policy to one endpoint's retry configurator.</summary>
    public static void Standard(IRetryConfigurator retry) =>
        retry.Exponential(RetryLimit, MinInterval, MaxInterval, IntervalDelta);
}
```

```csharp
// src/BFF/Web.Bff/Messaging/DependencyInjection.cs
using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Web.Bff.Persistence;

namespace Web.Bff.Messaging;

/// <summary>ADR-051's bus: one receive endpoint for the projection's events, and nothing published.</summary>
public static class DependencyInjection
{
    /// <summary>§3.2's BFF row; one queue, as every event writes rows here and calls nothing.</summary>
    public const string EventsQueue = "bff-order-events";

    public static IServiceCollection AddMassTransitMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Eager, so a host with no broker configured does not start; an empty environment variable counts as none.
        string? connectionString = configuration.GetConnectionString("RabbitMq");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§13.5).");
        }

        // §9.5's filter names DbContext. An alias, never a second registration, beside the filter that needs it.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<BffDbContext>());

        services.AddMassTransit(x =>
        {
            // On by default; §13.2 owns this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            // Registering and binding are two statements and both are needed.
            x.AddConsumer<IntegrationEventConsumer<OrderPlaced>>();
            x.AddConsumer<IntegrationEventConsumer<OrderConfirmed>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();
            x.AddConsumer<IntegrationEventConsumer<PaymentAuthorised>>();
            x.AddConsumer<IntegrationEventConsumer<PaymentRefunded>>();
            x.AddConsumer<IntegrationEventConsumer<ShipmentDispatched>>();
            x.AddConsumer<IntegrationEventConsumer<ShipmentDelivered>>();
            x.AddConsumer<IntegrationEventConsumer<ProductPublished>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                cfg.ReceiveEndpoint(
                    EventsQueue,
                    e =>
                    {
                        // Bare, with no redelivery: no handler meets a wait, and none maps a command (§9.8).
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8).
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderPlaced>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderConfirmed>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PaymentAuthorised>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PaymentRefunded>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDispatched>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDelivered>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);
                    });

                // No ConfigureEndpoints(context): §9.8 admits no endpoint without InboxFilter<>.
            });
        });

        // No readiness line: AddMassTransit registers "masstransit-bus", tagged ready (§13.5).
        return services;
    }
}
```

`IServiceCollection` and `IConfiguration` resolve through the web SDK's
implicit usings, as `Program.cs`'s own do.

In `src/BFF/Web.Bff/Web.Bff.csproj`, beside the other package references:

```xml
    <!-- UsingRabbitMq for ADR-051's consumers; Common.Infrastructure carries MassTransit's core alone. -->
    <PackageReference Include="MassTransit.RabbitMQ" />
    <!-- The projection's statements, §6.6's form; named directly though Common.Infrastructure carries it. -->
    <PackageReference Include="Dapper" />
```

- [ ] **Step 4: Run the test to see it pass**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~MessagingRegistrationTests"
```

Expected: 7 passed.

- [ ] **Step 5: See three gates refuse the code without its deploy halves**

```bash
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 deploy/canary/canary.py check
bash deploy/helm/smoke.sh
```

Expected: all three fail, each naming the BFF. The permission gate reports
`BFF: has messaging source and no broker account in definitions.json` (the
selector from Task 4 reading the new directory); the canary gate refuses
`Web.Bff`'s descriptor for a consumer with no `consume` signal and no
exemption; `smoke.sh` fails `web-bff reads RabbitMq in src/, so its chart
declares a broker`. Spec section 4 is why the three halves below share this
commit: a release built from `main` between them starts a pod that will not
start.

- [ ] **Step 6: Add the account**

Compute the hash with `tools/new-service`'s scheme, the local password
derived from the name as the shared fixture derives it:

```bash
py -3.12 -c "import hashlib,base64; n='bff'; s=hashlib.sha256((n+'-svc').encode()).digest()[:4]; print(base64.b64encode(s+hashlib.sha256(s+('local-dev-'+n).encode()).digest()).decode())"
```

Run the same command with `n='notifications'` first: it must print the
`password_hash` `definitions.json` already holds for `notifications-svc`,
which is the proof the scheme is the one the broker imports.

In `deploy/compose/rabbitmq/definitions.json`, after `notifications-svc`'s
user, a user entry shaped as that one is — `name` `bff-svc`, `password_hash`
the value the command printed for `bff`, the same `hashing_algorithm` and
empty `tags` — and after `notifications-svc`'s permissions entry:

```json
    {
      "user": "bff-svc",
      "vhost": "/",
      "configure": "^(bff-|Common\\.Contracts|MassTransit:)",
      "write": "^(bff-|MassTransit:)",
      "read": "^(bff-|Common\\.Contracts|MassTransit:)"
    }
```

The pure consumer's shape `tools/new-service` renders for Notifications:
it declares and reads the contract exchanges it binds and writes only its own
endpoints and the fault exchanges.

In the same step, delete PR-1's override from
`tests/Web.Bff.Tests/BffServiceFixture.cs` — the
`protected override bool BrokerAccountGranted => false;` line and the comment
above it. The account now has a grant, so the override's one claim — that
`definitions.json` grants `bff-svc` nothing — is false from this commit, and
it would silently skip any `HarnessWrite` a later test adds. With it gone,
`ServiceFixture<,,>` reads the grant as it does every service's.

- [ ] **Step 7: The chart's broker block and the descriptor's signal**

In `deploy/helm/web-bff/values.yaml`, `broker:`'s block becomes Shipping's
shape under the BFF's names, with a comment inside the five-line budget:

```yaml
# The bus (§9), under the BFF's own account: one Secret per host, never a
# shared one (ADR-036). It must carry a connection string for `bff-svc`, which
# deploy/compose/rabbitmq/definitions.json declares and check_permissions.py
# holds to the code; provisioning it on a deployed broker is §15.4's obligation.
broker:
  enabled: true
  secretRef:
    name: web-bff-rabbitmq
    key: connection-string
```

and any sentence PR-1 left in that file saying the host opens no broker
connection is cut — read the comments above `database:` and `broker:` and
delete the clause rather than appending a correction.

In `deploy/canary/deployables/web-bff.json`, `signals` becomes
`["http", "consume"]`. **Declared, not exempted**: Catalog's
`consumeExemption` argues that a feed set by another service's rate may not
reach `minimumRequests` in a dwell, and this queue is fed by every order's
several events and every publication, so a canary's share of it is a
measurable volume in any window the `http` signal is.

- [ ] **Step 8: Pin the BFF's half of the gate**

Append to `AHostsMessagingIsReadToo` in `test_check_permissions.py`:

```python
    def test_the_repository_s_bff_is_read(self):
        # The floor over the real tree: a glob that matched nothing would leave bff-svc an orphan account.
        self.assertIn("BFF", gate.messaging_dirs())

    def test_a_host_publishes_nothing(self):
        self.assertFalse(gate.publishes("BFF"))

    def test_the_bff_account_is_refused_a_contract_write(self):
        definitions = real()
        entry = permission(definitions, "bff-svc")
        entry["write"] = entry["write"].replace("|MassTransit:", "|Common\\.Contracts|MassTransit:")

        failures = run_against(definitions)
        self.assertTrue(
            any("bff-svc" in f and "no Domain project to publish from" in f for f in failures),
            f"the gate accepted a contract write for a host that publishes nothing: {failures}")

    def test_a_host_with_messaging_and_no_account_is_refused(self):
        definitions = real()
        definitions["users"] = [u for u in definitions["users"] if u["name"] != "bff-svc"]
        definitions["permissions"] = [e for e in definitions["permissions"] if e["user"] != "bff-svc"]

        failures = run_against(definitions)
        self.assertTrue(
            any(f.startswith("BFF:") for f in failures),
            f"the gate accepted a consuming host with no broker account: {failures}")
```

- [ ] **Step 9: The secret scan's entry for the hash**

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: one finding, `deploy/compose/rabbitmq/definitions.json`,
`credential-assignment`, with a fingerprint. Append to
`.github/secret-scan/allowed/deploy.txt`, beside the other accounts' hashes:

```
deploy/compose/rabbitmq/definitions.json | credential-assignment | <the fingerprint the scan printed> | The BFF's broker account's password hash, Section 14.1's local default (ADR-051).
```

Run the scan again: `0 finding(s)`.

- [ ] **Step 10: Run every gate the step touched**

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: every suite green, `broker permission gate: OK`, the canary check
and `smoke.sh` passing — the latter now asserting `web-bff reads RabbitMq in
src/, so its chart declares a broker` — and the scan clean.

- [ ] **Step 11: Commit**

```bash
git add src/BFF/Web.Bff/Messaging src/BFF/Web.Bff/Web.Bff.csproj tests/Web.Bff.Tests deploy/compose/rabbitmq deploy/helm/web-bff/values.yaml deploy/canary/deployables/web-bff.json .github/secret-scan/allowed/deploy.txt
git commit -m "feat(bff): bff-order-events binds ADR-051's eight events, under bff-svc, with the chart's broker"
```

## Task 6: The handlers, on the host

**Files:**
- Create: `src/BFF/Web.Bff/Orders/OrderProjection.cs`
- Create: `src/BFF/Web.Bff/Orders/ProductNameProjection.cs`
- Create: `src/BFF/Web.Bff/Orders/DependencyInjection.cs`
- Modify: `src/BFF/Web.Bff/Program.cs`
- Modify: `src/BFF/Web.Bff/Web.Bff.csproj` (the `Common.Contracts` comment)
- Modify: `tests/Web.Bff.Tests/BffFactory.cs`
- Modify: `tests/Web.Bff.Tests/BffServiceFixture.cs`
- Modify: `tests/Web.Bff.Tests/OptionsValidationTests.cs` (`MissingSettingFactory`)
- Modify: `tests/Web.Bff.Tests/PersistenceRegistrationTests.cs` (the readiness set)
- Create: `tests/Web.Bff.Tests/ProjectedOrder.cs`
- Create: `tests/Web.Bff.Tests/OrderEvents.cs`
- Create: `tests/Web.Bff.Tests/OrderProjectionTests.cs`

- [ ] **Step 1: The fixture reads what the handlers write**

`tests/Web.Bff.Tests/ProjectedOrder.cs`:

```csharp
namespace Web.Bff.Tests;

/// <summary>One <c>bff.Orders</c> row as the engine holds it, read past the model so a test sees the columns.</summary>
public sealed record ProjectedOrder
{
    public Guid OrderId { get; init; }

    public Guid? CustomerId { get; init; }

    public string? Currency { get; init; }

    public decimal? TotalAmount { get; init; }

    public DateTimeOffset? PlacedAt { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    public DateTimeOffset? DispatchedAt { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public string? CancelOutcome { get; init; }

    public DateTimeOffset? AuthorisedAt { get; init; }

    public decimal? AuthorisedAmount { get; init; }

    public DateTimeOffset? RefundedAt { get; init; }

    public decimal? RefundedAmount { get; init; }

    public string? PaymentCurrency { get; init; }

    public string? TrackingNumber { get; init; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset AsOf { get; init; }

    /// <summary>The facts alone, for comparing two rows built in two orders.</summary>
    public ProjectedOrder Facts() => this with { OrderId = Guid.Empty, FirstSeenAt = default, AsOf = default };
}

/// <summary>One <c>bff.OrderLines</c> row.</summary>
public sealed record ProjectedLine
{
    public int LineNumber { get; init; }

    public Guid ProductId { get; init; }

    public int Quantity { get; init; }

    public decimal UnitPrice { get; init; }
}
```

Add to `tests/Web.Bff.Tests/BffServiceFixture.cs` these members and
whichever of the usings below PR-1's file lacks:

```csharp
using Common.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Web.Bff.Persistence;
using Xunit;
using MessagingRegistration = Web.Bff.Messaging.DependencyInjection;
```


```csharp
    /// <summary>How long a staged step may take; a deadline, not a sleep, so a prompt step costs nothing.</summary>
    public static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Sends an event to <c>bff-order-events</c> as the account may, then awaits its inbox row.</summary>
    /// <remarks>
    /// To the queue, never a contract's exchange: <c>bff-svc</c> writes none (ADR-036), and nothing here widens it,
    /// so what the endpoint binds under this account is what the shipped grant allows.
    /// </remarks>
    public async Task DeliverAsync<T>(T message)
        where T : class, IIntegrationEvent
    {
        // Bounded, because a send the broker refuses is retried rather than failed.
        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(StepDeadline);

        ISendEndpoint endpoint = await Factory.Services.GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri($"queue:{MessagingRegistration.EventsQueue}"));

        await endpoint.Send(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            bounded.Token);

        // The inbox row is written after the handler's statement commits (§9.5), so its rows are there to read.
        await WaitUntilAsync(async () => (await InboxAsync(message.MessageId)).Count == 1);
    }

    /// <summary>Polls to <see cref="StepDeadline"/> and throws when it lapses.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StepDeadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The staged condition did not hold within {StepDeadline}.");
    }

    /// <summary>One order's row, untracked, or null.</summary>
    public async Task<ProjectedOrder?> OrderAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        BffDbContext db = scope.ServiceProvider.GetRequiredService<BffDbContext>();

        return await db.Database
            .SqlQuery<ProjectedOrder>(
                $"""
                SELECT OrderId, CustomerId, Currency, TotalAmount, PlacedAt, ConfirmedAt, DispatchedAt,
                    DeliveredAt, CancelledAt, CancelOutcome, AuthorisedAt, AuthorisedAmount, RefundedAt,
                    RefundedAmount, PaymentCurrency, TrackingNumber, FirstSeenAt, AsOf
                FROM bff.Orders
                WHERE OrderId = {orderId}
                """)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One order's lines, in their numbered order.</summary>
    public async Task<IReadOnlyList<ProjectedLine>> LinesAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        BffDbContext db = scope.ServiceProvider.GetRequiredService<BffDbContext>();

        return await db.Database
            .SqlQuery<ProjectedLine>(
                $"""
                SELECT LineNumber, ProductId, Quantity, UnitPrice
                FROM bff.OrderLines
                WHERE OrderId = {orderId}
                """)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
```

- [ ] **Step 2: Every factory carries a broker key**

In `tests/Web.Bff.Tests/BffFactory.cs`, below PR-1's `DatabaseConnectionString`:

```csharp
    /// <summary>A broker that does not resolve and carries no credential; the bus's start does not wait.</summary>
    public const string UnreachableBroker = "amqp://bff-rabbit.invalid:5672";

    /// <summary>The bus's key (§9), which <c>BffServiceFixture</c> points at its container.</summary>
    public string BrokerConnectionString { get; set; } = UnreachableBroker;
```

and `Settings` gains, after PR-1's `ConnectionStrings:Bff` entry:

```csharp
        new("ConnectionStrings:RabbitMq", BrokerConnectionString)
```

`MissingSettingFactory`, which replaces `Settings` whole, gains the same key
beside PR-1's database line, or every row of its theory would pass on the
missing broker instead of the missing credential:

```csharp
            new("ConnectionStrings:RabbitMq", UnreachableBroker),
```

`NoDatabaseFactory` filters `base.Settings` and so carries the key already,
which is the shape it must keep: it removes the database key alone.

In `BffServiceFixture`, the factory method becomes:

```csharp
    protected override BffFactory CreateFactory() =>
        new() { DatabaseConnectionString = ConnectionString, BrokerConnectionString = BrokerConnectionString };
```

The initializer's left side is the factory's property and its right side the
shared body's, which starts the broker under the fixture's name-derived
account (ADR-056) — Task 5's account is what lets it authenticate.

In `MessagingRegistrationTests`, `UnreachableBroker` becomes a reference to
`BffFactory.UnreachableBroker`, so one constant names the placeholder.

In `tests/Web.Bff.Tests/PersistenceRegistrationTests.cs`, PR-1's readiness
test becomes the set this PR leaves:

```csharp
    [Fact]
    public void The_readiness_set_is_sql_and_the_bus()
    {
        // Registration, asserted directly, since unwired readiness and instant readiness look alike (§13.5).
        using BffFactory factory = new();
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // Exactly these, so Catalog's hop joining the set fails here rather than in an outage (§9.7).
        options.Registrations.Select(r => r.Name).ShouldBe(["sql", "masstransit-bus"], ignoreOrder: true);
        options.Registrations.ShouldAllBe(r => r.Tags.Contains("ready"));
    }
```

replacing `The_readiness_set_is_the_projections_sql_check` whole, so one test
owns the set.

- [ ] **Step 3: The event builders**

```csharp
// tests/Web.Bff.Tests/OrderEvents.cs
using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Web.Bff.Tests;

/// <summary>ADR-051's eight events, each with a fresh message id, as their publishers write them.</summary>
internal static class OrderEvents
{
    public const decimal Total = 59.97m;

    public const string Currency = "GBP";

    public const string TrackingNumber = "SIM-4F2A9C";

    public static readonly Guid Lamp = Guid.Parse("0192f1b0-0000-7000-8000-00000000a1a1");

    public static OrderPlaced Placed(Guid order, Guid customer, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        CustomerId = customer,
        TotalAmount = Total,
        Currency = Currency,
        Lines = [new PlacedLine(Lamp, 3, 19.99m)]
    };

    public static OrderConfirmed Confirmed(
        Guid order,
        Guid customer,
        DateTimeOffset at,
        IReadOnlyList<ConfirmedLine>? lines = null) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = at,
            OrderId = order,
            CustomerId = customer,
            TotalAmount = Total,
            Currency = Currency,
            Lines = lines ?? [new ConfirmedLine(Lamp, 3, 19.99m)]
        };

    public static OrderCancelled Cancelled(
        Guid order,
        Guid customer,
        DateTimeOffset at,
        string reason = CancelReasons.CustomerRequest,
        string? origin = CancelOrigins.User) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = at,
            OrderId = order,
            CustomerId = customer,
            Reason = reason,
            Origin = origin
        };

    public static PaymentAuthorised Authorised(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        Reference = "pay_ref_zz",
        Amount = Total,
        Currency = Currency
    };

    public static PaymentRefunded Refunded(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        Reference = "pay_ref_zz",
        Amount = Total,
        Currency = Currency
    };

    public static ShipmentDispatched Dispatched(
        Guid order,
        DateTimeOffset at,
        string trackingNumber = TrackingNumber) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = at,
            OrderId = order,
            TrackingNumber = trackingNumber
        };

    public static ShipmentDelivered Delivered(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        TrackingNumber = TrackingNumber
    };

    public static ProductPublished Published(Guid product, string name, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = product,
        OccurredAt = at,
        ProductId = product,
        Name = name,
        ThumbnailUrl = null,
        Amount = 19.99m,
        Currency = Currency
    };
}
```

- [ ] **Step 4: Write the failing projection tests**

```csharp
// tests/Web.Bff.Tests/OrderProjectionTests.cs
using Common.Application;
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Orders;
using Web.Bff.Persistence;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-051's handlers against the real schema, driven through the host's own registrations.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class OrderProjectionTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _customer = Guid.CreateVersion7();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_placement_into_an_empty_table_creates_the_owned_row_with_its_lines()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Placed(order, _customer, At));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.CustomerId.ShouldBe(_customer);
        row.Currency.ShouldBe(OrderEvents.Currency);
        row.TotalAmount.ShouldBe(OrderEvents.Total);
        row.PlacedAt.ShouldBe(At);
        row.AsOf.ShouldBe(row.FirstSeenAt, "one write, one instant from the BFF's clock");

        ProjectedLine line = (await fixture.LinesAsync(order)).ShouldHaveSingleItem();
        line.ShouldBe(new ProjectedLine
        {
            LineNumber = 1,
            ProductId = OrderEvents.Lamp,
            Quantity = 3,
            UnitPrice = 19.99m
        });
    }

    [Fact]
    public async Task A_redelivered_placement_writes_nothing()
    {
        Guid order = Guid.CreateVersion7();
        OrderPlaced placed = OrderEvents.Placed(order, _customer, At);

        await ApplyAsync(placed);
        ProjectedOrder first = (await fixture.OrderAsync(order)).ShouldNotBeNull();

        await ApplyAsync(placed);

        (await fixture.OrderAsync(order)).ShouldBe(first, "a set column is never written again, AsOf included");
        (await fixture.LinesAsync(order)).Count.ShouldBe(1);
    }

    public static TheoryData<string, string> Pairs()
    {
        string[] ordering = ["Placed", "Confirmed", "Cancelled"];
        TheoryData<string, string> pairs = new()
        {
            { "Placed", "Confirmed" },
            { "Placed", "Cancelled" },
            { "Confirmed", "Cancelled" }
        };

        foreach (string other in new[] { "Authorised", "Refunded", "Dispatched", "Delivered" })
        {
            foreach (string attributing in ordering)
                pairs.Add(other, attributing);
        }

        return pairs;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Every_pair_commutes(string first, string second)
    {
        // Two orders, one per arrival order; the facts must agree whichever event landed first (§9.4, §10.7).
        Guid forwards = Guid.CreateVersion7();
        Guid backwards = Guid.CreateVersion7();

        await EventAsync(first, forwards);
        await EventAsync(second, forwards);
        await EventAsync(second, backwards);
        await EventAsync(first, backwards);

        ProjectedOrder one = (await fixture.OrderAsync(forwards)).ShouldNotBeNull();
        ProjectedOrder other = (await fixture.OrderAsync(backwards)).ShouldNotBeNull();
        other.Facts().ShouldBe(one.Facts());
        (await fixture.LinesAsync(backwards)).ShouldBe(await fixture.LinesAsync(forwards));
    }

    [Fact]
    public async Task A_cancellation_first_keeps_its_member_when_the_placement_lands()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(
            OrderEvents.Cancelled(order, _customer, At, CancelReasons.OutOfStock, CancelOrigins.Workflow));
        ProjectedOrder cancelled = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        cancelled.TotalAmount.ShouldBeNull("OrderCancelled carries no total, which PR-3's read reports as null");
        (await fixture.LinesAsync(order)).ShouldBeEmpty();

        await ApplyAsync(OrderEvents.Placed(order, _customer, At.AddMinutes(-1)));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.CancelOutcome.ShouldBe(BuyerStatuses.OutOfStock);
        row.CancelledAt.ShouldBe(At);
        row.PlacedAt.ShouldBe(At.AddMinutes(-1));
        row.TotalAmount.ShouldBe(OrderEvents.Total);
        (await fixture.LinesAsync(order)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_payment_event_first_creates_an_unowned_row_the_placement_attributes()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Authorised(order, At));

        ProjectedOrder unowned = (await fixture.OrderAsync(order)).ShouldNotBeNull(
            "§10.7 inserts on a missing row rather than dropping the event");
        unowned.CustomerId.ShouldBeNull();
        unowned.AuthorisedAmount.ShouldBe(OrderEvents.Total);
        unowned.PaymentCurrency.ShouldBe(OrderEvents.Currency, "an amount is stored with the currency that labels it");
        unowned.Currency.ShouldBeNull("the order's own currency is Ordering's to write");

        await ApplyAsync(OrderEvents.Placed(order, _customer, At.AddMinutes(-1)));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.CustomerId.ShouldBe(_customer);
        row.FirstSeenAt.ShouldBe(unowned.FirstSeenAt, "the list's keyset column never moves (ADR-051)");
        row.AsOf.ShouldBeGreaterThanOrEqualTo(unowned.AsOf);
    }

    [Fact]
    public async Task A_disagreeing_customer_is_left_alone()
    {
        Guid order = Guid.CreateVersion7();
        Guid stranger = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Placed(order, _customer, At));
        await ApplyAsync(OrderEvents.Confirmed(order, stranger, At.AddMinutes(1)));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.CustomerId.ShouldBe(_customer, "moving an order between buyers shows it to the wrong one (§10.7)");
        row.ConfirmedAt.ShouldBe(At.AddMinutes(1), "the step itself still lands");
    }

    [Fact]
    public async Task The_second_line_carrying_event_writes_no_lines()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Placed(order, _customer, At));
        await ApplyAsync(OrderEvents.Confirmed(
            order,
            _customer,
            At.AddMinutes(1),
            [new ConfirmedLine(Guid.CreateVersion7(), 1, 1m), new ConfirmedLine(Guid.CreateVersion7(), 2, 2m)]));

        ProjectedLine line = (await fixture.LinesAsync(order)).ShouldHaveSingleItem();
        line.ProductId.ShouldBe(OrderEvents.Lamp);
    }

    [Fact]
    public async Task A_repeated_product_keeps_both_lines()
    {
        // Keyed by position, since nothing in PlacedLine promises a product appears once.
        Guid order = Guid.CreateVersion7();
        OrderPlaced placed = OrderEvents.Placed(order, _customer, At) with
        {
            Lines = [new PlacedLine(OrderEvents.Lamp, 1, 19.99m), new PlacedLine(OrderEvents.Lamp, 2, 19.99m)]
        };

        await ApplyAsync(placed);

        (await fixture.LinesAsync(order)).Select(l => (l.LineNumber, l.Quantity)).ShouldBe([(1, 1), (2, 2)]);
    }

    [Theory]
    [InlineData(CancelOrigins.User, CancelReasons.PaymentDeclined, BuyerStatuses.Cancelled)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.StockTimeout, BuyerStatuses.OutOfStock)]
    [InlineData(CancelOrigins.Workflow, CancelReasons.PaymentTimeout, BuyerStatuses.Declined)]
    [InlineData(null, CancelReasons.PaymentDeclined, BuyerStatuses.Cancelled)]
    public async Task The_cancellation_member_is_stored_as_the_map_decides(
        string? origin,
        string reason,
        string member)
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Cancelled(order, _customer, At, reason, origin));

        (await fixture.OrderAsync(order)).ShouldNotBeNull().CancelOutcome.ShouldBe(member);
    }

    [Theory]
    [InlineData(BuyerStatuses.Cancelled)]
    [InlineData(BuyerStatuses.OutOfStock)]
    [InlineData(BuyerStatuses.Declined)]
    public async Task The_schema_admits_every_member_the_map_produces(string member) =>
        await fixture.ExecuteAsync(
            "INSERT INTO bff.Orders (OrderId, CancelledAt, CancelOutcome, FirstSeenAt, AsOf) " +
            "VALUES ({0}, SYSDATETIMEOFFSET(), {1}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());",
            Guid.CreateVersion7(),
            member);

    [Fact]
    public async Task The_schema_refuses_a_member_the_map_never_produces() =>
        await Should.ThrowAsync<Exception>(() => fixture.ExecuteAsync(
            "INSERT INTO bff.Orders (OrderId, CancelledAt, CancelOutcome, FirstSeenAt, AsOf) " +
            "VALUES ({0}, SYSDATETIMEOFFSET(), {1}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());",
            Guid.CreateVersion7(),
            "refunded"));

    [Fact]
    public async Task A_tracking_number_past_its_column_is_dropped_and_the_step_kept()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(
            OrderEvents.Dispatched(order, At, new string('Z', ProjectionLimits.TrackingNumberMaxLength + 1)));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull(
            "a value that cannot fit is dropped, never faulted on, or the endpoint stalls on it");
        row.DispatchedAt.ShouldBe(At);
        row.TrackingNumber.ShouldBeNull();
    }

    [Fact]
    public async Task A_payment_whose_currency_cannot_be_stored_writes_nothing()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Authorised(order, At) with { Currency = "POUNDS" });

        (await fixture.OrderAsync(order)).ShouldBeNull(
            "an amount without its currency is a number the client cannot render, and the schema refuses one");
    }

    [Fact]
    public async Task An_order_whose_currency_cannot_be_stored_keeps_its_owner_and_lines_and_drops_its_total()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Placed(order, _customer, At) with { Currency = "POUNDS" });

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull(
            "a value that cannot fit is dropped, never faulted on, or the endpoint stalls on it");
        row.CustomerId.ShouldBe(_customer);
        row.Currency.ShouldBeNull();
        row.TotalAmount.ShouldBeNull("CK_Orders_Total refuses a total stored without its currency");
        (await fixture.LinesAsync(order)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_later_name_replaces_an_earlier_one_and_an_earlier_one_does_not()
    {
        Guid product = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Published(product, "Walnut desk lamp", At));
        await ApplyAsync(OrderEvents.Published(product, "Oak desk lamp", At.AddDays(1)));
        await ApplyAsync(OrderEvents.Published(product, "Pine desk lamp", At.AddHours(1)));

        (await fixture.ScalarAsync<string>("SELECT Value = Name FROM bff.Products WHERE ProductId = {0}", product))
            .ShouldBe("Oak desk lamp", "Catalog's one clock mints every OccurredAt here (§10.7)");
    }

    [Fact]
    public async Task A_name_past_its_column_is_not_written()
    {
        Guid product = Guid.CreateVersion7();

        await ApplyAsync(
            OrderEvents.Published(product, new string('n', ProjectionLimits.ProductNameMaxLength + 1), At));

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.Products WHERE ProductId = {0}", product))
            .ShouldBe(0, "a line with no name reads productName null (§10.7), which beats a stalled endpoint");
    }

    /// <summary>Every handler the host registers for <typeparamref name="T"/>, as the consumer runs them.</summary>
    private async Task ApplyAsync<T>(T message)
        where T : class
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IIntegrationEventHandler<T>[] handlers = [.. scope.ServiceProvider.GetServices<IIntegrationEventHandler<T>>()];
        handlers.ShouldNotBeEmpty($"no handler for {typeof(T).Name}: §6.2's scan did not reach it");

        foreach (IIntegrationEventHandler<T> handler in handlers)
            await handler.HandleAsync(message, TestContext.Current.CancellationToken);
    }

    private Task EventAsync(string name, Guid order) =>
        name switch
        {
            "Placed" => ApplyAsync(OrderEvents.Placed(order, _customer, At)),
            "Confirmed" => ApplyAsync(OrderEvents.Confirmed(order, _customer, At.AddMinutes(1))),
            "Cancelled" => ApplyAsync(OrderEvents.Cancelled(
                order,
                _customer,
                At.AddMinutes(2),
                CancelReasons.PaymentDeclined,
                CancelOrigins.Workflow)),
            "Authorised" => ApplyAsync(OrderEvents.Authorised(order, At.AddMinutes(3))),
            "Refunded" => ApplyAsync(OrderEvents.Refunded(order, At.AddMinutes(4))),
            "Dispatched" => ApplyAsync(OrderEvents.Dispatched(order, At.AddMinutes(5))),
            "Delivered" => ApplyAsync(OrderEvents.Delivered(order, At.AddMinutes(6))),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not one of the seven order events")
        };
}
```

`Pairs` yields the three Ordering pairs and every payment and shipment event
beside each Ordering event: 15 rows, each run in both orders, so every pair
spec section 6 lists is applied forwards and backwards.

- [ ] **Step 5: Run them to see them fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~OrderProjectionTests|FullyQualifiedName~PersistenceRegistrationTests"
```

Expected: the suite builds, since every type it names already exists, and
fails at run time. The projection tests fail on `no handler for OrderPlaced:
§6.2's scan did not reach it` (the product-name cases on `ProductPublished`),
the schema's three admission cases and its refusal pass against PR-1's
constraint already, and the readiness test fails on `should be ["sql",
"masstransit-bus"] but was ["sql"]`.

- [ ] **Step 6: Write the order projection**

```csharp
// src/BFF/Web.Bff/Orders/OrderProjection.cs
using System.Data;
using System.Text.Json;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Dapper;
using Web.Bff.Persistence;

namespace Web.Bff.Orders;

/// <summary>ADR-051's order row, kept from seven events by set-once columns, so arrival order never matters.</summary>
/// <remarks>
/// Public, because §6.2's scan is public-only. Each statement inserts a missing row (§10.7) and fills only null
/// columns, which is what makes the inbox's later, separate commit safe to repeat (§9.5).
/// </remarks>
public sealed class OrderProjection(
    IDbConnectionFactory connections,
    TimeProvider clock,
    ILogger<OrderProjection> log)
    : IIntegrationEventHandler<OrderPlaced>,
      IIntegrationEventHandler<OrderConfirmed>,
      IIntegrationEventHandler<OrderCancelled>,
      IIntegrationEventHandler<PaymentAuthorised>,
      IIntegrationEventHandler<PaymentRefunded>,
      IIntegrationEventHandler<ShipmentDispatched>,
      IIntegrationEventHandler<ShipmentDelivered>
{
    /// <summary>The lines, inserted once by whichever line-carrying event arrives first.</summary>
    private const string LinesSql =
        """
        -- UPDLOCK makes the two line-carrying events for one order wait for each other.
        IF NOT EXISTS (SELECT 1 FROM bff.OrderLines WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @OrderId)
            INSERT INTO bff.OrderLines (OrderId, LineNumber, ProductId, Quantity, UnitPrice)
            SELECT @OrderId, l.LineNumber, l.ProductId, l.Quantity, l.UnitPrice
            FROM OPENJSON(@Lines)
                WITH (
                    LineNumber int '$.LineNumber',
                    ProductId uniqueidentifier '$.ProductId',
                    Quantity int '$.Quantity',
                    UnitPrice decimal(38, 10) '$.UnitPrice') AS l;
        """;

    /// <summary>The owner as the row now holds it, read after the commit for the mismatch check.</summary>
    private const string OwnerSql =
        """
        SELECT CustomerId
        FROM bff.Orders
        WHERE OrderId = @OrderId;
        """;

    private const string PlacedSql =
        $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, CustomerId, Currency, TotalAmount, PlacedAt, FirstSeenAt, AsOf)
            VALUES (@OrderId, @CustomerId, @Currency, @TotalAmount, @OccurredAt, @Now, @Now)
        WHEN MATCHED AND target.PlacedAt IS NULL THEN
            UPDATE SET
                CustomerId  = COALESCE(target.CustomerId, @CustomerId),
                Currency    = COALESCE(target.Currency, @Currency),
                TotalAmount = COALESCE(target.TotalAmount, @TotalAmount),
                PlacedAt    = @OccurredAt,
                AsOf        = @Now;

        {LinesSql}

        COMMIT;

        {OwnerSql}
        """;

    private const string ConfirmedSql =
        $"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, CustomerId, Currency, TotalAmount, ConfirmedAt, FirstSeenAt, AsOf)
            VALUES (@OrderId, @CustomerId, @Currency, @TotalAmount, @OccurredAt, @Now, @Now)
        WHEN MATCHED AND target.ConfirmedAt IS NULL THEN
            UPDATE SET
                CustomerId  = COALESCE(target.CustomerId, @CustomerId),
                Currency    = COALESCE(target.Currency, @Currency),
                TotalAmount = COALESCE(target.TotalAmount, @TotalAmount),
                ConfirmedAt = @OccurredAt,
                AsOf        = @Now;

        {LinesSql}

        COMMIT;

        {OwnerSql}
        """;

    private const string CancelledSql =
        $"""
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, CustomerId, CancelledAt, CancelOutcome, FirstSeenAt, AsOf)
            VALUES (@OrderId, @CustomerId, @OccurredAt, @CancelOutcome, @Now, @Now)
        WHEN MATCHED AND target.CancelledAt IS NULL THEN
            UPDATE SET
                CustomerId    = COALESCE(target.CustomerId, @CustomerId),
                CancelledAt   = @OccurredAt,
                CancelOutcome = @CancelOutcome,
                AsOf          = @Now;

        {OwnerSql}
        """;

    private const string AuthorisedSql =
        """
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, PaymentCurrency, AuthorisedAt, AuthorisedAmount, FirstSeenAt, AsOf)
            VALUES (@OrderId, @PaymentCurrency, @OccurredAt, @Amount, @Now, @Now)
        WHEN MATCHED AND target.AuthorisedAt IS NULL THEN
            UPDATE SET
                PaymentCurrency  = COALESCE(target.PaymentCurrency, @PaymentCurrency),
                AuthorisedAt     = @OccurredAt,
                AuthorisedAmount = @Amount,
                AsOf             = @Now;
        """;

    private const string RefundedSql =
        """
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, PaymentCurrency, RefundedAt, RefundedAmount, FirstSeenAt, AsOf)
            VALUES (@OrderId, @PaymentCurrency, @OccurredAt, @Amount, @Now, @Now)
        WHEN MATCHED AND target.RefundedAt IS NULL THEN
            UPDATE SET
                PaymentCurrency = COALESCE(target.PaymentCurrency, @PaymentCurrency),
                RefundedAt      = @OccurredAt,
                RefundedAmount  = @Amount,
                AsOf            = @Now;
        """;

    private const string DispatchedSql =
        """
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, TrackingNumber, DispatchedAt, FirstSeenAt, AsOf)
            VALUES (@OrderId, @TrackingNumber, @OccurredAt, @Now, @Now)
        WHEN MATCHED AND target.DispatchedAt IS NULL THEN
            UPDATE SET
                TrackingNumber = COALESCE(target.TrackingNumber, @TrackingNumber),
                DispatchedAt   = @OccurredAt,
                AsOf           = @Now;
        """;

    private const string DeliveredSql =
        """
        MERGE bff.Orders WITH (HOLDLOCK) AS target
        USING (SELECT OrderId = @OrderId) AS source
            ON target.OrderId = source.OrderId
        WHEN NOT MATCHED THEN
            INSERT (OrderId, TrackingNumber, DeliveredAt, FirstSeenAt, AsOf)
            VALUES (@OrderId, @TrackingNumber, @OccurredAt, @Now, @Now)
        WHEN MATCHED AND target.DeliveredAt IS NULL THEN
            UPDATE SET
                TrackingNumber = COALESCE(target.TrackingNumber, @TrackingNumber),
                DeliveredAt    = @OccurredAt,
                AsOf           = @Now;
        """;

    // CA1848 (ADR-019). Ids only, as §13.4 allows.
    private static readonly Action<ILogger, Guid, Guid?, Guid, Exception?> CustomerMismatch =
        LoggerMessage.Define<Guid, Guid?, Guid>(
            LogLevel.Warning,
            new EventId(1, nameof(CustomerMismatch)),
            "Order {OrderId} keeps customer {KeptCustomerId}; an Ordering event named {EventCustomerId}.");

    private static readonly Action<ILogger, string, Guid, int, Exception?> ValueDropped =
        LoggerMessage.Define<string, Guid, int>(
            LogLevel.Warning,
            new EventId(2, nameof(ValueDropped)),
            "Dropped {Field} on order {OrderId}: longer than its column's {Width} characters.");

    public Task HandleAsync(OrderPlaced integrationEvent, CancellationToken ct)
    {
        // CK_Orders_Total holds the pair together, so a currency that cannot be stored takes its total with it.
        string? currency = CurrencyOf(integrationEvent.Currency, integrationEvent.OrderId);

        return AttributeAsync(
            PlacedSql,
            integrationEvent.OrderId,
            integrationEvent.CustomerId,
            new
            {
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                Currency = currency,
                TotalAmount = currency is null ? (decimal?)null : integrationEvent.TotalAmount,
                integrationEvent.OccurredAt,
                Now = clock.GetUtcNow(),
                Lines = LinesJson(integrationEvent.Lines.Select(l => (l.ProductId, l.Quantity, l.UnitPrice)))
            },
            ct);
    }

    public Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken ct)
    {
        // CK_Orders_Total holds the pair together, so a currency that cannot be stored takes its total with it.
        string? currency = CurrencyOf(integrationEvent.Currency, integrationEvent.OrderId);

        return AttributeAsync(
            ConfirmedSql,
            integrationEvent.OrderId,
            integrationEvent.CustomerId,
            new
            {
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                Currency = currency,
                TotalAmount = currency is null ? (decimal?)null : integrationEvent.TotalAmount,
                integrationEvent.OccurredAt,
                Now = clock.GetUtcNow(),
                Lines = LinesJson(integrationEvent.Lines.Select(l => (l.ProductId, l.Quantity, l.UnitPrice)))
            },
            ct);
    }

    public Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct) =>
        AttributeAsync(
            CancelledSql,
            integrationEvent.OrderId,
            integrationEvent.CustomerId,
            new
            {
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                integrationEvent.OccurredAt,
                CancelOutcome = CancellationOutcome.Of(integrationEvent.Origin, integrationEvent.Reason),
                Now = clock.GetUtcNow()
            },
            ct);

    public Task HandleAsync(PaymentAuthorised integrationEvent, CancellationToken ct) =>
        PaymentAsync(
            AuthorisedSql,
            integrationEvent.OrderId,
            integrationEvent.Currency,
            integrationEvent.Amount,
            integrationEvent.OccurredAt,
            ct);

    public Task HandleAsync(PaymentRefunded integrationEvent, CancellationToken ct) =>
        PaymentAsync(
            RefundedSql,
            integrationEvent.OrderId,
            integrationEvent.Currency,
            integrationEvent.Amount,
            integrationEvent.OccurredAt,
            ct);

    public Task HandleAsync(ShipmentDispatched integrationEvent, CancellationToken ct) =>
        WriteAsync(
            DispatchedSql,
            new
            {
                integrationEvent.OrderId,
                TrackingNumber = TrackingOf(integrationEvent.TrackingNumber, integrationEvent.OrderId),
                integrationEvent.OccurredAt,
                Now = clock.GetUtcNow()
            },
            ct);

    public Task HandleAsync(ShipmentDelivered integrationEvent, CancellationToken ct) =>
        WriteAsync(
            DeliveredSql,
            new
            {
                integrationEvent.OrderId,
                TrackingNumber = TrackingOf(integrationEvent.TrackingNumber, integrationEvent.OrderId),
                integrationEvent.OccurredAt,
                Now = clock.GetUtcNow()
            },
            ct);

    /// <summary>An Ordering event's write, then the owner it left, which a disagreeing event never moves.</summary>
    private async Task AttributeAsync(
        string sql,
        Guid orderId,
        Guid customerId,
        object parameters,
        CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        Guid? kept = await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));

        // A publisher defect if it ever differs; moving the order would show it to the wrong buyer (§10.7, §11.4).
        if (kept != customerId)
            CustomerMismatch(log, orderId, kept, customerId, null);
    }

    /// <summary>A payment's amount, written only with the currency that labels it, as the schema requires.</summary>
    private Task PaymentAsync(
        string sql,
        Guid orderId,
        string currency,
        decimal amount,
        DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        // An amount whose currency cannot be stored would be a number nobody can render, so neither is written.
        string? paymentCurrency = CurrencyOf(currency, orderId);
        if (paymentCurrency is null)
            return Task.CompletedTask;

        return WriteAsync(
            sql,
            new
            {
                OrderId = orderId,
                PaymentCurrency = paymentCurrency,
                OccurredAt = occurredAt,
                Amount = amount,
                Now = clock.GetUtcNow()
            },
            ct);
    }

    private async Task WriteAsync(string sql, object parameters, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    private string? CurrencyOf(string? currency, Guid orderId) =>
        Fitting(currency, ProjectionLimits.CurrencyLength, "Currency", orderId);

    private string? TrackingOf(string? trackingNumber, Guid orderId) =>
        Fitting(trackingNumber, ProjectionLimits.TrackingNumberMaxLength, "TrackingNumber", orderId);

    /// <summary>Another service's text, kept only when it fits its column, so it never faults the endpoint.</summary>
    private string? Fitting(string? value, int width, string field, Guid orderId)
    {
        if (value is null || value.Length <= width)
            return value;

        ValueDropped(log, field, orderId, width, null);
        return null;
    }

    /// <summary>The lines as one parameter, numbered by their position in the event.</summary>
    private static string LinesJson(IEnumerable<(Guid ProductId, int Quantity, decimal UnitPrice)> lines) =>
        JsonSerializer.Serialize(lines.Select((line, index) => new
        {
            LineNumber = index + 1,
            line.ProductId,
            line.Quantity,
            line.UnitPrice
        }));
}
```

`ILogger<T>` resolves through the web SDK's implicit usings. `OPENJSON`'s
`decimal(38, 10)` parses wide on purpose: the column's own precision is the
one PR-1's configuration declares, and the insert converts to it.

- [ ] **Step 7: Write the product-name projection**

```csharp
// src/BFF/Web.Bff/Orders/ProductNameProjection.cs
using System.Data;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Dapper;
using Web.Bff.Persistence;

namespace Web.Bff.Orders;

/// <summary>The names a line resolves on read (§10.7), from Catalog's <c>ProductPublished</c>.</summary>
/// <remarks>Public, because §6.2's scan is public-only.</remarks>
public sealed class ProductNameProjection(IDbConnectionFactory connections, ILogger<ProductNameProjection> log)
    : IIntegrationEventHandler<ProductPublished>
{
    /// <summary>Guarded on <c>OccurredAt</c>, sound here alone: Catalog's one clock mints them (§10.7).</summary>
    private const string UpsertSql =
        """
        MERGE bff.Products WITH (HOLDLOCK) AS target
        USING (SELECT ProductId = @ProductId) AS source
            ON target.ProductId = source.ProductId
        WHEN NOT MATCHED THEN
            INSERT (ProductId, Name, PublishedAt)
            VALUES (@ProductId, @Name, @OccurredAt)
        WHEN MATCHED AND target.PublishedAt < @OccurredAt THEN
            UPDATE SET Name = @Name, PublishedAt = @OccurredAt;
        """;

    // CA1848 (ADR-019).
    private static readonly Action<ILogger, Guid, int, Exception?> NameDropped =
        LoggerMessage.Define<Guid, int>(
            LogLevel.Warning,
            new EventId(1, nameof(NameDropped)),
            "Product {ProductId}'s name is blank or longer than its column's {Width} characters; not written.");

    public async Task HandleAsync(ProductPublished integrationEvent, CancellationToken ct)
    {
        // A line with no name reads productName null (§10.7), which beats an endpoint stalled on one message.
        if (string.IsNullOrWhiteSpace(integrationEvent.Name) ||
            integrationEvent.Name.Length > ProjectionLimits.ProductNameMaxLength)
        {
            NameDropped(log, integrationEvent.ProductId, ProjectionLimits.ProductNameMaxLength, null);
            return;
        }

        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(
            new CommandDefinition(
                UpsertSql,
                new { integrationEvent.ProductId, integrationEvent.Name, integrationEvent.OccurredAt },
                cancellationToken: ct));
    }
}
```

- [ ] **Step 8: Register them, and the bus, on the host**

```csharp
// src/BFF/Web.Bff/Orders/DependencyInjection.cs
using Common.Application;
using Common.Infrastructure.Messaging;

namespace Web.Bff.Orders;

/// <summary>ADR-051's projection: its handlers and their instruments.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddOrderProjection(this IServiceCollection services)
    {
        // §6.2's scan, which reaches only public types, so every handler here is one.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        // The inbox filter's and the consumer's instruments (§13.3).
        services.AddSingleton<MessagingMetrics>();

        return services;
    }
}
```

In `src/BFF/Web.Bff/Program.cs`, add `using Web.Bff.Messaging;` and
`using Web.Bff.Orders;` after `using Web.Bff.Endpoints;`, and immediately
before `WebApplication app = builder.Build();`:

```csharp
// ADR-051's projection and the bus that feeds it; the bus's check joins SQL's in readiness (§13.5).
builder.Services.AddOrderProjection();
builder.Services.AddMassTransitMessaging(builder.Configuration);

```

In `src/BFF/Web.Bff/Web.Bff.csproj`, the `Common.Contracts` reference's
comment becomes ADR-051's sentence rather than ADR-045's prohibition:

```xml
    <!-- OrderLimits (§4.3, ADR-045) and the events ADR-051's projection consumes; nothing else crosses §4.2. -->
```

- [ ] **Step 9: Run the tests to see them pass**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~OrderProjectionTests|FullyQualifiedName~PersistenceRegistrationTests"
dotnet test tests/Web.Bff.Tests --filter "Category!=Integration"
```

Expected: the first run passes 38 tests over SQL Server and RabbitMQ
containers — Docker must be running, and a missing daemon fails on
`Failed to connect to Docker endpoint` rather than skipping; the second
passes every container-free test, the quote, identity and pipeline suites
among them, with `BffFactory`'s unreachable broker.

- [ ] **Step 10: Commit**

```bash
git add src/BFF/Web.Bff tests/Web.Bff.Tests
git commit -m "feat(bff): OrderProjection and ProductNameProjection keep bff.Orders by set-once columns"
```

## Task 7: `bff.orders.unattributed`, its index and its meter

**Files:**
- Create: `src/BFF/Web.Bff/Observability/IProjectionStats.cs`
- Create: `src/BFF/Web.Bff/Observability/ProjectionStats.cs`
- Create: `src/BFF/Web.Bff/Observability/ProjectionMetrics.cs`
- Create: `src/BFF/Web.Bff/Observability/MetricsInitialiser.cs`
- Modify: `src/BFF/Web.Bff/Orders/DependencyInjection.cs`
- Modify: `src/BFF/Web.Bff/Web.Bff.csproj` (`InternalsVisibleTo`)
- Modify: `src/BFF/Web.Bff.Persistence/Configurations/OrderRowConfiguration.cs`
- Create: `src/BFF/Web.Bff.Persistence/Migrations/<timestamp>_IndexUnattributedOrders.cs` and its designer, by `dotnet ef`
- Modify: `src/BFF/Web.Bff.Persistence/Migrations/BffDbContextModelSnapshot.cs`, by `dotnet ef`
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`
- Modify: `tests/Common.Web.Tests/ObservabilityTests.cs`
- Create: `tests/Web.Bff.Tests/UnattributedGaugeTests.cs`
- Create: `tests/Web.Bff.Tests/MetricsRegistrationTests.cs`

- [ ] **Step 1: Write the failing gauge and registration tests**

```csharp
// tests/Web.Bff.Tests/UnattributedGaugeTests.cs
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Web.Bff.Observability;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The age of the oldest row no Ordering event has attributed, read by the registered clock.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class UnattributedGaugeTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void An_empty_projection_reads_zero() =>
        ReadGauge(DateTimeOffset.UtcNow).ShouldBe(0);

    [Fact]
    public async Task An_unowned_row_reads_as_its_age()
    {
        await fixture.DeliverAsync(OrderEvents.Authorised(Guid.CreateVersion7(), At));

        ReadGauge(DateTimeOffset.UtcNow.AddMinutes(10)).ShouldBeInRange(570, 630);
    }

    [Fact]
    public async Task An_owned_row_is_not_counted()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Authorised(order, At));
        await fixture.DeliverAsync(OrderEvents.Placed(order, Guid.CreateVersion7(), At));

        ReadGauge(DateTimeOffset.UtcNow.AddMinutes(10)).ShouldBe(0, "an Ordering event attributed the row");
    }

    /// <summary>One reading over this suite's own stats reader and clock.</summary>
    private double ReadGauge(DateTimeOffset now)
    {
        // The factory has to outlive the collection: a Meter disposed with its factory publishes nothing.
        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        using ProjectionStats stats = new(new SqlConnectionFactory(fixture.ConnectionString), new FrozenClock(now));
        ProjectionMetrics metrics = new(factory, stats, NullLogger<ProjectionMetrics>.Instance);
        metrics.ShouldNotBeNull();

        Meter mine = factory.Create(ProjectionMetrics.MeterName);
        List<double> measured = [];
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine) && instrument.Name == "bff.orders.unattributed")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => measured.Add(value));

        listener.Start();
        listener.RecordObservableInstruments();

        // Fails closed: with nothing enabled there is no reading to assert over.
        return measured.ShouldHaveSingleItem("the listener enabled no unattributed gauge");
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
```

The gauge suite delivers through the queue with the `DeliverAsync` Task 6's
first step added to the fixture.

```csharp
// tests/Web.Bff.Tests/MetricsRegistrationTests.cs
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Web.Bff.Observability;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§13.6's registration rules over a <c>ServiceCollection</c>.</summary>
public sealed class MetricsRegistrationTests
{
    private static ServiceCollection BuildServices()
    {
        ServiceCollection services = new();
        services.AddOrderProjection();
        return services;
    }

    private static Type[] Registered() =>
    [
        .. BuildServices()
            .Select(d => d.ServiceType)
            .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
            .Distinct()
    ];

    [Fact]
    public void Every_metrics_type_is_forced_by_the_initialiser()
    {
        HashSet<Type> forced =
        [
            .. typeof(MetricsInitialiser).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
        ];

        // Both directions: unforced is an instrument that may never exist, forced-but-unregistered a host that
        // will not start.
        Registered().ShouldBe(forced, ignoreOrder: true);
    }

    [Fact]
    public void The_metrics_selector_actually_selects_something()
    {
        Registered().ShouldContain(typeof(MessagingMetrics));
        Registered().ShouldContain(typeof(ProjectionMetrics));
    }

    [Fact]
    public void The_initialiser_is_registered_as_a_hosted_service() =>
        BuildServices()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ShouldContain(typeof(MetricsInitialiser));
}
```

In `tests/Common.Web.Tests/ObservabilityTests.cs`, `Required` gains
`"Web.Bff.Projection",` after `"Notifications.Outbound",`.

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~UnattributedGaugeTests|FullyQualifiedName~MetricsRegistrationTests"
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~ObservabilityTests"
```

Expected: the first fails to build, `The type or namespace name
'Observability' does not exist in the namespace 'Web.Bff'`; the second fails
`Every_meter_an_alert_reads_from_is_collected` with `Web.Bff.Projection`
missing from the exported meters.

- [ ] **Step 3: Write the stats reader, the gauge and the initialiser**

```csharp
// src/BFF/Web.Bff/Observability/IProjectionStats.cs
namespace Web.Bff.Observability;

/// <summary>The question <see cref="ProjectionMetrics"/>' gauge asks of <c>bff.Orders</c>.</summary>
public interface IProjectionStats
{
    /// <summary>Zero when every row has an owner.</summary>
    double UnattributedAgeSeconds();
}
```

```csharp
// src/BFF/Web.Bff/Observability/ProjectionStats.cs
using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Web.Bff.Observability;

/// <summary><see cref="IProjectionStats"/>, cached briefly in Shipping's <c>ShipmentStats</c>' shape.</summary>
/// <remarks>It throws; <see cref="ProjectionMetrics"/> contains that into an absent series (§13.6).</remarks>
internal sealed class ProjectionStats(IDbConnectionFactory connections, TimeProvider clock)
    : IProjectionStats, IDisposable
{
    /// <summary>Inside one export interval, so a repeat callback within it reuses the result.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>Bounded, since a wait inside a gauge callback stalls every other callback in the pass.</summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>Over the filtered index <c>IndexUnattributedOrders</c> adds, so it reads unowned rows alone.</summary>
    private const string OldestSql =
        """
        SELECT MIN(FirstSeenAt)
        FROM bff.Orders
        WHERE CustomerId IS NULL;
        """;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    // The registered clock, which stamped FirstSeenAt, rather than the engine's.
    public double UnattributedAgeSeconds() =>
        _cache.GetOrCreate(nameof(UnattributedAgeSeconds), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = connections.Create();

            DateTimeOffset? oldest = connection.ExecuteScalar<DateTimeOffset?>(
                new CommandDefinition(OldestSql, commandTimeout: CommandTimeoutSeconds));

            return oldest is null ? 0 : Math.Max(0, (clock.GetUtcNow() - oldest.Value).TotalSeconds);
        });

    public void Dispose() => _cache.Dispose();
}
```

```csharp
// src/BFF/Web.Bff/Observability/ProjectionMetrics.cs
using System.Diagnostics.Metrics;

namespace Web.Bff.Observability;

/// <summary>The projection's own gauge, beside the delivery lag every consumer already records (§13.3).</summary>
public sealed class ProjectionMetrics
{
    /// <summary>Must equal the name §13.2's <c>AddMeter</c> registers, or nothing collects the gauge.</summary>
    public const string MeterName = "Web.Bff.Projection";

    // CA1848 (ADR-019), as ShipmentMetrics does.
    private static readonly Action<ILogger, Exception?> GaugeReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(GaugeReadFailed)),
            "Unattributed-order gauge read failed; this collection omits it rather than reporting a zero.");

    public ProjectionMetrics(IMeterFactory factory, IProjectionStats stats, ILogger<ProjectionMetrics> logger)
    {
        Meter meter = factory.Create(MeterName);

        // A row with no owner is invisible to its buyer (§10.7), and no delivery lag shows it.
        meter.CreateObservableGauge(
            "bff.orders.unattributed",
            () => Unattributed(stats, logger),
            unit: "s",
            description: "How long the oldest order with no owner has waited for one.");
    }

    /// <summary>Contained, since the collector abandons its pass on an exception (§13.6).</summary>
    private static List<Measurement<double>> Unattributed(IProjectionStats stats, ILogger logger)
    {
        try
        {
            return [new Measurement<double>(stats.UnattributedAgeSeconds())];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GaugeReadFailed(logger, exception);
            return [];
        }
    }
}
```

```csharp
// src/BFF/Web.Bff/Observability/MetricsInitialiser.cs
using Common.Infrastructure.Messaging;

namespace Web.Bff.Observability;

/// <summary>Constructs each metrics type at startup: an instrument never constructed does not exist (§13.6).</summary>
public sealed class MetricsInitialiser : IHostedService
{
    /// <summary>Resolving the parameters is the whole job; the guards are the read CS9113 asks for.</summary>
    public MetricsInitialiser(MessagingMetrics messaging, ProjectionMetrics projection)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(projection);
    }

    // `cancellationToken`, not `ct`: CA1725 matches the interface's parameter name, and ADR-019 makes it an error.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`AddOrderProjection` gains, after the `MessagingMetrics` line:

```csharp
        // The unattributed gauge, and the start-time construction §13.6 asks of every instrument.
        services.AddSingleton<IProjectionStats, ProjectionStats>();
        services.AddSingleton<ProjectionMetrics>();
        services.AddHostedService<MetricsInitialiser>();
```

with `using Web.Bff.Observability;`.

In `src/BFF/Web.Bff/Web.Bff.csproj`, a new item group after the package
references:

```xml
  <ItemGroup>
    <!-- ProjectionStats and SqlConnectionFactory are internal; the gauge's suite builds both. -->
    <InternalsVisibleTo Include="Web.Bff.Tests" />
  </ItemGroup>
```

In `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`, after the
`Notifications.Outbound` line, the comment starting at the same column as
its neighbours':

```csharp
                .AddMeter("Web.Bff.Projection")                    // ADR-051's projection
```

That is sixteen spaces, the call, twenty spaces, the comment — the same
column `tools/new-service`'s `update_observability_meters` pads to, and
above the blank line its anchor reads, so the next rendered service still
lands in the service-prefixed group.

- [ ] **Step 4: The gauge's index**

In `src/BFF/Web.Bff.Persistence/Configurations/OrderRowConfiguration.cs`,
beside PR-1's `IX_Orders_Owned`:

```csharp
        // The unattributed gauge's seek: unowned rows alone, oldest first.
        builder
            .HasIndex(o => o.FirstSeenAt)
            .HasDatabaseName("IX_Orders_Unattributed")
            .HasFilter("[CustomerId] IS NULL");
```

```bash
dotnet tool restore
dotnet ef migrations add IndexUnattributedOrders --project src/BFF/Web.Bff.Persistence --startup-project src/BFF/Web.Bff.Migrator --output-dir Migrations
```

Expected: one migration whose `Up` creates `IX_Orders_Unattributed` on
`bff.Orders (FirstSeenAt)` with the filter, whose `Down` drops it, and a
snapshot diff of that index alone. Anything else in the diff is PR-1's model
and the snapshot disagreeing, which is a stop, not a commit.

- [ ] **Step 5: Run the tests to see them pass**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~UnattributedGaugeTests|FullyQualifiedName~MetricsRegistrationTests"
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~ObservabilityTests"
```

Expected: 6 passed, then the observability suite green.

- [ ] **Step 6: Commit**

```bash
git add src/BFF/Web.Bff src/BFF/Web.Bff.Persistence src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs tests/Common.Web.Tests/ObservabilityTests.cs tests/Web.Bff.Tests
git commit -m "feat(bff): bff.orders.unattributed, the age of the oldest order with no owner"
```

## Task 8: Over the broker, and the Compose unit's key

**Files:**
- Modify: `tests/Web.Bff.Tests/BffServiceFixture.cs`
- Create: `tests/Web.Bff.Tests/BffOrderEventsTests.cs`
- Modify: `deploy/compose/services/web-bff.yml`
- Modify: `.github/secret-scan/allowed/deploy.txt`

- [ ] **Step 1: The fixture reads the broker**

Add to `BffServiceFixture`, beside the members Task 6 added:

```csharp
    /// <summary>The exchanges bound to one queue, read from the broker itself.</summary>
    public async Task<string[]> BindingsAsync(string queue) =>
    [
        .. (await BrokerRowsAsync(["list_bindings", "source_name", "destination_name"]))
            .Where(columns => columns.Length == 2 && columns[1] == queue)
            .Select(columns => columns[0])
    ];

    /// <summary>One account's grant on the default vhost, as the broker holds it rather than as a file says.</summary>
    public async Task<(string Configure, string Write, string Read)> BrokerPermissionsAsync(string user)
    {
        string[] row = (await BrokerRowsAsync(["list_permissions"]))
            .Single(columns => columns.Length == 4 && columns[0] == user);

        return (row[1], row[2], row[3]);
    }
```

- [ ] **Step 2: Write the failing broker tests**

```csharp
// tests/Web.Bff.Tests/BffOrderEventsTests.cs
using System.Text.Json;
using Common.Contracts.Ordering.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Orders;
using Xunit;
using MessagingRegistration = Web.Bff.Messaging.DependencyInjection;

namespace Web.Bff.Tests;

/// <summary>The BFF's row in §3.2 over a real broker and the real tables, as the narrow account runs them.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class BffOrderEventsTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private const string Account = "bff-svc";

    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The grant the broker is imported from, read where it is owned.</summary>
    private static (string Configure, string Write, string Read) ImportedGrant(string user)
    {
        using JsonDocument definitions = JsonDocument.Parse(
            File.ReadAllText(RepositoryFile.Locate("deploy/compose/rabbitmq/definitions.json")));
        JsonElement grant = definitions.RootElement.GetProperty("permissions")
            .EnumerateArray()
            .Single(p => p.GetProperty("user").GetString() == user && p.GetProperty("vhost").GetString() == "/");

        return (
            grant.GetProperty("configure").GetString()!,
            grant.GetProperty("write").GetString()!,
            grant.GetProperty("read").GetString()!);
    }

    [Fact]
    public async Task The_narrow_account_binds_every_event_in_the_bff_row_to_the_queue()
    {
        // Healthy first, since an endpoint declares its bindings as it starts, and a refused bind never starts.
        BusHealthStatus health = await fixture.Factory.Services.GetRequiredService<IBusControl>()
            .WaitForHealthStatus(BusHealthStatus.Healthy, BffServiceFixture.StepDeadline);
        health.ShouldBe(BusHealthStatus.Healthy, "a refused exchange.bind closes the channel and the endpoint with it");

        string[] bound = await fixture.BindingsAsync(MessagingRegistration.EventsQueue);

        foreach (Type consumed in MessagingRegistrationTests.Consumed)
        {
            string exchange = $"{consumed.Namespace}:{consumed.Name}";
            bound.ShouldContain(exchange, $"{exchange} is in the BFF's Consumes cell and is not bound");
        }
    }

    [Fact]
    public async Task The_account_holds_exactly_the_grant_definitions_json_ships()
    {
        (string configure, string write, string read) = await fixture.BrokerPermissionsAsync(Account);

        (configure, write, read).ShouldBe(ImportedGrant(Account));
        write.ShouldNotContain("Common", Case.Sensitive, "ADR-036: nothing to publish, so no contract to write");
    }

    [Fact]
    public async Task An_order_s_events_through_the_queue_make_the_row_section_10_7_describes()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Published(OrderEvents.Lamp, "Walnut desk lamp", At.AddDays(-30)));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At.AddDays(1)));
        await fixture.DeliverAsync(OrderEvents.Authorised(order, At.AddSeconds(5)));
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, At.AddSeconds(7)));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, At.AddDays(2)));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.CustomerId.ShouldBe(customer);
        row.TrackingNumber.ShouldBe(OrderEvents.TrackingNumber);
        BuyerStatus.Of(new OrderSteps(
            row.PlacedAt,
            row.ConfirmedAt,
            row.DispatchedAt,
            row.DeliveredAt,
            row.CancelledAt,
            row.CancelOutcome)).ShouldBe(BuyerStatuses.Delivered);

        (await fixture.LinesAsync(order)).ShouldHaveSingleItem().ProductId.ShouldBe(OrderEvents.Lamp);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Name FROM bff.Products WHERE ProductId = {0}",
            OrderEvents.Lamp)).ShouldBe("Walnut desk lamp");
    }

    [Fact]
    public async Task A_cancelled_and_refunded_order_reads_its_member_with_the_refund_beside_it()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Refunded(order, At.AddMinutes(3)));
        await fixture.DeliverAsync(OrderEvents.Cancelled(
            order,
            customer,
            At.AddMinutes(2),
            CancelReasons.PaymentDeclined,
            CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.CancelOutcome.ShouldBe(BuyerStatuses.Declined);
        row.RefundedAt.ShouldBe(At.AddMinutes(3), "a refund is a flag beside the status, never a member of it (§10.7)");
    }
}
```

- [ ] **Step 3: Run them**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~BffOrderEventsTests|FullyQualifiedName~UnattributedGaugeTests"
```

Expected: 7 passed. A binding failure here is the measurement Notifications'
PR-4 made for a pure consumer, repeated for `ProductPublished` and
`PaymentAuthorised`, which no pure consumer bound before: it means the
account needs a grant it lacks, and the fix is the account, never a widened
harness.

- [ ] **Step 4: The Compose unit's broker key**

In `deploy/compose/services/web-bff.yml`, under `environment:` after PR-1's
SQL key, copy `ConnectionStrings__RabbitMq`'s line from
`deploy/compose/services/notifications.yml` with `notifications` replaced by
`bff` in both the account and its password, and above it, inside the
five-line budget:

```yaml
      # The bus (§9), under the BFF's own account, which writes no contract (ADR-036, ADR-051).
      # A plain value, §14.1's local default; AddMassTransitMessaging throws without it.
```

and under `depends_on:`:

```yaml
      rabbitmq: { condition: service_healthy }
```

- [ ] **Step 5: The secret scan's entry for the Compose key**

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: one finding in `deploy/compose/services/web-bff.yml`,
`credential-assignment`, with a fingerprint. Append to
`.github/secret-scan/allowed/deploy.txt`, beside `notifications.yml`'s
broker entry:

```
deploy/compose/services/web-bff.yml | credential-assignment | <the fingerprint the scan printed> | Section 14.1's broker default for the BFF, the per-host account ADR-051 adds.
```

Run the scan again: `0 finding(s)`.

- [ ] **Step 6: Run the stack**

```bash
docker compose -f deploy/compose/docker-compose.yml config --quiet
docker compose -f deploy/compose/docker-compose.yml up -d --wait --build web-bff
docker compose -f deploy/compose/docker-compose.yml exec rabbitmq rabbitmqctl list_bindings source_name destination_name
docker compose -f deploy/compose/docker-compose.yml down
```

Expected: the config parses; `web-bff` reaches healthy after its migrator and
the broker; the binding list shows the eight contract exchanges against
`bff-order-events`. A host RabbitMQ holding 5672 is the known local conflict:
bring the stack up with a scratchpad-only port override, never an edit to
the unit.

- [ ] **Step 7: Commit**

```bash
git add tests/Web.Bff.Tests/BffServiceFixture.cs tests/Web.Bff.Tests/BffOrderEventsTests.cs deploy/compose/services/web-bff.yml .github/secret-scan/allowed/deploy.txt
git commit -m "test(bff): bff-order-events over a real broker under bff-svc, and the Compose unit's broker key"
```

## Task 9: The chapters, the ADR and the secrets rows

**Files:**
- Modify: `docs/backend-architecture/02-architecture-at-a-glance.md`
- Modify: `docs/backend-architecture/03-bounded-contexts.md`
- Modify: `docs/backend-architecture/12-test-strategy.md`
- Modify: `docs/backend-architecture/14-local-development.md`
- Modify: `docs/backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md`
- Modify: `docs/secrets.md`
- Modify: `.github/secret-scan/allowed/docs.txt`

Each names a set or cites ADR-051, never a count (spec section 12).

- [ ] **Step 1: §2.2**

PR-1 drew the database edge, `BFF --> SQL`, and left the label. In the
container view, `BFF[Web BFF<br/>aggregation only]` becomes:

```
        BFF[Web BFF<br/>aggregation and the order projection]
```

and after `NOT <--> MQ`:

```
    MQ --> BFF
```

One direction, because the BFF consumes and publishes nothing (ADR-051).

- [ ] **Step 2: §3.2**

After the Notifications row of the table:

```
| **Web.Bff** — a host, not a service (§10.1) | The buyer's order projection, a read model owning no fact ([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)) | — | `OrderPlaced`, `OrderConfirmed`, `OrderCancelled`, `PaymentAuthorised`, `PaymentRefunded`, `ShipmentDispatched`, `ShipmentDelivered`, `ProductPublished` | — |
```

Every name in it already has a Publishes cell, so the table still closes in
both directions. In the paragraph beginning "Note the shapes this produces",
the sentence

> Only Notifications is a *pure* consumer, though: the table above gives
> Shipping two events to publish, and ADR-051's projection is built on them.

becomes

> Only Notifications is a *pure* consumer among the services, though: the
> table above gives Shipping two events to publish, and ADR-051's
> projection — the BFF's row, a host's rather than a service's — is built
> on them.

- [ ] **Step 3: §12.1**

PR-1 added the schema level. In the pyramid's table, its row

```
| Host projection | The BFF's own schema (ADR-051): the migrator's run, the tables' constraints and the inbox purge | Real SQL Server (container), `WebApplicationFactory` | < 1 s | One suite | `Web.Bff.Tests` |
```

becomes:

```
| Host projection | The BFF's own schema and the consumers that write it (ADR-051): the migrator's run, each handler against every row shape another leaves, the broker binding under its account | Real SQL Server and RabbitMQ (containers), `WebApplicationFactory` | < 1 s | One suite | `Web.Bff.Tests` |
```

- [ ] **Step 4: §14.1**

In the model's fence, the `web-bff` block gains, after PR-1's SQL key, the
same line Task 8 wrote into `deploy/compose/services/web-bff.yml` — the
broker key under the BFF's account, copied from that file rather than from
this plan — and its `depends_on` gains `rabbitmq: { condition: service_healthy }`
beside PR-1's migrator entry.

- [ ] **Step 5: §14.2**

PR-1 gave the sample the BFF's database and migrator. The `web-bff` resource
gains the broker reference every service resource in the sample takes, after
PR-1's database reference:

```csharp
        .WithReference(mq).WaitFor(mq)
```

and the comment above the resource, which speaks of client credentials, is
unchanged.

- [ ] **Step 6: ADR-036's callout**

Before the closing `---`, in ADR-045's callout form:

```markdown
> **The BFF holds an account under this record, and nothing here has been
> edited.** [ADR-051](ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)
> gives `Web.Bff` consumers, so it authenticates as `bff-svc`, which writes
> its own `bff-` endpoints and the fault exchanges and no contract: it
> publishes nothing, the case this record argued from Notifications.
> `check_permissions.py` reads the host's `Messaging` directory by the same
> derivation as a service's, so the account is held to its source as theirs
> are.
```

- [ ] **Step 7: `docs/secrets.md`**

Under *A broker credential*, the opening sentence becomes:

```markdown
`ConnectionStrings__RabbitMq`, and there is **one per account
`definitions.json` declares** — each service's, and the BFF's under
[ADR-051](backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)
— since
[ADR-036](backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md):
`catalog-rabbitmq` and `ordering-rabbitmq`, never a shared Secret.
```

the rest of the paragraph unchanged. In the local-development table, the
RabbitMQ cell gains the BFF's pair after the pairs it already lists, in the
form those pairs take, with `bff` as the name: the account `bff-svc` and the
password `local-dev-<name>` derives, which is the one `ServiceFixture`
derives (`ServiceFixture.cs:181`) and `definitions.json`'s hash encodes.
This plan names the derivation rather than printing the value, because the
secret scan reads `docs/superpowers/` too.

The password reaches the broker by `definitions.json`'s import, so it has no
`${…}` seam, and the paragraph below that table already says why.

- [ ] **Step 8: The checks this class owes**

```bash
py -3.12 .github/licence-gate/licence_gate.py
py -3.12 .github/secret-scan/secret_scan.py
```

The scan names the broker default §14.1's fence now prints, as it named the
Compose unit's in Task 8. Append to `.github/secret-scan/allowed/docs.txt`,
beside the chapter's other local defaults:

```
docs/backend-architecture/14-local-development.md | credential-assignment | <the fingerprint the scan printed> | The BFF's broker default, as Section 14.1 prints the compose file it specifies (ADR-051).
```

and an entry of the same shape for `docs/secrets.md` if the scan names that
file's new table cell — an entry that matches no finding fails the build, so
add only what the scan prints. Run the scan again: `0 finding(s)`.

then run `/validate-blueprint` — chapters 2, 3, 12 and 14 are in its scope —
and `/check-links`, since §3.2 and ADR-036 gain links. Fix what either finds
and run it again.

Expected: both clean; the licence gate green, since no chapter prints a pin.

- [ ] **Step 9: Commit**

```bash
git add docs/backend-architecture docs/secrets.md .github/secret-scan/allowed/docs.txt
git commit -m "docs: §2.2, §3.2, §12.1, §14.1, §14.2 and ADR-036 name the BFF's consumers and bff-svc (ADR-051)"
```

## Task 10: Everything run, and the PR

- [ ] **Step 1: The whole solution and both gates**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 -m unittest discover -s deploy/canary
py -3.12 deploy/canary/canary.py check
bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 -m unittest discover -s tools/new-service
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: 0 warnings; every suite green with Docker running; the permission
gate, the canary check, `smoke.sh` and the secret scan clean; the scaffold's
suite green, its render's `AddMeter` insertion still
finding the shared block's anchor; the comment gate clean over every line
this branch adds.

- [ ] **Step 2: The PR**

`/pr`, with the class row `A+D+E` and the touch-set row exactly the Global
Constraints line, reasons under the table. The body carries `Closes #<this
PR's issue>` as a bare line and no closing keyword for #425, which the last
PR of the sequence closes.

## Self-review

- **Spec section 1.** The names: `bff-svc`, `bff-order-events` (Tasks 5, 8).
  Rank stored as set-once step columns and computed by `BuyerStatus.Of`, with
  `delivered` above the cancellation members (Tasks 3, 6). The cancellation
  member decided at write by `CancellationOutcome.Of` and stored, an unknown
  reason under `workflow` reading `cancelled`, its strings `CancelOutcomes`'
  (Tasks 2, 6). Lines written by whichever line-carrying event lands first,
  keyed by position, after the order's row as the cascading key requires
  (Task 6). Product names guarded on `OccurredAt` (Task 6). Delivery lag
  needs nothing new; the gauge is Task 7.
- **Spec section 3.** The account in its narrowest form (Task 5); the gate
  told by a glob over the hosts' tree, keyed by the tree's name, with the
  existing `publishes` selector unchanged, each half proved by a case whose
  subject is the selector (Tasks 4, 5). `BffFactory`'s placeholder broker key,
  carried by `MissingSettingFactory` and inherited by `NoDatabaseFactory`
  (Task 6). No contract, realm object or route moves.
- **Spec section 4.** The chart's `broker` block and the descriptor's
  `consume` signal share the commit that first reads the broker and
  registers a consumer, which is the one `smoke.sh` and `canary.py` would
  otherwise fail (Task 5).
- **Spec section 5.** `PaymentCurrency` written set-once by either payment
  event, and a payment whose currency cannot be stored writes nothing rather
  than an amount the schema refuses (Task 6); `CustomerId` written once and a
  disagreeing one logged with ids alone (Task 6); `IndexUnattributedOrders`
  (Task 7).
- **Spec section 6.** One queue, the inbox outside the in-memory outbox, a
  bare retry and no redelivery, and the `DbContext` alias beside the filter
  that needs it (Task 5); one statement batch per event, set and never
  overwrite (Task 6); every listed pair applied in both orders (Task 6);
  registration and binding asserted (Tasks 5, 8).
- **Spec section 9.** `ConnectionStrings__RabbitMq` under `bff-svc` (Task 8);
  the chart's broker half (Task 5); the password is a local default imported
  from `definitions.json`, so it reaches the unit, §14.1, §14.2's reference
  and the fixture, and the chart names its Secret. This plan prints no
  default: each is copied from the file that already holds one, and each new
  printing in `deploy/` and `docs/` takes its allow entry from the scan's own
  fingerprint (Tasks 5, 8, 9).
- **Spec section 10.** `bff.orders.unattributed` on `Web.Bff.Projection`,
  seconds, zero when empty, by the registered clock, with its `AddMeter`
  line (Task 7). The rule and runbook are PR-5's.
- **Spec section 11.** Container-free: the map, the rank, and
  registration, which composes over the in-memory harness. Over SQL: each
  handler, redelivery, the pairs, the disagreeing customer, the gauge. Over
  SQL and RabbitMQ: binding, the grant, an order's events
  through the queue, as section 11 says: under `bff-svc` a publish is
  refused by the grant the binding test measures, and widening it in the
  harness would measure a grant nothing deploys. So they are sent to the
  queue, which runs the same endpoint, filter and handlers, and the binding
  test proves the exchange-to-queue half.
- **Spec section 12, the rows taken by 2.** §2.2's broker edge and label,
  §3.2, §12.1's consumer level, §14.1's broker
  key, §14.2's broker reference, ADR-036's callout, `Web.Bff.csproj`'s
  sentence (Tasks 6, 9). `docs/secrets.md`'s rows (Task 9).
- **Not here, by the spec.** The routes, `cancellable` and the cursor
  (PR-3); the rebuild (PR-4); the alert, its runbook and its panel (PR-5).
