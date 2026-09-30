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

        // Storage's bound, decimal(19,4), so an oversize price is a 400, not a 500 at SaveChanges; .995 is
        // excluded because Money.Of rounds half-to-even to two places.
        RuleFor(x => x.Amount).NotNull().GreaterThanOrEqualTo(0).LessThan(999_999_999_999_999.995m);

        // NotEmpty first, since Matches alone skips null; letters, since Money.Of refuses "1$?" as a bug (§5.7);
        // \z, not $, since .NET's $ matches before a trailing newline.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
    }

    // A named method, since TryCreate's out parameter does not fit the chain within 120 columns.
    private static bool BeAnHttpUrl(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
