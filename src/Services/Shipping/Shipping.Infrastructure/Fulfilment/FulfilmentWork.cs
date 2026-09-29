namespace Shipping.Infrastructure.Fulfilment;

/// <summary>
/// One row a pass has leased, projected to exactly what the pass needs before
/// it loads the aggregate — the shipment, the order it answers for, the
/// carrier's reference when there is a booking to cancel, and when the
/// shipment was made, which the give-up age is measured from.
/// </summary>
public sealed record FulfilmentWork(
    Guid Id,
    Guid OrderId,
    string Status,
    string? CarrierReference,
    DateTimeOffset CreatedAt);
