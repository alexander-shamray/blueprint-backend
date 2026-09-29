using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Fulfilment;

/// <summary>
/// Spec section 4's first worker: it claims a shipment under a lease, reads its
/// address through <see cref="IDeliveryAddressSource"/>, books with
/// <see cref="ICarrierGateway"/>, and services an unanswered cancellation.
/// </summary>
/// <remarks>
/// The call sits outside any unit of work, between the claim and the commit, so
/// no transaction spans a third party's latency; a crash between the carrier's
/// answer and the commit repeats the call under the same key.
/// </remarks>
public sealed class FulfilmentWorker(IServiceScopeFactory scopes, ILogger<FulfilmentWorker> log) : BackgroundService
{
    /// <summary>
    /// How many rows one claim leases: one, and the number is the arithmetic:
    /// a row costs up to three calls' totals, the lease is stamped at the
    /// claim and has to outlive every row's turn, and more throughput is more
    /// replicas — which is what the lease makes safe (spec, section 4).
    /// </summary>
    public const int ClaimBatchSize = 1;

    /// <summary>
    /// How long a claim holds the row it leased. Longer than the three calls
    /// a pass can make — the address read, the booking and the compensating
    /// cancel — so a slow pass is not re-claimed underneath itself, and short
    /// enough that a replica killed mid-call releases its row within the
    /// minute.
    /// </summary>
    public const int LeaseSeconds = 60;

    /// <summary>
    /// The reason a shipment carries when it waited past
    /// <see cref="FulfilmentOptions.GiveUpAge"/>: ADR-052's terminal outcome
    /// with a reason of its own, so an operator can tell it from an owner or
    /// a carrier answering that it cannot be done.
    /// </summary>
    public const string GaveUpReason = "gave_up";

