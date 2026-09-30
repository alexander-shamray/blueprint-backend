using Common.Application;

namespace Inventory.Application.Stock.SetOnHand;

// Nullable, so an omitted count is a field-keyed 400 rather than binding as 0 and resetting the stock.
public sealed record SetOnHandCommand(Guid ProductId, int? OnHand) : ICommand<Result>;
