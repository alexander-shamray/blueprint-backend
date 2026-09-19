using FluentValidation.TestHelper;
using Inventory.Application.Reservations.Fulfil;
using Xunit;

namespace Inventory.Application.Tests;

public class FulfilReservationValidatorTests
{
    private readonly FulfilReservationValidator _validator = new();

    [Fact]
    public void An_empty_order_id_is_refused()
    {
        _validator.TestValidate(new FulfilReservationCommand(Guid.Empty))
            .ShouldHaveValidationErrorFor(c => c.OrderId);
    }

    [Fact]
    public void A_real_order_id_is_accepted()
    {
        _validator.TestValidate(new FulfilReservationCommand(Guid.CreateVersion7()))
            .ShouldNotHaveAnyValidationErrors();
    }
}
