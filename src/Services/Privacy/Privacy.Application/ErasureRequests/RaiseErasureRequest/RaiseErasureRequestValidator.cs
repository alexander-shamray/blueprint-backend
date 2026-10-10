using FluentValidation;

namespace Privacy.Application.ErasureRequests.RaiseErasureRequest;

/// <summary>The user-input half of the boundary, a field-keyed 400 before any handler runs (§6.3, §10.5).</summary>
public sealed class RaiseErasureRequestValidator : AbstractValidator<RaiseErasureRequestCommand>
{
    public RaiseErasureRequestValidator()
    {
        RuleFor(x => x.SubjectId).NotEmpty();
    }
}
