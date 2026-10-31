using System.Data.Common;
using Dapper;

namespace Common.Infrastructure.Migration;

/// <summary>§7.1's isolation: every service database runs read-committed snapshot, or its migrator refuses.</summary>
/// <remarks>
/// ADR-185 owns why. EF Core's CREATE DATABASE sets it; a database made beforehand, or switched off since, lacks it.
/// </remarks>
public static class ReadCommittedSnapshot
{
    /// <returns>Null when the connection's database runs it; otherwise the sentence the job fails with.</returns>
    public static async Task<string?> RefusalAsync(DbConnection connection, CancellationToken ct)
    {
        bool on = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();",
            cancellationToken: ct));

        return on
            ? null
            : "The database runs read committed without row versioning, with READ_COMMITTED_SNAPSHOT off, where "
              + "the services' reads are argued and measured under it (ADR-185). Run section 7.1's ALTER DATABASE "
              + "CURRENT SET READ_COMMITTED_SNAPSHOT ON once, then rerun the job; nothing was migrated.";
    }
}
