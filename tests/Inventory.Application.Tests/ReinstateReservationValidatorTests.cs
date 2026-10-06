using FluentValidation.TestHelper;
using Inventory.Application.Reservations.Reinstate;
using Xunit;

namespace Inventory.Application.Tests;

public class ReinstateReservationValidatorTests
{
    private readonly ReinstateReservationValidator _validator = new();

    [Fact]
    public void An_empty_command_id_is_refused()
    {
        _validator
            .TestValidate(new ReinstateReservationCommand(Guid.Empty, Guid.CreateVersion7()))
            .ShouldHaveValidationErrorFor(c => c.CommandId);
    }

    [Fact]
    public void A_command_id_is_all_the_validator_asks_for()
    {
        _validator
            .TestValidate(new ReinstateReservationCommand(Guid.CreateVersion7(), Guid.Empty))
            .ShouldNotHaveAnyValidationErrors();
    }
}
