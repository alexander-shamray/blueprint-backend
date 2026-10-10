using Common.Application;

namespace Shipping.Application.Privacy.EndWaitingShipment;

public sealed record EndWaitingShipmentCommand(Guid OrderId) : ICommand<Result>;
