using System.Data;
using System.Data.Common;
using Common.Application;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Notifications.Application.Devices;
using Notifications.Infrastructure.Persistence;

namespace Notifications.Infrastructure.Delivery;

/// <summary>The worker's reads of ADR-117's device rows, and the one delete a provider's answer makes.</summary>
/// <remarks>
/// The token leaves this class only in a <see cref="PushDevice"/>, whose text names the installation alone, and is
/// never a log or span attribute (ADR-117).
/// </remarks>
internal sealed class PushDevices(IDbConnectionFactory connections, NotificationsDbContext db)
{
    private const string Columns = "InstallationId, SubjectId, Platform, AppId, Token";

    // Every row holding a token: a cleared row has none, and reaches nobody (ADR-117).
    private const string OfSubjectSql =
        $"""
        SELECT {Columns}
        FROM notifications.DeviceInstallations
        WHERE SubjectId = @SubjectId
            AND Token IS NOT NULL
        ORDER BY InstallationId;
        """;

    private const string ByInstallationSql =
        $"SELECT {Columns} FROM notifications.DeviceInstallations WHERE InstallationId = @InstallationId;";

    // ADR-117's guard: only while the row still holds the token the send used, so a rotation under the send keeps it.
    private const string DeleteHoldingSql =
        "DELETE FROM notifications.DeviceInstallations WHERE InstallationId = @InstallationId AND Token = @Token;";

    /// <summary>The subject's devices that hold a token, the fan-out's one row each.</summary>
    public async Task<IReadOnlyList<PushDevice>> OfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return
        [
            .. await connection.QueryAsync<PushDevice>(
                new CommandDefinition(OfSubjectSql, new { SubjectId = subjectId }, cancellationToken: ct))
        ];
    }

    /// <summary>One installation's row as it stands now, or null when it is gone.</summary>
    public async Task<PushDevice?> GetAsync(Guid installationId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.QuerySingleOrDefaultAsync<PushDevice>(
            new CommandDefinition(ByInstallationSql, new { InstallationId = installationId }, cancellationToken: ct));
    }

    /// <summary>Deletes the row while it holds <paramref name="token"/>, in the unit's transaction.</summary>
    public async Task<int> DeleteHoldingAsync(Guid installationId, string token, CancellationToken ct)
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // In the outcome's own transaction, as ADR-117 asks, so the delivery's end and the delete land together.
        if (current is null)
        {
            throw new InvalidOperationException(
                "A device row is deleted on an answer only inside the unit of work's transaction (§6.3).");
        }

        DbConnection connection = db.Database.GetDbConnection();

        return await connection.ExecuteAsync(
            new CommandDefinition(
                DeleteHoldingSql,
                new
                {
                    InstallationId = installationId,

                    // varchar at the column's width, as SqlDeviceStore binds it, so the key's seek survives.
                    Token = new DbString { Value = token, IsAnsi = true, Length = DeviceLimits.MaxTokenLength }
                },
                current.GetDbTransaction(),
                cancellationToken: ct));
    }
}
