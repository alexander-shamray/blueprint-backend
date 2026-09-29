using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <remarks>
/// The aggregate, not the rank, keeps a delivery from getting ahead of its
/// despatch: <c>Shipment.Deliver</c> despatches a Booked row first. Rank
/// decides which instant that despatch carries, the carrier's collection when
/// the page has one. Publication order is the outbox's, by OccurredAt (§9.4),
/// and is not promised here. The read <c>Include</c>s the tracking events
/// because <c>Shipment.Record</c> deduplicates over the loaded ones (spec,
/// section 5).
/// </remarks>
public sealed class ApplyTrackingPageHandler(IShipmentRepository shipments, TimeProvider clock)
    : ICommandHandler<ApplyTrackingPageCommand, Result>
{
    public async Task<Result> HandleAsync(ApplyTrackingPageCommand command, CancellationToken ct)
    {
        Shipment? shipment = await shipments.GetAsync(command.ShipmentId, ct);

        // A refusal rather than a throw for a row the claim projected and the
        // read no longer finds: no code path deletes a shipment, so this guards
        // a hand or a migration. A refusal is not counted as an applied page,
        // and it rolls the unit back rather than moving anything (§6.3).
        if (shipment is null)
            return Result.Failure(ShipmentErrors.NotFound);

        DateTimeOffset now = clock.GetUtcNow();

        foreach (CarrierEvent carrierEvent in command.Page.OrderBy(Rank).ThenBy(e => e.OccurredAt))
            shipment.Record(carrierEvent.CarrierEventId, carrierEvent.Status, carrierEvent.OccurredAt, now);

        shipment.PollApplied(command.NextPollAt);

        return Result.Success();
    }

    /// <summary>
    /// The order a page is applied in: the two statuses that promote, in the
    /// order they promote, and everything that moves nothing last. It is an
    /// ordering and not a second state machine — the aggregate still refuses a
    /// superseded arrival, and this only decides which of a page's facts is
    /// offered first.
    /// </summary>
    private static int Rank(CarrierEvent carrierEvent) => carrierEvent.Status switch
    {
        TrackingStatus.Collected => 0,
        TrackingStatus.Delivered => 1,
        _ => 2,
    };
}
