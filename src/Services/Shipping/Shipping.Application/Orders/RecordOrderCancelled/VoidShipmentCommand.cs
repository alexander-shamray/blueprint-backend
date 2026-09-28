using Common.Application;

namespace Shipping.Application.Orders.RecordOrderCancelled;

public sealed record VoidShipmentCommand(Guid OrderId) : ICommand<Result>;
