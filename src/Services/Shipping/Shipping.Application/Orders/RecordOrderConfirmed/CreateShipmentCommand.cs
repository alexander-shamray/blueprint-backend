using Common.Application;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

/// <summary>Carries no <c>OccurredAt</c>: both handlers take the instant from <see cref="TimeProvider"/>.</summary>
public sealed record CreateShipmentCommand(Guid OrderId) : ICommand<Result>;
