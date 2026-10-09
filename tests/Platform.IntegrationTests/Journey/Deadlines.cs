using Common.Infrastructure.Outbox;
using Shipping.Infrastructure.Carrier;

namespace Platform.IntegrationTests.Journey;

/// <summary>How long each convergence predicate may take, and the sum that says so (§12.1).</summary>
/// <remarks>
/// A leg is one message crossing one hop: the outbox's poll, the broker, and one retry at the ladder's first rung.
/// A scenario's legs are counted from the saga's diagram (§9.6).
/// </remarks>
internal static class Deadlines
{
    // First, because a static initialiser runs in the order written and every leg below reads it.
    private static TimeSpan FirstRetryRung { get; } = new[]
    {
        Ordering.Infrastructure.Messaging.RetryPolicy.MinInterval,
        Inventory.Infrastructure.Messaging.RetryPolicy.MinInterval,
        Payments.Infrastructure.Messaging.RetryPolicy.MinInterval,
        Shipping.Infrastructure.Messaging.RetryPolicy.MinInterval,
        Notifications.Infrastructure.Messaging.RetryPolicy.MinInterval
    }.Max();

    /// <summary>One leg: the broker lane's p99 (§13.7), the event's arrival p95 and the first retry rung.</summary>
    /// <remarks>
    /// The target is a bound on the oldest row, which contains the poll; the poll is named beside it so the
    /// target is checked to leave room for one, as §13.7 says it does.
    /// </remarks>
    public static TimeSpan Leg { get; } = Checked(Slo.BrokerLaneOutbox) + Slo.EventEndToEnd + FirstRetryRung;

    /// <summary>Placed to confirmed: OrderPlaced, ReserveStock, StockReserved, AuthorisePayment, PaymentAuthorised,
    /// ConfirmOrder and OrderConfirmed.</summary>
    public static TimeSpan Confirmed { get; } = Legs(7);

    /// <summary>Confirmed to despatched: Shipping takes OrderConfirmed, a fulfilment tick books it, a tracking poll
    /// finds it collected, and ShipmentDispatched reaches Ordering with MarkOrderShipped after it.</summary>
    public static TimeSpan Dispatched { get; } =
        Legs(3) + CarrierHop.FulfilmentTick + CarrierHop.TrackingPollInterval + CarrierHop.TrackingTick;

    /// <summary>Despatched to delivered: the carrier's feed carries both events, and the next poll applies the
    /// rest of the page.</summary>
    public static TimeSpan Delivered { get; } = Legs(1) + CarrierHop.TrackingTick;

    /// <summary>A notice owed to sent: the event reaches Notifications, and the send tick picks the row up.</summary>
    public static TimeSpan Notified { get; } = Legs(1) + Notifications.Infrastructure.Mail.MailHop.SendTick;

    /// <summary>The whole fulfilment path, from placing the order to its last notice.</summary>
    public static TimeSpan Journey { get; } = Confirmed + Dispatched + Delivered + Notified;

    /// <summary>A compensation: the failing verdict, the release and the cancellation, and the notice.</summary>
    public static TimeSpan Compensated { get; } = Legs(7) + Notified;

    /// <summary>A product's price reaching Ordering: one leg.</summary>
    public static TimeSpan Projected { get; } = Legs(1);

    public static TimeSpan Legs(int count) => count * Leg;

    // The poll is the part of the target the platform owns; a target tighter than it could never be met.
    private static TimeSpan Checked(TimeSpan target) =>
        target >= OutboxDispatcher.PollInterval
            ? target
            : throw new InvalidOperationException(
                $"§13.7 allows the broker lane {target}, which is inside the dispatcher's poll of " +
                $"{OutboxDispatcher.PollInterval}.");
}
