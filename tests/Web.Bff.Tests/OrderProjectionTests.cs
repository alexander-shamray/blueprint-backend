using Common.Application;
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        cancelled.TotalAmount.ShouldBeNull("OrderCancelled carries no total, which the read reports as null (§10.7)");
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
        row.AsOf.ShouldBeGreaterThan(unowned.AsOf, "a write that changes the row moves AsOf");
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
    public async Task A_disagreeing_customer_is_logged_and_an_agreeing_one_is_not()
    {
        // Built by hand for its logger: an inverted comparison would warn on every event and change no row.
        Guid order = Guid.CreateVersion7();
        WarningLog log = new();
        OrderProjection projection = new(new SqlConnectionFactory(fixture.ConnectionString), TimeProvider.System, log);

        await projection.HandleAsync(OrderEvents.Placed(order, _customer, At), TestContext.Current.CancellationToken);
        await projection.HandleAsync(
            OrderEvents.Confirmed(order, _customer, At.AddMinutes(1)), TestContext.Current.CancellationToken);

        log.Warnings.ShouldBeEmpty("the customer agreed both times");

        OrderConfirmed stranger = OrderEvents.Confirmed(order, Guid.CreateVersion7(), At.AddMinutes(2));
        await projection.HandleAsync(stranger, TestContext.Current.CancellationToken);

        log.Warnings.ShouldHaveSingleItem().ShouldBe("CustomerMismatch");
    }

    [Fact]
    public async Task Handlers_creating_one_row_at_once_leave_one_row_and_every_fact()
    {
        // MERGE's HOLDLOCK is the claim: without it two inserts race and one fails on the key.
        Guid[] orders = [.. Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7())];

        await Task.WhenAll(orders.SelectMany(order => new[]
        {
            ApplyAsync(OrderEvents.Placed(order, _customer, At)),
            ApplyAsync(OrderEvents.Authorised(order, At.AddSeconds(5))),
            ApplyAsync(OrderEvents.Dispatched(order, At.AddDays(1)))
        }));

        foreach (Guid order in orders)
        {
            ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
            row.CustomerId.ShouldBe(_customer);
            row.AuthorisedAt.ShouldBe(At.AddSeconds(5));
            row.DispatchedAt.ShouldBe(At.AddDays(1));
        }
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
    public async Task The_schema_refuses_a_member_the_map_never_produces()
    {
        Exception refused = await Should.ThrowAsync<Exception>(() => fixture.ExecuteAsync(
            "INSERT INTO bff.Orders (OrderId, CancelledAt, CancelOutcome, FirstSeenAt, AsOf) " +
            "VALUES ({0}, SYSDATETIMEOFFSET(), {1}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());",
            Guid.CreateVersion7(),
            "refunded"));

        refused.Message.ShouldContain("CK_Orders_CancelOutcome");
    }

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
    public async Task A_blank_tracking_number_is_not_stored_and_a_later_real_one_is()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Dispatched(order, At, "   "));
        await ApplyAsync(OrderEvents.Delivered(order, At.AddMinutes(1)));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.DispatchedAt.ShouldBe(At);
        row.TrackingNumber.ShouldBe(OrderEvents.TrackingNumber, "a stored blank would hold the column against it");
    }

    [Fact]
    public async Task A_blank_currency_is_not_stored_and_takes_its_total_with_it()
    {
        Guid order = Guid.CreateVersion7();

        await ApplyAsync(OrderEvents.Placed(order, _customer, At) with { Currency = "   " });

        ProjectedOrder placed = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        placed.Currency.ShouldBeNull();
        placed.TotalAmount.ShouldBeNull("CK_Orders_Total refuses a total stored without its currency");

        await ApplyAsync(OrderEvents.Confirmed(order, _customer, At.AddMinutes(1)));

        ProjectedOrder confirmed = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        confirmed.Currency.ShouldBe(OrderEvents.Currency, "a stored blank would hold the column against it");
        confirmed.TotalAmount.ShouldBe(OrderEvents.Total);
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

    /// <summary>The names of the warnings a handler logs, which is all a mismatch leaves behind.</summary>
    private sealed class WarningLog : ILogger<OrderProjection>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(eventId.Name ?? "");
        }
    }
}
