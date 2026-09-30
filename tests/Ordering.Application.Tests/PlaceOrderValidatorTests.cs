using Common.Contracts.Ordering.V1;
using FluentValidation.Results;
using Ordering.Application.Orders.PlaceOrder;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>§6.4's validator, which decides only the request's shape, so it needs no container (§5.7).</summary>
public class PlaceOrderValidatorTests
{
    private static readonly PlaceOrderValidator Validator = new();

    private static readonly Guid Product = Guid.CreateVersion7();

    private static readonly Guid OtherProduct = Guid.CreateVersion7();

    private static AddressDto AnAddress() =>
        new("1 Test Street", null, "Almaty", "050000", "KZ");

    private static PlaceOrderCommand WithItems(int count) =>
        new(
            Guid.CreateVersion7(),
            [.. Enumerable.Range(0, count).Select(_ => new PlaceOrderItem(Guid.CreateVersion7(), 1))],
            AnAddress(),
            "EUR");

    [Fact]
    public void An_order_at_the_item_ceiling_is_accepted()
    {
        Validator.Validate(WithItems(OrderLimits.MaxLines)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_order_past_the_item_ceiling_is_a_validation_failure()
    {
        // ProjectedPriceReader binds a SQL parameter per product id, so the ceiling keeps a 400 from being a 500.
        ValidationResult result = Validator.Validate(WithItems(OrderLimits.MaxLines + 1));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(PlaceOrderCommand.Items));
    }

    [Fact]
    public void The_ceiling_is_well_inside_what_the_price_query_can_ask_for()
    {
        // SQL Server's parameter limit; the assertion leaves one for @Currency.
        const int sqlServerParameterLimit = 2100;

        OrderLimits.MaxLines.ShouldBeLessThan(sqlServerParameterLimit - 1);
    }

    [Fact]
    public void Two_lines_for_one_product_may_not_exceed_the_quantity_ceiling_between_them()
    {
        // Order.AddLine merges lines for one product, so a per-item bound alone does not bound the order.
        PlaceOrderCommand command = new(
            Guid.CreateVersion7(),
            [new PlaceOrderItem(Product, OrderLimits.MaxQuantity), new PlaceOrderItem(Product, 1)],
            AnAddress(),
            "EUR");

        ValidationResult result = Validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(PlaceOrderCommand.Items));
    }

    [Fact]
    public void Two_lines_for_one_product_at_the_ceiling_between_them_are_accepted()
    {
        PlaceOrderCommand command = new(
            Guid.CreateVersion7(),
            [
                new PlaceOrderItem(Product, OrderLimits.MaxQuantity - 1),
                new PlaceOrderItem(Product, 1)
            ],
            AnAddress(),
            "EUR");

        Validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Two_products_at_the_quantity_ceiling_are_both_accepted()
    {
        // The rule is per product, which a basket-wide Sum would break by refusing this valid order.
        PlaceOrderCommand command = new(
            Guid.CreateVersion7(),
            [
                new PlaceOrderItem(Product, OrderLimits.MaxQuantity),
                new PlaceOrderItem(OtherProduct, OrderLimits.MaxQuantity)
            ],
            AnAddress(),
            "EUR");

        Validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_quantity_that_overflows_the_merge_is_a_400_and_not_a_500()
    {
        // Enumerable.Sum over int is checked, so the assertion is that Validate returns rather than throws.
        PlaceOrderCommand command = new(
            Guid.CreateVersion7(),
            [new PlaceOrderItem(Product, int.MaxValue), new PlaceOrderItem(Product, int.MaxValue)],
            AnAddress(),
            "EUR");

        ValidationResult result = Should.NotThrow(() => Validator.Validate(command));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_null_item_list_is_a_400_and_not_a_500()
    {
        // FluentValidation runs every validator in a rule unless Cascade(Stop), so null must not reach the next.
        PlaceOrderCommand command = new(Guid.CreateVersion7(), null!, AnAddress(), "EUR");

        ValidationResult result = Should.NotThrow(() => Validator.Validate(command));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void An_omitted_CommandId_is_refused_before_any_key_is_claimed()
    {
        // An omitted CommandId binds as Guid.Empty, one key every such caller would share (§8.5).
        PlaceOrderCommand command = WithItems(1) with { CommandId = Guid.Empty };

        ValidationResult result = Validator.Validate(command);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == nameof(PlaceOrderCommand.CommandId));
    }

    [Fact]
    public void An_empty_order_is_refused_at_the_edge_as_well_as_in_the_domain()
    {
        // Order.Place also refuses this; here it is a 400 rather than a 500 (§5.7).
        Validator.Validate(WithItems(0)).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("EURO")]
    [InlineData("EU")]
    [InlineData("E1R")]
    [InlineData("EUR\n")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency)
    {
        // "EUR\n" is the case \z catches and $ does not: .NET's $ matches
        // before a trailing newline, so the domain would have seen it.
        PlaceOrderCommand command = WithItems(1) with { Currency = currency };

        Validator.Validate(command).IsValid.ShouldBeFalse();
    }
}
