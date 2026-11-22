using FluentValidation.Results;
using Privacy.Application.ErasureRequests.AmendResponders;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class AmendErasureRespondersValidatorTests
{
    [Fact]
    public void A_plain_reason_passes()
    {
        new AmendErasureRespondersValidator().Validate(Amendment("Ordering retired; DPO ticket 4411."))
            .IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("retired\r\nApproved by: dpo")]
    [InlineData("retired \u202Eroloc")]
    [InlineData("retired\u0000")]
    [InlineData("retired\u2028x")]
    public void A_reason_with_a_control_format_or_separator_character_fails(string reason)
    {
        ValidationResult result = new AmendErasureRespondersValidator().Validate(Amendment(reason));

        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(AmendErasureRespondersCommand.Reason));
    }

    private static AmendErasureRespondersCommand Amendment(string reason) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), [], ["ordering"], reason);
}
