using Common.Application;

namespace Inventory.Application.Reservations.Fulfil;

public sealed record FulfilReservationCommand(Guid OrderId) : ICommand<Result>;
