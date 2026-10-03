# BFF order read PR-3 — `GET /v1/orders` and `/v1/orders/{id}` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve §10.7's two buyer routes from the projection PR-2 fills.
`GET /v1/orders` returns the caller's own orders as a `CursorPage<T>`,
newest first by `FirstSeenAt`, clamped by a constant the read owns, in two
round trips whatever the page size; `GET /v1/orders/{id}` returns one order
with its lines' quantities and unit prices, its payment and its shipment, and
answers 404 for an order somebody else owns and for one nobody owns by the
same query. The subject is the principal's and never the request's. Neither
route makes an outbound call. §10.7 gains the sentence that an owned order no
placed or confirmed event has reached carries no lines and a null total.

**Architecture:** the read lives in `Web.Bff.Orders`, PR-2's namespace, beside
`BuyerStatus` and `BuyerStatuses`, which it calls rather than repeats. One
`OrderReader` over PR-1's `IDbConnectionFactory` runs Dapper against
`bff.Orders`, `bff.OrderLines` and `bff.Products`: a keyset seek on
`IX_Orders_Owned` fetching one row more than the page, then one statement for
every line and name on the page, keyed by the page's ids as **one** JSON
parameter read through `OPENJSON` — §6.6's escalated history query, one host
out. The detail is one batch of two statements, both filtered on id **and**
customer. A pure `OrderView` turns the rows into §10.7's wire shape and holds
every rule the response states, so those rules are tested without a
container. `OrderEndpoints` is checkout's group form: `RequireAuthorization()`
at the group, the cursor through `Common.Application.Cursor`, the 404 through
`Result<T>.ToHttpResult()` and an `Error` catalogue of the read's own.

**Tech Stack:** ASP.NET Core minimal APIs, Dapper over SQL Server 2022
(`OPENJSON`, keyset seek), xUnit v3 with Shouldly, Testcontainers through
PR-1's `BffServiceFixture` and PR-2's delivery helpers, Markdown.

**Spec:** `docs/superpowers/specs/2026-10-03-bff-order-read-design.md`,
sections 1 (what an owned row shows before its lines arrive, the list's
order, product names, authorisation), 2 (the wire shape, both routes), 4
(PR-3's row), 5 (the columns read, `PaymentCurrency`), 7 (the read), 11 (the
host tests) and 12 (§10.7's sentence and §10.1's re-read).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D.** Touch set:

  `src/BFF/Web.Bff/**`, `tests/Web.Bff.Tests/**`, `docs/backend-architecture/10-api-gateway.md`

  Why each, since the row is paths only. A is the BFF's tree — the reader,
  its view and response types, its error catalogue and the page bounds under
  `Orders/`, the routes under `Endpoints/`, the host's two lines in
  `Program.cs` — and its one suite. D is the one chapter spec section 12
  gives this PR: §10.7's field list gains its sentence, and §10.1 is re-read
  in the same file and left as it reads (Task 6 says why).
- **Mutexes**: `src/BFF/Web.Bff/Program.cs`, the composition root. No
  repo-wide mutex surface: no package, no project, no migration. Dapper is
  referenced by PR-2.
- **Depends on PR-2 having merged**, and therefore on PR-1. These are the
  names this plan consumes, as those plans spell them; Task 1 reads each on
  disk and stops on a miss:
  - PR-1: `Web.Bff.Persistence` with `BffSchema.Name`, `OrderRow`'s columns —
    `OrderId`, `CustomerId`, `Currency`, `TotalAmount`, `PlacedAt`,
    `ConfirmedAt`, `DispatchedAt`, `DeliveredAt`, `CancelledAt`,
    `CancelOutcome`, `AuthorisedAt`, `AuthorisedAmount`, `RefundedAt`,
    `RefundedAmount`, `PaymentCurrency`, `TrackingNumber`, `FirstSeenAt`,
    `AsOf` — `OrderLineRow`'s `OrderId`, `LineNumber`, `ProductId`,
    `Quantity`, `UnitPrice`, `ProductRow`'s `ProductId`, `Name`; the index
    `IX_Orders_Owned` on `(CustomerId, FirstSeenAt DESC, OrderId DESC)`
    filtered to `CustomerId IS NOT NULL`; `CK_Orders_Total`, which holds
    `Currency` and `TotalAmount` together; an `IDbConnectionFactory`
    singleton registered by `AddBffPersistence`; `BffServiceFixture` and
    `BffIntegrationCollection`, whose member classes are
    `Category=Integration`.
  - PR-2: `Web.Bff.Orders.BuyerStatuses` (`Placed`, `Confirmed`,
    `Dispatched`, `Delivered`, `Cancelled`, `OutOfStock`, `Declined`),
    `Web.Bff.Orders.OrderSteps` and `Web.Bff.Orders.BuyerStatus.Of`, which
    returns null only for a row no step has reached; the handlers filling
    the three tables; `BffServiceFixture.DeliverAsync<T>`, which sends an
    event to `bff-order-events` and returns once its inbox row exists, and
    `BffServiceFixture.OrderAsync`; `tests/Web.Bff.Tests/OrderEvents.cs`
    (`Placed`, `Confirmed`, `Cancelled`, `Authorised`, `Refunded`,
    `Dispatched`, `Delivered`, `Published`, the constants `Total`,
    `Currency`, `TrackingNumber` and the product `Lamp`); the readiness set
    `["sql", "masstransit-bus"]`; `BffFactory`'s placeholder broker key, so
    container-free suites still start.
- **Three spec decisions this plan carries out**, each in its task: an
  unreadable cursor gets the first page through `Common.Application.Cursor`
  (spec section 7; Task 3); neither GET declares a retry safety, because
  ADR-058's rule selects writes and a GET is none (spec section 7; Task 5);
  `unitPrice` is a money object, as every amount on the wire is (spec
  section 2; Task 2).
- **This plan prints no credential and no connection string**: the secret
  scan reads `docs/superpowers/` too. Nothing here needs one.
- **Comments obey `docs/style-guide.md`'s *Comments* budget**: a summary is
  one sentence, a `<remarks>` cites and is four lines, a block is five. The
  code below is written to it; run the comment gate after committing.
- `py -3.12`, never `python`. Container tests are
  `[Collection(nameof(BffIntegrationCollection))]` and never skipped.

## Task 1: PR-2's names, verified on disk

**Files:** none changed.

- [ ] **Step 1: Read each name this plan consumes**

```bash
git fetch origin main
git log --oneline -1 origin/main
grep -rn "PaymentCurrency\|FirstSeenAt\|AsOf" src/BFF/Web.Bff.Persistence/OrderRow.cs
grep -rn "IX_Orders_Owned" src/BFF/Web.Bff.Persistence/Configurations
grep -rn "class BuyerStatuses\|record struct OrderSteps\|class BuyerStatus\b" src/BFF/Web.Bff/Orders
grep -rn "IDbConnectionFactory" src/BFF/Web.Bff/BffPersistence.cs
grep -rn "DeliverAsync\|OrderAsync" tests/Web.Bff.Tests/BffServiceFixture.cs
grep -n "Placed(\|Cancelled(\|Authorised(\|Refunded(\|Dispatched(\|Delivered(\|Published(" \
    tests/Web.Bff.Tests/OrderEvents.cs
grep -n "MapCheckoutEndpoints\|AddOrderProjection" src/BFF/Web.Bff/Program.cs
```

