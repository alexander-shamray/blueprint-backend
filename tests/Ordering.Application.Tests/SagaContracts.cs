using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Ordering.Application.Tests;

/// <summary>The events §9.6's saga reacts to, built for a test.</summary>
/// <remarks>
/// A builder, because every V1 member §12.6 does not list as additive is <c>required</c>; <c>OccurredAt</c> is
/// fixed because the saga copies it onto <c>StartedAt</c>.
/// </remarks>
internal static class SagaContracts
{
    internal static readonly DateTimeOffset Occurred = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    internal static readonly Guid Product = Guid.Parse("6f0b1f7e-0d4f-4c53-9a2f-0b6a1e2d3c40");

    internal const decimal Total = 129.98m;

    internal const string Currency = "EUR";

    internal static OrderPlaced OrderPlaced(Guid orderId, Guid customerId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        CustomerId = customerId,
        TotalAmount = Total,
        Currency = Currency,
        Lines = [new PlacedLine(Product, 2, 64.99m)]
    };

    internal static StockReserved StockReserved(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId
    };

    internal static StockReservationFailed StockReservationFailed(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        UnavailableProductIds = [Product]
    };

    internal static PaymentAuthorised PaymentAuthorised(Guid orderId, string reference) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        Reference = reference,
        Amount = Total,
        Currency = Currency
    };

    internal static PaymentDeclined PaymentDeclined(Guid orderId, string reason) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        Reason = reason
    };

    internal static StockReleased StockReleased(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId
    };

    /// <summary>A cancellation from §11.4's endpoint or the saga's own echo, either carrying any reason.</summary>
    /// <remarks>
    /// <c>origin</c> defaults to <see cref="CancelOrigins.User"/>, so a test that states none meets the
    /// missing-instance branch's fault rather than its silent return.
    /// </remarks>
    internal static OrderCancelled OrderCancelled(
        Guid orderId,
        Guid customerId,
        string reason,
        string? origin = CancelOrigins.User) => new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = orderId,
            OccurredAt = Occurred,
            OrderId = orderId,
            CustomerId = customerId,
            Reason = reason,
            Origin = origin
        };

    /// <summary>The aggregate's own confirmation, which §9.6's <c>AwaitingConfirmation</c> waits for.</summary>
    /// <remarks>No delivery address: a broadcast event carries identifiers, not personal data (ADR-035).</remarks>
    internal static OrderConfirmed OrderConfirmed(Guid orderId, Guid customerId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        CustomerId = customerId,
        TotalAmount = Total,
        Currency = Currency,
        Lines = [new ConfirmedLine(Product, 2, 64.99m)]
    };

    internal static ShipmentDispatched ShipmentDispatched(Guid orderId, string tracking) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Occurred,
        OrderId = orderId,
        TrackingNumber = tracking
    };
}
