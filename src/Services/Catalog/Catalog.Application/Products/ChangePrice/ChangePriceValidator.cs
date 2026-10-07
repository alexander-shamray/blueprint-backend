using FluentValidation;

namespace Catalog.Application.Products.ChangePrice;

/// <summary>The user-input half of the boundary, a field-keyed 400 before any handler runs (§6.3, §10.5).</summary>
public sealed class ChangePriceValidator : AbstractValidator<ChangePriceCommand>
{
    public ChangePriceValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();

        PriceRules.Price(this, x => x.Amount, x => x.Currency);
    }
}
