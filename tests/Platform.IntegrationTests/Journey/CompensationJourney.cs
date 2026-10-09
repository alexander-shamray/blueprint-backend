using System.Globalization;
using System.Net;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Journey;

/// <summary>The saga's compensations, each caused by the service whose answer causes it (§9.6, §12.1).</summary>
/// <remarks>
/// A wait of minutes is delivered as its expiry; a state of one message is held by stopping the outbox of the
/// service whose answer ends it, as §13.6's stuck outbox is.
/// </remarks>
[Collection(nameof(JourneyCollection))]
public sealed class CompensationJourney(FirstJurisdictionWorld world)
{
    private const decimal UnitPrice = 19.90m;

    /// <summary>A price whose minor units end in 01, which the provider's simulator declines (§14.1).</summary>
    private const decimal DeclinedPrice = 10.01m;

    private const int Quantity = 2;
    private const int Stocked = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Covers("AwaitingStock", "StockReservationFailed")]
    public async Task Stock_that_cannot_cover_the_order_cancels_it_before_anything_is_charged()
    {
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, stocked: 1);
        await using OrderTrace trace = OrderTrace.Start(world, order, 1);

        await trace.UntilAsync(Cancelled, Deadlines.Compensated, "the order cancelled and its saga finished");

        OrderSnapshot done = trace.Latest!;
        done.Available.ShouldBe(1, "the stock was never touched");
        done.Reserved.ShouldBe(0);
        done.PaymentStatus.ShouldBe("none");
        world.AuthorisationsFor(order).ShouldBe(0, "nothing was reserved, so nothing was asked of the provider");
        world.BookingsFor(order).ShouldBe(0);
        await NoticesAsync(order, Sent(TemplateKeys.OrderPlaced), Sent(TemplateKeys.OrderCancelled));
        await AssertToldWhyAsync(order, CancelReasons.OutOfStock);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("AwaitingPayment", "PaymentDeclined")]
    [Covers("Compensating", "StockReleased")]
    public async Task A_declined_card_releases_the_stock_and_cancels_the_order()
    {
        JourneyOrder order = await PlaceAsync(DeclinedPrice, 1, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);

        await trace.UntilAsync(Cancelled, Deadlines.Compensated, "the order cancelled and its saga finished");

        OrderSnapshot done = trace.Latest!;
        done.PaymentStatus.ShouldBe("Declined");
        done.Available.ShouldBe(Stocked, "the reservation was released");
        done.Reserved.ShouldBe(0);
        world.AuthorisationsFor(order).ShouldBe(1, "a decline is final, so the provider is asked once");
        world.BookingsFor(order).ShouldBe(0);
        (await world.RefundsAsync(order)).ShouldBe(0, "nothing was authorised, so nothing is voided");
        (await world.ReviewsAsync(order)).ShouldBeEmpty("a decline is an outcome, not an incident (§9.6)");
        await NoticesAsync(
            order,
            Sent(TemplateKeys.OrderPlaced),
            Sent(TemplateKeys.PaymentDeclined),
            Sent(TemplateKeys.OrderCancelled));
        await AssertToldWhyAsync(order, CancelReasons.PaymentDeclined);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("AwaitingStock", "OrderCancelled")]
    [Covers("Compensating", "StockReserved")]
    public async Task Cancelling_while_stock_is_reserving_releases_it_when_the_answer_arrives()
    {
        // Inventory has reserved and not yet said so: the customer cancels into that silence.
        using IDisposable inventoryHeld = await world.OutboxOf(JourneyWorld.Inventory).HoldAsync(Ct);
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { SagaState: "AwaitingStock", Reserved: Quantity },
            Deadlines.Legs(2),
            "the stock reserved and Inventory silent");

        (await world.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await trace.UntilAsync(
            s => s is { OrderStatus: "Cancelled", SagaState: "Compensating" },
            Deadlines.Legs(2),
            "the saga compensating for a cancellation");
        inventoryHeld.Dispose();

        await trace.UntilAsync(
            Cancelled,
            Deadlines.Compensated,
            "the late StockReserved absorbed and the saga finished");

        OrderSnapshot done = trace.Latest!;
        done.Available.ShouldBe(Stocked, "the reservation Inventory made was released");
        done.Reserved.ShouldBe(0);
        done.PaymentStatus.ShouldBe("none", "the late reservation was absorbed, not answered with a charge");
        world.AuthorisationsFor(order).ShouldBe(0);
        await NoticesAsync(order, Sent(TemplateKeys.OrderPlaced), Sent(TemplateKeys.OrderCancelled));
        await AssertToldWhyAsync(order, CancelReasons.CustomerRequest);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("Compensating", "StockReservationFailed")]
    public async Task Cancelling_while_stock_is_refused_absorbs_the_refusal_when_it_arrives()
    {
        // Inventory has refused and not yet said so: the customer cancels into that silence.
        using IDisposable inventoryHeld = await world.OutboxOf(JourneyWorld.Inventory).HoldAsync(Ct);
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, stocked: 1);
        await using OrderTrace trace = OrderTrace.Start(world, order, 1);
        await trace.UntilAsync(
            s => s.SagaState == "AwaitingStock",
            Deadlines.Legs(1),
            "the saga waiting on Inventory");

        (await world.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await trace.UntilAsync(
            s => s is { OrderStatus: "Cancelled", SagaState: "Compensating" },
            Deadlines.Legs(2),
            "the saga compensating for a cancellation");
        inventoryHeld.Dispose();

        await trace.UntilAsync(Cancelled, Deadlines.Compensated, "the late refusal absorbed and the saga finished");

        trace.Latest!.Available.ShouldBe(1);
        world.AuthorisationsFor(order).ShouldBe(0);
        await NoticesAsync(order, Sent(TemplateKeys.OrderPlaced), Sent(TemplateKeys.OrderCancelled));
        await AssertToldWhyAsync(order, CancelReasons.CustomerRequest);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("AwaitingPayment", "OrderCancelled")]
    [Covers("Compensating", "PaymentAuthorised")]
    public async Task Cancelling_while_the_card_is_authorising_flags_the_money_and_voids_it()
    {
        // The provider has said yes and Payments has not yet said so: the customer cancels into that silence.
        using IDisposable paymentsHeld = await world.OutboxOf(JourneyWorld.Payments).HoldAsync(Ct);
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { SagaState: "AwaitingPayment", PaymentStatus: "Authorised" },
            Deadlines.Legs(5),
            "the card authorised and Payments silent");

        (await world.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await trace.UntilAsync(
            s => s is { OrderStatus: "Cancelled", SagaState: "Compensating", Reserved: 0 },
            Deadlines.Legs(3),
            "the order cancelled and the stock released, the saga still owed Payments' verdict");
        paymentsHeld.Dispose();

        await trace.UntilAsync(Cancelled, Deadlines.Compensated, "the late verdict taken and the saga finished");

        (await world.ReviewsAsync(order)).ShouldBe(
            [ReviewReasons.PaymentAuthorisedDuringCompensation],
            "money moved after the cancellation, which is a row and not a pager (§9.6)");
        (await world.RefundsAsync(order)).ShouldBe(1, "Payments voids the authorisation off OrderCancelled itself");
        await NoticesAsync(
            order,
            Sent(TemplateKeys.OrderPlaced),
            Sent(TemplateKeys.OrderCancelled),
            Sent(TemplateKeys.PaymentRefunded));
        await AssertTotalToldAsync(order, TemplateKeys.PaymentRefunded);
        await AssertToldWhyAsync(order, CancelReasons.CustomerRequest);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("Compensating", "PaymentDeclined")]
    public async Task Cancelling_while_the_card_is_authorising_then_declined_charges_nothing_and_raises_nothing()
    {
        using IDisposable paymentsHeld = await world.OutboxOf(JourneyWorld.Payments).HoldAsync(Ct);
        JourneyOrder order = await PlaceAsync(DeclinedPrice, 1, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { SagaState: "AwaitingPayment", PaymentStatus: "Declined" },
            Deadlines.Legs(5),
            "the card declined and Payments silent");

        (await world.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await trace.UntilAsync(
            s => s is { OrderStatus: "Cancelled", SagaState: "Compensating", Reserved: 0 },
            Deadlines.Legs(3),
            "the order cancelled and the stock released");
        paymentsHeld.Dispose();

        await trace.UntilAsync(Cancelled, Deadlines.Compensated, "the late decline taken and the saga finished");

        (await world.ReviewsAsync(order)).ShouldBeEmpty("a decline discharges the obligation and moved no money");
        (await world.RefundsAsync(order)).ShouldBe(0);
        await NoticesAsync(
            order,
            Sent(TemplateKeys.OrderPlaced),
            Suppressed(TemplateKeys.PaymentDeclined),
            Sent(TemplateKeys.OrderCancelled));
        await AssertToldWhyAsync(order, CancelReasons.CustomerRequest);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("Confirmed", "OrderCancelled")]
    public async Task Cancelling_a_confirmed_order_before_despatch_is_flagged_voided_and_refunded()
    {
        // A parcel nobody has collected, which is the only kind a cancellation can still stop.
        using IDisposable parcels = world.CarrierHoldsParcels();
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { SagaState: "Confirmed", ShipmentStatus: "Booked" },
            Deadlines.Confirmed + Deadlines.Dispatched,
            "the order confirmed and its parcel booked");

        (await world.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await trace.UntilAsync(
            s => s is { OrderStatus: "Cancelled", SagaState: OrderSnapshot.None, ShipmentStatus: "Voided" },
            Deadlines.Compensated,
            "the order cancelled, the booking voided and the saga finished");

        OrderSnapshot done = trace.Latest!;
        done.Available.ShouldBe(Stocked, "Inventory releases off OrderCancelled itself, which the saga does not send");
        done.Reserved.ShouldBe(0);
        (await world.ReviewsAsync(order)).ShouldBe(
            [ReviewReasons.CancelledAfterConfirmation],
            "a despatch may have been moving, so a person looks (§9.6, ADR-029)");
        (await world.RefundsAsync(order)).ShouldBe(1);
        await NoticesAsync(
            order,
            Sent(TemplateKeys.OrderPlaced),
            Sent(TemplateKeys.OrderConfirmed),
            Sent(TemplateKeys.OrderCancelled),
            Sent(TemplateKeys.PaymentRefunded));
        await AssertTotalToldAsync(order, TemplateKeys.PaymentRefunded);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    public async Task A_shipped_order_cannot_be_cancelled_and_nothing_moves()
    {
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { OrderStatus: "Shipped", SagaState: OrderSnapshot.None },
            Deadlines.Confirmed + Deadlines.Dispatched,
            "the order shipped and its saga finished");

        HttpResponseMessage refused = await world.CancelAsync(order);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("already shipped");
        OrderSnapshot after = await world.SnapshotAsync(order);
        after.OrderStatus.ShouldBe("Shipped");
        (await world.RefundsAsync(order)).ShouldBe(0, "a refused cancellation voids nothing");
        (await world.ReviewsAsync(order)).ShouldBeEmpty();
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("AwaitingStock", "StockTimeout.Received")]
    public async Task A_stock_wait_that_lapses_cancels_the_order_and_Inventory_releases_off_the_cancellation()
    {
        using IDisposable inventoryHeld = await world.OutboxOf(JourneyWorld.Inventory).HoldAsync(Ct);
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { SagaState: "AwaitingStock", Reserved: Quantity },
            Deadlines.Legs(2),
            "the stock reserved and Inventory silent");

        await world.ExpireAsync(new StockReservationExpired(order.Id));
        await trace.UntilAsync(Cancelled, Deadlines.Compensated, "the order cancelled and its saga finished");
        inventoryHeld.Dispose();

        // The saga finalises without a ReleaseStock; the reservation goes because Inventory consumes the event.
        await trace.UntilAsync(s => s.Reserved == 0, Deadlines.Legs(2), "the reservation released");
        trace.Latest!.Available.ShouldBe(Stocked);
        await NoticesAsync(order, Sent(TemplateKeys.OrderPlaced), Sent(TemplateKeys.OrderCancelled));
        await AssertToldWhyAsync(order, CancelReasons.StockTimeout);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("AwaitingPayment", "PaymentTimeout.Received")]
    [Covers("Compensating", "OrderCancelled")]
    public async Task A_payment_wait_that_lapses_compensates_and_flags_the_money_that_arrives_late()
    {
        using IDisposable paymentsHeld = await world.OutboxOf(JourneyWorld.Payments).HoldAsync(Ct);
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { SagaState: "AwaitingPayment", PaymentStatus: "Authorised" },
            Deadlines.Legs(5),
            "the card authorised and Payments silent");

        await world.ExpireAsync(new PaymentAuthorisationExpired(order.Id));
        await trace.UntilAsync(
            s => s is { OrderStatus: "Cancelled", SagaState: "Compensating", Reserved: 0 },
            Deadlines.Compensated,
            "the order cancelled and the stock released, the saga still owed Payments' verdict");
        paymentsHeld.Dispose();

        await trace.UntilAsync(Cancelled, Deadlines.Compensated, "the late verdict taken and the saga finished");

        (await world.ReviewsAsync(order)).ShouldBe([ReviewReasons.PaymentAuthorisedDuringCompensation]);
        (await world.RefundsAsync(order)).ShouldBe(1);
        await NoticesAsync(
            order,
            Sent(TemplateKeys.OrderPlaced),
            Sent(TemplateKeys.OrderCancelled),
            Sent(TemplateKeys.PaymentRefunded));
        await AssertTotalToldAsync(order, TemplateKeys.PaymentRefunded);
        await AssertToldWhyAsync(order, CancelReasons.PaymentTimeout);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("AwaitingConfirmation", "ConfirmationTimeout.Received")]
    public async Task An_order_never_acknowledged_after_the_card_is_charged_is_escalated_and_not_compensated()
    {
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(s => s.SagaState == "AwaitingStock", Deadlines.Legs(1), "the saga started");

        // OrderPlaced is out; everything Ordering says from here is held, the acknowledgement among it.
        using IDisposable orderingHeld = await world.OutboxOf(JourneyWorld.Ordering).HoldAsync(Ct);
        await trace.UntilAsync(
            s => s is { SagaState: "AwaitingConfirmation", OrderStatus: "Confirmed" },
            Deadlines.Confirmed,
            "the order confirmed and Ordering silent");

        await world.ExpireAsync(new ConfirmationExpired(order.Id));
        await trace.UntilAsync(s => s.SagaState == OrderSnapshot.None, Deadlines.Compensated, "the saga finished");

        (await world.ReviewsAsync(order)).ShouldBe(
            [ReviewReasons.NotConfirmed],
            "the card is charged and the order unacknowledged, which wants a person (§9.6)");
        trace.Latest!.OrderStatus.ShouldBe("Confirmed", "§3.2 gives Ordering no refund command, so it is not unwound");
        trace.Latest.PaymentStatus.ShouldBe("Authorised");
        (await world.RefundsAsync(order)).ShouldBe(0);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("Confirmed", "DespatchTimeout.Received")]
    public async Task A_despatch_that_never_comes_is_escalated_and_the_stock_stays_reserved()
    {
        using IDisposable shippingHeld = await world.OutboxOf(JourneyWorld.Shipping).HoldAsync(Ct);
        JourneyOrder order = await PlaceAsync(UnitPrice, Quantity, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { SagaState: "Confirmed", ShipmentStatus: "Booked" },
            Deadlines.Confirmed + Deadlines.Dispatched,
            "the order confirmed and its parcel booked");

        await world.ExpireAsync(new DespatchExpired(order.Id));
        await trace.UntilAsync(s => s.SagaState == OrderSnapshot.None, Deadlines.Compensated, "the saga finished");

        (await world.ReviewsAsync(order)).ShouldBe([ReviewReasons.NotDespatched]);
        trace.Latest!.OrderStatus.ShouldBe("Confirmed", "a human now owns the order; nothing compensates it (§9.6)");
        trace.Latest.Reserved.ShouldBe(Quantity, "a despatch may be moving, so the stock is not released");
        (await world.RefundsAsync(order)).ShouldBe(0);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    [Fact]
    [Covers("Compensating", "ReleaseTimeout.Received")]
    public async Task A_release_that_is_never_answered_cancels_the_order_and_flags_the_stock()
    {
        // The card declines unannounced, then Inventory is silenced, then the decline is let out: the release
        // is asked of an Inventory that does it and cannot say so.
        using IDisposable paymentsHeld = await world.OutboxOf(JourneyWorld.Payments).HoldAsync(Ct);
        JourneyOrder order = await PlaceAsync(DeclinedPrice, 1, Stocked);
        await using OrderTrace trace = OrderTrace.Start(world, order, Stocked);
        await trace.UntilAsync(
            s => s is { SagaState: "AwaitingPayment", PaymentStatus: "Declined" },
            Deadlines.Legs(5),
            "the card declined and Payments silent");

        using IDisposable inventoryHeld = await world.OutboxOf(JourneyWorld.Inventory).HoldAsync(Ct);
        paymentsHeld.Dispose();
        await trace.UntilAsync(
            s => s is { SagaState: "Compensating", Reserved: 0 },
            Deadlines.Legs(3),
            "the stock released and Inventory silent");

        await world.ExpireAsync(new StockReleaseExpired(order.Id));
        await trace.UntilAsync(Cancelled, Deadlines.Compensated, "the order cancelled and its saga finished");

        (await world.ReviewsAsync(order)).ShouldBe(
            [ReviewReasons.StockNotReleased],
            "the order is cancelled either way, and a reservation nobody confirmed released is Inventory's to settle");
        await NoticesAsync(
            order,
            Sent(TemplateKeys.OrderPlaced),
            Sent(TemplateKeys.PaymentDeclined),
            Sent(TemplateKeys.OrderCancelled));
        await AssertToldWhyAsync(order, CancelReasons.PaymentDeclined);
        trace.ShouldHaveBeenLegal();
        await AssertNothingDeadLetteredAsync();
    }

    /// <summary>No consumer gave up on a message: a transition the saga declares is not one that faults.</summary>
    private async Task AssertNothingDeadLetteredAsync() =>
        (await world.DeadLettersAsync()).ShouldBeEmpty("a scenario meant to fail nothing dead-lettered a message");

    private static bool Cancelled(OrderSnapshot s) => s is { OrderStatus: "Cancelled", SagaState: OrderSnapshot.None };

    private async Task<JourneyOrder> PlaceAsync(decimal price, int quantity, int stocked)
    {
        Guid product = await world.PublishProductAsync(price);
        await world.StockAsync(product, stocked);

        return await world.PlaceAsync(product, quantity, price);
    }

    private static string Sent(string key) => $"{key}:Sent";

    /// <summary>A decline is withheld from a customer who cancelled, and the row says so (ADR-049).</summary>
    private static string Suppressed(string key) => $"{key}:Suppressed";

    /// <summary>Exactly these notices in exactly these states: the ones the order owes and no others.</summary>
    private async Task NoticesAsync(JourneyOrder order, params string[] expected)
    {
        string[] wanted = [.. expected.Order(StringComparer.Ordinal)];
        string[] seen = [];

        try
        {
            await Convergence.UntilAsync(
                async () =>
                {
                    seen = [.. (await world.NoticesAsync(order)).Order(StringComparer.Ordinal)];

                    return seen.SequenceEqual(wanted);
                },
                Deadlines.Notified,
                $"the notices {string.Join(", ", wanted)}, and no others");
        }
        catch (TimeoutException lapsed)
        {
            throw new TimeoutException($"{lapsed.Message} It held {string.Join(", ", seen)}.", lapsed);
        }
    }

    /// <summary>A notice that names an amount states the order's total as its currency writes it.</summary>
    private async Task AssertTotalToldAsync(JourneyOrder order, string key)
    {
        TemplateSet templates = TemplateSet.Embedded;
        string locale = world.Jurisdiction.Locale;
        Template template = templates.Find(key, templates.CurrentVersion(key), locale)!;
        string total = order.Total.ToString("N2", CultureInfo.GetCultureInfo(locale)) +
            " " + world.Jurisdiction.Currency;

        MailpitMessage notice = (await world.MailAsync(order)).Single(m => m.Subject == template.Subject);

        notice.Text.ShouldContain(
            total,
            Case.Sensitive,
            $"the {key} notice states the order's total in its currency's places");
    }

    /// <summary>The cancellation notice says why, in the customer's language and the saga's own vocabulary.</summary>
    private async Task AssertToldWhyAsync(JourneyOrder order, string reason)
    {
        TemplateSet templates = TemplateSet.Embedded;
        int version = templates.CurrentVersion(TemplateKeys.OrderCancelled);
        string locale = world.Jurisdiction.Locale;
        Template cancelled = templates.Find(TemplateKeys.OrderCancelled, version, locale)!;

        MailpitMessage notice = (await world.MailAsync(order)).Single(m => m.Subject == cancelled.Subject);

        notice.Text.ShouldContain(
            templates.Reasons(version, locale)![reason],
            Case.Sensitive,
            $"the notice says why in {locale}: {reason}");
    }
}
