using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Application.Products.WithdrawProduct;

/// <summary>Loads, checks the caller owns the product (§11.4), and lets the aggregate withdraw it (§5.4).</summary>
public sealed class WithdrawProductHandler(IProductRepository products, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<WithdrawProductCommand, Result>
{
    public async Task<Result> HandleAsync(WithdrawProductCommand command, CancellationToken ct)
    {
        Product? product = await products.GetAsync(new ProductId(command.ProductId), ct);
        if (product is null || !ProductOwnership.IsCallers(product, currentUser))
            return Result.Failure(ProductErrors.NotFound);

        // A rule failure rather than the aggregate's guard, since the request was well-formed (§10.5).
        if (product.WithdrawnAt is not null)
            return Result.Failure(ProductErrors.Withdrawn);

        product.Withdraw(clock.GetUtcNow());

        // No SaveChangesAsync: TransactionBehavior owns the commit (§6.3).
        return Result.Success();
    }
}
