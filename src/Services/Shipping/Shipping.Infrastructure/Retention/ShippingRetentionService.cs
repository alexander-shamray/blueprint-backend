using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shipping.Infrastructure.Retention;

/// <summary>ADR-053's two statutory windows, applied; the shipment's own record survives both.</summary>
/// <remarks>Apart from <c>RetentionPurgeService</c>, whose windows are housekeeping (ADR-053).</remarks>
public sealed class ShippingRetentionService : BackgroundService
{
    /// <summary>Slow on purpose: a window is measured in days, and a pass competes with the claims.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Under SQL Server's 2,100-parameter ceiling, as the delete carries one parameter per key.</summary>
    public const int BatchSize = 500;

    /// <summary>Batches per table per pass, so a backlog drains and a pass still ends.</summary>
    public const int MaxBatchesPerPass = 20;

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

    /// <summary>One pass over both tables, public so tests drive it rather than race a timer (§12.4).</summary>
    public async Task<(int Addresses, int TrackingEvents)> PurgeAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The registered clock rather than DateTimeOffset.UtcNow, for §9.5's reason: a test host substitutes it.
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        // Validated at start (ADR-053), so the value is present here.
        int addresses = await PurgeAddressesAsync(connection, now - _windows.AddressRetention!.Value, ct);
        Purged(_log, addresses, "delivery addresses", null);

        int events = await PurgeTrackingEventsAsync(connection, now - _windows.TrackingRetention!.Value, ct);
        Purged(_log, events, "tracking events", null);

        return (addresses, events);
    }

    /// <summary>Selected, then deleted by key, so the delete locks nothing on <c>Shipments</c>.</summary>
    private static async Task<int> PurgeAddressesAsync(
        IDbConnection connection,
        DateTimeOffset before,
        CancellationToken ct)
    {
        int total = 0;

        for (int batch = 0; batch < MaxBatchesPerPass; batch++)
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
                break;

            total += await connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM shipping.DeliveryAddresses WHERE OrderId IN @Orders;",
                    new { Orders = orders },
                    cancellationToken: ct));

            if (orders.Length < BatchSize)
                break;
        }

        return total;
    }

    /// <summary>Delivered only: a voided shipment's feed records what became of a parcel nobody received.</summary>
    private static async Task<int> PurgeTrackingEventsAsync(
        IDbConnection connection,
        DateTimeOffset before,
        CancellationToken ct)
    {
        int total = 0;

        for (int batch = 0; batch < MaxBatchesPerPass; batch++)
        {
            Guid[] shipments =
            [
                .. await connection.QueryAsync<Guid>(
                    new CommandDefinition(
                        """
                        SELECT TOP (@BatchSize) shipment.Id
                        FROM shipping.Shipments shipment
                        WHERE shipment.Status = 'Delivered'
                            AND shipment.TerminalAt IS NOT NULL
                            AND shipment.TerminalAt < @Before
                            AND EXISTS (
                                SELECT 1 FROM shipping.TrackingEvents e WHERE e.ShipmentId = shipment.Id)
                        ORDER BY shipment.TerminalAt;
                        """,
                        new { BatchSize, Before = before },
                        cancellationToken: ct))
            ];

            if (shipments.Length == 0)
                break;

            total += await connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM shipping.TrackingEvents WHERE ShipmentId IN @Shipments;",
                    new { Shipments = shipments },
                    cancellationToken: ct));

            if (shipments.Length < BatchSize)
                break;
        }

        return total;
    }
}
