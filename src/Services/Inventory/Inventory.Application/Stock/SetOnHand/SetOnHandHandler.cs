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

        // Ensure, then load, then set: the row exists before it is read, so a
        // first write and a stock-take are one code path. EnsureAsync's lock
        // is held to the commit whether the row existed or not, so two admin
        // writes, or an admin write and a ledger statement, serialise on the
        // row rather than race — the second waits, then sees the first's
        // commit. The rowversion is EF's own guard on the update and fires
        // for nothing this path can meet.
        DateTimeOffset now = clock.GetUtcNow();
        await items.EnsureAsync(product, now, ct);
        StockItem item = await items.GetAsync(product, ct)
            ?? throw new InvalidOperationException($"StockItems has no row for {product} after EnsureAsync.");

        int onHand = command.OnHand
            ?? throw new InvalidOperationException(
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
