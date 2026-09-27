namespace Shipping.Application.Carrier;

/// <summary>The carrier's answer to a <see cref="BookingRequest"/>.</summary>
public abstract record BookingResult
{
    private BookingResult() { }

    public sealed record Booked(string Reference, string TrackingNumber) : BookingResult;

    public sealed record Refused(string Reason) : BookingResult;
}
