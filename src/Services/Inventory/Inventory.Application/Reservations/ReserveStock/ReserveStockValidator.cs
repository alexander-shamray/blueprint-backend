using Common.Contracts.Ordering.V1;
using FluentValidation;

namespace Inventory.Application.Reservations.ReserveStock;

/// <summary>Ordering's contract bounds: the saga built this from a valid order, so a violation is its bug.</summary>
public sealed class ReserveStockValidator : AbstractValidator<ReserveStockCommand>
{
    public ReserveStockValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.Lines).NotEmpty();
        RuleFor(c => c.Lines)
            .Must(lines => lines.Count <= OrderLimits.MaxLines)
            .WithMessage("More lines than an order can carry.");
        RuleFor(c => c.Lines)
            .Must(lines => lines.Select(l => l.ProductId).Distinct().Count() == lines.Count)
            .WithMessage("A product appears at most once.");
        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Quantity)
                .GreaterThanOrEqualTo(OrderLimits.MinQuantity)
                .LessThanOrEqualTo(OrderLimits.MaxQuantity);
        });
    }
}
