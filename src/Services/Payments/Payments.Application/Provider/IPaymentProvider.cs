namespace Payments.Application.Provider;

/// <summary>
/// §3.2's anti-corruption layer: the provider in this service's vocabulary.
/// </summary>
/// <remarks>
/// A transient fault throws <see cref="PaymentProviderUnavailableException"/>
/// and is never an <see cref="AuthorisationResult"/>, so "the provider is down"
/// cannot reach the saga as a decline. Every call carries its request's
/// idempotency key, which is what lets the caller's unit of work be retried
/// whole after the provider has answered.
/// </remarks>
public interface IPaymentProvider
{
    Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct);

    Task VoidAsync(VoidRequest request, CancellationToken ct);
}
