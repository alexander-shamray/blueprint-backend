using Common.Contracts.Ordering.V1;
using FluentValidation;

namespace Ordering.Application.Orders.PlaceOrder;

/// <summary>§6.4's validator: the request's shape only; the order's own rules are <c>Order</c>'s (§5.7).</summary>
public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        // Guid.Empty would be one key shared by every caller; validation runs before any claim (§6.3).
        RuleFor(x => x.CommandId).NotEmpty();
        // NotEmpty first, since Matches skips null; \z, not $, which matches before a trailing newline (§6.4).
        RuleFor(x => x.Currency).NotEmpty().Matches(@"^[A-Za-z]{3}\z");
        // OrderLimits' bounds, which Web.Bff's quote shares (ADR-045). Cascade(Stop), or on "items": null the
        // size predicate would dereference the null NotEmpty just rejected (§6.4).
        RuleFor(x => x.Items)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(items => items.Count <= OrderLimits.MaxLines)
            .WithMessage($"An order cannot contain more than {OrderLimits.MaxLines} items.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();

            // GreaterThanOrEqualTo, since MinQuantity is the smallest quantity a line may carry (ADR-045).
            item
                .RuleFor(i => i.Quantity)
                .GreaterThanOrEqualTo(OrderLimits.MinQuantity)
                .LessThanOrEqualTo(OrderLimits.MaxQuantity);
        });

        // Again over the merged quantity, since Order.AddLine merges a repeated product (ADR-045). Summed as long,
        // as Enumerable.Sum over int is checked and would throw before RuleForEach reports (§6.4).
        RuleFor(x => x.Items)
            .Must(items => items
                .GroupBy(i => i.ProductId)
                .All(product => product.Sum(i => (long)i.Quantity) <= OrderLimits.MaxQuantity))
            .WithMessage($"An order cannot contain more than {OrderLimits.MaxQuantity} of one product.")
            .When(x => x.Items is not null && x.Items.All(i => i is not null));

        // The address as a whole first, or a null member would produce a failure for each of its parts.
        RuleFor(x => x.ShippingAddress).NotNull();
        When(
            x => x.ShippingAddress is not null,
            () =>
            {
                RuleFor(x => x.ShippingAddress.Line1).NotEmpty().MaximumLength(200);
                RuleFor(x => x.ShippingAddress.Line2).MaximumLength(200);
                RuleFor(x => x.ShippingAddress.City).NotEmpty().MaximumLength(100);
                RuleFor(x => x.ShippingAddress.PostalCode).NotEmpty().MaximumLength(20);

                RuleFor(x => x.ShippingAddress.Country).NotEmpty().Matches(@"^[A-Za-z]{2}\z");
            });
    }
}
