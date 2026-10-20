using FluentValidation;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests.AmendResponders;

/// <summary>The shape of an amendment; whether the request's set can take it is the aggregate's to say.</summary>
public sealed class AmendErasureRespondersValidator : AbstractValidator<AmendErasureRespondersCommand>
{
    public AmendErasureRespondersValidator()
    {
        // Guid.Empty would be one key for all of a caller's amendments (§8.5); validation runs before a claim (§6.3).
        RuleFor(x => x.CommandId).NotEmpty();
        RuleFor(x => x.RequestId).NotEmpty();
        RuleFor(x => x.Add).NotNull();
        RuleFor(x => x.Remove).NotNull();

        // A null element is refused before the name check, which would dereference it for a 500 rather than this
        // 400 (§6.4); Must, unlike a child validator, is handed the null.
        RuleForEach(x => x.Add)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(ErasureRequest.IsResponderName)
            .WithMessage("Each name added is a holder's name.");
        RuleForEach(x => x.Remove)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(ErasureRequest.IsResponderName)
            .WithMessage("Each name removed is a holder's name.");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(ErasureRequest.MaxReasonLength);
    }
}