Expected: every line prints at least one match.

- [ ] **Step 2: Reconcile or stop**

A missing name is PR-1 or PR-2 unfinished: stop and report it, because every
task below writes against it. A renamed one is reconciled here, once, and the
rename is named in the first commit's body.

## Task 2: The wire shape and the view that fills it

**Files:**
- Create: `src/BFF/Web.Bff/Orders/OrderResponses.cs`
- Create: `src/BFF/Web.Bff/Orders/OrderReadRows.cs`
- Create: `src/BFF/Web.Bff/Orders/OrderView.cs`
- Create: `tests/Web.Bff.Tests/OrderViewTests.cs`

Every rule the response states lives in `OrderView`, a pure function of the
rows, so each is tested here without a container and the reader below only
fetches. **`unitPrice` is a `Money`**, as spec section 2 says: §10.7 has
every amount travel with its currency, so a bare number here would be the
one amount on the wire a client had to label itself.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Web.Bff.Tests/OrderViewTests.cs
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§10.7's response rules over the rows alone, so none of them needs a database to hold.</summary>
public sealed class OrderViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid Order = Guid.Parse("0192f1c4-0000-7000-8000-000000000001");

    private static readonly Guid Lamp = Guid.Parse("0192f1b0-0000-7000-8000-00000000a1a1");

    private static OrderReadRow Placed() => new()
    {
        OrderId = Order,
        Currency = "GBP",
        TotalAmount = 59.97m,
        PlacedAt = At,
        FirstSeenAt = At,
        AsOf = At.AddSeconds(1)
    };

    private static OrderLineReadRow Line(string? name = "Walnut desk lamp") => new()
    {
        OrderId = Order,
        LineNumber = 0,
        ProductId = Lamp,
        Quantity = 3,
        UnitPrice = 19.99m,
        ProductName = name
    };

    [Fact]
    public void A_placed_order_carries_its_total_its_lines_and_the_server_s_line_total()
    {
        OrderSummary order = OrderView.Summary(Placed(), [Line()]);

        order.Status.ShouldBe(BuyerStatuses.Placed);
        order.Total.ShouldBe(new Money(59.97m, "GBP"));
        order.Lines.ShouldHaveSingleItem().LineTotal.ShouldBe(new Money(59.97m, "GBP"));
        order.Lines[0].ProductName.ShouldBe("Walnut desk lamp");
        order.AsOf.ShouldBe(At.AddSeconds(1));
    }

    [Fact]
    public void An_owned_row_only_a_cancellation_reached_has_no_total_and_no_lines()
    {
        OrderReadRow row = new()
        {
            OrderId = Order,
            CancelledAt = At,
            CancelOutcome = BuyerStatuses.Declined,
            FirstSeenAt = At,
            AsOf = At
        };

        OrderSummary order = OrderView.Summary(row, []);

        order.Status.ShouldBe(BuyerStatuses.Declined);
        order.Total.ShouldBeNull();
        order.Lines.ShouldBeEmpty();
        order.Timeline.Cancelled.ShouldBe(At);
    }

    [Fact]
    public void Lines_read_before_the_total_was_known_are_not_shown_without_their_currency() =>
        OrderView.Summary(Placed() with { Currency = null, TotalAmount = null }, [Line()]).Lines.ShouldBeEmpty(
            "the two statements read two snapshots, and an amount with no currency is one the client cannot render");

    [Fact]
    public void A_product_Catalog_has_not_named_reads_as_a_null_name() =>
        OrderView.Summary(Placed(), [Line(name: null)]).Lines[0].ProductName.ShouldBeNull();

    [Theory]
    [InlineData(BuyerStatuses.Placed, true)]
    [InlineData(BuyerStatuses.Confirmed, true)]
    [InlineData(BuyerStatuses.Dispatched, false)]
    [InlineData(BuyerStatuses.Delivered, false)]
    [InlineData(BuyerStatuses.Cancelled, false)]
    [InlineData(BuyerStatuses.OutOfStock, false)]
    [InlineData(BuyerStatuses.Declined, false)]
    public void Cancellable_is_Order_Cancel_s_rule_read_from_the_status(string status, bool expected) =>
        OrderView.IsCancellable(status).ShouldBe(expected);

    [Fact]
    public void The_timeline_carries_each_step_s_own_instant_and_the_status_the_highest()
    {
        OrderReadRow row = Placed() with
        {
            ConfirmedAt = At.AddMinutes(1),
            DispatchedAt = At.AddMinutes(2),
            CancelledAt = At.AddMinutes(3),
            CancelOutcome = BuyerStatuses.Cancelled
        };

        OrderSummary order = OrderView.Summary(row, [Line()]);

        order.Status.ShouldBe(BuyerStatuses.Cancelled, "a cancellation outranks a despatch (§10.7)");
        order.Cancellable.ShouldBeFalse();
        order.Timeline.ShouldBe(new OrderTimeline(At, At.AddMinutes(1), At.AddMinutes(2), null, At.AddMinutes(3)));
    }

    [Fact]
    public void A_refund_is_a_flag_and_an_instant_beside_the_status()
    {
        OrderSummary order = OrderView.Summary(
            Placed() with { RefundedAt = At.AddDays(1), RefundedAmount = 59.97m, PaymentCurrency = "GBP" },
            [Line()]);

        order.Refunded.ShouldBeTrue();
        order.RefundedAt.ShouldBe(At.AddDays(1));
        order.Status.ShouldBe(BuyerStatuses.Placed);
    }

    [Fact]
    public void The_detail_adds_quantity_and_unit_price_per_line()
    {
        OrderLineDetail line = OrderView.Detail(Placed(), [Line()]).Lines.ShouldHaveSingleItem();

        line.Quantity.ShouldBe(3);
        line.UnitPrice.ShouldBe(new Money(19.99m, "GBP"));
        line.LineTotal.ShouldBe(new Money(59.97m, "GBP"));
    }

    [Fact]
    public void The_detail_has_no_payment_and_no_shipment_until_their_events()
    {
        OrderDetail order = OrderView.Detail(Placed(), [Line()]);

        order.Payment.ShouldBeNull();
        order.Shipment.ShouldBeNull();
    }

    [Fact]
    public void An_authorisation_is_labelled_with_the_payment_events_own_currency()
    {
        OrderDetail order = OrderView.Detail(
            Placed() with { AuthorisedAt = At.AddSeconds(5), AuthorisedAmount = 59.97m, PaymentCurrency = "GBP" },
            [Line()]);

        order.Payment.ShouldBe(new PaymentFacts(At.AddSeconds(5), new Money(59.97m, "GBP"), RefundedAmount: null));
    }

    [Fact]
    public void A_refund_that_beat_its_authorisation_shows_the_refund_half_alone()
    {
        OrderDetail order = OrderView.Detail(
            Placed() with { RefundedAt = At.AddDays(1), RefundedAmount = 59.97m, PaymentCurrency = "GBP" },
            [Line()]);

        order.Payment.ShouldBe(new PaymentFacts(AuthorisedAt: null, Amount: null, new Money(59.97m, "GBP")));
        order.Refunded.ShouldBeTrue();
    }

    [Fact]
    public void A_despatch_gives_the_shipment_its_tracking_number()
    {
        OrderDetail order = OrderView.Detail(
            Placed() with { DispatchedAt = At.AddDays(1), TrackingNumber = "SIM-4F2A9C" },
            [Line()]);

        order.Shipment.ShouldBe(new ShipmentFacts("SIM-4F2A9C", At.AddDays(1), DeliveredAt: null));
        order.Status.ShouldBe(BuyerStatuses.Dispatched);
    }

    [Fact]
    public void A_despatch_whose_number_was_not_stored_still_has_its_shipment()
    {
        // PR-2's handler drops a tracking number wider than its column and still records the despatch.
        OrderDetail order = OrderView.Detail(Placed() with { DispatchedAt = At.AddDays(1) }, [Line()]);

        order.Shipment.ShouldBe(new ShipmentFacts(TrackingNumber: null, At.AddDays(1), DeliveredAt: null));
    }

    [Fact]
    public void An_owned_row_no_step_has_reached_is_a_handler_defect_and_says_so() =>
        Should.Throw<InvalidOperationException>(() =>
                OrderView.Summary(new OrderReadRow { OrderId = Order, FirstSeenAt = At, AsOf = At }, []))
            .Message.ShouldContain("no step");
}
```

- [ ] **Step 2: Run it to see it fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~OrderViewTests"
```

