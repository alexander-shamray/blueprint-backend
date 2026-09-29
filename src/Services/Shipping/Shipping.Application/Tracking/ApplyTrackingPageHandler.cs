using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <remarks>
/// Applied by rank, not by arrival: the carrier's key orders nothing (spec,
/// section 5) and the aggregate is monotonic, but events raised in one unit of
/// work are read in the order raised, and a delivery ahead of its despatch is a
/// timeline no consumer can read. The read <c>Include</c>s the tracking events
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
        // a hand or a migration, and a throw from a worker is a row retried for
        // ever (spec, section 5).
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
