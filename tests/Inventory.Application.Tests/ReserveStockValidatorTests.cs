using Common.Contracts.Ordering.V1;
using FluentValidation.TestHelper;
using Inventory.Application.Reservations.ReserveStock;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Xunit;

namespace Inventory.Application.Tests;

public class ReserveStockValidatorTests
{
    private readonly ReserveStockValidator _validator = new();

    [Fact]
    public void No_lines_is_refused()
    {
        _validator.TestValidate(new ReserveStockCommand(Guid.CreateVersion7(), []))
            .ShouldHaveValidationErrorFor(c => c.Lines);
    }

    [Fact]
    public void A_repeated_product_is_refused()
    {
        ProductId product = ProductId.New();
        _validator.TestValidate(new ReserveStockCommand(Guid.CreateVersion7(), [new(product, 1), new(product, 2)]))
            .ShouldHaveValidationErrorFor(c => c.Lines);
    }

    [Fact]
    public void A_zero_quantity_is_refused()
    {
        _validator.TestValidate(new ReserveStockCommand(Guid.CreateVersion7(), [new(ProductId.New(), 0)]))
            .ShouldHaveValidationErrorFor("Lines[0].Quantity");
    }

    [Fact]
    public void More_lines_than_an_order_can_carry_are_refused()
    {
        ReservationLine[] lines =
            [.. Enumerable.Range(0, OrderLimits.MaxLines + 1).Select(_ => new ReservationLine(ProductId.New(), 1))];

        _validator.TestValidate(new ReserveStockCommand(Guid.CreateVersion7(), lines))
            .ShouldHaveValidationErrorFor(c => c.Lines);
    }

    [Fact]
    public void A_quantity_past_the_contract_ceiling_is_refused()
    {
        _validator.TestValidate(new ReserveStockCommand(
                Guid.CreateVersion7(), [new(ProductId.New(), OrderLimits.MaxQuantity + 1)]))
            .ShouldHaveValidationErrorFor("Lines[0].Quantity");
    }
}