Expected: build failure, `The type or namespace name 'OrderReadRow' could not
be found`.

- [ ] **Step 3: Write the response types, the rows and the view**

```csharp
// src/BFF/Web.Bff/Orders/OrderResponses.cs
namespace Web.Bff.Orders;

/// <summary>An amount with its currency, as the server computed it; the client never computes (§10.7).</summary>
public sealed record Money(decimal Amount, string Currency);

/// <summary>Each rank step's instant, keyed by name so a client never draws by position (§10.7).</summary>
public sealed record OrderTimeline(
    DateTimeOffset? Placed,
    DateTimeOffset? Confirmed,
    DateTimeOffset? Dispatched,
    DateTimeOffset? Delivered,
    DateTimeOffset? Cancelled);

/// <summary>One line as the list carries it; the name resolves on read, null before Catalog's event (§10.7).</summary>
public sealed record OrderLineSummary(Guid ProductId, string? ProductName, Money LineTotal);

/// <summary>One order as both routes carry it (§10.7, ADR-051).</summary>
/// <param name="Total">Null, and <paramref name="Lines"/> empty, while the order has no stored currency.</param>
/// <param name="AsOf">The BFF's clock at the row's last write: when it learned, not that nothing followed.</param>
public sealed record OrderSummary(
    Guid OrderId,
    string Status,
    OrderTimeline Timeline,
    bool Refunded,
    DateTimeOffset? RefundedAt,
    bool Cancellable,
    Money? Total,
    IReadOnlyList<OrderLineSummary> Lines,
    DateTimeOffset AsOf);

/// <summary>One line as the detail carries it, with what it was placed at (§10.7).</summary>
public sealed record OrderLineDetail(
    Guid ProductId,
    string? ProductName,
    Money LineTotal,
    int Quantity,
    Money UnitPrice);

/// <summary>The payment outcome there is to give; a decline has none, and the status says so (§10.7).</summary>
public sealed record PaymentFacts(DateTimeOffset? AuthorisedAt, Money? Amount, Money? RefundedAmount);

/// <summary>Shipping's two milestones and the number a buyer takes to the carrier, null if unstorable (§10.7).</summary>
public sealed record ShipmentFacts(string? TrackingNumber, DateTimeOffset? DispatchedAt, DateTimeOffset? DeliveredAt);

/// <summary>One order as the detail route carries it: the summary's members and the three it adds (§10.7).</summary>
public sealed record OrderDetail(
    Guid OrderId,
    string Status,
    OrderTimeline Timeline,
    bool Refunded,
    DateTimeOffset? RefundedAt,
    bool Cancellable,
    Money? Total,
    IReadOnlyList<OrderLineDetail> Lines,
    DateTimeOffset AsOf,
    PaymentFacts? Payment,
    ShipmentFacts? Shipment);
```

```csharp
// src/BFF/Web.Bff/Orders/OrderReadRows.cs
namespace Web.Bff.Orders;

/// <summary>One <c>bff.Orders</c> row as the read selects it; the customer is the query's, never read back.</summary>
public sealed record OrderReadRow
{
    public Guid OrderId { get; init; }

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
}

/// <summary>One <c>bff.OrderLines</c> row with the name <c>bff.Products</c> holds for it, if any.</summary>
public sealed record OrderLineReadRow
{
    public Guid OrderId { get; init; }

    public int LineNumber { get; init; }

    public Guid ProductId { get; init; }

    public int Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public string? ProductName { get; init; }
}
```

```csharp
// src/BFF/Web.Bff/Orders/OrderView.cs
namespace Web.Bff.Orders;

/// <summary>§10.7's wire shape from the projection's rows: every rule the response states, and no query.</summary>
public static class OrderView
{
    public static OrderSummary Summary(OrderReadRow row, IReadOnlyList<OrderLineReadRow> lines)
    {
        string status = StatusOf(row);

        return new OrderSummary(
            row.OrderId,
            status,
            TimelineOf(row),
            row.RefundedAt is not null,
            row.RefundedAt,
            IsCancellable(status),
            TotalOf(row),
            [.. Priced(row, lines).Select(l => new OrderLineSummary(l.ProductId, l.ProductName, LineTotal(l, row)))],
            row.AsOf);
    }

    public static OrderDetail Detail(OrderReadRow row, IReadOnlyList<OrderLineReadRow> lines)
    {
        string status = StatusOf(row);

        return new OrderDetail(
            row.OrderId,
            status,
            TimelineOf(row),
            row.RefundedAt is not null,
            row.RefundedAt,
            IsCancellable(status),
            TotalOf(row),
            [
                .. Priced(row, lines).Select(l => new OrderLineDetail(
                    l.ProductId,
                    l.ProductName,
                    LineTotal(l, row),
                    l.Quantity,
                    new Money(l.UnitPrice, row.Currency!)))
            ],
            row.AsOf,
            PaymentOf(row),
            ShipmentOf(row));
    }

    /// <summary><c>Order.Cancel</c>'s rule from a projection that lags it: a hint, not an authority (§10.7).</summary>
    public static bool IsCancellable(string status) =>
        status is BuyerStatuses.Placed or BuyerStatuses.Confirmed;

    private static string StatusOf(OrderReadRow row) =>
        BuyerStatus.Of(new OrderSteps(
                row.PlacedAt,
                row.ConfirmedAt,
                row.DispatchedAt,
                row.DeliveredAt,
                row.CancelledAt,
                row.CancelOutcome)) ??
            throw new InvalidOperationException(
                $"Order {row.OrderId} is owned and has no step. Only the three Ordering events set a " +
                "customer, and each sets its own step in the same statement (ADR-051), so this row " +
                "was written by something other than the projection's handlers.");

    private static OrderTimeline TimelineOf(OrderReadRow row) =>
        new(row.PlacedAt, row.ConfirmedAt, row.DispatchedAt, row.DeliveredAt, row.CancelledAt);

    private static Money? TotalOf(OrderReadRow row) =>
        row.Currency is null ? null : new Money(row.TotalAmount!.Value, row.Currency);

    // Lines and the currency commit together, but the read takes two snapshots, so lines met first wait for it.
    private static IEnumerable<OrderLineReadRow> Priced(OrderReadRow row, IReadOnlyList<OrderLineReadRow> lines) =>
        row.Currency is null ? [] : lines.OrderBy(l => l.LineNumber);

    private static Money LineTotal(OrderLineReadRow line, OrderReadRow row) =>
        new(line.UnitPrice * line.Quantity, row.Currency!);

    private static PaymentFacts? PaymentOf(OrderReadRow row) =>
        row.PaymentCurrency is null
            ? null
            : new PaymentFacts(
                row.AuthorisedAt,
                row.AuthorisedAmount is { } authorised ? new Money(authorised, row.PaymentCurrency) : null,
                row.RefundedAmount is { } refunded ? new Money(refunded, row.PaymentCurrency) : null);

    private static ShipmentFacts? ShipmentOf(OrderReadRow row) =>
        (row.DispatchedAt ?? row.DeliveredAt) is null
            ? null
            : new ShipmentFacts(row.TrackingNumber, row.DispatchedAt, row.DeliveredAt);
}
```

