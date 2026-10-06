using Common.Application;
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The read against rows the real handlers wrote: ownership, the keyset and each page's lines.</summary>
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
        await fixture.ExecuteAsync("UPDATE bff.Orders SET FirstSeenAt = {0} WHERE OrderId IN ({1}, {2})", At, a, b);

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
    public async Task Each_order_on_one_page_carries_its_own_lines_and_no_other()
    {
        Guid older = Guid.CreateVersion7();
        Guid newer = Guid.CreateVersion7();
        Guid olderProduct = Guid.CreateVersion7();
        Guid newerProduct = Guid.CreateVersion7();

        await fixture.DeliverAsync(
            OrderEvents.Placed(older, _buyer, At) with { Lines = [new PlacedLine(olderProduct, 1, 5m)] });
        await fixture.DeliverAsync(
            OrderEvents.Placed(newer, _buyer, At.AddMinutes(1)) with
            {
                Lines = [new PlacedLine(newerProduct, 2, 7m), new PlacedLine(olderProduct, 4, 9m)]
            });

        IReadOnlyList<OrderSummary> items = (await Reader.ListAsync(_buyer, null, OrderPage.DefaultLimit, Ct)).Items;

        items.Select(o => o.OrderId).ShouldBe([newer, older]);
        items[0].Lines.Select(l => l.ProductId).ShouldBe([newerProduct, olderProduct]);
        items[1].Lines.Select(l => l.ProductId).ShouldBe([olderProduct]);
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
        detail.Payment.ShouldBe(
            new PaymentFacts(
                At.AddSeconds(5),
                new Money(OrderEvents.Total, OrderEvents.Currency),
                RefundedAmount: null));
        detail.Shipment.ShouldBe(new ShipmentFacts(OrderEvents.TrackingNumber, At.AddDays(1), DeliveredAt: null));
        detail.AsOf.ShouldBe((await fixture.OrderAsync(order))!.AsOf);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
