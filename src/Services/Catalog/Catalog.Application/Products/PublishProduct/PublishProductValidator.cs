using FluentValidation;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>The user-input half of the boundary, a field-keyed 400 before any handler runs (§6.3, §10.5).</summary>
public sealed class PublishProductValidator : AbstractValidator<PublishProductCommand>
{
    public PublishProductValidator()
    {
        // Guid.Empty would be one key shared by every caller; validation runs before any key is claimed (§6.3).
        RuleFor(x => x.CommandId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);

        // The scheme is the point: an href bound to a javascript: or data:text/html URL is stored XSS (§5.7's
        // division: input, not a bug). Absolute, because a relative URI has no scheme to refuse.
        RuleFor(x => x.ThumbnailUrl)
            .MaximumLength(400)
            .Must(BeAnHttpUrl)
            .WithMessage("'{PropertyName}' must be an absolute http or https URL.")
            .When(x => x.ThumbnailUrl is not null);

        PriceRules.Price(this, x => x.Amount, x => x.Currency);
    }

    // A named method, since TryCreate's out parameter does not fit the chain within 120 columns.
    private static bool BeAnHttpUrl(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
