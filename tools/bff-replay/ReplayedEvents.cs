using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace BffReplay;

/// <summary>ADR-051's eight events: what <c>bff-order-events</c> binds, and the only rows a replay sends.</summary>
public static class ReplayedEvents
{
    public static readonly IReadOnlyList<Type> Types =
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

    /// <summary>The share a publisher owns, selected by the contract namespace its name gives (§9.2).</summary>
    public static IReadOnlyList<Type> PublishedBy(Publisher publisher) =>
        [.. Types.Where(type => type.Namespace == publisher.ContractNamespace)];
}
