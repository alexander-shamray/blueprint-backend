using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>One booking withdrawn by its <see cref="Reference"/>, keyed as <see cref="BookingRequest"/> is.</summary>
public sealed record CancellationRequest(ShipmentId ShipmentId, string Reference)
{
    public string IdempotencyKey => $"cancel:{ShipmentId.Value}";
}
