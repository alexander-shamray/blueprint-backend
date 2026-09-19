namespace Inventory.Application.Stock.GetStock;

public sealed record StockDto(Guid ProductId, int Available, int Reserved, DateTimeOffset UpdatedAt);
