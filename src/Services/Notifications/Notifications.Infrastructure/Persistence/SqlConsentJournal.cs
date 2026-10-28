using System.Data;
using System.Data.Common;
using System.Text.Json;
using Common.Application;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Notifications.Application.Preferences;
using Notifications.Application.Preferences.SetPreference;

namespace Notifications.Infrastructure.Persistence;

/// <summary>ADR-120's journal: an append decided in one batch against the pair's newest entry, held locked.</summary>
/// <remarks>
/// UPDLOCK and HOLDLOCK take the subject's pair range, so two writes for one pair, and an append and the erasure's
/// anonymising, run one after the other; the two unique keys are the backstop ADR-120 names.
/// </remarks>
internal sealed class SqlConsentJournal(NotificationsDbContext db, IDbConnectionFactory connections) : IConsentJournal
{
    // The pair's newest is its highest position, which the append assigns under the lock: never a chain link, which
    // the retention pass may delete, nor a time, which two API replicas' clocks need not agree on (ADR-120).
    private const string Newest =
        """
        NOT EXISTS (SELECT 1 FROM notifications.ConsentEntries h
            WHERE h.SubjectId = e.SubjectId
                AND h.Topic = e.Topic
                AND h.Channel = e.Channel
                AND h.Position > e.Position)
        """;

    private const string NewestSql =
        $"""
        SELECT e.EntryId, e.Topic, e.Channel, e.OptedIn, e.WrittenAt, e.Client, Supersedes = e.SupersedesEntryId
        FROM notifications.ConsentEntries e
        WHERE e.SubjectId = @SubjectId
            AND {Newest};
        """;

    private const string NewestOfPairSql =
        $"""
        SELECT e.EntryId, e.Topic, e.Channel, e.OptedIn, e.WrittenAt, e.Client, Supersedes = e.SupersedesEntryId
        FROM notifications.ConsentEntries e
        WHERE e.SubjectId = @SubjectId
            AND e.Topic = @Topic
            AND e.Channel = @Channel
            AND {Newest};
        """;

    private const string AppendSql =
        $"""
        -- Every entry of the pair's range, so the lock covers the range and not only the row the newest is.
        DECLARE @Pair TABLE (EntryId uniqueidentifier NOT NULL, OptedIn bit NOT NULL,
            WrittenAt datetimeoffset(7) NOT NULL, Client varchar(255) NOT NULL, Supersedes uniqueidentifier NULL,
            Position int NOT NULL);
        INSERT @Pair
        SELECT e.EntryId, e.OptedIn, e.WrittenAt, e.Client, e.SupersedesEntryId, e.Position
        FROM notifications.ConsentEntries e WITH (UPDLOCK, HOLDLOCK)
        WHERE e.SubjectId = @SubjectId
            AND e.Topic = @Topic
            AND e.Channel = @Channel;

        DECLARE @Newest TABLE (EntryId uniqueidentifier NOT NULL, OptedIn bit NOT NULL,
            WrittenAt datetimeoffset(7) NOT NULL, Client varchar(255) NOT NULL, Supersedes uniqueidentifier NULL);
        INSERT @Newest
        SELECT TOP (1) EntryId, OptedIn, WrittenAt, Client, Supersedes FROM @Pair ORDER BY Position DESC;

        DECLARE @NewestId uniqueidentifier = (SELECT EntryId FROM @Newest);
        DECLARE @Position int = ISNULL((SELECT MAX(Position) FROM @Pair), 0) + 1;

        -- Only while the entry read is still the newest, which also means it still holds the caller's subject.
        IF (@ReadVersion IS NULL AND @NewestId IS NULL) OR @NewestId = @ReadVersion
        BEGIN
            INSERT notifications.ConsentEntries
                (EntryId, SubjectId, Topic, Channel, OptedIn, WrittenAt, Client, SupersedesEntryId, Position,
                ErasedUnder)
            VALUES (@EntryId, @SubjectId, @Topic, @Channel, @OptedIn, @Now, @Client, @ReadVersion, @Position,
                @ErasedUnder);

            SELECT
                Outcome    = 'Appended',
                EntryId    = @EntryId,
                OptedIn    = @OptedIn,
                WrittenAt  = @Now,
                Client     = @Client,
                Supersedes = @ReadVersion;
            RETURN;
        END;

        -- A repeat holds the value asked for and supersedes what the request read; anything else lost the race.
        SELECT
            Outcome = CASE
                WHEN n.OptedIn = @OptedIn
                    AND (n.Supersedes = @ReadVersion OR (n.Supersedes IS NULL AND @ReadVersion IS NULL))
                    THEN 'Repeated'
                ELSE 'PreconditionFailed'
            END,
            n.EntryId, n.OptedIn, n.WrittenAt, n.Client, n.Supersedes
        FROM (SELECT One = 1) AS one
        LEFT JOIN @Newest AS n
            ON 1 = 1;
        """;

