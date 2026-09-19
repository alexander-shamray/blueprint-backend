using FluentValidation;

namespace Inventory.Application.Reservations.ReleaseStock;

public sealed class ReleaseStockValidator : AbstractValidator<ReleaseStockCommand>
{
    public ReleaseStockValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
    }
}
