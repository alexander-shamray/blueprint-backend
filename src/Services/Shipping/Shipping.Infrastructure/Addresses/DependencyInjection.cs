using Common.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Delivery.V1;
using Polly;
using Shipping.Application.Addresses;

namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// ADR-052's client, apart from <c>AddShippingInfrastructure</c> because it
/// parses its base address at registration and refuses to start without one,
/// which is a rule about §15.4's key rather than about persistence.
/// </summary>
public static class DependencyInjection
{
    private const string Section = "AddressSource";
    public const string BaseUrlKey = $"{Section}:BaseUrl";

    public static IServiceCollection AddDeliveryAddressSource(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // A key rather than PricingHop's literal because the value is checked
        // here and inventoried by §15.4, and a checked value is one the
        // deployment has to supply. No https rule as the carrier has: traffic
        // inside the cluster is plain HTTP/2 (§9.7), and this hop stays there.
        Uri parsed = ConfiguredBaseUrl.Read(
            configuration,
            BaseUrlKey,
            whenMissing: "Shipping cannot read an address.",
            whenUserInfo: "this host authenticates with §11.5's grant alone.",
            peer: "Ordering");

        services.AddSingleton<AddressMetrics>();
        services.AddScoped<IDeliveryAddressSource, GrpcDeliveryAddressSource>();

        IHttpClientBuilder client = services
            .AddGrpcClient<DeliveryAddresses.DeliveryAddressesClient>(AddressHop.ClientName, o => o.Address = parsed);

        // Resilience is registered first so that it sits outermost, and the
        // credential handler runs inside it (§9.7, §11.5): the handler then
        // runs once per attempt, so a retried attempt asks the token cache
        // again instead of replaying the first attempt's token.
        client.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = AddressHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = AddressHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = AddressHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = AddressHop.RetryDelay;
            options.Retry.MaxDelay = AddressHop.MaxRetryDelay;

            // A Retry-After replaces the capped backoff AddressHop's budget is
            // summed from, so honouring one would spend the total unaccounted.
            options.Retry.ShouldRetryAfterHeader = false;

            options.CircuitBreaker.FailureRatio = AddressHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = AddressHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = AddressHop.CircuitBreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = AddressHop.CircuitBreakerBreakDuration;
        });

        client.AddHttpMessageHandler<ClientCredentialsHandler>();

        return services;
    }
}
