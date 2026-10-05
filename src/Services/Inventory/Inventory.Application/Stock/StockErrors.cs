using Common.Application;

namespace Inventory.Application.Stock;

public static class StockErrors
{
    public static readonly Error NotFound =
        Error.NotFound("stock.not_found", "No stock record for that product.");

    public static readonly Error BelowReserved =
        Error.Rule("stock.below_reserved", "On-hand stock cannot be below what is reserved.");
}
