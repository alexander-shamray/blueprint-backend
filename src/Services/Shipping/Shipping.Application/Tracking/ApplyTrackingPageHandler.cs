using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <remarks>The aggregate, not the rank, keeps a delivery behind its despatch; publication order is §9.4's.</remarks>
public sealed class ApplyTrackingPageHandler(IShipmentRepository shipments, TimeProvider clock)
    : ICommandHandler<ApplyTrackingPageCommand, Result>
{
    public async Task<Result> HandleAsync(ApplyTrackingPageCommand command, CancellationToken ct)
    {
        Shipment? shipment = await shipments.GetAsync(command.ShipmentId, ct);

        // A refusal, not a throw: no code path deletes a shipment, and it rolls the unit back (§6.3).
        if (shipment is null)
            return Result.Failure(ShipmentErrors.NotFound);

        DateTimeOffset now = clock.GetUtcNow();

        foreach (CarrierEvent carrierEvent in command.Page.OrderBy(Rank).ThenBy(e => e.OccurredAt))
            shipment.Record(carrierEvent.CarrierEventId, carrierEvent.Status, carrierEvent.OccurredAt, now);

        shipment.PollApplied(command.NextPollAt);

        return Result.Success();
    }

    /// <summary>Most advanced first, since the key keeps a repeated id's first arrival; not a state machine.</summary>
    private static int Rank(CarrierEvent carrierEvent) => carrierEvent.Status switch
    {
        TrackingStatus.Delivered => 0,
        TrackingStatus.Collected => 1,
        _ => 2,
    };
}
