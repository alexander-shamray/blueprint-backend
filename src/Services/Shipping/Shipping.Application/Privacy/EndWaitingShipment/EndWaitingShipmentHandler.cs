using Common.Application;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Privacy.EndWaitingShipment;

/// <summary>Ends a pending shipment whose address is being erased, which a worker would otherwise ask for again.</summary>
/// <remarks>
/// Booked or despatched, a shipment needs no address and is left to its carrier. One command per shipment, since a
/// transaction holds one aggregate (§2.3). Repeating it finds the shipment ended and changes nothing (ADR-052).
/// </remarks>
public sealed class EndWaitingShipmentHandler(IShipmentRepository shipments, TimeProvider clock)
    : ICommandHandler<EndWaitingShipmentCommand, Result>
{
    /// <summary>The reason the shipment ends with, beside the owner's and the carrier's refusals.</summary>
    public const string Reason = "personal_data_erased";

    public async Task<Result> HandleAsync(EndWaitingShipmentCommand command, CancellationToken ct)
    {
        Shipment? shipment = await shipments.GetByOrderAsync(new OrderId(command.OrderId), ct);

        shipment?.MarkUnfulfillable(Reason, clock.GetUtcNow());

        return Result.Success();
    }
}