`PaymentOf` keys on `PaymentCurrency` because PR-1's
`CK_Orders_PaymentCurrency` holds it present exactly when either amount is,
so it is the one column that says a payment event has reached the row.
`ShipmentOf` keys on the two shipment steps, `DispatchedAt ?? DeliveredAt`,
because each shipment event sets its own step and that is what spec section 2
means by "null until either shipment event". It does not key on
`TrackingNumber`: PR-2's handlers drop a number wider than its column and
still record the step, so the number is nullable in `ShipmentFacts` and null
only in that case.

- [ ] **Step 4: Run it to see it pass**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~OrderViewTests"
```

Expected: 20 passed.

- [ ] **Step 5: Commit**

```bash
git add src/BFF/Web.Bff/Orders/OrderResponses.cs src/BFF/Web.Bff/Orders/OrderReadRows.cs \
    src/BFF/Web.Bff/Orders/OrderView.cs tests/Web.Bff.Tests/OrderViewTests.cs
git commit -m "feat(bff): OrderView, §10.7's wire shape from the projection's rows"
```

The body argues: every rule the response states is a function of the rows
and is tested without a database; `unitPrice` is a `Money` because every
amount on the wire carries its currency (§10.7); the payment and shipment
members key on the column the schema holds present exactly when their events
have arrived; an owned row with no step throws, because no handler can write
one.

## Task 3: The page bounds, the error catalogue and the reader

**Files:**
- Create: `src/BFF/Web.Bff/Orders/OrderPage.cs`
- Create: `src/BFF/Web.Bff/Orders/OrderReadErrors.cs`
- Create: `src/BFF/Web.Bff/Orders/OrderReader.cs`
- Create: `tests/Web.Bff.Tests/OrderPageTests.cs`
- Create: `tests/Web.Bff.Tests/OrderReaderTests.cs`

**An unreadable cursor gets the first page**, as spec section 7 says.
`Common.Application.Cursor` encodes exactly a `(DateTimeOffset, Guid)` keyset
position — Catalog's list and §6.6's history query both use it — and its
`Decode` returns null for anything unreadable, "so an edited cursor gets the
first page". A second encoding in the BFF would be a second owner of §6.5's
opaque cursor, so the read calls `Cursor` and inherits its answer, and a
host test pins it.

**The clamp is 20 by default and 50 at most, owned by `OrderPage`.** A list
row carries its lines, so the clamp times `OrderLimits.MaxLines` bounds the
line rows one page can return — §6.6's argument for its own history query,
whose page is not multiplied by a hundred lines and so can afford a larger
ceiling. A limit outside the range is clamped rather than refused, as
Catalog's is.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Web.Bff.Tests/OrderPageTests.cs
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The list's bounds: clamped rather than refused (§10.7), small because a row carries its lines.</summary>
public sealed class OrderPageTests
{
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(OrderPage.DefaultLimit, OrderPage.DefaultLimit)]
    [InlineData(OrderPage.MaxLimit, OrderPage.MaxLimit)]
    [InlineData(OrderPage.MaxLimit + 1, OrderPage.MaxLimit)]
    [InlineData(int.MaxValue, OrderPage.MaxLimit)]
    public void A_limit_is_clamped_into_the_page_s_range(int requested, int expected) =>
        OrderPage.Clamp(requested).ShouldBe(expected);
}
```

