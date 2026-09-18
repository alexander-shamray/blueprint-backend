using System.Data.Common;
using Dapper;
using Inventory.Application.Reservations;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// §7.3's targeted pessimistic update, per line, on the transaction the unit
/// of work opened. Lines run in <c>ProductId</c> order so two reservations
/// over the same products take their row locks in one sequence.
/// </summary>
internal sealed class SqlStockLedger(InventoryDbContext db) : IStockLedger
{
    private const string Savepoint = "Reserve";

    // §7.3's statement, as printed, with two additions the spec's section 4
    // argues: the OUTPUT returns the stamp, and the stamp is monotonic per
    // row — the clock when it is ahead of the row, one tick past the row
    // otherwise — so two serialised writers' levels carry strictly ordered
    // OccurredAt values whatever the server clock does between them.
    // Zero rows affected is "not enough stock".
    private const string Stamp =
        "CASE WHEN SYSDATETIMEOFFSET() > UpdatedAt THEN SYSDATETIMEOFFSET() ELSE DATEADD(ns, 100, UpdatedAt) END";

    private static readonly string TakeSql =
        $"""
        UPDATE inventory.StockItems
        SET Available = Available - @Quantity, Reserved = Reserved + @Quantity, UpdatedAt = {Stamp}
        OUTPUT inserted.Available, inserted.UpdatedAt
        WHERE ProductId = @ProductId
            AND Available >= @Quantity;
        """;

    // No guard: a release returns what was held, whatever the level is now.
    private static readonly string GiveBackSql =
        $"""
        UPDATE inventory.StockItems
        SET Available = Available + @Quantity, Reserved = Reserved - @Quantity, UpdatedAt = {Stamp}
        OUTPUT inserted.Available, inserted.UpdatedAt
        WHERE ProductId = @ProductId;
        """;

    private sealed record LevelRow(int Available, DateTimeOffset UpdatedAt);

    public async Task<LedgerOutcome> TryTakeAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();

        await connection.ExecuteAsync(new CommandDefinition(
            $"SAVE TRANSACTION {Savepoint};", transaction: transaction, cancellationToken: ct));

        List<ReservedLevel> levels = [];
        List<ProductId> unavailable = [];

        foreach (ReservationLine line in lines.OrderBy(l => l.ProductId.Value))
        {
            LevelRow? row = await connection.QuerySingleOrDefaultAsync<LevelRow>(new CommandDefinition(
                TakeSql,
                new { ProductId = line.ProductId.Value, line.Quantity },
                transaction: transaction,
                cancellationToken: ct));

            if (row is null)
                unavailable.Add(line.ProductId);
            else
                levels.Add(new ReservedLevel(line.ProductId, row.Available, row.UpdatedAt));
        }

        if (unavailable.Count == 0)
            return new LedgerOutcome(levels, unavailable);

        await connection.ExecuteAsync(new CommandDefinition(
            $"ROLLBACK TRANSACTION {Savepoint};", transaction: transaction, cancellationToken: ct));

        return new LedgerOutcome([], unavailable);
    }

    public async Task<IReadOnlyList<ReservedLevel>> GiveBackAsync(
        IReadOnlyList<ReservationLine> lines,
        CancellationToken ct)
    {
        (DbConnection connection, DbTransaction transaction) = Current();
        List<ReservedLevel> levels = [];

        foreach (ReservationLine line in lines.OrderBy(l => l.ProductId.Value))
        {
            LevelRow? row = await connection.QuerySingleOrDefaultAsync<LevelRow>(new CommandDefinition(
                GiveBackSql,
                new { ProductId = line.ProductId.Value, line.Quantity },
                transaction: transaction,
                cancellationToken: ct));

            // A held line implies its row: a reservation holds stock a
            // statement decremented, and the row it decremented cannot have
            // gone. No row is the ledger disagreeing with itself, and a
            // release that skipped it would publish StockReleased for stock it
            // never returned; the transaction rolls back instead.
            if (row is null)
                throw new InvalidOperationException($"Product {line.ProductId} has a held line and no stock row.");

            levels.Add(new ReservedLevel(line.ProductId, row.Available, row.UpdatedAt));
        }

        return levels;
    }

    private (DbConnection, DbTransaction) Current()
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // The same refusal EfUnitOfWork.ExecuteRawAsync makes: a statement
        // with no transaction autocommits on its own, outside the unit the
        // caller believes it is in.
        if (current is null)
        {
            throw new InvalidOperationException(
                "The stock ledger runs only inside the unit of work's transaction (§6.3).");
        }

        return (db.Database.GetDbConnection(), current.GetDbTransaction());
    }
}
