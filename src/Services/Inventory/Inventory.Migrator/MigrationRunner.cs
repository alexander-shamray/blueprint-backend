using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Migrator;

/// <summary><c>Database.Migrate()</c> (§7.4), then §14.3's gated seed, and the exit code that makes it a job.</summary>
/// <remarks>A type, since the exit code is the whole contract with §7.4's <c>backoffLimit</c>.</remarks>
public sealed class MigrationRunner(
    InventoryDbContext db,
    ILogger<MigrationRunner> logger,
    // The default, not the annotation, is what lets the container build this with no seeder registered (§14.3).
    InventorySeeder? seeder = null)
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
            "Inventory schema is already current; nothing to apply.");

    private static readonly Action<ILogger, Exception?> Applied =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(3, nameof(Applied)),
            "Inventory schema migrated.");

    private static readonly Action<ILogger, Exception?> Failed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4, nameof(Failed)),
            // Not "unchanged": a later migration can fail after an earlier one committed.
            "Inventory migration failed; the schema may be partially applied. The job exits non-zero.");

    private static readonly Action<ILogger, Exception?> NotSeeding =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(5, nameof(NotSeeding)),
            "Not seeding: Seed:Enabled is not true, or the environment is not Development.");

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

            // The hook reruns on every deploy (§7.4), so applying nothing is a success.
            await db.Database.MigrateAsync(ct);

            Applied(logger, null);

            // Said either way, since a gate closed in silence reads exactly like a seeder that is broken (§14.3).
            if (seeder is null)
                NotSeeding(logger, null);
            else
                await seeder.SeedAsync(ct);

            return 0;
        }
        catch (Exception ex)
        {
            // Broad: every failure is the same outcome to the Job, and an escaped one loses the operator's sentence.
            Failed(logger, ex);
            return 1;
        }
    }
}
