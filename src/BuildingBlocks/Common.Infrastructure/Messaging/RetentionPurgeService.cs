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

/// <summary>
/// §9.4's, §9.5's and §8.5's retention purges, in one hosted service. An outbox
/// nobody prunes grows without bound and eventually degrades the filtered index
/// the dispatcher's claim depends on; an inbox nobody prunes grows for the life
/// of the service and its composite-key index degrades with it; and idempotency
/// markers accumulate one row per protected command, for ever.
/// </summary>
/// <remarks>
/// One service covering every table, which is §9.5's shape: the alternative is
/// a hosted service per table and one of them being the one nobody notices has
/// stopped, which is §9.3's argument against a second outbox mechanism.
/// <para>
/// The third table is not like the other two. A purged outbox row loses a
/// debugging record and a purged inbox row a duplicate suppression the broker
/// will not exercise again; a purged idempotency marker loses a correctness
/// property, because it is what refuses a retry of a command that already
/// committed. That is why <see cref="RetentionPolicy.IdempotencyWindow"/> has
/// a floor and why its pass is the only one that asks something before
/// deleting.
/// </para>
/// <para>
/// Age is necessary there and not sufficient: the marker's pass compares a
/// column against a cutoff to find candidates, asks
/// <see cref="Common.Application.IIdempotencyStore"/> which of them it has let
/// go of (ADR-039), and then deletes each row by identity — its key and the
/// <c>rowversion</c> the select returned — because a key names a command
/// rather than a row, and a retry can commit a fresh marker under one this
/// pass already chose (ADR-041).
/// </para>
/// </remarks>
public sealed class RetentionPurgeService : BackgroundService
{
    // Compiled once rather than parsed per call — CA1848 (ADR-019), the shape
    // §9.4's dispatcher takes. The generic arguments bind to the placeholders
    // by position, not by the delegate's parameter names, and a transposition
    // fails nothing: the structured fields are simply swapped.
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

    // Rows per DELETE, and the number is SQL Server's: each row costs two
    // parameters — key and version — and the server refuses a statement
    // carrying more than 2,100, so a pass at the default BatchSize of 5,000
    // would fail on the batch. Chunking here keeps BatchSize meaning rows
    // considered per batch rather than capping it at another layer's limit.
    // 900 rather than 1,000 leaves room under the ceiling. Private because it
    // is not a knob, and a test stages a batch wider than one chunk to reach
    // the second; raise this past that batch and the test covers nothing, so
    // move both in the same change.
    private const int RowsPerDelete = 900;

    private readonly IServiceScopeFactory _scopes;
    private readonly IIdempotencyStore _claims;
    private readonly RetentionPolicy _policy;
    private readonly ILogger<RetentionPurgeService> _log;

    // Composed once from the registered tables, exactly as the dispatcher
    // composes its three. Instance fields rather than consts for that reason
    // and no other.
    private readonly string _outboxSql;
    private readonly string _inboxSql;
    private readonly string _idempotencyCandidateSql;

    // Not a statement: the delete's is composed per chunk, because its VALUES
    // list is as long as the chunk. This is the qualified table name it needs.
    private readonly string _markerTable;

