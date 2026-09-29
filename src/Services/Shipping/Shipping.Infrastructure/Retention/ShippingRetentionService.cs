using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shipping.Infrastructure.Retention;

/// <summary>
/// ADR-053's two statutory windows, applied: an address is deleted its window
/// after its shipment turns terminal, a shipment's tracking events theirs after
/// delivery, and the shipment's own record survives both.
/// </summary>
/// <remarks>
/// Its own hosted service, not a branch of a worker's tick: a purge is measured
/// in days and a poll in seconds. Separate from <c>RetentionPurgeService</c>
/// because these windows are statutory and its are housekeeping (ADR-053).
/// </remarks>
public sealed class ShippingRetentionService : BackgroundService
{
    /// <summary>
    /// How often a pass runs. Slow on purpose: a statutory window is measured
    /// in days, and a purge competing with two workers' claims for the same
    /// table's locks buys nothing.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Candidate keys per pass. Well under SQL Server's 2,100-parameter
    /// ceiling, because the delete carries one parameter per key.
    /// </summary>
    public const int BatchSize = 500;

    private static readonly Action<ILogger, int, string, Exception?> Purged =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1, nameof(Purged)),
            "Shipping retention deleted {Rows} row(s) from {Table}.");

    private static readonly Action<ILogger, Exception?> PurgeFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(PurgeFailed)),
            "Shipping retention failed; retrying next pass.");

    private readonly IServiceScopeFactory _scopes;
    private readonly ShippingJurisdictionOptions _windows;
    private readonly ILogger<ShippingRetentionService> _log;

    public ShippingRetentionService(
        IServiceScopeFactory scopes,
        IOptions<ShippingJurisdictionOptions> windows,
        ILogger<ShippingRetentionService> log)
    {
        _scopes = scopes;
        _windows = windows.Value;
        _log = log;
    }

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name (ADR-019 makes it an error).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Logged and swallowed, because an exception out of ExecuteAsync
                // stops the host: a database blip during housekeeping must not
                // take the service down. The token rather than the type, for
                // §9.4's reason.
                PurgeFailed(_log, ex);
            }
        }
    }

    /// <summary>
    /// One pass over both tables. Returns the rows deleted from each. Public so
    /// tests drive it directly instead of racing a timer — the seam
    /// <c>RetentionPurgeService.PurgeAsync</c> offers, for the same reason
    /// (§12.4).
    /// </summary>
    public async Task<(int Addresses, int TrackingEvents)> PurgeAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The registered clock rather than DateTimeOffset.UtcNow, for §9.5's
        // reason: a test host substitutes it, and a row written on one clock
        // and aged on another is one no substituted clock can reason about.
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        // Validated at start, so the value is present here by construction
        // (ADR-053); the assertion is what the annotations bought.
        int addresses = await PurgeAddressesAsync(connection, now - _windows.AddressRetention!.Value, ct);
        Purged(_log, addresses, "delivery addresses", null);

        int events = await PurgeTrackingEventsAsync(connection, now - _windows.TrackingRetention!.Value, ct);
        Purged(_log, events, "tracking events", null);

        return (addresses, events);
    }

    /// <summary>
    /// Every address whose shipment turned terminal before <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// Selected then deleted by identity rather than deleted by a join: a join in
    /// the <c>DELETE</c> holds locks on <c>Shipments</c>, the table both workers
    /// claim from with <c>UPDLOCK</c>, and a purge blocking a claim reads as a
    /// slow carrier. It is <c>RetentionPurgeService</c>'s shape and the delete by
    /// identity ADR-052 asks for.
    /// </remarks>
    private static async Task<int> PurgeAddressesAsync(
        IDbConnection connection,
        DateTimeOffset before,
        CancellationToken ct)
    {
        Guid[] orders =
        [
            .. await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT TOP (@BatchSize) address.OrderId
                    FROM shipping.DeliveryAddresses address
                    INNER JOIN shipping.Shipments shipment ON shipment.OrderId = address.OrderId
                    WHERE shipment.TerminalAt IS NOT NULL
                        AND shipment.TerminalAt < @Before
                    ORDER BY shipment.TerminalAt;
                    """,
                    new { BatchSize, Before = before },
                    cancellationToken: ct))
        ];

        if (orders.Length == 0)
            return 0;

        return await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM shipping.DeliveryAddresses WHERE OrderId IN @Orders;",
                new { Orders = orders },
                cancellationToken: ct));
    }

    /// <summary>
    /// Every tracking event of a shipment delivered before <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// <c>Delivered</c> and not any terminal state, because a voided shipment's
    /// feed is the record of what the carrier did with a parcel nobody
    /// received; ADR-053's clock for these rows starts at the delivery.
    /// </remarks>
    private static async Task<int> PurgeTrackingEventsAsync(
        IDbConnection connection,
        DateTimeOffset before,
        CancellationToken ct)
    {
        Guid[] shipments =
        [
            .. await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT TOP (@BatchSize) Id
                    FROM shipping.Shipments
                    WHERE Status = 'Delivered'
                        AND TerminalAt IS NOT NULL
                        AND TerminalAt < @Before
                        AND EXISTS (SELECT 1 FROM shipping.TrackingEvents e WHERE e.ShipmentId = Id)
                    ORDER BY TerminalAt;
                    """,
                    new { BatchSize, Before = before },
                    cancellationToken: ct))
        ];

        if (shipments.Length == 0)
            return 0;

        return await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM shipping.TrackingEvents WHERE ShipmentId IN @Shipments;",
                new { Shipments = shipments },
                cancellationToken: ct));
    }
}
