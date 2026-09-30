using Common.Application;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <summary>Ends a shipment the carrier never finished, releasing the tracking lease in the same commit.</summary>
public sealed class AbandonShipmentHandler(IShipmentRepository shipments, TimeProvider clock)
    : ICommandHandler<AbandonShipmentCommand, Result>
{
    public async Task<Result> HandleAsync(AbandonShipmentCommand command, CancellationToken ct)
    {
        Shipment? shipment = await shipments.GetAsync(command.ShipmentId, ct);

        // ApplyTrackingPageHandler's refusal, for its reason.
        if (shipment is null)
            return Result.Failure(ShipmentErrors.NotFound);

        DateTimeOffset now = clock.GetUtcNow();

        shipment.Abandon(now);

        // Terminal, so no poll is scheduled whatever instant is passed.
        shipment.PollApplied(now);

        return Result.Success();
    }
}
