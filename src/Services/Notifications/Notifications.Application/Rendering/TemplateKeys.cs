using Common.Contracts.Ordering.V1;

namespace Notifications.Application.Rendering;

/// <summary>§3.2's seven subscriptions as template keys, each with the closed set of names it may use.</summary>
public static class TemplateKeys
{
    public const string OrderPlaced = "order-placed";

    public const string OrderConfirmed = "order-confirmed";

    public const string OrderCancelled = "order-cancelled";

    public const string PaymentDeclined = "payment-declined";

    public const string PaymentRefunded = "payment-refunded";

    public const string ShipmentDispatched = "shipment-dispatched";

    public const string ShipmentDelivered = "shipment-delivered";

    /// <summary>What each key's template may name; a name outside its key's set refuses the host at start.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Placeholders { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [OrderPlaced] = Set(
                PlaceholderNames.OrderId,
                PlaceholderNames.Date,
                PlaceholderNames.Amount,
                PlaceholderNames.Currency),
            [OrderConfirmed] = Set(
                PlaceholderNames.OrderId,
                PlaceholderNames.Date,
                PlaceholderNames.Amount,
                PlaceholderNames.Currency),
            [OrderCancelled] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date, PlaceholderNames.CancelReason),
            [PaymentDeclined] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date),
            [PaymentRefunded] = Set(
                PlaceholderNames.OrderId,
                PlaceholderNames.Date,
                PlaceholderNames.Amount,
                PlaceholderNames.Currency),
            [ShipmentDispatched] = Set(
                PlaceholderNames.OrderId,
                PlaceholderNames.Date,
                PlaceholderNames.TrackingNumber),
            [ShipmentDelivered] = Set(
                PlaceholderNames.OrderId,
                PlaceholderNames.Date,
                PlaceholderNames.TrackingNumber),
        };

    /// <summary>The codes a cancellation's map phrases: <see cref="CancelReasons"/>' five, Ordering's.</summary>
    public static IReadOnlyList<string> CancellationCodes { get; } =
    [
        CancelReasons.OutOfStock,
        CancelReasons.StockTimeout,
        CancelReasons.PaymentDeclined,
        CancelReasons.PaymentTimeout,
        CancelReasons.CustomerRequest,
    ];

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);
}
