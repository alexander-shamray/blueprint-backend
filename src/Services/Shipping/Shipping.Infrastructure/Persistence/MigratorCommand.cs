using Common.Infrastructure.Migration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Shipping.Infrastructure.Persistence;

/// <summary>§7.4's timeout and §7.1's isolation, for the migrator §4.2 keeps from Common.Infrastructure.</summary>
public static class MigratorCommand
{
    public static int TimeoutSeconds => MigratorCommandTimeout.Seconds;

    /// <returns>Null for a database not yet made: the provider's CREATE DATABASE then sets it (ADR-185).</returns>
    /// <remarks>Exists, not CanConnect, which reads any failure as absent and would skip §7.4's read.</remarks>
    public static async Task<string?> IsolationRefusalAsync(DbContext db, CancellationToken ct) =>
        await db.Database.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct)
            ? await ReadCommittedSnapshot.RefusalAsync(db.Database.GetDbConnection(), ct)
            : null;
}
