using System.Data;
using Common.Application;
using Common.Infrastructure.Switching;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Devices;
using Notifications.Infrastructure.Jurisdiction;
using Notifications.Infrastructure.Push;

namespace Notifications.Infrastructure.Retention;

/// <summary>ADR-053's three windows, ADR-117's stale window over the device rows and ADR-120's journal.</summary>
/// <remarks>
/// Apart from <c>RetentionPurgeService</c>, whose windows are housekeeping (ADR-053). An order record goes only once
/// no pending notice names its order, since that notice waits on it; a push delivery ages with its notice.
/// </remarks>
public sealed class NotificationsRetentionService : SwitchedPollingWorker
{
    /// <summary>The name an operator lists to switch the windows' pass off (ADR-113).</summary>
    public const string Name = "notifications-retention";

    /// <summary>Slow on purpose: a window is measured in days, and a pass competes with the claims.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Under SQL Server's 2,100-parameter ceiling, as the delete carries one parameter per key.</summary>
    public const int BatchSize = 500;

    /// <summary>Batches per table per pass, so a backlog drains and a pass still ends.</summary>
    public const int MaxBatchesPerPass = 20;

    // A dispatched push notice's outcome is its deliveries', so it is never aged while one of them is pending, and is
    // aged from the last of them to end, so each keeps ADR-053 rule 4's window; they go with it, by the cascade.
    private const string NoPendingDelivery =
        """
        NOT EXISTS (
            SELECT 1 FROM notifications.PushDeliveries d
            WHERE d.NotificationId = n.NotificationId
                AND d.Status = 'Pending')
        """;

    private const string LastDelivery =
        """
        OUTER APPLY (
            SELECT CompletedAt = MAX(d.CompletedAt)
            FROM notifications.PushDeliveries d
            WHERE d.NotificationId = n.NotificationId) last
        """;

    private const string NoticeEndedAt = "COALESCE(last.CompletedAt, n.CompletedAt)";

    private const string EndedNotices =
        $"""
        SELECT TOP (@BatchSize) n.NotificationId
        FROM notifications.NotificationLog n
        {LastDelivery}
        WHERE n.CompletedAt IS NOT NULL
            AND {NoticeEndedAt} < @Before
            AND n.Status <> 'Pending'
            AND {NoPendingDelivery}
        ORDER BY n.CompletedAt;
        """;

    private const string DeleteNotices =
        $"""
        DELETE n FROM notifications.NotificationLog n
        {LastDelivery}
        WHERE n.NotificationId IN @Keys
            AND n.CompletedAt IS NOT NULL
            AND {NoticeEndedAt} < @Before
            AND n.Status <> 'Pending'
            AND {NoPendingDelivery};
        """;

    private const string ExpiredOrders =
        """
        SELECT TOP (@BatchSize) r.OrderId
        FROM notifications.OrderRecords r
        WHERE r.RecordedAt < @Before
            AND NOT EXISTS (
                SELECT 1 FROM notifications.NotificationLog n
                WHERE n.OrderId = r.OrderId
                    AND n.Status = 'Pending')
        ORDER BY r.RecordedAt;
        """;

    // An ended notice that never had its customer assigned is found by an erasure only through this record, so what it
    // holds goes first (docs/personal-data.md, NotificationLog); a row with a customer stays findable by it.
    private const string BlankUnassignedNotices =
        """
        UPDATE n
        SET Languages = NULL, Parameters = ''
        FROM notifications.NotificationLog n
        WHERE n.OrderId IN @Keys
            AND n.CustomerId IS NULL
            AND n.Status <> 'Pending'
            AND EXISTS (
                SELECT 1 FROM notifications.OrderRecords r
                WHERE r.OrderId = n.OrderId
                    AND r.RecordedAt < @Before)
            AND NOT EXISTS (
                SELECT 1 FROM notifications.NotificationLog p
                WHERE p.OrderId = n.OrderId
                    AND p.Status = 'Pending');
        """;

