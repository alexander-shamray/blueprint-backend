namespace Inventory.Application.Reservations.GetReservation;

public sealed record ReservationLineDto(Guid ProductId, int Quantity);

public sealed record ReservationDto(
    Guid OrderId,
    string Status,
    IReadOnlyList<ReservationLineDto> Lines,
    DateTimeOffset UpdatedAt);