    private static readonly Action<ILogger, Guid, Guid, Exception?> PassFailed =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(1, nameof(PassFailed)),
            "Fulfilment pass for shipment {ShipmentId} on order {OrderId} failed; the row backs off.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> Superseded =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Warning,
            new EventId(2, nameof(Superseded)),
            "Shipment {ShipmentId} on order {OrderId} was voided during its booking; the booking was handed back.");

    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(ClaimFailed)),
            "Fulfilment claim failed; retrying next tick.");

    private static readonly Action<ILogger, Guid, Guid, string, Exception?> Orphaned =
        LoggerMessage.Define<Guid, Guid, string>(
            LogLevel.Error,
            new EventId(4, nameof(Orphaned)),
            "Shipment {ShipmentId} on order {OrderId} was voided during its booking, and carrier booking " +
            "{CarrierReference} could not be handed back; it needs cancelling at the carrier.");

    private static readonly Action<ILogger, Guid, Guid, string, Exception?> BookingUncommitted =
        LoggerMessage.Define<Guid, Guid, string>(
            LogLevel.Error,
            new EventId(5, nameof(BookingUncommitted)),
            "Shipment {ShipmentId} on order {OrderId} was booked as carrier booking {CarrierReference}, but the " +
            "booking was not committed; if the row is voided before a pass books it again, it needs cancelling " +
            "at the carrier.");

    private static readonly Action<ILogger, Guid, Guid, TimeSpan, Exception?> GaveUp =
        LoggerMessage.Define<Guid, Guid, TimeSpan>(
            LogLevel.Warning,
            new EventId(6, nameof(GaveUp)),
            "Shipment {ShipmentId} on order {OrderId} was pending past its give-up age of {GiveUpAge}; it is " +
            "unfulfillable and will not be retried.");

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(CarrierHop.FulfilmentTick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The filter asks the token, not the exception's type. No host
                // sets BackgroundServiceExceptionBehavior, so the default turns
                // one escaped exception into a stopped host — and a gateway
                // enforcing its own deadline throws OperationCanceledException
                // while this token is still live (spec, section 4).
                ClaimFailed(log, ex);
            }
        }
    }

    /// <summary>
    /// One claim-and-fulfil pass. Returns the number of rows moved. Public so
    /// tests drive it directly instead of racing a timer (§12.4).
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();
        FulfilmentClaims claims = claimScope.ServiceProvider.GetRequiredService<FulfilmentClaims>();

        IReadOnlyList<FulfilmentWork> claimed = await claims.ClaimAsync(ct);

        int moved = 0;

        foreach (FulfilmentWork work in claimed)
        {
            // A scope per row, not per batch: a handler that throws mid-write
            // would otherwise hand the next row its own tracked, half-mutated
            // state.
            await using AsyncServiceScope row = scopes.CreateAsyncScope();

            try
            {
                if (await FulfilAsync(row.ServiceProvider, work, ct))
                    moved++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Again the token rather than the type, and the address and the
                // carrier are both here: an outage, a refused credential and a
                // defect all back the row off, and only the second is counted.
                PassFailed(log, work.Id, work.OrderId, ex);
                await claims.FailAsync(work.Id, ct);
            }
        }

        return moved;
    }

    /// <summary>
    /// One leased row, taken as far as the carrier's answers let it go.
    /// Internal rather than private so a suite can hand it the row a repeated
    /// commit reloads: the already-booked one cannot be produced on cue.
    /// </summary>
    internal async Task<bool> FulfilAsync(IServiceProvider sp, FulfilmentWork work, CancellationToken ct)
    {
        ShipmentId id = new(work.Id);
        OrderId order = new(work.OrderId);
        ICarrierGateway carrier = sp.GetRequiredService<ICarrierGateway>();

        if (work.Status == nameof(ShipmentStatus.Booked))
        {
            CancellationResult cancellation =
                await carrier.CancelAsync(new CancellationRequest(id, work.CarrierReference!), ct);

            CommitOutcome answered = await CommitAsync(sp, id, (shipment, now) => cancellation switch
            {
                CancellationResult.Cancelled => shipment.CarrierCancelled(now),
                CancellationResult.TooLate => shipment.CarrierRefusedCancellation(now),
                _ => throw new InvalidOperationException($"Unknown cancellation answer {cancellation.GetType().Name}.")
            }, ct);

            return answered.Moved;
        }

        // Asked before the owner or the carrier is: past the age the saga has
        // already raised the order for review, so a booking made now would
        // ship an order somebody is deciding about (ADR-052).
        TimeSpan giveUpAge = sp.GetRequiredService<IOptions<FulfilmentOptions>>().Value.GiveUpAge!.Value;

        if (sp.GetRequiredService<TimeProvider>().GetUtcNow() - work.CreatedAt >= giveUpAge)
        {
            CommitOutcome abandoned = await CommitPendingAsync(
                sp, id, (shipment, now) => shipment.MarkUnfulfillable(GaveUpReason, now), ct);

            if (abandoned.Moved)
                GaveUp(log, work.Id, work.OrderId, giveUpAge, null);

            return abandoned.Moved;
        }

        IDeliveryAddressStore store = sp.GetRequiredService<IDeliveryAddressStore>();
        DeliveryAddress? address = await store.GetAsync(order, ct);

        if (address is null)
        {
            AddressLookup lookup = await sp.GetRequiredService<IDeliveryAddressSource>().GetAsync(order, ct);

            if (lookup is AddressLookup.NoSuchOrder)
            {
                // Terminal, and not retried: ADR-052's fifth row. The order has
                // no address anybody can be shown, so the shipment cannot be
                // fulfilled and no later pass would learn otherwise.
                CommitOutcome unfulfillable = await CommitPendingAsync(
                    sp, id, (shipment, now) => shipment.MarkUnfulfillable("no_such_order", now), ct);

                return unfulfillable.Moved;
            }

            AddressLookup.Found found = (AddressLookup.Found)lookup;
            address = found.Address;

            // Committed on its own, before the booking: the pass that crashes
            // after the carrier has answered repeats from here, and a stored
            // address is one call this service does not make twice (ADR-052).
            await store.SaveAsync(
                order, found.CustomerId, address, sp.GetRequiredService<TimeProvider>().GetUtcNow(), ct);
        }

        BookingResult booking = await carrier.BookAsync(new BookingRequest(id, address), ct);

        if (booking is BookingResult.Refused refused)
        {
            CommitOutcome declined = await CommitPendingAsync(
                sp, id, (shipment, now) => shipment.MarkUnfulfillable(refused.Reason, now), ct);

            return declined.Moved;
        }

        BookingResult.Booked booked = (BookingResult.Booked)booking;

        CommitOutcome committed;

        try
        {
            committed = await CommitPendingAsync(
                sp, id, (shipment, now) => shipment.Book(booked.Reference, booked.TrackingNumber, now), ct);
        }
        catch (Exception ex)
        {
            // The row's catch backs it off without the reference, and the
            // cancel consumer may void it before the next pass rebooks it.
            BookingUncommitted(log, work.Id, work.OrderId, booked.Reference, ex);
            throw;
        }

        // The reloaded row decides, not the move: a refused move also comes
        // back when the execution strategy repeated a commit whose
        // acknowledgement was lost (§6.3), and that row holds this booking.
        if (committed.Status != ShipmentStatus.Voided)
            return committed.Status == ShipmentStatus.Booked;

        await HandBackAsync(carrier, id, work, booked.Reference, ct);

        return false;
    }

    /// <summary>
    /// The order was cancelled while the carrier was answering, and spec
    /// section 6 says a Pending shipment is never booked, so the booking goes
    /// back under the shipment's cancel key. The row is Voided and final, so
    /// nothing here is left to a later pass: a booking the carrier keeps is
    /// logged for a person, whether it answered too late or did not answer.
    /// A booking whose commit threw is logged with its reference before the
    /// row backs off; a crash before this call leaves no line at all, the
    /// price of the consumers never waiting on a lease.
    /// </summary>
    private async Task HandBackAsync(
        ICarrierGateway carrier,
        ShipmentId id,
        FulfilmentWork work,
        string reference,
        CancellationToken ct)
    {
        CancellationResult result;

        try
        {
            result = await carrier.CancelAsync(new CancellationRequest(id, reference), ct);
        }
        catch (Exception ex)
        {
            Orphaned(log, work.Id, work.OrderId, reference, ex);
            return;
        }

        if (result is CancellationResult.Cancelled)
            Superseded(log, work.Id, work.OrderId, null);
        else
            Orphaned(log, work.Id, work.OrderId, reference, null);
    }

    /// <summary>
    /// A leased Pending row's commit, made once more when the cancel consumer
    /// voided the row between the reload and the save: the strategy does not
    /// retry a conflict, and a backed-off Voided row is never claimed again.
    /// Once is enough, because that consumer is the one other writer such a
    /// row has, and it writes the row once.
    /// </summary>
    private static async Task<CommitOutcome> CommitPendingAsync(
        IServiceProvider sp,
        ShipmentId id,
        Func<Shipment, DateTimeOffset, bool> move,
        CancellationToken ct)
    {
        try
        {
            return await CommitAsync(sp, id, move, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await CommitAsync(sp, id, move, ct);
        }
    }

    private static async Task<CommitOutcome> CommitAsync(
        IServiceProvider sp,
        ShipmentId id,
        Func<Shipment, DateTimeOffset, bool> move,
        CancellationToken ct)
    {
        IUnitOfWork unitOfWork = sp.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.ExecuteAsync(
            async inner =>
            {
                Shipment shipment = await sp.GetRequiredService<IShipmentRepository>().GetAsync(id, inner)
                    ?? throw new InvalidOperationException($"Shipment {id.Value} was claimed and is now absent.");

                bool moved = move(shipment, sp.GetRequiredService<TimeProvider>().GetUtcNow());

                // Released whatever the move decided: a row the pass is done
                // with must not hold its lease until it lapses, and a
                // superseded arrival is done with (spec, section 5).
                shipment.ReleaseClaim();
                await unitOfWork.SaveChangesAsync(inner);

                return new CommitOutcome(moved, shipment.Status);
            },
            ct);
    }

    /// <summary>
    /// What the move decided, and the state of the row it was decided on —
    /// the row the last attempt reloaded, when the strategy retried the unit.
    /// </summary>
    private readonly record struct CommitOutcome(bool Moved, ShipmentStatus Status);
}
