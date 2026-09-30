using Common.Contracts.Ordering.V1;
using FluentValidation;

namespace Web.Bff.Endpoints;

/// <summary>A basket as the screen holds it; a repeated product's quantities are merged, as the order does.</summary>
/// <remarks>A POST body of records rather than a query string or two parallel arrays (ADR-045).</remarks>
public sealed record QuoteRequest(string Currency, IReadOnlyList<QuoteRequestLine> Lines);

public sealed record QuoteRequestLine(Guid ProductId, int Quantity);

/// <summary>§6.4's validator, which <c>CheckoutEndpoints</c> calls itself because this host has no pipeline.</summary>
/// <remarks>The bounds are <see cref="OrderLimits"/>'s, so a quote refuses what the order will (§4.3).</remarks>
internal sealed class QuoteRequestValidator : AbstractValidator<QuoteRequest>
{
    public QuoteRequestValidator()
    {
        // NotEmpty first, because Matches skips null; \z, because .NET's $ matches before a trailing newline.
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");

        // Cascade(Stop), so an explicit null list is a 400 rather than a null dereference below.
        RuleFor(x => x.Lines)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(lines => lines.Count <= OrderLimits.MaxLines)
            .WithMessage($"A quote cannot contain more than {OrderLimits.MaxLines} lines.")
            // A JSON [null] binds as a null element, which the merged-quantity rule below would dereference.
            .Must(lines => lines.All(line => line is not null))
            .WithMessage("A quote line cannot be null.")
            // Over the merged quantity, as the order merges a repeated product (ADR-045); summed as long, because
            // Sum over int is checked and would throw a 500 before the per-line rules run.
            .Must(lines => lines
                .GroupBy(line => line.ProductId)
                .All(product => product.Sum(line => (long)line.Quantity) <= OrderLimits.MaxQuantity))
            .WithMessage($"A quote cannot contain more than {OrderLimits.MaxQuantity} of one product.");

        // Guarded, because the class-level cascade still runs this rule on a null element refused above.
        RuleForEach(x => x.Lines)
            .Where(line => line is not null)
            .ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity)
                .GreaterThanOrEqualTo(OrderLimits.MinQuantity)
                .LessThanOrEqualTo(OrderLimits.MaxQuantity);
        });
    }
}
