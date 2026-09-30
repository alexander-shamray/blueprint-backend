namespace Shipping.Application.Carrier;

/// <summary>The carrier in this service's vocabulary, behind §3.1's anti-corruption layer.</summary>
/// <remarks>A fault throws <see cref="CarrierUnavailableException"/>, never a refusal.</remarks>
public interface ICarrierGateway
{
    Task<BookingResult> BookAsync(BookingRequest request, CancellationToken ct);

    Task<CancellationResult> CancelAsync(CancellationRequest request, CancellationToken ct);

    /// <summary>One booking's translated page; an empty page is an answer, not a fault.</summary>
    Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string reference, CancellationToken ct);
}
