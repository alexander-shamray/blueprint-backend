using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Application.Products.ChangePrice;

/// <summary>Loads, checks the currency the validator cannot see, and lets the aggregate reprice (§5.4, §6.4).</summary>
public sealed class ChangePriceHandler(IProductRepository products, TimeProvider clock)
    : ICommandHandler<ChangePriceCommand, Result>
{
    public async Task<Result> HandleAsync(ChangePriceCommand command, CancellationToken ct)
    {
        Product? product = await products.GetAsync(new ProductId(command.ProductId), ct);
        if (product is null)
            return Result.Failure(ProductErrors.NotFound);

        // Non-null by the validator's NotNull, which runs before any handler (§6.3).
        var price = Money.Of(command.Amount!.Value, command.Currency);

        // A rule failure rather than the aggregate's guard, since the request was well-formed (§10.5).
        if (price.Currency != product.Price.Currency)
            return Result.Failure(ProductErrors.CurrencyFixed);

        product.ChangePrice(price, clock.GetUtcNow());

        // No SaveChangesAsync: TransactionBehavior owns the commit (§6.3).
        return Result.Success();
    }
}
