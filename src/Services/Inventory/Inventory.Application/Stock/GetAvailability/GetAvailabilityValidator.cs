using FluentValidation;

namespace Inventory.Application.Stock.GetAvailability;

/// <summary>The input half of ADR-154's hop, validated as a command is (§6.3), so the id list is bounded.</summary>
public sealed class GetAvailabilityValidator : AbstractValidator<GetAvailabilityQuery>
{
    /// <summary>The most ids one request may carry, well inside SQL Server's 2,100 parameters.</summary>
    public const int MaxProductIds = 100;

    public GetAvailabilityValidator()
    {
        // NotNull, not NotEmpty: an empty id list is a legal request with an empty answer.
        RuleFor(x => x.ProductIds).NotNull();
        RuleFor(x => x.ProductIds.Count).LessThanOrEqualTo(MaxProductIds).When(x => x.ProductIds is not null);
    }
}
