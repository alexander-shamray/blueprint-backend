using Common.Contracts.Ordering.V1;
using FluentValidation;

namespace Inventory.Application.Reservations.ReserveStock;

/// <summary>
/// Ordering's contract bounds, because the saga built this from an order that
/// already satisfied them: a violation is the sender's bug, not a stock decision.
/// </summary>
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