```csharp
// tests/Web.Bff.Tests/OrderReaderTests.cs
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The read against rows the real handlers wrote: ownership, the keyset and one statement per page.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class OrderReaderTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _buyer = Guid.CreateVersion7();

    private OrderReader Reader => fixture.Factory.Services.GetRequiredService<OrderReader>();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_list_returns_the_caller_s_orders_newest_first_and_nobody_else_s()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();
        Guid theirs = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Placed(first, _buyer, At));
        await fixture.DeliverAsync(OrderEvents.Placed(second, _buyer, At.AddMinutes(1)));
        await fixture.DeliverAsync(OrderEvents.Placed(theirs, Guid.CreateVersion7(), At.AddMinutes(2)));

        CursorPage<OrderSummary> page = await Reader.ListAsync(_buyer, null, OrderPage.DefaultLimit, Ct);

        page.Items.Select(o => o.OrderId).ShouldBe([second, first], "newest first by FirstSeenAt (spec, section 1)");
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task A_row_no_Ordering_event_has_reached_is_listed_for_nobody_and_found_for_nobody()
    {
        Guid unowned = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Authorised(unowned, At));

        (await Reader.ListAsync(_buyer, null, OrderPage.DefaultLimit, Ct)).Items.ShouldBeEmpty();
        (await Reader.FindAsync(_buyer, unowned, Ct)).Error.ShouldBe(OrderReadErrors.NotFound);
    }

    [Fact]
    public async Task Another_buyer_s_order_and_an_unknown_id_are_the_same_answer()
    {
        Guid theirs = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(theirs, Guid.CreateVersion7(), At));

        Result<OrderDetail> owned = await Reader.FindAsync(_buyer, theirs, Ct);
        Result<OrderDetail> unknown = await Reader.FindAsync(_buyer, Guid.CreateVersion7(), Ct);

        owned.Error.ShouldBe(OrderReadErrors.NotFound, "403 would confirm the order exists (§10.7)");
        unknown.Error.ShouldBe(owned.Error);
    }

    [Fact]
    public async Task A_page_boundary_between_two_orders_seen_at_one_instant_neither_repeats_nor_skips()
    {
        Guid a = Guid.CreateVersion7();
        Guid b = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(a, _buyer, At));
        await fixture.DeliverAsync(OrderEvents.Placed(b, _buyer, At));

        // The handlers stamp FirstSeenAt from the clock; forcing a tie is the case the id tiebreaker exists for.
        await fixture.ExecuteAsync(
            "UPDATE bff.Orders SET FirstSeenAt = {0} WHERE OrderId IN ({1}, {2})", At, a, b);

        CursorPage<OrderSummary> first = await Reader.ListAsync(_buyer, null, 1, Ct);
        CursorPage<OrderSummary> second = await Reader.ListAsync(_buyer, first.NextCursor, 1, Ct);

        first.NextCursor.ShouldNotBeNull();
        second.NextCursor.ShouldBeNull();
        first.Items.Concat(second.Items).Select(o => o.OrderId).ShouldBe([a, b], ignoreOrder: true);
    }

    [Fact]
    public async Task A_page_s_lines_and_names_arrive_with_it()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Published(OrderEvents.Lamp, "Walnut desk lamp", At));
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));

        OrderSummary listed = (await Reader.ListAsync(_buyer, null, OrderPage.DefaultLimit, Ct)).Items.Single();

        listed.Total.ShouldBe(new Money(OrderEvents.Total, OrderEvents.Currency));
        OrderLineSummary line = listed.Lines.ShouldHaveSingleItem();
        line.ProductName.ShouldBe("Walnut desk lamp");
        line.LineTotal.Amount.ShouldBe(OrderEvents.Total);
    }

    [Fact]
    public async Task A_product_published_before_the_queue_existed_has_a_null_name()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));

        (await Reader.FindAsync(_buyer, order, Ct)).Value.Lines.ShouldHaveSingleItem().ProductName.ShouldBeNull();
    }

    [Fact]
    public async Task An_owned_row_only_a_cancellation_reached_is_listed_with_no_lines_and_no_total()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Cancelled(order, _buyer, At));

        OrderSummary listed = (await Reader.ListAsync(_buyer, null, OrderPage.DefaultLimit, Ct)).Items.Single();

        listed.Status.ShouldBe(BuyerStatuses.Cancelled);
        listed.Total.ShouldBeNull();
        listed.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_detail_carries_the_payment_the_shipment_and_the_row_s_own_instant()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        await fixture.DeliverAsync(OrderEvents.Authorised(order, At.AddSeconds(5)));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, _buyer, At.AddSeconds(6)));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At.AddDays(1)));

        OrderDetail detail = (await Reader.FindAsync(_buyer, order, Ct)).Value;

        detail.Status.ShouldBe(BuyerStatuses.Dispatched);
        detail.Payment.ShouldBe(new PaymentFacts(
            At.AddSeconds(5),
            new Money(OrderEvents.Total, OrderEvents.Currency),
            RefundedAmount: null));
        detail.Shipment.ShouldBe(new ShipmentFacts(OrderEvents.TrackingNumber, At.AddDays(1), DeliveredAt: null));
        detail.AsOf.ShouldBe((await fixture.OrderAsync(order))!.AsOf);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~OrderPageTests|FullyQualifiedName~OrderReaderTests"
```

Expected: build failure, `The name 'OrderPage' does not exist in the current
context`.

- [ ] **Step 3: Write the bounds, the catalogue and the reader**

```csharp
// src/BFF/Web.Bff/Orders/OrderPage.cs
namespace Web.Bff.Orders;

/// <summary>The list route's page bounds (§6.5, ADR-016), owned by the read rather than by the request.</summary>
/// <remarks>A row carries its lines, so the ceiling times <c>OrderLimits.MaxLines</c> bounds a page (§6.6).</remarks>
public static class OrderPage
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 50;

    /// <summary>Clamped, never refused, as §10.7 says of <c>limit</c>.</summary>
    public static int Clamp(int limit) => Math.Clamp(limit, 1, MaxLimit);
}
```

```csharp
// src/BFF/Web.Bff/Orders/OrderReadErrors.cs
using Common.Application;

namespace Web.Bff.Orders;

/// <summary>Every <see cref="Error"/> the order read returns, constructed here and nowhere else (§10.5).</summary>
public static class OrderReadErrors
{
    /// <summary>One answer for an unknown, another buyer's or an unowned order, confirming none (§10.7).</summary>
    public static readonly Error NotFound = Error.NotFound("order.not_found", "No order with that id.");
}
```

The code is Ordering's own `order.not_found` because the client switches on
the code (§10.5) and this is the same situation for the same screen: a buyer
asking for an order they cannot see. Two spellings of one answer would make
the client handle a difference that is not one.

```csharp
// src/BFF/Web.Bff/Orders/OrderReader.cs
using System.Data;
using System.Text.Json;
using Common.Application;
using Dapper;

namespace Web.Bff.Orders;

/// <summary>§10.7's two reads over ADR-051's projection, bound to a customer the caller never names.</summary>
public sealed class OrderReader(IDbConnectionFactory connections)
{
    /// <summary>The columns both reads select, so the two routes cannot read different facts about one row.</summary>
    private const string Columns =
        """
        o.OrderId, o.Currency, o.TotalAmount, o.PlacedAt, o.ConfirmedAt, o.DispatchedAt, o.DeliveredAt,
        o.CancelledAt, o.CancelOutcome, o.AuthorisedAt, o.AuthorisedAmount, o.RefundedAt, o.RefundedAmount,
        o.PaymentCurrency, o.TrackingNumber, o.FirstSeenAt, o.AsOf
        """;

    /// <summary>Catalog's keyset seek on <c>IX_Orders_Owned</c>; the id breaks a tie across a page.</summary>
    private const string PageSql =
        $"""
        SELECT TOP (@Take) {Columns}
        FROM bff.Orders o
        -- IS NOT NULL repeats the index's filter, so the seek matches it whatever the parameter.
        WHERE o.CustomerId = @CustomerId
            AND o.CustomerId IS NOT NULL
            AND (@AfterFirstSeenAt IS NULL
                OR o.FirstSeenAt < @AfterFirstSeenAt
                OR (o.FirstSeenAt = @AfterFirstSeenAt AND o.OrderId < @AfterId))
        ORDER BY o.FirstSeenAt DESC, o.OrderId DESC;
        """;

    /// <summary>Every line and name on the page in one statement, the ids as one JSON parameter (§6.6).</summary>
    private const string PageLinesSql =
        """
        SELECT l.OrderId, l.LineNumber, l.ProductId, l.Quantity, l.UnitPrice, ProductName = p.Name
        FROM bff.OrderLines l
        INNER JOIN OPENJSON(@OrderIds) j
            ON l.OrderId = CAST(j.value AS uniqueidentifier)
        LEFT JOIN bff.Products p ON p.ProductId = l.ProductId;
        """;

    /// <summary>The row and its lines, each filtered on id and customer, so another's order reads as none.</summary>
    private const string DetailSql =
        $"""
        SELECT {Columns}
        FROM bff.Orders o
        WHERE o.OrderId = @OrderId AND o.CustomerId = @CustomerId;

        SELECT l.OrderId, l.LineNumber, l.ProductId, l.Quantity, l.UnitPrice, ProductName = p.Name
        FROM bff.OrderLines l
        INNER JOIN bff.Orders o ON o.OrderId = l.OrderId AND o.CustomerId = @CustomerId
        LEFT JOIN bff.Products p ON p.ProductId = l.ProductId
        WHERE l.OrderId = @OrderId;
        """;

    public async Task<CursorPage<OrderSummary>> ListAsync(
        Guid customerId,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        int take = OrderPage.Clamp(limit);
        (DateTimeOffset SortKey, Guid Id)? after = Cursor.Decode(cursor);

        using IDbConnection connection = connections.Create();

        // One extra row says whether a next page exists, without a COUNT(*).
        List<OrderReadRow> rows = (await connection.QueryAsync<OrderReadRow>(
            new CommandDefinition(
                PageSql,
                new
                {
                    CustomerId = customerId,
                    Take = take + 1,
                    AfterFirstSeenAt = after?.SortKey,
                    AfterId = after?.Id
                },
                cancellationToken: ct))).AsList();

        bool hasMore = rows.Count > take;
        List<OrderReadRow> page = hasMore ? rows.GetRange(0, take) : rows;

        ILookup<Guid, OrderLineReadRow> lines = await PageLinesAsync(connection, page, ct);

        OrderSummary[] items = [.. page.Select(row => OrderView.Summary(row, [.. lines[row.OrderId]]))];

        string? next = hasMore ? Cursor.Encode(page[^1].FirstSeenAt, page[^1].OrderId) : null;

        return new CursorPage<OrderSummary>(items, next);
    }

    public async Task<Result<OrderDetail>> FindAsync(Guid customerId, Guid orderId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        using SqlMapper.GridReader grid = await connection.QueryMultipleAsync(
            new CommandDefinition(
                DetailSql,
                new { OrderId = orderId, CustomerId = customerId },
                cancellationToken: ct));

        OrderReadRow? row = await grid.ReadSingleOrDefaultAsync<OrderReadRow>();
        List<OrderLineReadRow> lines = (await grid.ReadAsync<OrderLineReadRow>()).AsList();

        return row is null
            ? Result.Failure<OrderDetail>(OrderReadErrors.NotFound)
            : Result.Success(OrderView.Detail(row, lines));
    }

    private static async Task<ILookup<Guid, OrderLineReadRow>> PageLinesAsync(
        IDbConnection connection,
        List<OrderReadRow> page,
        CancellationToken ct)
    {
        // A first request from a buyer with no orders is ordinary, and its lines can only be none.
        if (page.Count == 0)
            return Array.Empty<OrderLineReadRow>().ToLookup(line => line.OrderId);

        IEnumerable<OrderLineReadRow> lines = await connection.QueryAsync<OrderLineReadRow>(
            new CommandDefinition(
                PageLinesSql,
                new { OrderIds = JsonSerializer.Serialize(page.Select(row => row.OrderId)) },
                cancellationToken: ct));

        return lines.ToLookup(line => line.OrderId);
    }
}
```

