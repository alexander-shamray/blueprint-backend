using Ordering.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ordering.Migrator;

/// <summary>
/// <c>Database.Migrate()</c> and nothing else (§7.4), plus the exit code that
/// makes it a job.
/// </summary>
/// <remarks>
/// A type rather than a few lines in <c>Program.cs</c>, for two reasons. The
/// exit-code contract is the whole interface between this process and §7.4's
/// <c>backoffLimit: 2</c> — a swallowed exception makes the Job succeed against
/// an unmigrated database and lets the new pods take traffic — so it is worth a
/// test, and top-level statements are not callable from one. And CA1848 wants
/// the log messages compiled once, which needs fields, which top-level
/// statements do not have.
/// </remarks>
public sealed class MigrationRunner(OrderingDbContext db, ILogger<MigrationRunner> logger)
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
            "Ordering schema is already current; nothing to apply.");

    private static readonly Action<ILogger, Exception?> Applied =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(3, nameof(Applied)),
            "Ordering schema migrated.");

    private static readonly Action<ILogger, Exception?> Failed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4, nameof(Failed)),
            // Not "the schema is unchanged" — that is not something this
            // process can promise. A migration may suppress its transaction,
            // and a later one can fail after an earlier one has committed, so
            // the honest report to whoever reads this line at 3am is that the
            // schema may be half applied.
            "Ordering migration failed; the schema may be partially applied. The job exits non-zero.");

    /// <returns>
    /// 0 once every pending migration has applied; 1 if the run threw, in
    /// which case the schema may be left partially applied.
    /// </returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            // Read before applying, so the log says what this run did rather
            // than what the schema now looks like. On a database that does not
            // exist yet this reports every migration, which is the truthful
            // answer — MigrateAsync creates it.
            string[] pending = [.. await db.Database.GetPendingMigrationsAsync(ct)];

            if (pending.Length == 0)
                AlreadyCurrent(logger, null);
            else
                Applying(logger, pending.Length, string.Join(", ", pending), null);

            // The pre-upgrade hook reruns on every deploy (§7.4), so applying
            // nothing is a successful outcome and not a reason to skip the call.
            await db.Database.MigrateAsync(ct);

            Applied(logger, null);
            return 0;
        }
        catch (Exception ex)
        {
            // Broad on purpose. Every failure mode here — unreachable server,
            // a login without DDL, a migration that will not apply — is the
            // same outcome to the Job: do not let the deploy proceed. Narrowing
            // this to DbException would let an unanticipated fault escape as an
            // unhandled exception, which exits non-zero with a stack trace
            // instead of the sentence an operator needs.
            Failed(logger, ex);
            return 1;
        }
    }
}
