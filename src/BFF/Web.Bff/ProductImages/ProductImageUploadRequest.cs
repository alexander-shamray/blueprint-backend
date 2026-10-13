using Common.Application;
using FluentValidation;

namespace Web.Bff.ProductImages;

/// <summary>The two values the client chooses (§10.8); the server chooses everything else.</summary>
/// <remarks>Nullable, so an omitted size is the validator's 400 rather than a bound 0 and a zero-byte upload.</remarks>
public sealed record ProductImageUploadRequest(string? ContentType, long? SizeBytes);

/// <summary>§6.4's validator, which the endpoint calls itself because this host has no pipeline.</summary>
internal sealed class ProductImageUploadRequestValidator : AbstractValidator<ProductImageUploadRequest>
{
    public ProductImageUploadRequestValidator()
    {
        // Ordinal, so 'IMAGE/PNG' is refused rather than signed under a type the store would then compare exactly.
        RuleFor(x => x.ContentType)
            .NotEmpty()
            .Must(type => ProductImageKeys.ExtensionFor(type) is not null)
            .WithMessage(
                $"'{{PropertyName}}' must be one of {string.Join(", ", ProductImageKeys.AllowedContentTypes)}.");

        RuleFor(x => x.SizeBytes)
            .NotNull()
            .GreaterThan(0)
            .LessThanOrEqualTo(ProductImageKeys.MaxSizeBytes);
    }
}
