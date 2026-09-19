using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;

namespace Inventory.Application.Reservations;

public sealed record LedgerOutcome(IReadOnlyList<ReservedLevel> Levels, IReadOnlyList<ProductId> Unavailable);

public interface IStockLedger
{
    /// <summary>
    /// Runs §7.3's statement per line in ProductId order under a savepoint;
    /// rolls back to it when any line is short. Never partial.
    /// </summary>
    Task<LedgerOutcome> TryTakeAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);

    /// <summary>Returns each line to Available with no guard. Levels after.</summary>
    Task<IReadOnlyList<ReservedLevel>> GiveBackAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);
}
