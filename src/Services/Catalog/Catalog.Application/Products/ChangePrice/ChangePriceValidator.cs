using FluentValidation;

namespace Catalog.Application.Products.ChangePrice;

/// <summary>The user-input half of the boundary, a field-keyed 400 before any handler runs (§6.3, §10.5).</summary>
public sealed class ChangePriceValidator : AbstractValidator<ChangePriceCommand>
{
    public ChangePriceValidator()
    {
        // Guid.Empty would be one key shared by every caller; validation runs before any key is claimed (§6.3).
        RuleFor(x => x.CommandId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();

        PriceRules.Price(this, x => x.Amount, x => x.Currency);
    }
}
