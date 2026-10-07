using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>Thin by §6.4's design; no metric, because this can still roll back or be replayed whole (§6.3).</summary>
public sealed class PublishProductHandler(IProductRepository products, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<PublishProductCommand, Result<Guid>>
{
    public Task<Result<Guid>> HandleAsync(PublishProductCommand command, CancellationToken ct)
    {
        // Non-null by the validator's NotNull, which runs before any handler (§6.3). The seller is the principal,
        // never a body field (§11.4); the endpoint's policy guarantees one, and a dispatch with none records none.
        var product = Product.Publish(
            command.Name,
            command.ThumbnailUrl,
            Money.Of(command.Amount!.Value, command.Currency),
            clock.GetUtcNow(),
            currentUser.IsAuthenticated ? new SellerId(currentUser.Id) : null);

        products.Add(product);

        return Task.FromResult(Result.Success(product.Id.Value));
    }
}
