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
        // The projection's handler drops a tracking number wider than its column and still records the despatch.
        OrderDetail order = OrderView.Detail(Placed() with { DispatchedAt = At.AddDays(1) }, [Line()]);

        order.Shipment.ShouldBe(new ShipmentFacts(TrackingNumber: null, At.AddDays(1), DeliveredAt: null));
    }

    [Fact]
    public void An_owned_row_no_step_has_reached_is_a_handler_defect_and_says_so() =>
        Should
            .Throw<InvalidOperationException>(() =>
                OrderView.Summary(new OrderReadRow { OrderId = Order, FirstSeenAt = At, AsOf = At }, []))
            .Message.ShouldContain("no step");
}
