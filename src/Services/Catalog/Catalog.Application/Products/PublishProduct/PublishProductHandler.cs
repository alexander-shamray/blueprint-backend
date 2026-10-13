using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>Thin by §6.4's design; no metric, because this can still roll back or be replayed whole (§6.3).</summary>
public sealed class PublishProductHandler(
    IProductRepository products,
    IProductImageStore images,
    ICurrentUser currentUser,
    TimeProvider clock)
    : ICommandHandler<PublishProductCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(PublishProductCommand command, CancellationToken ct)
    {
        string? thumbnailUrl = null;

        if (command.ImageKey is not null)
        {
            // The key must sit under the caller's own prefix (§10.8), so a seller cannot attach another's upload.
            if (!currentUser.IsAuthenticated ||
                !ProductImageKeys.TryParseUploadKey(
                    command.ImageKey,
                    currentUser.Id,
                    out Guid image,
                    out string extension))
            {
                return Result.Failure<Guid>(ProductErrors.ImageKeyInvalid);
            }

            // Derived from the source key, so a retried publish copies to the same place (§10.8).
            ImageAttachment attachment = await images.AttachAsync(
                command.ImageKey,
                ProductImageKeys.ProductKey(currentUser.Id, image, extension),
                ProductImageKeys.ContentTypeFor(extension)!,
                ct);

            if (attachment.Outcome == ImageAttachOutcome.NotUploaded)
                return Result.Failure<Guid>(ProductErrors.ImageNotUploaded);
            if (attachment.Outcome == ImageAttachOutcome.NotAnImage)
                return Result.Failure<Guid>(ProductErrors.ImageNotAnImage);

            thumbnailUrl = attachment.Url;
        }

        // Non-null by the validator's NotNull, which runs before any handler (§6.3). The seller is the principal,
        // never a body field (§11.4); the endpoint's policy guarantees one, and a dispatch with none records none.
        var product = Product.Publish(
            command.Name,
            thumbnailUrl,
            Money.Of(command.Amount!.Value, command.Currency),
            clock.GetUtcNow(),
            currentUser.IsAuthenticated ? new SellerId(currentUser.Id) : null);

        products.Add(product);

        return Result.Success(product.Id.Value);
    }
}
