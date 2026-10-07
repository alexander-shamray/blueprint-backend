using Catalog.Application.Products.PublishProduct;
using FluentValidation;

namespace Catalog.Application.Products.GetProducts;

/// <summary>ADR-073's bounds on the listing, as §10.5's field-keyed 400 before the handler reads anything.</summary>
public sealed class GetProductsValidator : AbstractValidator<GetProductsQuery>
{
    /// <summary>A published name's own ceiling, since a longer search can match no product.</summary>
    public const int MaxSearchLength = PublishProductValidator.MaxNameLength;

    public GetProductsValidator()
    {
        RuleFor(x => x.Q).MaximumLength(MaxSearchLength);

        RuleFor(x => x.Sort)
            .Must(sort => string.IsNullOrEmpty(sort) || ProductSort.All.Contains(sort))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", ProductSort.All)}.");

        // An unreadable cursor is the first page (§6.5); a readable one minted under another query is a client
        // holding a position in an ordering it is no longer asking for, so it is refused rather than misread.
        RuleFor(x => x.Cursor)
            .Must((query, cursor) => ProductCursor.Decode(cursor)?.Matches(query.Ordering, query.Search) ?? true)
            .WithMessage("'{PropertyName}' was issued for another sort or search; start again without it.");
    }
}
