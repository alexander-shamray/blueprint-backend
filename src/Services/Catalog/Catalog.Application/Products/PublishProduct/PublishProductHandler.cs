using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>Thin by §6.4's design; no metric, because this can still roll back or be replayed whole (§6.3).</summary>
public sealed class PublishProductHandler(IProductRepository products, TimeProvider clock)
    : ICommandHandler<PublishProductCommand, Result<Guid>>
{
    public Task<Result<Guid>> HandleAsync(PublishProductCommand command, CancellationToken ct)
    {
        // Non-null by the validator's NotNull, which runs before any handler (§6.3).
        var product = Product.Publish(
            command.Name,
            command.ThumbnailUrl,
            Money.Of(command.Amount!.Value, command.Currency),
            clock.GetUtcNow());

        products.Add(product);

        return Task.FromResult(Result.Success(product.Id.Value));
    }
}
