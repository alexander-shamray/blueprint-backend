using Common.Application;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

/// <summary>
/// Neither this nor <c>VoidShipmentCommand</c> carries the event's
/// <c>OccurredAt</c>, and that is the decision rather than an omission: both
/// handlers take the instant from <see cref="TimeProvider"/>, as every other
/// writer in this service does, so a second member would be one the
/// aggregate never sees.
/// </summary>
public sealed record CreateShipmentCommand(Guid OrderId) : ICommand<Result>;
