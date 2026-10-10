using FluentValidation;

namespace Privacy.Application.ErasureRequests.ReissueErasureRequest;

public sealed class ReissueErasureRequestValidator : AbstractValidator<ReissueErasureRequestCommand>
{
    public ReissueErasureRequestValidator()
    {
        // Guid.Empty would be one key for all of a caller's requests (§8.5); validation runs before a claim (§6.3).
        RuleFor(x => x.CommandId).NotEmpty();
        RuleFor(x => x.RequestId).NotEmpty();
    }
}
