using Common.Application;

namespace Inventory.Application.Reservations.Reinstate;

public sealed record ReinstateReservationCommand(Guid OrderId) : ICommand<Result>;
