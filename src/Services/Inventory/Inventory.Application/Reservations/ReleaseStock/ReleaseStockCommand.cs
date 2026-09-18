using Common.Application;

namespace Inventory.Application.Reservations.ReleaseStock;

public sealed record ReleaseStockCommand(Guid OrderId, CommandOrigin Origin) : ICommand<Result>;
