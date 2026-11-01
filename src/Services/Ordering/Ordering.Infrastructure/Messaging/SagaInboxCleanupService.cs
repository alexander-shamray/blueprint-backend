using System.Data;
using System.Data.Common;
using Common.Application;
using Common.Infrastructure.Switching;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// Removes MassTransit's inbox rows once their duplicate-detection window has passed, seeking each bucket of the
/// cleanup index in turn, in place of the library's cleanup, which orders by Delivered alone (ADR-182); one replica
/// at a time, giving a bucket up rather than wait past <see cref="LockTimeout"/> on the consumers' locks (ADR-189).
/// </summary>
public sealed class SagaInboxCleanupService(
    IServiceScopeFactory scopes,
    ILogger<SagaInboxCleanupService> log,
    OperatorSwitches? switches = null)
    : SwitchedPollingWorker(switches)
{
    /// <summary>The name ADR-113's switches would read; ADR-104 gives Ordering none, so here nothing does.</summary>
    public const string Name = "saga-inbox-cleanup";

    /// <summary>The library's default window, so a duplicate is still caught for as long as it was (ADR-032).</summary>
    public static readonly TimeSpan DuplicateDetectionWindow = TimeSpan.FromMinutes(30);

    // The library's batch and delay: a bucket's statement holds its locks for a hundred rows at most.
    private const int BatchSize = 100;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>The session lock a pass holds, so one replica cleans while the others skip it (ADR-189).</summary>
    internal const string PassLock = "ordering.saga-inbox-cleanup";

    /// <summary>
    /// How long a bucket's statement waits on any one lock before it gives the bucket up until the next pass: a
    /// delete's check that no outbox row still names the inbox row reads under locks, and waits on a consumer's
    /// rows in flight (ADR-189).
    /// </summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(100);

    // SQL Server's "Lock request time out period exceeded", which ends the statement and nothing else.
    internal const int LockTimeoutNumber = 1222;

    // Owned by the session, so it holds across the pass's statements. The pool resets a session's settings, the
    // lock timeout among them, only when it next hands it out, so the lock is released before the connection goes
    // back rather than left held by an idle session.
    private static readonly string BeginPassSql =
        $"""
        DECLARE @taken int;
        EXEC @taken = sp_getapplock
            @Resource = '{PassLock}', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
        IF @taken >= 0 SET LOCK_TIMEOUT {(int)LockTimeout.TotalMilliseconds};
        SELECT Taken = CONVERT(bit, CASE WHEN @taken >= 0 THEN 1 ELSE 0 END);
        """;

    private const string EndPassSql =
        $"EXEC sp_releaseapplock @Resource = '{PassLock}', @LockOwner = 'Session';";

    // Compiled once, for CA1848 (ADR-019).
    private static readonly Action<ILogger, int, Exception?> Removed =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(1, nameof(Removed)),
            "Saga inbox cleanup removed {Rows} row(s).");

    private static readonly Action<ILogger, Exception?> CleanupFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(CleanupFailed)),
            "Saga inbox cleanup failed; retrying next pass.");

    /// <summary>
    /// One bucket's expired rows. Delivered is set only once every outbox row has reached the broker, and a null
    /// fails the comparison, so an undelivered row is never removed (§9.5); the range ends at the window, so a
    /// delivery under way, which stamps the present, is never in it.
    /// </summary>
    internal static readonly string CleanupSql =
        $"""
        DELETE TOP ({BatchSize}) FROM [ordering].[InboxState]
        WHERE {OrderingDbContext.InboxStateCleanupBucket} = @Bucket
            AND Delivered < @Before;
        """;

    private bool _filled;

    public override string SwitchName => Name;

    protected override TimeSpan PassInterval => Interval;

    // As the library's loop: a full batch means more are due, so the next pass starts at once.
    protected override bool PassFilled => _filled;

    protected override async Task RunPassAsync(CancellationToken stoppingToken) =>
        Removed(log, await CleanUpAsync(stoppingToken), null);

    protected override void OnPassFailed(Exception exception) => CleanupFailed(log, exception);

    /// <summary>
    /// One statement a bucket on the replica holding <see cref="PassLock"/>, another's pass removing nothing, public
    /// so tests drive a pass rather than race a timer.
    /// </summary>
    public async Task<int> CleanUpAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        // The base type, for the asynchronous open IDbConnection lacks; open for the pass, so the session lock holds.
        await using DbConnection connection =
            (DbConnection)scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();
        await connection.OpenAsync(ct);

        _filled = false;

        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(BeginPassSql, cancellationToken: ct)))
            return 0;

        try
        {
            return await DeleteExpiredAsync(connection, scope.ServiceProvider, ct);
        }
        finally
        {
            // Not the pass's token: a shutdown still releases the lock before the connection goes back.
            await connection.ExecuteAsync(new CommandDefinition(EndPassSql, cancellationToken: CancellationToken.None));
        }
    }

    private async Task<int> DeleteExpiredAsync(DbConnection connection, IServiceProvider services, CancellationToken ct)
    {
        // The library stamps Delivered from the host's UTC clock, so the cutoff is read from the same clock.
        DateTime before = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime
            - DuplicateDetectionWindow;

        int total = 0;
        bool filled = false;

        for (int bucket = 0; bucket < OrderingDbContext.InboxStateCleanupBuckets; bucket++)
        {
            DynamicParameters parameters = new();
            parameters.Add("Bucket", (byte)bucket, DbType.Byte);
            parameters.Add("Before", before, DbType.DateTime2);

            int deleted;

            try
            {
                // CommandDefinition, so a shutdown's token reaches a blocked delete.
                deleted = await connection.ExecuteAsync(
                    new CommandDefinition(CleanupSql, parameters, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number == LockTimeoutNumber)
            {
                // The statement alone rolled back; the bucket waits for the next pass, which this one does not
                // hasten, so a cleanup that meets consumers backs off rather than queueing behind them.
                continue;
            }

            total += deleted;
            filled |= deleted == BatchSize;
        }

        _filled = filled;

        return total;
    }
}
