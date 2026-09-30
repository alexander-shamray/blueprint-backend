using Inventory.Domain.Stock;

namespace Inventory.Domain.Reservations;

public sealed record ReservationLine(ProductId ProductId, int Quantity);

/// <summary>
/// What §7.3's statement returns for one line: the level it left and the instant it stamped under the row lock,
/// which is the level event's <c>OccurredAt</c>.
/// </summary>
public sealed record ReservedLevel(ProductId ProductId, int Available, DateTimeOffset UpdatedAt);
