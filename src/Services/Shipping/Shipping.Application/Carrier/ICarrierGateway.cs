namespace Shipping.Application.Carrier;

/// <summary>
/// The carrier in this service's vocabulary (spec, section 9).
/// </summary>
/// <remarks>
/// A transient fault throws <see cref="CarrierUnavailableException"/> and is
/// never a <see cref="BookingResult"/> or <see cref="CancellationResult"/>, so
/// "the carrier is down" cannot reach a shipment as a refusal (spec, section
/// 9). Each call that writes carries its idempotency key, which is what lets a
/// worker's pass repeat whole after the carrier answered and before the commit.
/// </remarks>
public interface ICarrierGateway
{
    Task<BookingResult> BookAsync(BookingRequest request, CancellationToken ct);

    Task<CancellationResult> CancelAsync(CancellationRequest request, CancellationToken ct);

    /// <summary>
    /// The carrier's page for one booking, already translated. An empty page
    /// is an answer: a carrier that has not heard of the reference yet.
    /// </summary>
    Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string reference, CancellationToken ct);
}
