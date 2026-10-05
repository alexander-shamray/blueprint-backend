using Common.Domain;
using FluentValidation;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>The user-input half of the boundary, a field-keyed 400 before any handler runs (§6.3, §10.5).</summary>
public sealed class PublishProductValidator : AbstractValidator<PublishProductCommand>
{
    /// <summary>The first amount decimal(19,4) cannot hold.</summary>
    private const decimal StorageCeiling = 1_000_000_000_000_000m;

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

        // Storage's bound, decimal(19,4), so an oversize price is a 400, not a 500 at SaveChanges; judged after
        // Money.Of's rounding to the currency's exponent (ADR-067), which can carry an amount under it up to it.
        RuleFor(x => x.Amount)
            .NotNull()
            .GreaterThanOrEqualTo(0)
            .Must((command, amount) => amount is not { } value || FitsStorage(value, command.Currency))
            .WithMessage("'{PropertyName}' is more than a price can hold once rounded to its currency's minor unit.");

        // NotEmpty first, since Matches alone skips null; letters, since Money.Of refuses "1$?" as a bug (§5.7);
        // \z, not $, since .NET's $ matches before a trailing newline.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
    }

    // Money.Of's rounding, applied here because Money.Of would throw on the currency this rule is not judging.
    private static bool FitsStorage(decimal amount, string? currency)
    {
        int exponent = CurrencyMinorUnits.Of(currency ?? string.Empty);

        return decimal.Round(amount, exponent, MidpointRounding.ToEven) < StorageCeiling;
    }

    // A named method, since TryCreate's out parameter does not fit the chain within 120 columns.
    private static bool BeAnHttpUrl(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