    // Asked again, so a notice a consumer wrote since the select keeps the record it will wait on. A delete locks the
    // record's key before its CustomerId entry, the reverse of an erasure's first statement, so a deadlock between
    // them is possible; housekeeping yields, its next pass retries, and the erasure completes.
    private const string DeleteOrders =
        """
        SET DEADLOCK_PRIORITY LOW;
        DELETE r FROM notifications.OrderRecords r
        WHERE r.OrderId IN @Keys
            AND r.RecordedAt < @Before
            AND NOT EXISTS (
                SELECT 1 FROM notifications.NotificationLog n
                WHERE n.OrderId = r.OrderId
                    AND n.Status = 'Pending');
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
        """
        DELETE FROM notifications.ContactRecords
        WHERE CustomerId IN @Keys
            AND FetchedAt < @Before;
        """;

    private const string StaleDevices =
        """
        SELECT TOP (@BatchSize) InstallationId
        FROM notifications.DeviceInstallations
        WHERE LastSeenAt < @Before
        ORDER BY LastSeenAt;
        """;

    // Guarded by the row's last-seen time, so a registration the API made since the select keeps its row (ADR-118).
    private const string DeleteDevices =
        """
        DELETE FROM notifications.DeviceInstallations
        WHERE InstallationId IN @Keys
            AND LastSeenAt < @Before;
        """;

    // When an entry stopped being current: the first entry written after it in its pair, or its erasure when none
    // survives to say (ADR-120). A pair's newest has neither, so it is never aged.
    private const string Later =
        """
        OUTER APPLY (
            SELECT WrittenAt = MIN(n.WrittenAt)
            FROM notifications.ConsentEntries n
            WHERE n.SupersedesEntryId = e.EntryId
                OR (n.SubjectId = e.SubjectId
                    AND n.Topic = e.Topic
                    AND n.Channel = e.Channel
                    AND n.Position > e.Position)) later
        """;

    private const string EndedAt = "COALESCE(later.WrittenAt, e.AnonymisedAt)";

    // ADR-120's second condition: no kept record of a send, notice or delivery, names the entry or the entry it ended.
    private const string Unnamed =
        """
        NOT EXISTS (
            SELECT 1 FROM notifications.NotificationLog n
            WHERE n.ConsentEntryId = e.EntryId
                OR n.ConsentEntryId = e.SupersedesEntryId)
        AND NOT EXISTS (
            SELECT 1 FROM notifications.PushDeliveries d
            WHERE d.ConsentEntryId = e.EntryId
                OR d.ConsentEntryId = e.SupersedesEntryId)
        """;

    private const string ExpiredConsent =
        $"""
        SELECT TOP (@BatchSize) e.EntryId
        FROM notifications.ConsentEntries e
        {Later}
        WHERE {EndedAt} < @Before
            AND {Unnamed}
        ORDER BY {EndedAt};
        """;

    // Asked again, so a send the pass stamped since the select keeps the entry it names.
    private const string DeleteConsent =
        $"""
        DELETE e FROM notifications.ConsentEntries e
        {Later}
        WHERE e.EntryId IN @Keys
            AND {EndedAt} < @Before
            AND {Unnamed};
        """;

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
    private readonly DeviceTokenOptions _devices;
    private readonly PushMetrics _metrics;
    private readonly ILogger<NotificationsRetentionService> _log;

    public NotificationsRetentionService(
        IServiceScopeFactory scopes,
        IOptions<NotificationsJurisdictionOptions> windows,
        DeviceTokenOptions devices,
        PushMetrics metrics,
        ILogger<NotificationsRetentionService> log,
        OperatorSwitches? switches = null)
        : base(switches)
    {
        _scopes = scopes;
        _windows = windows.Value;
        _devices = devices;
        _metrics = metrics;
        _log = log;
    }

    public override string SwitchName => Name;

    protected override TimeSpan PassInterval => Interval;

    // A pass at start, or a deployment restarting more often than Interval would never apply ADR-053's windows.
    protected override bool PassAtStart => true;

    protected override Task RunPassAsync(CancellationToken stoppingToken) => PurgeAsync(stoppingToken);

    protected override void OnPassFailed(Exception exception) => PurgeFailed(_log, exception);

    /// <summary>One pass over the five tables, public so tests drive it rather than race a timer (§12.4).</summary>
    public async Task<(int Notifications, int Contacts, int Orders, int Devices, int Consent)> PurgeAsync(
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The registered clock rather than DateTimeOffset.UtcNow, for §9.5's reason: a test host substitutes it.
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        // Validated at start (ADR-053), so each window is present here.
        int notifications = await PurgeWindowAsync(
            connection,
            EndedNotices,
            DeleteNotices,
            now - _windows.LogRetention!.Value,
            null,
            ct);
        Purged(_log, notifications, "the notification log", null);

        int orders = await PurgeWindowAsync(
            connection,
            ExpiredOrders,
            DeleteOrders,
            now - _windows.OrderRetention!.Value,
            BlankUnassignedNotices,
            ct);
        Purged(_log, orders, "order records", null);

        int contacts = await PurgeWindowAsync(
            connection,
            ExpiredContacts,
            DeleteContacts,
            now - _windows.ContactRetention!.Value,
            null,
            ct);
        Purged(_log, contacts, "contact records", null);

        int devices = await PurgeWindowAsync(
            connection,
            StaleDevices,
            DeleteDevices,
            now - _devices.StaleAfter,
            null,
            ct);
        Purged(_log, devices, "device installations", null);
        _metrics.DevicesRemoved(PushMetrics.Stale, devices);

        // After the log's own window, so an entry the pass just freed of its last send goes in the same pass.
        int consent = await PurgeWindowAsync(
            connection,
            ExpiredConsent,
            DeleteConsent,
            now - _windows.ConsentRetention!.Value,
            null,
            ct);
        Purged(_log, consent, "consent entries", null);

        return (notifications, contacts, orders, devices, consent);
    }

    /// <summary>Selected, then deleted by key, so a delete locks nothing the select did not choose.</summary>
    private static async Task<int> PurgeWindowAsync(
        IDbConnection connection,
        string select,
        string delete,
        DateTimeOffset before,
        string? beforeDelete,
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

            if (beforeDelete is not null)
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(beforeDelete, new { Keys = keys, Before = before }, cancellationToken: ct));
            }

            total += await connection.ExecuteAsync(
                new CommandDefinition(delete, new { Keys = keys, Before = before }, cancellationToken: ct));

            if (keys.Length < BatchSize)
                break;
        }

        return total;
    }
}
