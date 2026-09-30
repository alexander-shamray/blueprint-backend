using Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Catalog.Migrator;

/// <summary><c>Database.Migrate()</c> and nothing else (§7.4), plus the exit code that makes it a job.</summary>
public sealed class MigrationRunner(CatalogDbContext db, ILogger<MigrationRunner> logger)
{
    private static readonly Action<ILogger, int, string, Exception?> Applying =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1, nameof(Applying)),
            "Applying {Count} pending migration(s): {Migrations}");

    private static readonly Action<ILogger, Exception?> AlreadyCurrent =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2, nameof(AlreadyCurrent)),
            "Catalog schema is already current; nothing to apply.");

    private static readonly Action<ILogger, Exception?> Applied =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(3, nameof(Applied)),
            "Catalog schema migrated.");

    private static readonly Action<ILogger, Exception?> Failed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4, nameof(Failed)),
            // Not "unchanged": a later migration can fail after an earlier one has committed.
            "Catalog migration failed; the schema may be partially applied. The job exits non-zero.");

    /// <returns>0 once every pending migration has applied; 1 if the run threw.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            // Read before applying, so the log says what this run did.
            string[] pending = [.. await db.Database.GetPendingMigrationsAsync(ct)];

            if (pending.Length == 0)
                AlreadyCurrent(logger, null);
            else
                Applying(logger, pending.Length, string.Join(", ", pending), null);

            // Applying nothing is a success: the pre-upgrade hook reruns on every deploy (§7.4).
            await db.Database.MigrateAsync(ct);

            Applied(logger, null);
            return 0;
        }
        catch (Exception ex)
        {
            // Broad on purpose: every failure here means the deploy must not proceed, and an escaped exception
            // would exit without the sentence an operator needs.
            Failed(logger, ex);
            return 1;
        }
    }
}
