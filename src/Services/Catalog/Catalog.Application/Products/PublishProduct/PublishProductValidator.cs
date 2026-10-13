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

        // A key and never a URL (§10.8): the handler rebuilds the stored URL from the configured base, so no
        // scheme or host a seller sends reaches the page. Only the bound is checked here, since the seller's own
        // prefix is the principal's and the handler's to compare.
        RuleFor(x => x.ImageKey)
            .NotEmpty()
            .MaximumLength(ProductImageKeys.LongestDestinationKeyLength)
            .When(x => x.ImageKey is not null);

        PriceRules.Price(this, x => x.Amount, x => x.Currency);
    }

    // Every buyer reads the name, so one that reads differently from what it holds is a spoof (ADR-085): the
    // categories ThirdPartyText refuses (ADR-084). Its bound is MaximumLength's, and null and blank are NotEmpty's.
    private static bool BeVisibleText(string? name) =>
        string.IsNullOrWhiteSpace(name) || ThirdPartyText.Recordable(name, int.MaxValue);
}
