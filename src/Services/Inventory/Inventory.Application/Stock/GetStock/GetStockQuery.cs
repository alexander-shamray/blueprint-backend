using Common.Application;

namespace Inventory.Application.Stock.GetStock;

public sealed record GetStockQuery(Guid ProductId) : IQuery<StockDto?>;
