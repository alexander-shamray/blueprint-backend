using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Notifications.Application.Devices;
using Notifications.Application.Devices.RegisterDevice;
using Notifications.Application.Devices.SignOutDevice;

namespace Notifications.Infrastructure.Persistence;

/// <summary>ADR-117's registration and sign-out, each one batch deciding against the row it holds locked.</summary>
/// <remarks>
/// UPDLOCK and HOLDLOCK take the installation's key, or its range when it has no row yet, so two requests for one
/// installation run one after the other, as do two that name one token (ADR-118).
/// </remarks>
internal sealed class SqlDeviceStore(NotificationsDbContext db, DeviceTokenOptions options) : IDeviceStore
{
    // What a sign-out leaves of a row: its installation id and sequence (ADR-117), and its last-seen time (ADR-118).
    // The requests go with the subject, since one joined to an open request names it (ADR-138).
    private const string Cleared =
        "SubjectId = NULL, Platform = NULL, AppId = NULL, Token = NULL, RegisteredAt = NULL, ErasedUnder = N'[]'";

    private const string RegisterSql =
        $"""
        DECLARE @Row TABLE (SubjectId uniqueidentifier NULL, Platform varchar(8) NULL, AppId varchar(255) NULL,
            Token varchar(1024) NULL, Sequence bigint NOT NULL);
        INSERT @Row
        SELECT SubjectId, Platform, AppId, Token, Sequence
        FROM notifications.DeviceInstallations WITH (UPDLOCK, HOLDLOCK)
        WHERE InstallationId = @InstallationId;

        DECLARE @Current bigint = (SELECT Sequence FROM @Row);
        IF @Current IS NOT NULL
        BEGIN
            IF @Sequence = @Current
            BEGIN
                IF EXISTS (SELECT 1 FROM @Row WHERE SubjectId = @SubjectId AND Platform = @Platform
                    AND AppId = @AppId AND Token = @Token)
                    SELECT 'Repeated' AS Outcome, @Current AS Sequence;
                ELSE
                    SELECT 'Stale' AS Outcome, @Current AS Sequence;
                RETURN;
            END;
            IF @Sequence < @Current OR @Sequence - @Current > @MaxSequenceStep
            BEGIN
                SELECT 'Stale' AS Outcome, @Current AS Sequence;
                RETURN;
            END;
        END;

        UPDATE notifications.DeviceInstallations WITH (UPDLOCK, HOLDLOCK)
        SET {Cleared}
        WHERE Platform = @Platform AND AppId = @AppId AND Token = @Token AND InstallationId <> @InstallationId;

        IF @Current IS NULL
            INSERT notifications.DeviceInstallations
                (InstallationId, SubjectId, Platform, AppId, Token, Sequence, RegisteredAt, LastSeenAt, ErasedUnder)
            VALUES (@InstallationId, @SubjectId, @Platform, @AppId, @Token, @Sequence, @Now, @Now, @ErasedUnder);
        ELSE
            UPDATE notifications.DeviceInstallations
            SET RegisteredAt = CASE
                    WHEN RegisteredAt IS NOT NULL AND SubjectId = @SubjectId AND Platform = @Platform
                        AND AppId = @AppId AND Token = @Token THEN RegisteredAt
                    ELSE @Now
                END,
                SubjectId = @SubjectId, Platform = @Platform, AppId = @AppId, Token = @Token,
                Sequence = @Sequence, LastSeenAt = @Now, ErasedUnder = @ErasedUnder
            WHERE InstallationId = @InstallationId;

        UPDATE notifications.DeviceInstallations
        SET {Cleared}
        WHERE InstallationId IN (
            SELECT InstallationId
            FROM notifications.DeviceInstallations WITH (UPDLOCK, HOLDLOCK)
            WHERE SubjectId = @SubjectId
            ORDER BY LastSeenAt DESC, InstallationId
            OFFSET @MaxPerSubject ROWS);

        SELECT 'Applied' AS Outcome, @Sequence AS Sequence;
        """;

