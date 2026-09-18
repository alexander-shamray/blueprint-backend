using Common.Application;

namespace Inventory.Application.Stock.SetOnHand;

// Nullable because a bare int cannot say "absent": an omitted count would
// bind as 0 and reset the stock indistinguishably from a deliberate stock-take
// of nothing. The validator's NotNull turns the omission into the field-keyed
// 400 every other bad field gets — the same reason Catalog's price is a
// decimal? on PublishProductCommand.
public sealed record SetOnHandCommand(Guid ProductId, int? OnHand) : ICommand<Result>;
