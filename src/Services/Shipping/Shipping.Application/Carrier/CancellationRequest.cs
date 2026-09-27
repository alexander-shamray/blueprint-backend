using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>
/// One booking withdrawn from the carrier, addressed by the carrier's own
/// <see cref="Reference"/>, because a cancel names the booking and not the
/// shipment. The key is the aggregate's (spec, section 4): the crash that
/// doubles the call is the one between the carrier's answer and the commit,
/// and the next pass repeats the call under the same key and receives the
/// first answer.
/// </summary>
public sealed record CancellationRequest(ShipmentId ShipmentId, string Reference)
{
    public string IdempotencyKey => $"cancel:{ShipmentId.Value}";
}
