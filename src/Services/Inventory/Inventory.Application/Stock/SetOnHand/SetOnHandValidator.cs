using FluentValidation;

namespace Inventory.Application.Stock.SetOnHand;

public sealed class SetOnHandValidator : AbstractValidator<SetOnHandCommand>
{
    public SetOnHandValidator()
    {
        RuleFor(c => c.ProductId).NotEmpty();
        RuleFor(c => c.OnHand).NotNull().GreaterThanOrEqualTo(0);
    }
}
