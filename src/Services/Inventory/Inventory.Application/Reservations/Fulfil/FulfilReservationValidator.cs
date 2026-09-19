using FluentValidation;

namespace Inventory.Application.Reservations.Fulfil;

public sealed class FulfilReservationValidator : AbstractValidator<FulfilReservationCommand>
{
    public FulfilReservationValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
    }
}
