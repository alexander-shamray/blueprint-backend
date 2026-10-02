using FluentValidation;

namespace Inventory.Application.Reservations.Reinstate;

public sealed class ReinstateReservationValidator : AbstractValidator<ReinstateReservationCommand>
{
    public ReinstateReservationValidator()
    {
        // An omitted id binds Guid.Empty, which would key every such request alike; refused before any claim (§6.3).
        RuleFor(c => c.CommandId).NotEmpty();
    }
}
