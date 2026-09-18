using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;

namespace Inventory.Application.Reservations;

public sealed record LedgerOutcome(IReadOnlyList<ReservedLevel> Levels, IReadOnlyList<ProductId> Unavailable);

public interface IStockLedger
{
    /// Runs §7.3's statement per line in ProductId order under a savepoint;
    /// rolls back to it when any line is short. Never partial.
    Task<LedgerOutcome> TryTakeAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);

    /// Returns each line to Available with no guard. Levels after.
    Task<IReadOnlyList<ReservedLevel>> GiveBackAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);
}