    private const string SignOutSql =
        $"""
        DECLARE @Current bigint, @Owner uniqueidentifier;
        SELECT @Current = Sequence, @Owner = SubjectId
        FROM notifications.DeviceInstallations WITH (UPDLOCK, HOLDLOCK)
        WHERE InstallationId = @InstallationId;

        -- No row, or another subject's live one: a DELETE clears only the caller's (ADR-117), so nothing is written.
        IF @Current IS NULL OR @Owner <> @SubjectId
        BEGIN
            SELECT 'Applied' AS Outcome, @Sequence AS Sequence;
            RETURN;
        END;

        -- A cleared row is ordered as any row is, and its equal sequence is the repeat.
        IF @Owner IS NULL AND @Sequence = @Current
        BEGIN
            SELECT 'Repeated' AS Outcome, @Current AS Sequence;
            RETURN;
        END;

        IF @Sequence <= @Current OR @Sequence - @Current > @MaxSequenceStep
        BEGIN
            SELECT 'Stale' AS Outcome, @Current AS Sequence;
            RETURN;
        END;

        -- A cleared row has no subject, so any caller's sign-out above it moves its sequence and nothing else
        -- (ADR-121): a registration delayed past the sign-out is then stale, and the unchanged last-seen time lets no
        -- caller keep the row past the stale window.
        IF @Owner IS NULL
            UPDATE notifications.DeviceInstallations
            SET Sequence = @Sequence
            WHERE InstallationId = @InstallationId;
        ELSE
            UPDATE notifications.DeviceInstallations
            SET {Cleared}, Sequence = @Sequence, LastSeenAt = @Now
            WHERE InstallationId = @InstallationId;

        SELECT 'Applied' AS Outcome, @Sequence AS Sequence;
        """;

    public Task<DeviceWrite> RegisterAsync(
        RegisterDeviceCommand command,
        IReadOnlyList<Guid> erasedUnder,
        DateTimeOffset now,
        CancellationToken ct) =>
        DecideAsync(
            RegisterSql,
            new
            {
                command.InstallationId,
                command.SubjectId,
                Platform = Ansi(command.Platform, DeviceLimits.MaxPlatformLength),
                AppId = Ansi(command.AppId, DeviceLimits.MaxAppIdLength),
                Token = Ansi(command.Token, DeviceLimits.MaxTokenLength),
                command.Sequence,
                Now = now,
                options.MaxSequenceStep,
                options.MaxPerSubject,
                ErasedUnder = JsonSerializer.Serialize(erasedUnder)
            },
            ct);

    public Task<DeviceWrite> SignOutAsync(SignOutDeviceCommand command, DateTimeOffset now, CancellationToken ct) =>
        DecideAsync(
            SignOutSql,
            new
            {
                command.InstallationId,
                command.SubjectId,
                command.Sequence,
                Now = now,
                options.MaxSequenceStep
            },
            ct);

    // varchar at the column's width: an nvarchar parameter converts the column, and the unique key's seek with it.
    private static DbString Ansi(string value, int length) => new() { Value = value, IsAnsi = true, Length = length };

    private async Task<DeviceWrite> DecideAsync(string sql, object parameters, CancellationToken ct)
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // As in SqlNotificationsPersonalDataStore: with no transaction, the batch would commit outside the unit.
        if (current is null)
        {
            throw new InvalidOperationException(
                "A device row is written only inside the unit of work's transaction (§6.3).");
        }

        DbConnection connection = db.Database.GetDbConnection();

        (string outcome, long sequence) = await connection.QuerySingleAsync<(string, long)>(
            new CommandDefinition(sql, parameters, current.GetDbTransaction(), cancellationToken: ct));

        return new DeviceWrite(Enum.Parse<DeviceWriteOutcome>(outcome), sequence);
    }
}
