using Inventory.Domain.Stock;

namespace Inventory.Domain.Reservations;

public sealed record ReservationLine(ProductId ProductId, int Quantity);

/// <summary>
/// What §7.3's statement returns for one line: the level it left and the
/// instant it assigned under the row lock, which is the level event's
/// OccurredAt — never a clock read before the statement, which two
/// serialised writers could take in the other order.
/// </summary>
public sealed record ReservedLevel(ProductId ProductId, int Available, DateTimeOffset UpdatedAt);