The page's ids are at most `OrderPage.MaxLimit`, which an `IN` list would
hold; they travel as one parameter anyway because §6.6's history query is
the shape to copy and its argument — the clamp bounds rows, not every list a
row multiplies into — is the one reviewers of this read will ask about. The
lines' own order is `OrderView`'s (`LineNumber`), so the statement orders
nothing.

- [ ] **Step 4: Register the reader**

In `src/BFF/Web.Bff/Program.cs`, immediately after PR-2's
`builder.Services.AddMassTransitMessaging(builder.Configuration);`:

```csharp
// §10.7's read over the projection; a singleton, as the connection factory it holds is (§6.5).
builder.Services.AddSingleton<OrderReader>();
```

`using Web.Bff.Orders;` is already there from PR-2.

- [ ] **Step 5: Run them to see them pass**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~OrderPageTests|FullyQualifiedName~OrderReaderTests"
```

Expected: 15 passed — 7 container-free and 8 over SQL Server and RabbitMQ
containers. Docker must be running; a missing daemon fails on `Failed to
connect to Docker endpoint` rather than skipping.

- [ ] **Step 6: Commit**

```bash
git add src/BFF/Web.Bff/Orders src/BFF/Web.Bff/Program.cs \
    tests/Web.Bff.Tests/OrderPageTests.cs tests/Web.Bff.Tests/OrderReaderTests.cs
