namespace Payments.Application.Provider;

/// <summary>§3.1's anti-corruption layer: the provider in this service's vocabulary.</summary>
/// <remarks>
/// A transient fault throws <see cref="PaymentProviderUnavailableException"/>, never a decline. Each call carries
/// an idempotency key, so the caller's unit can be retried whole after the provider has answered.
/// </remarks>
public interface IPaymentProvider
{
    Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct);

    Task VoidAsync(VoidRequest request, CancellationToken ct);
}
