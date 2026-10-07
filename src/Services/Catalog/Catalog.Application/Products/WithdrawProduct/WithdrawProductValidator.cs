using FluentValidation;

namespace Catalog.Application.Products.WithdrawProduct;

/// <summary>The user-input half of the boundary, a field-keyed 400 before any handler runs (§6.3, §10.5).</summary>
public sealed class WithdrawProductValidator : AbstractValidator<WithdrawProductCommand>
{
    public WithdrawProductValidator()
    {
        // Guid.Empty would be one key for all of a caller's requests (§8.5); validation runs before a claim (§6.3).
        RuleFor(x => x.CommandId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
    }
}
