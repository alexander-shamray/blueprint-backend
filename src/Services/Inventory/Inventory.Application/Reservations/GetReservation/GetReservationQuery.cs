using Common.Application;

namespace Inventory.Application.Reservations.GetReservation;

public sealed record GetReservationQuery(Guid OrderId) : IQuery<ReservationDto?>;