    public async Task<IReadOnlyList<ConsentEntry>> NewestAsync(Guid subjectId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return
        [
            .. await connection.QueryAsync<ConsentEntry>(
                new CommandDefinition(NewestSql, new { SubjectId = subjectId }, cancellationToken: ct))
        ];
    }

    public async Task<ConsentEntry?> NewestAsync(Guid subjectId, ConsentPair pair, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.QuerySingleOrDefaultAsync<ConsentEntry>(
            new CommandDefinition(
                NewestOfPairSql,
                new
                {
                    SubjectId = subjectId,
                    Topic = Ansi(pair.Topic, ConsentVocabulary.MaxTopicLength),
                    Channel = Ansi(pair.Channel, ConsentVocabulary.MaxChannelLength)
                },
                cancellationToken: ct));
    }

    public async Task<ConsentAppend> AppendAsync(
        SetPreferenceCommand command,
        IReadOnlyList<Guid> erasedUnder,
        DateTimeOffset now,
        CancellationToken ct)
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // As in SqlDeviceStore: with no transaction, the batch would commit outside the unit.
        if (current is null)
        {
            throw new InvalidOperationException(
                "A journal entry is appended only inside the unit of work's transaction (§6.3).");
        }

        DbConnection connection = db.Database.GetDbConnection();

        AppendRow row = await connection.QuerySingleAsync<AppendRow>(
            new CommandDefinition(
                AppendSql,
                new
                {
                    EntryId = Guid.CreateVersion7(),
                    command.SubjectId,
                    Topic = Ansi(command.Topic, ConsentVocabulary.MaxTopicLength),
                    Channel = Ansi(command.Channel, ConsentVocabulary.MaxChannelLength),
                    command.OptedIn,
                    command.ReadVersion,
                    Client = Ansi(command.Client, ConsentVocabulary.MaxClientLength),
                    Now = now,
                    ErasedUnder = JsonSerializer.Serialize(erasedUnder)
                },
                current.GetDbTransaction(),
                cancellationToken: ct));

        ConsentEntry? newest = row.EntryId is { } id
            ? new ConsentEntry(id, command.Topic, command.Channel, row.OptedIn!.Value, row.WrittenAt!.Value,
                row.Client!, row.Supersedes)
            : null;

        return new ConsentAppend(Enum.Parse<ConsentAppendOutcome>(row.Outcome), newest);
    }

    // varchar at the column's width: an nvarchar parameter converts the column, and the index's seek with it.
    private static DbString Ansi(string value, int length) => new() { Value = value, IsAnsi = true, Length = length };

    /// <summary>The batch's one row; every entry column is null when the pair has no entry.</summary>
    private sealed class AppendRow
    {
        public string Outcome { get; init; } = "";
        public Guid? EntryId { get; init; }
        public bool? OptedIn { get; init; }
        public DateTimeOffset? WrittenAt { get; init; }
        public string? Client { get; init; }
        public Guid? Supersedes { get; init; }
    }
}
