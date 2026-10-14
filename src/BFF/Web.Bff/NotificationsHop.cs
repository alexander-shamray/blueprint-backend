using Common.Web;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Web.Bff;

/// <summary>The hop to Notifications.Api, named in one place (§9.7, ADR-017): devices and preferences.</summary>
/// <remarks>
/// Its own numbers, not <see cref="PricingHop"/>'s, so neither budget is a term in the other; one client per service,
/// each with these numbers and its own breaker, so a fault in one method's rows opens no other's.
/// </remarks>
public static class NotificationsHop
{
    /// <summary>The device client's name; explicit, as its resilience options' name derives from it.</summary>
    public const string ClientName = "notifications-devices";

    /// <summary>The name <c>AddStandardResilienceHandler</c> registers the device client's options under.</summary>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>The preferences client's name, on the same terms as <see cref="ClientName"/>.</summary>
    public const string PreferencesClientName = "notifications-preferences";

    /// <summary>The options name the preferences client's resilience options are registered under.</summary>
    public const string PreferencesResilienceOptionsName = $"{PreferencesClientName}-standard";

    /// <summary>Notifications.Api's gRPC endpoint: plain inside the cluster (§10.1), on its HTTP/2-only port.</summary>
    /// <remarks>A literal: the host is the Kubernetes Service name, which does not vary (§15.4).</remarks>
    public static readonly Uri Address = new("http://notifications-api:8081");

    /// <summary>§9.7's outermost bound for this hop, never left at its default.</summary>
    /// <remarks>These values are named because §9.7's budget is arithmetic over several at once.</remarks>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>HTTP retries after the first attempt; both methods are convergent, so a retry is harmless.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The cap applied after jitter, which makes §9.7's budget a bound rather than a statistic.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>One row under a held lock, so the attempt is sized as a peer's, inside §9.7's band.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.4);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Every app start registers, so the rate is the buyers', as the quote's is.</summary>
    public const int CircuitBreakerMinimumThroughput = 10;

    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(15);

    /// <summary>One gRPC client on this hop: these numbers, the caller's token and the correlation ID (§9.7).</summary>
    public static IHttpClientBuilder AddNotificationsClient<TClient>(this IServiceCollection services, string name)
        where TClient : class
    {
        // A local rather than one chain, because AddStandardResilienceHandler returns a different builder (§9.7).
        IHttpClientBuilder client = services.AddGrpcClient<TClient>(name, o => o.Address = Address);

        // Resilience first, so it sits outermost and the token handler inside runs once per attempt (§9.7).
        client.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TotalRequestTimeout;

            options.Retry.MaxRetryAttempts = MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = RetryDelay;
            options.Retry.MaxDelay = MaxRetryDelay;

            options.AttemptTimeout.Timeout = AttemptTimeout;

            options.CircuitBreaker.FailureRatio = CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.BreakDuration = CircuitBreakerBreakDuration;
        });

        // The caller's token and no client credentials: Notifications acts for the buyer the token names (ADR-117).
        services.TryAddTransient<CallerTokenHandler>();
        client.AddHttpMessageHandler<CallerTokenHandler>();

        // §10.4's outbound half, as on the pricing hop; Program.cs registers the handler itself.
        client.AddHttpMessageHandler<CorrelationIdHandler>();

        return client;
    }
}
