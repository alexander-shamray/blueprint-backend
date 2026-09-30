using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;

namespace Inventory.Application.Reservations;

public sealed record LedgerOutcome(IReadOnlyList<ReservedLevel> Levels, IReadOnlyList<ProductId> Unavailable);

public interface IStockLedger
{
    /// <summary>§7.3's statement per line under a savepoint, rolled back to when any line is short.</summary>
    Task<LedgerOutcome> TryTakeAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);

    /// <summary>Returns each line to Available with no guard, and the levels after.</summary>
    Task<IReadOnlyList<ReservedLevel>> GiveBackAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);

    /// <summary>Moves Reserved down only; a short line is a ledger fault, not a business outcome.</summary>
    Task FulfilAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct);
}
