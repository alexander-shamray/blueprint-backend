using FluentValidation.TestHelper;
using Inventory.Application.Stock.SetOnHand;
using Xunit;

namespace Inventory.Application.Tests;

public class SetOnHandValidatorTests
{
    private readonly SetOnHandValidator _validator = new();

    [Fact]
    public void A_negative_count_is_refused_before_the_handler()
    {
        _validator.TestValidate(new SetOnHandCommand(Guid.CreateVersion7(), -1))
            .ShouldHaveValidationErrorFor(c => c.OnHand);
    }

    [Fact]
    public void An_empty_product_id_is_refused()
    {
        _validator.TestValidate(new SetOnHandCommand(Guid.Empty, 1))
            .ShouldHaveValidationErrorFor(c => c.ProductId);
    }

    [Fact]
    public void Zero_is_a_valid_stock_take()
    {
        _validator.TestValidate(new SetOnHandCommand(Guid.CreateVersion7(), 0))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void An_omitted_count_is_refused_rather_than_read_as_zero()
    {
        _validator.TestValidate(new SetOnHandCommand(Guid.CreateVersion7(), null))
            .ShouldHaveValidationErrorFor(c => c.OnHand);
    }
}
