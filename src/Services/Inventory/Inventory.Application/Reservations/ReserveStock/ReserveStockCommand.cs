using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.ReserveStock;

public sealed record ReserveStockCommand(Guid OrderId, IReadOnlyList<ReservationLine> Lines) : ICommand<Result>;