git commit -m "feat(bff): OrderReader, the keyset list and the owner-bound detail over the projection"
```

The body argues: the cursor is `Common.Application.Cursor`'s, so an edited
one gets the first page as every other list's does (spec section 7); the
clamp is 50 because a row carries its lines; a page
is two statements whatever its size, the second keyed by one JSON parameter
(§6.6); the detail filters both statements on the customer, so another
buyer's order and an unknown id are one answer by construction.

## Task 4: The two routes

**Files:**
- Create: `src/BFF/Web.Bff/Endpoints/OrderEndpoints.cs`
- Modify: `src/BFF/Web.Bff/Program.cs`
- Create: `tests/Web.Bff.Tests/OrderEndpointTests.cs`

- [ ] **Step 1: Write the failing host tests**

```csharp
// tests/Web.Bff.Tests/OrderEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Application;
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§10.7's routes through the real host: the subject from the principal, the 404, the wire's names.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class OrderEndpointTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _buyer = Guid.CreateVersion7();

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private HttpClient As(Guid subject)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, subject.ToString());

        return client;
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged_on_both_routes()
    {
        using HttpClient anonymous = fixture.Factory.CreateClient();

        (await anonymous.GetAsync("/v1/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/v1/orders/{Guid.CreateVersion7()}", Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_owner_reads_the_order_on_both_routes()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);

        CursorPage<OrderSummary>? page =
            await client.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders", Ct);
        OrderDetail? detail = await client.GetFromJsonAsync<OrderDetail>($"/v1/orders/{order}", Ct);

        page.ShouldNotBeNull().Items.ShouldHaveSingleItem().OrderId.ShouldBe(order);
        detail.ShouldNotBeNull().Status.ShouldBe(BuyerStatuses.Placed);
        detail.Cancellable.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_buyer_s_order_answers_exactly_as_an_unknown_one_does()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient stranger = As(Guid.CreateVersion7());

        HttpResponseMessage theirs = await stranger.GetAsync($"/v1/orders/{order}", Ct);
        HttpResponseMessage unknown = await stranger.GetAsync($"/v1/orders/{Guid.CreateVersion7()}", Ct);

        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound, "403 would confirm the order exists (§10.7)");
        theirs.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await Problem(theirs)).ShouldBe(await Problem(unknown));
        (await stranger.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders", Ct))!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_wire_carries_section_10_7_s_names_and_the_list_carries_no_detail_members()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);

        using JsonDocument page = JsonDocument.Parse(await client.GetStringAsync("/v1/orders", Ct));
        using JsonDocument detail = JsonDocument.Parse(await client.GetStringAsync($"/v1/orders/{order}", Ct));

        Names(page.RootElement).ShouldBe(["items", "nextCursor"]);
        JsonElement listed = page.RootElement.GetProperty("items")[0];
        Names(listed).ShouldBe(
            ["asOf", "cancellable", "lines", "orderId", "refunded", "refundedAt", "status", "timeline", "total"]);
        Names(listed.GetProperty("timeline")).ShouldBe(["cancelled", "confirmed", "delivered", "dispatched", "placed"]);
        Names(listed.GetProperty("lines")[0]).ShouldBe(["lineTotal", "productId", "productName"]);
        Names(listed.GetProperty("total")).ShouldBe(["amount", "currency"]);

        Names(detail.RootElement).ShouldContain("payment");
        Names(detail.RootElement).ShouldContain("shipment");
        Names(detail.RootElement.GetProperty("lines")[0]).ShouldBe(
            ["lineTotal", "productId", "productName", "quantity", "unitPrice"]);
        detail.RootElement.GetProperty("status").GetString().ShouldBe("placed");
    }

    [Fact]
    public async Task A_limit_past_the_ceiling_is_clamped_rather_than_refused()
    {
        for (int i = 0; i <= OrderPage.MaxLimit; i++)
            await fixture.DeliverAsync(OrderEvents.Placed(Guid.CreateVersion7(), _buyer, At.AddSeconds(i)));

        using HttpClient client = As(_buyer);
        CursorPage<OrderSummary>? page =
            await client.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders?limit=1000", Ct);

        page.ShouldNotBeNull().Items.Count.ShouldBe(OrderPage.MaxLimit);
        page.NextCursor.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_edited_cursor_gets_the_first_page_as_every_list_s_does()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);

        CursorPage<OrderSummary>? page =
            await client.GetFromJsonAsync<CursorPage<OrderSummary>>("/v1/orders?cursor=not-a-cursor", Ct);

        page.ShouldNotBeNull().Items.ShouldHaveSingleItem().OrderId.ShouldBe(order);
    }

    [Fact]
    public async Task Neither_route_spends_the_hop_budget()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, _buyer, At));
        using HttpClient client = As(_buyer);
        int before = fixture.Factory.Tokens.Issued;

        await client.GetStringAsync("/v1/orders", Ct);
        await client.GetStringAsync($"/v1/orders/{order}", Ct);

        // The pricing client's credential handler asks for a token on every attempt, so none asked is no call (§9.7).
        fixture.Factory.Tokens.Issued.ShouldBe(before, "the read makes no synchronous call (ADR-051)");
    }

    private static async Task<(int Status, string? Code, string? Detail)> Problem(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement root = body.RootElement;

        return (root.GetProperty("status").GetInt32(), root.GetProperty("code").GetString(),
            root.GetProperty("detail").GetString());
    }

    private static string[] Names(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
```

`Problem` compares status, code and detail rather than the whole body, because
§10.5's problem response carries a trace id that differs per request and is
not part of what the two answers must share.

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~OrderEndpointTests"
```

Expected: the build passes and every test but the anonymous one fails on a
404 from routing — `Response status code does not indicate success: 404` —
because no route is mapped; the anonymous one fails asserting 401 against
that 404.

- [ ] **Step 3: Write the routes and map them**

```csharp
// src/BFF/Web.Bff/Endpoints/OrderEndpoints.cs
using Common.Application;
using Common.Web;
using Web.Bff.Orders;

namespace Web.Bff.Endpoints;

/// <summary>§10.7's buyer order read, served from ADR-051's projection and calling nothing.</summary>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/orders")
            .WithTags("Orders")
            // Fail closed at the group (§11.4); the subject below is the principal's, never the request's (§10.7).
            .RequireAuthorization();

        // CursorPage, not Result (§6.2): a list has no failure to map.
        group
            .MapGet(
                "/",
                async (
                    string? cursor,
                    ICurrentUser user,
                    OrderReader orders,
                    CancellationToken ct,
                    int limit = OrderPage.DefaultLimit) =>
                    Results.Ok(await orders.ListAsync(user.Id, cursor, limit, ct)))
            .WithName("ListOrders");

        group
            .MapGet(
                "/{id:guid}",
                async (Guid id, ICurrentUser user, OrderReader orders, CancellationToken ct) =>
                    (await orders.FindAsync(user.Id, id, ct)).ToHttpResult())
            .WithName("GetOrder");
    }
}
```

In `src/BFF/Web.Bff/Program.cs`, immediately after `app.MapCheckoutEndpoints();`:

```csharp
app.MapOrderEndpoints();          // §10.7 — ADR-051's projection, no hop
```

and the header comment of `CheckoutEndpoints`, "The BFF's one screen, and the
only thing in this host that spends §9.7's hop budget.", becomes false in its
first clause; replace the summary with:

```csharp
/// <summary>The checkout screen, and the only thing in this host that spends §9.7's hop budget.</summary>
```

- [ ] **Step 4: Run them to see them pass**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~OrderEndpointTests"
```

Expected: 7 passed over the containers.

- [ ] **Step 5: Commit**

```bash
git add src/BFF/Web.Bff/Endpoints/OrderEndpoints.cs src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs \
    src/BFF/Web.Bff/Program.cs tests/Web.Bff.Tests/OrderEndpointTests.cs
git commit -m "feat(bff): GET /v1/orders and /v1/orders/{id}, §10.7's routes"
```

The body argues: the group fails closed and the subject is
`ICurrentUser.Id`; the detail's 404 is `Result<T>.ToHttpResult()` over the
read's own catalogue, so it is §10.5's problem response and one answer for
every order the caller cannot see; the wire's names are asserted from the
JSON rather than from the records, so a renamed member fails here; no token
is asked for during either route, which is the observable form of "no
outbound call".

## Task 5: The endpoint table the write rule reads

**Files:**
- Modify: `tests/Web.Bff.Tests/WriteEndpointRuleTests.cs`

**Neither GET declares `RetrySafe(RetrySafety.ReadOnly)`**, as spec section
7 says. ADR-058's rule, `WriteEndpointRule`, selects endpoints
whose methods include `POST`, `PUT`, `PATCH` or `DELETE`; a GET is not one,
so a declaration on it is metadata no rule reads, and Catalog's own
`GetProducts` declares none. `RetrySafety.ReadOnly` exists for a **write**
method that writes nothing — the quote's `POST`. What the test owes is proof
that the two GETs are in the table the rule reads and are left out of its
selection by being reads, which is a statement about what the gate is looking
at.

- [ ] **Step 1: Write the failing assertion**

In `WriteEndpointRuleTests`, add:

```csharp
    [Fact]
    public void The_order_reads_are_in_the_table_and_outside_the_writes()
    {
        string[] mapped = Names(Endpoints);

        // In the table the rule reads, so leaving them out of Writes is the rule's judgement and not a blind spot.
        mapped.ShouldContain("ListOrders");
        mapped.ShouldContain("GetOrder");
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(["Quote"]);
    }
```

- [ ] **Step 2: Prove it by mutation, then run it**

Delete the line `app.MapOrderEndpoints();` from `Program.cs` by hand — an
edit, never a stash, since the stash stack is shared with every other
worktree — and run:

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected: the new test fails on `mapped should contain "ListOrders"`, and the
two existing tests pass. Restore the line, check `git diff --stat` shows only
the test file, and run it again:

```bash
git diff --stat
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected: 3 passed. `BffFactory` here is the container-free factory, so the
run needs no daemon: the host starts against PR-1's unreachable database and
PR-2's unreachable broker, and building the endpoint table queries neither.

- [ ] **Step 3: Commit**

```bash
git add tests/Web.Bff.Tests/WriteEndpointRuleTests.cs
git commit -m "feat(bff): WriteEndpointRuleTests sees the order reads in its table and outside its writes"
```

The body argues it: a `RetrySafe` declaration on a GET is metadata no rule
reads, Catalog's list declares none, and the test proves the
rule saw the two reads rather than that they were absent.

## Task 6: §10.7's sentence, and §10.1 re-read

**Files:**
- Modify: `docs/backend-architecture/10-api-gateway.md`

- [ ] **Step 1: §10.7's field list**

In `### The fields both routes carry`, replace the last bullet:

```markdown
- **Money as the server's numbers**, an amount and a currency per line and in
  total. The client formats and never computes: §10.1's rule against
  aggregating a figure the client has to redo is the same rule one field down.
```

with:

```markdown
- **Money as the server's numbers**, an amount and a currency per line and in
  total. The client formats and never computes: §10.1's rule against
  aggregating a figure the client has to redo is the same rule one field down.
  **An order no placed or confirmed event has reached has no lines and a null
  total**: `OrderCancelled` supplies the customer and nothing priced, and the
  row it created is shown as far as the projection knows it rather than
  hidden until it knows more.
```

- [ ] **Step 2: §10.1, re-read and left**

Spec section 12 asks for §10.1 to be moved only if a sentence there says the
BFF holds no state. Read the section:

```bash
sed -n '1,108p' docs/backend-architecture/10-api-gateway.md | grep -n -i "bff\|database\|state"
```

What it says of the BFF, as of the tree this plan was written against: the
gateway "does not … access any database" (the gateway's list, not the
BFF's); the trap "Put aggregation in a **BFF**"; and "What is actually built
here is one `Web.Bff` making one call to Catalog (§2.2, §9.7) — the diagram
is the ceiling, not the inventory." Each is still true after this PR — the
BFF still makes exactly one synchronous call, and the read adds none — so
§10.1 is not edited. If the sentence has since changed to claim the BFF holds
nothing, amend that sentence alone to cite ADR-051 and say so in the commit
body.

- [ ] **Step 3: The blueprint audit and the link check**

Run `/validate-blueprint` (spec section 12: every PR that edits a chapter),
then `/check-links`, since the bullet cites a section. Fix what either
reports in this file; a finding elsewhere is left and named in the PR body.

- [ ] **Step 4: Commit**

```bash
git add docs/backend-architecture/10-api-gateway.md
git commit -m "docs: §10.7 names the owned order with no lines and a null total"
```

The body argues: the read returns such a row (spec section 1), and a client
meeting a null total unannounced is the rendering fault §10.7's
`productName` paragraph exists to prevent; §10.1 was re-read and left, with
the sentence quoted.

## Task 7: Everything run, and the PR

- [ ] **Step 1: The whole suite and the gates**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx --filter "Category!=Integration"
dotnet test tests/Web.Bff.Tests
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: the build has no warning (ADR-019 makes one an error); every
container-free test passes; `Web.Bff.Tests` passes whole over its containers,
Docker running; the secret scan and the comment gate report nothing. The
comment gate judges `HEAD`, so it runs after the last commit.

- [ ] **Step 2: The PR body's two rows, checked before opening**

Draft the body with `/pr`. Its rows:

```markdown
| Class | A+D |
| Touch set | `src/BFF/Web.Bff/**`, `tests/Web.Bff.Tests/**`, `docs/backend-architecture/10-api-gateway.md` |
```

Validate them with `main`'s `locality_gate.py` over the drafted body and
`git diff --name-only origin/main..HEAD`, as `.github/workflows/locality-gate.yml`
feeds it, before the PR is opened. The body names this PR's issue with a bare
closing line and names #425 only as a reference, never beside a closing
keyword.

- [ ] **Step 3: Open the PR**

`/pr`. The checks section says `/validate-blueprint` and `/check-links` ran
(Task 6).

## Self-review

Against the spec, section by section:

- **Section 1, what an owned row shows before its lines**: `OrderView`
  returns a null total and no lines (Task 2), the reader lists such a row
  (Task 3), and §10.7 gains the sentence (Task 6). ✓
- **Section 1, the list's order**: `FirstSeenAt DESC, OrderId DESC`, the tie
  tested across a page boundary (Task 3). ✓
- **Section 1, product names**: resolved on read by a `LEFT JOIN` to
  `bff.Products`; null before Catalog's event (Tasks 2, 3). ✓
- **Section 1, authorisation**: authenticated, no permission, the subject
  `ICurrentUser.Id` (Task 4); anonymous challenged (Task 4). ✓
- **Section 2, the wire shape**: status, timeline's five fixed keys,
  `refunded` and `refundedAt`, `cancellable` true exactly for `placed` and
  `confirmed`, a nullable `total`, `lineTotal` computed, per-order `asOf`;
  the detail's `quantity`, `unitPrice`, `payment` with `PaymentCurrency` and
  the refund-first half, `shipment` keyed on the tracking number; neither
  detail member on the list — names asserted from the JSON (Tasks 2, 4). ✓
  `unitPrice` is a `Money`, argued in Task 2.
- **Section 4, PR-3's row**: the routes (Task 4), the reader, cursor and
  clamp (Task 3), the response types (Task 2), the subject binding and the
  404 (Tasks 3, 4), §10.7's sentence (Task 6). Class A+D. ✓
- **Section 5**: every column the read needs, `PaymentCurrency` labelling
  both payment amounts (Tasks 2, 3). ✓
- **Section 7**: one group, `RequireAuthorization()`; the clamp a constant
  the read owns; one seek plus one lines statement per page, the ids as one
  parameter; the detail one query on id and customer for the row and its
  lines; §10.5's error shape for the 404; no outbound call, asserted (Tasks
  3, 4); the cursor is `Common.Application.Cursor` and an unreadable one
  gets the first page (Task 3); no `RetrySafe` on a GET, because ADR-058's
  rule reads writes (Task 5).
- **Section 11, through the host**: owner, another buyer and an unowned row
  on both routes; the clamp; the page-boundary tie; a null `productName`; an
  owned row with no lines; no outbound call (Tasks 3, 4). The readiness set
  is PR-2's assertion and unchanged here. ✓
- **Section 12**: §10.7's sentence (Task 6); §10.1 re-read, quoted and left
  (Task 6). ✓

## Interfaces for PR-4 and PR-5

- **The read changes nothing PR-4's rebuild writes or clears**: it reads
  `bff.Orders`, `bff.OrderLines` and `bff.Products` only, so a `--reset`
  followed by a replay is visible to both routes as soon as the handlers
  have written, and the rebuild's own suite can assert its result through
  `OrderReader` (`ListAsync`, `FindAsync`) rather than through SQL.
- **Route names**: `ListOrders` and `GetOrder`, under
  `MapGroup("/v1/orders")`; the error code `order.not_found` from
  `OrderReadErrors.NotFound`.
- **Bounds**: `OrderPage.DefaultLimit` and `OrderPage.MaxLimit`; PR-5's
  dashboard, if it charts the read, reads `http.server.request.duration` by
  route template, which ASP.NET Core records as `/v1/orders` and
  `/v1/orders/{id:guid}`.
