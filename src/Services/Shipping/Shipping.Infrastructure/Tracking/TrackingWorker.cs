using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shipping.Application.Carrier;
using Shipping.Application.Tracking;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Tracking;

/// <summary>
/// The second of this service's two workers (spec, section 4): it asks the
/// carrier what has happened to each booked shipment and applies the answer.
/// </summary>
/// <remarks>
/// <c>FulfilmentWorker</c>'s shape, which is <c>OutboxDispatcher</c>'s: the
/// claim leases its rows, each row fails alone, and the loop's filter asks
/// the token because no host sets <c>BackgroundServiceExceptionBehavior</c>.
/// A second loop, not a branch: the two are paced by different things.
/// </remarks>
public sealed class TrackingWorker(
    IServiceScopeFactory scopes,
    ILogger<TrackingWorker> log) : BackgroundService
{
    /// <summary>
    /// How many rows one claim leases. Smaller than the outbox's, because each
    /// row here is a round trip to a third party rather than a publish.
    /// </summary>
    public const int ClaimBatchSize = 20;

    /// <summary>
    /// How long a claim holds its rows: above <see cref="PassBudget"/> and
    /// <c>CarrierHop.TotalRequestTimeout</c>, so a row still being called
    /// about stays out of either worker's next claim — the lease is one column.
    /// </summary>
    /// <remarks>
    /// Shorter than <c>FulfilmentWorker.LeaseSeconds</c> and not one constant
    /// with it: that pass makes two hops and this one makes one, so each lease
    /// bounds its own worst case.
    /// </remarks>
    public const int LeaseSeconds = 45;

    /// <summary>
    /// The most one pass spends on carrier calls. Below §15.3's
    /// thirty-second shutdown drain and above one hop's total, so a pass
    /// always makes at least one call and never outlives the host's stop.
    /// </summary>
    public static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(25);

    /// <summary>
    /// ADR-054's tracking age, measured from <c>Shipment.CreatedAt</c>: a
    /// shipment the carrier has not finished by then is abandoned rather than
    /// polled. Ninety days: a ceiling past any carrier's delivery.
    /// </summary>
    public static readonly TimeSpan GiveUpAge = TimeSpan.FromDays(90);

    // Compiled once rather than parsed per call — CA1848 (ADR-019), the shape
    // §9.4's dispatcher takes.
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

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name (ADR-019 makes it an error).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Due-ness is the row's NextPollAt, stamped after its call returns, so
        // it falls part-way into a tick. A loop period as long as the poll
        // interval would claim that row one tick late; this one only bounds
        // how late a due row is picked up (CarrierHop.TrackingTick).
        using PeriodicTimer timer = new(CarrierHop.TrackingTick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The claim itself failed — database unreachable. Next tick.
                // The filter asks the token, not the exception's type: a call
                // enforcing its own deadline throws OperationCanceledException
                // while this token is still live, and testing the type would
                // let that escape and fault the whole background service.
                ClaimFailed(log, ex);
            }
        }
    }

    /// <summary>
    /// One claim-and-poll pass. Returns the number of shipments whose page the
    /// handler applied, or which it abandoned — a claimed row whose command
    /// refused is not one. Public
    /// so tests drive it directly instead of racing a timer, the same seam
    /// <c>OutboxDispatcher.ProcessBatchAsync</c> offers (§12.4).
    /// </summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();

        TrackingClaims claims = claimScope.ServiceProvider.GetRequiredService<TrackingClaims>();
        IReadOnlyList<TrackingWork> claimed = await claims.ClaimAsync(ct);

        TimeProvider clock = claimScope.ServiceProvider.GetRequiredService<TimeProvider>();
        DateTimeOffset started = clock.GetUtcNow();
        int applied = 0;

        foreach (TrackingWork work in claimed)
        {
            // A row whose call could not finish inside the budget is released
            // rather than started: the next tick claims it, and nothing is left
            // leased behind a pass that ran out of time.
            if (clock.GetUtcNow() - started + CarrierHop.TotalRequestTimeout > PassBudget)
            {
                await claims.ReleaseAsync(work.Id, ct);
                continue;
            }

            try
            {
                if (await PollAsync(work, ct))
                    applied++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One row's carrier, one row's backoff. A page that cannot be
                // read is not a fact about any other shipment. Logged before
                // the backoff is written, as FulfilmentWorker does, so a
                // database fault in FailAsync cannot hide the carrier's.
                PollFailed(log, work.Id, work.OrderId, work.PollAttempts + 1, ex);

                await claims.FailAsync(work.Id, ct);
            }
        }

        return applied;
    }

    /// <summary>
    /// One shipment's page: read outside any transaction, applied inside one.
    /// Answers whether the handler applied it.
    /// </summary>
    /// <remarks>
    /// The call is made before the unit of work opens, which is the whole of
    /// section 4's bulkhead: a transaction held across a third party's latency
    /// is a lock nothing downstream can wait out. The claim is what makes that
    /// safe — the row is this pass's until the lease lapses.
    /// </remarks>
    private async Task<bool> PollAsync(TrackingWork work, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        // Asked before the carrier is, so a carrier that never answers cannot
        // hold a shipment, and its address, past the age (ADR-054).
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

        // The command releases the claim through Shipment.PollApplied, inside
        // the same unit of work as the page it applied: the lease is dropped by
        // the commit that used it, never by a second statement that could land
        // on its own.
        Result result = await dispatcher.SendAsync(
            new ApplyTrackingPageCommand(new ShipmentId(work.Id), page, nextPollAt),
            ct);

        // A refusal is not an applied page, and the caller's count says so:
        // ShipmentErrors.NotFound is a row the claim projected and the
        // handler's read no longer found, which no code path here can cause,
        // and §6.3's behaviour rolled the unit back rather than moving
        // anything.
        return result.IsSuccess;
    }
}
