using Common.Infrastructure.Migration;
using Microsoft.EntityFrameworkCore;

namespace Notifications.Infrastructure.Persistence;

/// <summary>§7.4's timeout and §7.1's isolation, for the migrator §4.2 keeps from Common.Infrastructure.</summary>
public static class MigratorCommand
{
    public static int TimeoutSeconds => MigratorCommandTimeout.Seconds;

    /// <returns>Null for a database not yet made: the provider's CREATE DATABASE then sets it (ADR-185).</returns>
    public static async Task<string?> IsolationRefusalAsync(DbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct)
            ? await ReadCommittedSnapshot.RefusalAsync(db.Database.GetDbConnection(), ct)
            : null;
}
