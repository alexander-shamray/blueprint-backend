using System.Data;
using Common.Application;
using Common.Infrastructure.Switching;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// Removes MassTransit's inbox rows once their duplicate-detection window has passed, seeking each bucket of the
/// cleanup index in turn, in place of the library's cleanup, which orders by Delivered alone (ADR-182).
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

    /// <summary>One statement a bucket, public so tests drive a pass rather than race a timer.</summary>
    public async Task<int> CleanUpAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The library stamps Delivered from the host's UTC clock, so the cutoff is read from the same clock.
        DateTime before = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime
            - DuplicateDetectionWindow;

        int total = 0;
        bool filled = false;

        for (int bucket = 0; bucket < OrderingDbContext.InboxStateCleanupBuckets; bucket++)
        {
            DynamicParameters parameters = new();
            parameters.Add("Bucket", (byte)bucket, DbType.Byte);
            parameters.Add("Before", before, DbType.DateTime2);

            // CommandDefinition, so a shutdown's token reaches a blocked delete.
            int deleted = await connection.ExecuteAsync(
                new CommandDefinition(CleanupSql, parameters, cancellationToken: ct));

            total += deleted;
            filled |= deleted == BatchSize;
        }

        _filled = filled;

        return total;
    }
}
