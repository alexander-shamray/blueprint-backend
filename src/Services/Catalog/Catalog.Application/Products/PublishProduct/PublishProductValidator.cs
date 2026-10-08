using Common.Application;
using FluentValidation;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>The user-input half of the boundary, a field-keyed 400 before any handler runs (§6.3, §10.5).</summary>
public sealed class PublishProductValidator : AbstractValidator<PublishProductCommand>
{
    /// <summary>The <c>Name</c> column's width, which the listing's search shares (ADR-073).</summary>
    public const int MaxNameLength = 200;

    public PublishProductValidator()
    {
        // Guid.Empty would be one key for all of a caller's requests (§8.5); validation runs before a claim (§6.3).
        RuleFor(x => x.CommandId).NotEmpty();
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(MaxNameLength)
            .Must(BeVisibleText)
            .WithMessage("'{PropertyName}' must not contain control, format, line-break or broken characters.");

        // The scheme is the point: an href bound to a javascript: or data:text/html URL is stored XSS (§5.7's
        // division: input, not a bug), and an http image is mixed content on the buyer's page (ADR-085).
        RuleFor(x => x.ThumbnailUrl)
            .MaximumLength(400)
            .Must(BeAnHttpsUrl)
            .WithMessage("'{PropertyName}' must be an absolute https URL.")
            .When(x => x.ThumbnailUrl is not null);

        PriceRules.Price(this, x => x.Amount, x => x.Currency);
    }

    // A named method, since TryCreate's out parameter does not fit the chain within 120 columns.
    private static bool BeAnHttpsUrl(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    // Every buyer reads the name, so one that reads differently from what it holds is a spoof (ADR-085): the
    // categories ThirdPartyText refuses (ADR-084). Its bound is MaximumLength's, and null and blank are NotEmpty's.
    private static bool BeVisibleText(string? name) =>
        string.IsNullOrWhiteSpace(name) || ThirdPartyText.Recordable(name, int.MaxValue);
}
