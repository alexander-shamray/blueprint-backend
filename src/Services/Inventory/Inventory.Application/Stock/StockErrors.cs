using Common.Application;

namespace Inventory.Application.Stock;

public static class StockErrors
{
    public static readonly Error BelowReserved =
        Error.Rule("stock.below_reserved", "On-hand stock cannot be below what is reserved.");
}
