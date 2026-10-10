using FluentValidation.Results;
using Privacy.Application.ErasureRequests.RecordCompletion;
using Privacy.Application.ErasureRequests.ReissueErasureRequest;
using Privacy.Domain.ErasureRequests;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class CompletionAndReissueValidatorTests
{
    [Fact]
    public void A_well_formed_completion_passes()
    {
        new RecordErasureCompletionValidator()
            .Validate(new RecordErasureCompletionCommand(Guid.CreateVersion7(), "ordering", 0))
            .IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("ordering", -1)]
    public void A_completion_with_no_name_or_a_negative_count_fails(string responder, int count)
    {
        new RecordErasureCompletionValidator()
            .Validate(new RecordErasureCompletionCommand(Guid.CreateVersion7(), responder, count))
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_name_wider_than_the_column_fails()
    {
        ValidationResult result = new RecordErasureCompletionValidator().Validate(
            new RecordErasureCompletionCommand(
                Guid.CreateVersion7(),
                new string('a', ErasureRequest.MaxResponderLength + 1),
                1));

        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(RecordErasureCompletionCommand.Responder));
    }

    [Fact]
    public void A_completion_for_no_request_fails()
    {
        new RecordErasureCompletionValidator()
            .Validate(new RecordErasureCompletionCommand(Guid.Empty, "ordering", 1))
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_reissue_needs_a_command_id_and_a_request()
    {
        ReissueErasureRequestValidator validator = new();

        validator.Validate(new ReissueErasureRequestCommand(Guid.CreateVersion7(), Guid.CreateVersion7()))
            .IsValid.ShouldBeTrue();
        validator.Validate(new ReissueErasureRequestCommand(Guid.Empty, Guid.CreateVersion7())).IsValid.ShouldBeFalse();
        validator.Validate(new ReissueErasureRequestCommand(Guid.CreateVersion7(), Guid.Empty)).IsValid.ShouldBeFalse();
    }
}