    public RetentionPurgeService(
        IServiceScopeFactory scopes,
        OutboxTable outbox,
        InboxTable inbox,
        IdempotencyMarkerTable markers,
        IIdempotencyStore claims,
        RetentionPolicy policy,
        ILogger<RetentionPurgeService> log)
    {
        _scopes = scopes;
        _claims = claims;
        _policy = policy;
        _log = log;

        // ProcessedAt IS NOT NULL is load-bearing, not defensive: purging on
        // age alone would delete the abandoned rows — Attempts at the cap,
        // never processed — that §13.6's alert exists to surface, so an
        // abandoned row survives a purge that removes a processed one of the
        // same age. The window is a parameter and only the table name is
        // interpolated, which is what OutboxTable's shape check is for.
        _outboxSql =
            $"""
            DELETE TOP (@BatchSize) FROM {outbox.QualifiedName}
            WHERE ProcessedAt IS NOT NULL
                AND ProcessedAt < @Before;
            """;

        // Age alone here, and the asymmetry is the point: an inbox row records
        // that a message was handled, so there is no unfinished state for a
        // predicate to protect. What protects it instead is the window itself,
        // which must outlast the broker's longest redelivery (§9.5).
        _inboxSql =
            $"""
            DELETE TOP (@BatchSize) FROM {inbox.QualifiedName}
            WHERE HandledAt < @Before;
            """;

        // The marker is two statements where the other two are one. Age is
        // necessary on both and sufficient on neither: what decides is
        // IIdempotencyStore.UnheldAsync agreeing that the claim behind the row
        // is gone (ADR-039), because a window compared against a window puts
        // Redis's clock on one side and SQL Server's on the other with nothing
        // coupling their rates.
        //
        // The cutoff is computed here rather than by the caller (ADR-038):
        // CommittedAt is written by a SYSDATETIMEOFFSET() default, so the row's
        // age is one clock's arithmetic whichever of §15.3's replicas wrote it.
        // The outbox and the inbox keep the parameterised form because their
        // windows are housekeeping, where a substitutable TimeProvider is worth
        // more than a clock nothing can move (§9.5).
        //
        // Oldest first, so a batch that cannot be fully deleted leaves the rows
        // likeliest to still hold a claim — the newest — at the tail where the
        // pass stops rather than at the head where it would block.
        _idempotencyCandidateSql =
            $"""
            SELECT TOP (@BatchSize) [Key], {IdempotencyMarker.RowVersionColumn}
            FROM {markers.QualifiedName}
            WHERE CommittedAt < DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())
            ORDER BY CommittedAt;
            """;

        // Delimited, because Key is a reserved word in T-SQL and the column is
        // named for what it holds rather than around the parser.
        //
        // The version bound is what makes the delete safe. A key names a
        // command, not a row: past the guarantee the key is claimable again,
        // so a retry can commit a fresh marker under it between this pass's
        // SELECT and its DELETE, and §15.3's replicas mean a second purger's
        // delete can arrive after the replacement exists. A key-only delete
        // removes the replacement with a live claim behind it, and re-reading
        // the age cutoff does not help, because SYSDATETIMEOFFSET() moves; an
        // arbitrary clock cannot be out-predicated (ADR-041).
        //
        // (Key, RowVersion) is the row's identity by constraint: a timestamp
        // carries no uniqueness, and a rowversion reads no clock — SQL Server's
        // counter is unique and monotonic, and a row nothing updates carries
        // the value it was inserted with for life. The column is a shadow
        // property on IdempotencyMarker, declared by each service's own
        // configuration and named from the entity so this statement and that
        // mapping cannot drift.
        //
        // Composed per chunk because the VALUES list is as long as the chunk.
        // The only interpolation is the table name, whose shape
        // IdempotencyMarkerTable checks, and the row count — never a value.
        _markerTable = markers.QualifiedName;
    }

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name (ADR-019 makes it an error), and a reader consulting
    // BackgroundService's documentation is reading about that one.
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
                // Logged and swallowed, because an exception out of
                // ExecuteAsync stops the host: a database blip during
                // housekeeping must not take the service down. The token
                // rather than the type, for §9.4's reason — a cancellation
                // raised while the token is still live is a failure, not a
                // shutdown.
                PurgeFailed(_log, ex);
            }
        }
    }

    /// <summary>
    /// One purge pass over every table. Returns the rows deleted from each.
    /// Public so tests drive it directly instead of racing a timer — the same
    /// seam <c>OutboxDispatcher.ProcessBatchAsync</c> offers, for the same
    /// reason (§12.4).
    /// </summary>
    public async Task<(int Outbox, int Inbox, int Idempotency)> PurgeAsync(CancellationToken ct)
    {
        // One scope and one connection for the pass, disposed at the end of it.
        // The purge is housekeeping on this service's own database and shares
        // nothing with a command's transaction, which is why it goes through
        // the query-side port rather than through IUnitOfWork (§6.5, §9.6).
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The registered clock rather than DateTimeOffset.UtcNow, for §9.5's
        // reason: a test host substitutes it, and a row written on one clock
        // and aged on another is one no substituted clock can reason about.
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        int outbox = await DeleteAsync(
            connection,
            _outboxSql,
            new { _policy.BatchSize, Before = now - _policy.OutboxWindow },
            ct);
        Purged(_log, outbox, "outbox", null);

        int inbox = await DeleteAsync(
            connection,
            _inboxSql,
            new { _policy.BatchSize, Before = now - _policy.InboxWindow },
            ct);
        Purged(_log, inbox, "inbox", null);

        // The third takes no `now` at all — neither clock is compared against
        // the claim. Its own method, because selecting, asking and deleting is
        // three steps where the two above are one (ADR-039).
        int idempotency = await PurgeMarkersAsync(connection, ct);
        Purged(_log, idempotency, "idempotency", null);

        return (outbox, inbox, idempotency);
    }

    /// <summary>
    /// §8.5's markers: the rows past their window whose claim the store has
    /// already let go. Selects, asks, deletes — and deletes nothing the store
    /// still holds a claim for.
    /// </summary>
    /// <remarks>
    /// The store is asked rather than out-counted, which is the whole of
    /// ADR-039: a window compared against a window puts Redis's clock on one
    /// side and SQL Server's on the other with nothing coupling their rates.
    /// </remarks>
    private async Task<int> PurgeMarkersAsync(IDbConnection connection, CancellationToken ct)
    {
        // The window as a duration and not a cutoff, because the statement
        // computes the cutoff from the server's own clock (ADR-038). An int
        // because RetentionPolicy caps a window at ten years, which fits
        // DATEADD's int argument.
        //
        // Rounded up, where a cast would round down: the window has sub-second
        // resolution, and truncating selects the marker fractionally before the
        // window asked for, the one direction this setting may not be wrong
        // in. Keeping the row slightly longer costs nothing, because the floor
        // is a lower bound.
        int windowSeconds = (int)Math.Ceiling(_policy.IdempotencyWindow.TotalSeconds);

        int total = 0;

        for (int batch = 0; batch < _policy.MaxBatchesPerPass; batch++)
        {
            MarkerCandidate[] candidates = [.. await connection.QueryAsync<MarkerCandidate>(
                new CommandDefinition(
                    _idempotencyCandidateSql,
                    new { _policy.BatchSize, WindowSeconds = windowSeconds },
                    cancellationToken: ct))];

            if (candidates.Length == 0)
                break;

            // Keys for the store, which answers about commands; the rows
            // themselves go to the delete, which acts on writes.
            string[] keys = [.. candidates.Select(candidate => candidate.Key)];

            // Not caught, and the caller's `catch` is why that is safe: an
            // unreachable store leaves every marker in place and the pass is
            // logged and retried next interval. Treating a failed lookup as
            // "no claim" would delete the row that refuses a duplicate, which
            // is the failure this call exists to remove.
            IReadOnlyCollection<string> unheld = await _claims.UnheldAsync(keys, ct);

            // Back to the rows, because the delete names a version and not just
            // a key. A HashSet rather than a scan per candidate: the batch is
            // five thousand by default.
            HashSet<string> gone = [.. unheld];

            int deleted = await DeleteRowsAsync(
                connection,
                [.. candidates.Where(candidate => gone.Contains(candidate.Key))],
                ct);
            total += deleted;

            // Two ways to stop, and the second asks the store rather than the
            // database. A short SELECT means the table holds no further
            // candidates. A batch the store released nothing from means every
            // row it returned is still claimed, and the next SELECT, ordered
            // oldest first, would return those same rows.
            //
            // Not `deleted < candidates.Length`: a partially deleted batch is
            // not returned unchanged, because TOP refills the deleted slots
            // with the next-oldest candidates, so one held key at the head
            // would end a pass after one batch. Not `deleted == 0` either:
            // §15.3 ships three replicas, so another purger can delete every
            // row this one selected before its own DELETE runs, and that zero
            // is concurrent progress rather than a batch nobody may touch.
            // `gone` is what the claim store released, so an empty one is the
            // only state where continuing is certain to be futile.
            //
            // What is left is bounded: at BatchSize 1, a held oldest key stops
            // every pass until its claim expires, and nothing starves for
            // longer than a claim lives.
            if (candidates.Length < _policy.BatchSize || gone.Count == 0)
                break;
        }

        return total;
    }

    /// <summary>
    /// Deletes the rows matching the given (key, version) pairs, chunked to
    /// stay inside SQL Server's parameter limit. Returns the rows actually
    /// removed.
    /// </summary>
    /// <remarks>
    /// Identity rather than a predicate, because an arbitrary clock cannot be
    /// out-predicated: a key names a command and not a row, so a retry can
    /// commit a fresh marker under a key this pass has already selected, and
    /// the version is SQL Server's own counter, so a replacement never carries
    /// its predecessor's value (ADR-041). A count lower than the rows handed in
    /// means another replica got there first, which is ordinary.
    /// </remarks>
    private async Task<int> DeleteRowsAsync(
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

                // Sized, because an unsized binary parameter goes over as
                // varbinary(max) and the VALUES list below would then be a
                // derived table of max-length columns compared against a
                // binary(8). A rowversion is eight bytes, always.
                parameters.Add($"v{index}", chunk[index].RowVersion, DbType.Binary, size: 8);
            }

            deleted += await connection.ExecuteAsync(
                new CommandDefinition(DeleteSql(chunk.Length), parameters, cancellationToken: ct));
        }

        return deleted;
    }

    /// <summary>
    /// The delete for a chunk of <paramref name="rows"/> rows, joining the
    /// table to the (key, version) pairs the pass selected.
    /// </summary>
    /// <remarks>
    /// The row count is the only thing that varies, and it is an int. Every
    /// value travels as a parameter; the table name is
    /// <c>IdempotencyMarkerTable</c>'s, shape-checked where it is composed.
    /// </remarks>
    private string DeleteSql(int rows)
    {
        string pairs = string.Join(
            ", ",
            Enumerable.Range(0, rows).Select(index => $"(@k{index}, @v{index})"));

        return $"""
            DELETE marker
            FROM {_markerTable} marker
            INNER JOIN (VALUES {pairs}) AS selected([Key], {IdempotencyMarker.RowVersionColumn})
                ON marker.[Key] = selected.[Key]
                AND marker.{IdempotencyMarker.RowVersionColumn} = selected.{IdempotencyMarker.RowVersionColumn};
            """;
    }

    /// <summary>
    /// Deletes in batches until one comes back short or the pass's ceiling is
    /// reached. A short batch means the table is drained; the ceiling means the
    /// backlog is larger than one pass should hold a lock for, and the next
    /// pass continues it.
    /// </summary>
    private async Task<int> DeleteAsync(
        IDbConnection connection,
        string sql,
        object parameters,
        CancellationToken ct)
    {
        int total = 0;

        for (int batch = 0; batch < _policy.MaxBatchesPerPass; batch++)
        {
            // CommandDefinition, so the token reaches the command: with the
            // plain overload a shutdown cannot interrupt a blocked delete and
            // the host waits out the SQL timeout (§9.4).
            int deleted = await connection.ExecuteAsync(
                new CommandDefinition(sql, parameters, cancellationToken: ct));

            total += deleted;

            if (deleted < _policy.BatchSize)
                break;
        }

        return total;
    }

    /// <summary>
    /// One row of the marker pass's selection: the key to delete by, and the
    /// <c>rowversion</c> that identifies the row rather than the command.
    /// </summary>
    /// <remarks>
    /// A record rather than the bare key, because the key alone cannot tell a
    /// marker from its own replacement — which is the whole of the delete's
    /// version bound. <c>CommittedAt</c> is not carried: it selects the
    /// candidates and decides nothing about which row is which.
    /// </remarks>
    private sealed record MarkerCandidate(string Key, byte[] RowVersion);
}
