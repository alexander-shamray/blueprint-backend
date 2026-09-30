using Common.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Delivery.V1;
using Polly;
using Shipping.Application.Addresses;

namespace Shipping.Infrastructure.Addresses;

/// <summary>ADR-052's client, apart from <c>AddShippingInfrastructure</c> as its rule is about §15.4's key.</summary>
public static class DependencyInjection
{
    private const string Section = "AddressSource";
    public const string BaseUrlKey = $"{Section}:BaseUrl";

    public static IServiceCollection AddDeliveryAddressSource(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // A key the deployment supplies (§15.4). No https rule: in-cluster traffic is plain HTTP/2 (§9.7).
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

        // Resilience first, so it is outermost and a retried attempt asks for a token again (§9.7, §11.5).
        client.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = AddressHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = AddressHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = AddressHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = AddressHop.RetryDelay;
            options.Retry.MaxDelay = AddressHop.MaxRetryDelay;

            // MaxDelay does not cap a Retry-After, so one long header would spend the budget AddressHop counts on.
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
