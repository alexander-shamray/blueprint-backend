using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.Jurisdiction;

namespace Notifications.Infrastructure.Retention;

/// <summary>ADR-053's three windows, applied: the record of a send, the order record and the contact.</summary>
/// <remarks>
/// Apart from <c>RetentionPurgeService</c>, whose windows are housekeeping (ADR-053). An order record goes only once
/// no pending notice names its order, since that notice waits on it.
/// </remarks>
public sealed class NotificationsRetentionService : BackgroundService
{
    /// <summary>Slow on purpose: a window is measured in days, and a pass competes with the claims.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Under SQL Server's 2,100-parameter ceiling, as the delete carries one parameter per key.</summary>
    public const int BatchSize = 500;

    /// <summary>Batches per table per pass, so a backlog drains and a pass still ends.</summary>
    public const int MaxBatchesPerPass = 20;

    private const string EndedNotices =
        """
        SELECT TOP (@BatchSize) NotificationId
        FROM notifications.NotificationLog
        WHERE CompletedAt IS NOT NULL AND CompletedAt < @Before AND Status <> 'Pending'
        ORDER BY CompletedAt;
        """;

    private const string DeleteNotices =
        """
        DELETE FROM notifications.NotificationLog
        WHERE NotificationId IN @Keys AND CompletedAt < @Before AND Status <> 'Pending';
        """;

    private const string ExpiredOrders =
        """
        SELECT TOP (@BatchSize) r.OrderId
        FROM notifications.OrderRecords r
        WHERE r.RecordedAt < @Before
            AND NOT EXISTS (
                SELECT 1 FROM notifications.NotificationLog n WHERE n.OrderId = r.OrderId AND n.Status = 'Pending')
        ORDER BY r.RecordedAt;
        """;

    // Asked again, so a notice a consumer wrote since the select keeps the record it will wait on.
    private const string DeleteOrders =
        """
        DELETE r FROM notifications.OrderRecords r
        WHERE r.OrderId IN @Keys
            AND r.RecordedAt < @Before
            AND NOT EXISTS (
                SELECT 1 FROM notifications.NotificationLog n WHERE n.OrderId = r.OrderId AND n.Status = 'Pending');
        """;

    private const string ExpiredContacts =
        """
        SELECT TOP (@BatchSize) CustomerId
        FROM notifications.ContactRecords
        WHERE FetchedAt < @Before
        ORDER BY FetchedAt;
        """;

    // Asked again, so a row a pass refreshed since the select is kept.
    private const string DeleteContacts =
        "DELETE FROM notifications.ContactRecords WHERE CustomerId IN @Keys AND FetchedAt < @Before;";

    private static readonly Action<ILogger, int, string, Exception?> Purged =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1, nameof(Purged)),
            "Notifications retention deleted {Rows} row(s) from {Table}.");

    private static readonly Action<ILogger, Exception?> PurgeFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(PurgeFailed)),
            "Notifications retention failed; retrying next pass.");

    private readonly IServiceScopeFactory _scopes;
    private readonly NotificationsJurisdictionOptions _windows;
    private readonly ILogger<NotificationsRetentionService> _log;

    public NotificationsRetentionService(
        IServiceScopeFactory scopes,
        IOptions<NotificationsJurisdictionOptions> windows,
        ILogger<NotificationsRetentionService> log)
    {
        _scopes = scopes;
        _windows = windows.Value;
        _log = log;
    }

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        // A pass at start, or a deployment restarting more often than Interval would never apply ADR-053's windows.
        do
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Swallowed, since an exception out of ExecuteAsync stops the host; the token, for §9.4's reason.
                PurgeFailed(_log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over the three tables, public so tests drive it rather than race a timer (§12.4).</summary>
    public async Task<(int Notifications, int Contacts, int Orders)> PurgeAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The registered clock rather than DateTimeOffset.UtcNow, for §9.5's reason: a test host substitutes it.
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        // Validated at start (ADR-053), so each window is present here.
        int notifications = await PurgeWindowAsync(
            connection, EndedNotices, DeleteNotices, now - _windows.LogRetention!.Value, ct);
        Purged(_log, notifications, "the notification log", null);

        int orders = await PurgeWindowAsync(
            connection, ExpiredOrders, DeleteOrders, now - _windows.OrderRetention!.Value, ct);
        Purged(_log, orders, "order records", null);

        int contacts = await PurgeWindowAsync(
            connection, ExpiredContacts, DeleteContacts, now - _windows.ContactRetention!.Value, ct);
        Purged(_log, contacts, "contact records", null);

        return (notifications, contacts, orders);
    }

    /// <summary>Selected, then deleted by key, so a delete locks nothing the select did not choose.</summary>
    private static async Task<int> PurgeWindowAsync(
        IDbConnection connection,
        string select,
        string delete,
        DateTimeOffset before,
        CancellationToken ct)
    {
        int total = 0;

        for (int batch = 0; batch < MaxBatchesPerPass; batch++)
        {
            Guid[] keys =
            [
                .. await connection.QueryAsync<Guid>(
                    new CommandDefinition(select, new { BatchSize, Before = before }, cancellationToken: ct))
            ];

            if (keys.Length == 0)
                break;

            total += await connection.ExecuteAsync(
                new CommandDefinition(delete, new { Keys = keys, Before = before }, cancellationToken: ct));

            if (keys.Length < BatchSize)
                break;
        }

        return total;
    }
}
