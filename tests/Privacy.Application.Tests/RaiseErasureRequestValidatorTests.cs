using FluentValidation.Results;
using Privacy.Application.ErasureRequests.RaiseErasureRequest;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class RaiseErasureRequestValidatorTests
{
    [Fact]
    public void A_subject_passes()
    {
        ValidationResult result = new RaiseErasureRequestValidator()
            .Validate(new RaiseErasureRequestCommand(Guid.CreateVersion7()));

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_subject_is_a_field_keyed_failure()
    {
        ValidationResult result = new RaiseErasureRequestValidator()
            .Validate(new RaiseErasureRequestCommand(Guid.Empty));

        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(RaiseErasureRequestCommand.SubjectId));
    }
}
