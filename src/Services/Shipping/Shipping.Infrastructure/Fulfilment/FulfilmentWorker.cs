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

/// <summary>Claims a shipment under a lease, reads its address, books it, and services a cancellation.</summary>
/// <remarks>
/// Each call sits outside any unit of work, as ADR-052's read does, so no transaction spans a third party's
/// latency; a crash before the commit repeats the call under the same key.
/// </remarks>
public sealed class FulfilmentWorker(IServiceScopeFactory scopes, ILogger<FulfilmentWorker> log) : BackgroundService
{
    /// <summary>One, since a row costs up to three calls and the lease must outlive every row's turn.</summary>
    public const int ClaimBatchSize = 1;

    /// <summary>Longer than a pass's three calls, so a slow pass is not re-claimed underneath itself.</summary>
    public const int LeaseSeconds = 60;

    /// <summary>ADR-052's terminal outcome with a reason of its own, apart from a refusal's.</summary>
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
            "booking was not committed; if the row is voided or given up before a pass books it again, it needs " +
            "cancelling at the carrier.");

    private static readonly Action<ILogger, Guid, Guid, TimeSpan, Exception?> GaveUp =
        LoggerMessage.Define<Guid, Guid, TimeSpan>(
            LogLevel.Warning,
            new EventId(6, nameof(GaveUp)),
            "Shipment {ShipmentId} on order {OrderId} was pending past its give-up age of {GiveUpAge}; it is " +
            "unfulfillable and will not be retried.");

    private static readonly Action<ILogger, Guid, Guid, TimeSpan, Exception?> GaveUpCancellation =
        LoggerMessage.Define<Guid, Guid, TimeSpan>(
            LogLevel.Warning,
            new EventId(7, nameof(GaveUpCancellation)),
            "The carrier did not answer the cancellation of shipment {ShipmentId} on order {OrderId} within its " +
            "give-up age of {GiveUpAge}; it is recorded as refused, and tracking goes on.");

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
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
                // The token, not the type: a gateway's own deadline throws the same type, and an escape stops the host.
                ClaimFailed(log, ex);
            }
        }
    }

    /// <summary>One claim-and-fulfil pass, public so tests drive it rather than race a timer (§12.4).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();
        FulfilmentClaims claims = claimScope.ServiceProvider.GetRequiredService<FulfilmentClaims>();

        // On the stop's token, as the rows are: a stop between the lease's commit and its read holds the batch for
        // LeaseSeconds, the cost a stop already puts on every row it cuts short, and nothing is lost.
        IReadOnlyList<FulfilmentWork> claimed = await claims.ClaimAsync(ct);

        int moved = 0;

        foreach (FulfilmentWork work in claimed)
        {
            // A scope per row, so a row that throws mid-write hands the next none of its tracked state.
            await using AsyncServiceScope row = scopes.CreateAsyncScope();

            try
            {
                if (await FulfilAsync(row.ServiceProvider, work, ct))
                    moved++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The token again: an outage, a refused credential and a defect all back the row off.
                PassFailed(log, work.Id, work.OrderId, ex);
                await claims.FailAsync(work.Id, ct);
            }
        }

        return moved;
    }

    /// <summary>One leased row; internal so a suite can hand it the row a repeated commit reloads.</summary>
    internal async Task<bool> FulfilAsync(IServiceProvider sp, FulfilmentWork work, CancellationToken ct)
    {
        ShipmentId id = new(work.Id);
        OrderId order = new(work.OrderId);
        ICarrierGateway carrier = sp.GetRequiredService<ICarrierGateway>();
        TimeSpan giveUpAge = sp.GetRequiredService<IOptions<FulfilmentOptions>>().Value.GiveUpAge!.Value;
        DateTimeOffset now = sp.GetRequiredService<TimeProvider>().GetUtcNow();

        if (work.Status == nameof(ShipmentStatus.Booked))
        {
            // Asked before the carrier is, and recorded as its refusal (ADR-054).
            if (now - work.CancellationRequestedAt!.Value >= giveUpAge)
            {
                CommitOutcome ended = await CommitAsync(
                    sp, id, (shipment, at) => shipment.CarrierRefusedCancellation(at), ct);

                if (ended.Moved)
                    GaveUpCancellation(log, work.Id, work.OrderId, giveUpAge, null);

                return ended.Moved;
            }

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

        // Asked before the owner or the carrier is: by this age the saga has raised the order for review (ADR-052).
        // The contact row stays, to go on ShippingRetentionService's window like any terminal shipment's.
        if (now - work.CreatedAt >= giveUpAge)
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
                // Terminal and not retried: ADR-052's fifth row.
                CommitOutcome unfulfillable = await CommitPendingAsync(
                    sp, id, (shipment, now) => shipment.MarkUnfulfillable("no_such_order", now), ct);

                return unfulfillable.Moved;
            }

            AddressLookup.Found found = (AddressLookup.Found)lookup;
            address = found.Address;

            // Committed before the booking, so a pass repeated after a crash does not read it twice (ADR-052).
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
            // The row backs off without the reference, and the cancel consumer may void it before a rebooking.
            BookingUncommitted(log, work.Id, work.OrderId, booked.Reference, ex);
            throw;
        }

        // The reloaded row decides, not the move: a refused move also comes back when the strategy repeated a
        // commit whose acknowledgement was lost (§6.3), and that row holds this booking.
        if (committed.Status != ShipmentStatus.Voided)
            return committed.Status == ShipmentStatus.Booked;

        await HandBackAsync(carrier, id, work, booked.Reference, ct);

        return false;
    }

    /// <summary>Hands back a booking made while the order was cancelled, logging one the carrier keeps.</summary>
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
    /// A Pending row's commit, made once more when the cancel consumer voided it between reload and save, since
    /// the strategy does not retry a conflict and that consumer, the row's one other writer, writes it once.
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

                // Released whatever the move decided, so a finished row does not hold its lease until it lapses.
                shipment.ReleaseClaim();
                await unitOfWork.SaveChangesAsync(inner);

                return new CommitOutcome(moved, shipment.Status);
            },
            ct);
    }

    /// <summary>What the move decided, and the status of the row the last attempt reloaded.</summary>
    private readonly record struct CommitOutcome(bool Moved, ShipmentStatus Status);
}
