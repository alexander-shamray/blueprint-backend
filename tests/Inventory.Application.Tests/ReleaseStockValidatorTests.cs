using FluentValidation.TestHelper;
using Inventory.Application.Reservations.ReleaseStock;
using Xunit;

namespace Inventory.Application.Tests;

public class ReleaseStockValidatorTests
{
    private readonly ReleaseStockValidator _validator = new();

    [Fact]
    public void An_empty_order_id_is_refused()
    {
        _validator.TestValidate(new ReleaseStockCommand(Guid.Empty, CommandOrigin.User))
            .ShouldHaveValidationErrorFor(c => c.OrderId);
    }
}
