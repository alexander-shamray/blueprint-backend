using System.Data;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Messaging;

/// <summary>§9.4's, §9.5's and §8.5's retention purges, in one hosted service (§9.5).</summary>
/// <remarks>A marker goes once <see cref="IIdempotencyStore"/> lets its claim go (ADR-039, ADR-041).</remarks>
public sealed class RetentionPurgeService : BackgroundService
{
    // Compiled once, for CA1848 (ADR-019); the arguments bind to the placeholders by position.
    private static readonly Action<ILogger, int, string, Exception?> Purged =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1, nameof(Purged)),
            "Retention purge deleted {Rows} row(s) from {Table}.");

    private static readonly Action<ILogger, Exception?> PurgeFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(PurgeFailed)),
            "Retention purge failed; retrying next pass.");

    // Two parameters a row against SQL Server's limit of 2,100, with room to spare.
    private const int RowsPerDelete = 900;

    private readonly IServiceScopeFactory _scopes;
    private readonly RetentionPolicy _policy;
    private readonly ILogger<RetentionPurgeService> _log;

    // Null when no OutboxTable is registered, as for §4.1's pure consumer (§9.5).
    private readonly string? _outboxSql;
    private readonly string _inboxSql;

    // Null when neither marker half is registered, as for a host with no command pipeline (§9.5).
    private readonly MarkerHalf? _markers;

    public RetentionPurgeService(
        IServiceScopeFactory scopes,
        InboxTable inbox,
        RetentionPolicy policy,
        ILogger<RetentionPurgeService> log,
        OutboxTable? outbox = null,
        IdempotencyMarkerTable? markers = null,
        IIdempotencyStore? claims = null)
    {
        // A marker table with no store to ask could only be purged by guessing whether its claim is gone (ADR-039).
        if ((markers is null) != (claims is null))
        {
            string given = markers is null ? nameof(IIdempotencyStore) : nameof(IdempotencyMarkerTable);
            string missing = markers is null ? nameof(IdempotencyMarkerTable) : nameof(IIdempotencyStore);

            throw new InvalidOperationException(
                $"{nameof(RetentionPurgeService)} was given an {given} and no {missing}. The marker purge needs " +
                "both halves (§9.5, ADR-039): register both for a host that runs commands, or neither for one that " +
                "runs none.");
        }

        _scopes = scopes;
        _policy = policy;
        _log = log;

        // ProcessedAt IS NOT NULL keeps the abandoned rows §13.6's alert surfaces.
        _outboxSql = outbox is null
            ? null
            : $"""
              DELETE TOP (@BatchSize) FROM {outbox.QualifiedName}
              WHERE ProcessedAt IS NOT NULL
                  AND ProcessedAt < @Before;
              """;

        // Age alone: an inbox row has no unfinished state, and the window outlasts redelivery (§9.5).
        _inboxSql =
            $"""
            DELETE TOP (@BatchSize) FROM {inbox.QualifiedName}
            WHERE HandledAt < @Before;
            """;

        // Candidates only: the store decides (ADR-039), and the cutoff is on the database's clock (ADR-038).
        // Oldest first, so the rows likeliest still claimed sit at the tail where a pass stops.
        _markers = markers is null
            ? null
            : new MarkerHalf(
                $"""
                SELECT TOP (@BatchSize) [Key], {IdempotencyMarker.RowVersionColumn}
                FROM {markers.QualifiedName}
                WHERE CommittedAt < DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())
                ORDER BY CommittedAt;
                """,
                markers.QualifiedName,
                claims!);
    }

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_policy.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Swallowed, since an exception out of ExecuteAsync stops the host; a live token means a failure.
                PurgeFailed(_log, ex);
            }
        }
    }

    /// <summary>One purge pass over every table, public so tests drive it rather than race a timer (§9.5).</summary>
    public async Task<(int Outbox, int Inbox, int Idempotency)> PurgeAsync(CancellationToken ct)
    {
        // The query-side port, not IUnitOfWork: the purge shares no command's transaction.
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The registered clock, which a test host substitutes (§9.5).
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        int outbox = 0;
        if (_outboxSql is not null)
        {
            outbox = await DeleteAsync(
                connection,
                _outboxSql,
                new { _policy.BatchSize, Before = now - _policy.OutboxWindow },
                ct);
            Purged(_log, outbox, "outbox", null);
        }

        int inbox = await DeleteAsync(
            connection,
            _inboxSql,
            new { _policy.BatchSize, Before = now - _policy.InboxWindow },
            ct);
        Purged(_log, inbox, "inbox", null);

        // No `now`: the markers' cutoff is the database's clock (ADR-038).
        int idempotency = 0;
        if (_markers is not null)
        {
            idempotency = await PurgeMarkersAsync(_markers, connection, ct);
            Purged(_log, idempotency, "idempotency", null);
        }

        return (outbox, inbox, idempotency);
    }

    /// <summary>§8.5's markers past their window whose claim the store has already let go (ADR-039).</summary>
    private async Task<int> PurgeMarkersAsync(MarkerHalf markers, IDbConnection connection, CancellationToken ct)
    {
        // A duration, not a cutoff (ADR-038), rounded up so no marker is selected before its window.
        int windowSeconds = (int)Math.Ceiling(_policy.IdempotencyWindow.TotalSeconds);

        int total = 0;

        for (int batch = 0; batch < _policy.MaxBatchesPerPass; batch++)
        {
            MarkerCandidate[] candidates = [.. await connection.QueryAsync<MarkerCandidate>(
                new CommandDefinition(
                    markers.CandidateSql,
                    new { _policy.BatchSize, WindowSeconds = windowSeconds },
                    cancellationToken: ct))];

            if (candidates.Length == 0)
                break;

            string[] keys = [.. candidates.Select(candidate => candidate.Key)];

            // Not caught: a failed lookup leaves every marker for the next pass rather than deleting one.
            IReadOnlyCollection<string> unheld = await markers.Claims.UnheldAsync(keys, ct);

            HashSet<string> gone = [.. unheld];

            int deleted = await DeleteRowsAsync(
                markers.Table,
                connection,
                [.. candidates.Where(candidate => gone.Contains(candidate.Key))],
                ct);
            total += deleted;

            // A short SELECT means none are left; an empty `gone` means the next SELECT returns the same rows.
            // Not `deleted == 0`: another replica may have deleted them first, which is progress.
            if (candidates.Length < _policy.BatchSize || gone.Count == 0)
                break;
        }

        return total;
    }

    /// <summary>Deletes the given (key, version) rows in chunks; fewer means another replica was first.</summary>
    private static async Task<int> DeleteRowsAsync(
        string table,
        IDbConnection connection,
        IReadOnlyCollection<MarkerCandidate> rows,
        CancellationToken ct)
    {
        int deleted = 0;

        foreach (MarkerCandidate[] chunk in rows.Chunk(RowsPerDelete))
        {
            DynamicParameters parameters = new();

            for (int index = 0; index < chunk.Length; index++)
            {
                parameters.Add($"k{index}", chunk[index].Key, DbType.String);

                // Sized: an unsized binary parameter goes over as varbinary(max) against a binary(8).
                parameters.Add($"v{index}", chunk[index].RowVersion, DbType.Binary, size: 8);
            }

            deleted += await connection.ExecuteAsync(
                new CommandDefinition(DeleteSql(table, chunk.Length), parameters, cancellationToken: ct));
        }

        return deleted;
    }

    /// <summary>The delete for a chunk of <paramref name="rows"/> rows; every value travels as a parameter.</summary>
    private static string DeleteSql(string table, int rows)
    {
        string pairs = string.Join(
            ", ",
            Enumerable.Range(0, rows).Select(index => $"(@k{index}, @v{index})"));

        return $"""
            DELETE marker
            FROM {table} marker
            INNER JOIN (VALUES {pairs}) AS selected([Key], {IdempotencyMarker.RowVersionColumn})
                ON marker.[Key] = selected.[Key]
                AND marker.{IdempotencyMarker.RowVersionColumn} = selected.{IdempotencyMarker.RowVersionColumn};
            """;
    }

    /// <summary>Deletes in batches until one comes back short or the pass's ceiling is reached.</summary>
    private async Task<int> DeleteAsync(
        IDbConnection connection,
        string sql,
        object parameters,
        CancellationToken ct)
    {
        int total = 0;

        for (int batch = 0; batch < _policy.MaxBatchesPerPass; batch++)
        {
            // CommandDefinition, so a shutdown's token reaches a blocked delete.
            int deleted = await connection.ExecuteAsync(
                new CommandDefinition(sql, parameters, cancellationToken: ct));

            total += deleted;

            if (deleted < _policy.BatchSize)
                break;
        }

        return total;
    }

    /// <summary>A key and the <c>rowversion</c> that tells a marker from its replacement (ADR-041).</summary>
    private sealed record MarkerCandidate(string Key, byte[] RowVersion);

    /// <summary>The marker purge's two statements' table and the store that decides between them (ADR-039).</summary>
    private sealed record MarkerHalf(string CandidateSql, string Table, IIdempotencyStore Claims);
}
