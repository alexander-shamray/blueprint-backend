using Common.Application;
using Common.Domain;
using Inventory.Domain.Stock;

namespace Inventory.Application.Stock.SetOnHand;

public sealed class SetOnHandHandler(IStockItemRepository items, TimeProvider clock)
    : ICommandHandler<SetOnHandCommand, Result>
{
    public async Task<Result> HandleAsync(SetOnHandCommand command, CancellationToken ct)
    {
        var product = new ProductId(command.ProductId);

        // Ensure, then load, then set: one path for a first write and a stock-take, and EnsureAsync's lock, held to
        // the commit, serialises admin writes and ledger statements on the row.
        DateTimeOffset now = clock.GetUtcNow();
        await items.EnsureAsync(product, now, ct);
        StockItem item = await items.GetAsync(product, ct) ??
            throw new InvalidOperationException($"StockItems has no row for {product} after EnsureAsync.");

        int onHand = command.OnHand ??
            throw new InvalidOperationException(
                "SetOnHandCommand reached the handler with no count; the validator refuses that (§6.4).");

        try
        {
            item.SetOnHand(onHand, now);
        }
        catch (DomainException)
        {
            return Result.Failure(StockErrors.BelowReserved);
        }

        return Result.Success();
    }
}
