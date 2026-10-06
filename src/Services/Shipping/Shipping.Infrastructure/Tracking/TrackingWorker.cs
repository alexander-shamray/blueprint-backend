using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shipping.Application.Carrier;
using Shipping.Application.Tracking;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Tracking;

/// <summary>Asks the carrier what has happened to each booked shipment and applies the answer.</summary>
/// <remarks>
/// <see cref="Fulfilment.FulfilmentWorker"/>'s shape, a second loop rather than a branch, since the two are
/// paced by different things.
/// </remarks>
public sealed class TrackingWorker(
    IServiceScopeFactory scopes,
    ILogger<TrackingWorker> log) : BackgroundService
{
    /// <summary>Carrier calls one pass has in flight; smaller than the outbox's, each row being a round trip.</summary>
    public const int ClaimBatchSize = 20;

    /// <summary>Above <c>CarrierHop.TotalRequestTimeout</c>, so a row in flight is not claimed again.</summary>
    /// <remarks>Not <see cref="Fulfilment.FulfilmentWorker.LeaseSeconds"/>: each bounds its own hops.</remarks>
    public const int LeaseSeconds = 45;

    /// <summary>ADR-054's tracking age, measured from <c>Shipment.CreatedAt</c>: a ceiling past any delivery.</summary>
    public static readonly TimeSpan GiveUpAge = TimeSpan.FromDays(90);

    // CA1848 (ADR-019), the shape §9.4's dispatcher takes.
    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(ClaimFailed)),
            "Tracking claim failed; retrying next tick.");

    private static readonly Action<ILogger, Guid, Guid, int, Exception?> PollFailed =
        LoggerMessage.Define<Guid, Guid, int>(
            LogLevel.Warning,
            new EventId(2, nameof(PollFailed)),
            "Tracking poll for shipment {ShipmentId} of order {OrderId} failed; attempt {Attempt}, backing off.");

    private static readonly Action<ILogger, Guid, Guid, TimeSpan, Exception?> Abandoned =
        LoggerMessage.Define<Guid, Guid, TimeSpan>(
            LogLevel.Warning,
            new EventId(3, nameof(Abandoned)),
            "Shipment {ShipmentId} of order {OrderId} was not delivered within its tracking age of {GiveUpAge}; " +
            "it is abandoned and no longer polled.");

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Shorter than the poll interval, since NextPollAt falls part-way into a tick; it bounds how late a row is.
        using PeriodicTimer timer = new(CarrierHop.TrackingTick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The token, not the type: a call's own deadline throws the same type.
                ClaimFailed(log, ex);
            }
        }
    }

    /// <summary>One claim-and-poll pass, public so tests drive it rather than race a timer (§12.4).</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();

        TrackingClaims claims = claimScope.ServiceProvider.GetRequiredService<TrackingClaims>();

        // On the stop's token, as the rows are: a stop between the lease's commit and its read holds the batch for
        // LeaseSeconds, the cost a stop already puts on every row it cuts short, and nothing is lost.
        IReadOnlyList<TrackingWork> claimed = await claims.ClaimAsync(ct);

        // Every row at once, so a pass lasts one hop and the lease bounds it; WhenAll, so one row's fault
        // leaves the others to finish and still reaches ExecuteAsync.
        bool[] applied = await Task.WhenAll(claimed.Select(work => PollOrBackOffAsync(claims, work, ct)));

        return applied.Count(isApplied => isApplied);
    }

    private async Task<bool> PollOrBackOffAsync(TrackingClaims claims, TrackingWork work, CancellationToken ct)
    {
        try
        {
            return await PollAsync(work, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Logged before the backoff is written, so a database fault in FailAsync cannot hide the carrier's.
            PollFailed(log, work.Id, work.OrderId, work.PollAttempts + 1, ex);

            await claims.FailAsync(work.Id, ct);

            return false;
        }
    }

    /// <summary>One shipment's page, read outside any transaction and applied inside one.</summary>
    private async Task<bool> PollAsync(TrackingWork work, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        // Asked before the carrier is, so a carrier that never answers cannot hold an address past the age (ADR-054).
        if (scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow() - work.CreatedAt >= GiveUpAge)
        {
            Result abandoned = await dispatcher.SendAsync(new AbandonShipmentCommand(new ShipmentId(work.Id)), ct);

            if (abandoned.IsSuccess)
                Abandoned(log, work.Id, work.OrderId, GiveUpAge, null);

            return abandoned.IsSuccess;
        }

        IReadOnlyList<CarrierEvent> page = await scope.ServiceProvider
            .GetRequiredService<ICarrierGateway>()
            .GetEventsAsync(work.CarrierReference, ct);

        DateTimeOffset nextPollAt =
            scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow()
            + CarrierHop.TrackingPollInterval;

        // The command releases the claim through Shipment.PollApplied, in the commit that applied the page.
        Result result = await dispatcher.SendAsync(
            new ApplyTrackingPageCommand(new ShipmentId(work.Id), page, nextPollAt),
            ct);

        // A refusal rolled the unit back (§6.3), so it is not an applied page.
        return result.IsSuccess;
    }
}
