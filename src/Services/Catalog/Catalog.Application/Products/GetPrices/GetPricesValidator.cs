using FluentValidation;

namespace Catalog.Application.Products.GetPrices;

/// <summary>The input half of §9.7's hop, validated as a command is (§6.3), so the id list is bounded.</summary>
public sealed class GetPricesValidator : AbstractValidator<GetPricesQuery>
{
    /// <summary>The most ids one request may carry, a constant on §15.4's test.</summary>
    /// <remarks>
    /// Well inside SQL Server's 2,100 parameters, since Dapper expands <c>IN @ProductIds</c> one per id. Not the
    /// order's bound: <c>OrderLimits.MaxLines</c> is equal today, and nothing holds the two together (ADR-045).
    /// </remarks>
    public const int MaxProductIds = 100;

    public GetPricesValidator()
    {
        // NotNull, not NotEmpty: an empty id list is a legal request with an empty answer.
        RuleFor(x => x.ProductIds).NotNull();
        RuleFor(x => x.ProductIds.Count).LessThanOrEqualTo(MaxProductIds).When(x => x.ProductIds is not null);

        // PublishProductValidator's currency rule, for its reasons.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
    }
}
