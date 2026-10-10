using FluentValidation;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests.RecordCompletion;

/// <summary>The shape of an answer; a name outside the stored set is the aggregate's to flag, not this one's to refuse.</summary>
public sealed class RecordErasureCompletionValidator : AbstractValidator<RecordErasureCompletionCommand>
{
    public RecordErasureCompletionValidator()
    {
        RuleFor(x => x.RequestId).NotEmpty();
        RuleFor(x => x.Responder).NotEmpty().MaximumLength(ErasureRequest.MaxResponderLength);
        RuleFor(x => x.Count).GreaterThanOrEqualTo(0);
    }
}
