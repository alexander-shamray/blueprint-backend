using Common.Web;
using FluentValidation;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using DevicesGrpc = Notifications.Devices.V1.Devices;

namespace Web.Bff.Devices;

/// <summary>§10.10's registration: the app list, the validators and the hop to Notifications (ADR-117).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddDeviceRegistration(this IServiceCollection services)
    {
        // Validated at start (§15.4): an app list a registration cannot be checked against refuses to boot.
        services
            .AddOptions<PushOptions>()
            .BindConfiguration(PushOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PushOptions>, PushOptionsValidator>();

        services.AddSingleton<IValidator<DeviceRegistration>, DeviceRegistrationValidator>();
        services.AddSingleton<IValidator<DeviceSignOut>, DeviceSignOutValidator>();

        // A local rather than one chain, because AddStandardResilienceHandler returns a different builder (§9.7).
        IHttpClientBuilder devices = services
            .AddGrpcClient<DevicesGrpc.DevicesClient>(
                NotificationsHop.ClientName,
                o => o.Address = NotificationsHop.Address);

        // Resilience first, so it sits outermost and the token handler inside runs once per attempt (§9.7).
        devices.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = NotificationsHop.TotalRequestTimeout;

            options.Retry.MaxRetryAttempts = NotificationsHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = NotificationsHop.RetryDelay;
            options.Retry.MaxDelay = NotificationsHop.MaxRetryDelay;

            options.AttemptTimeout.Timeout = NotificationsHop.AttemptTimeout;

            options.CircuitBreaker.FailureRatio = NotificationsHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = NotificationsHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.BreakDuration = NotificationsHop.CircuitBreakerBreakDuration;
        });

        // The caller's token and no client credentials: Notifications registers the buyer the token names (ADR-117).
        services.AddTransient<CallerTokenHandler>();
        devices.AddHttpMessageHandler<CallerTokenHandler>();

        // §10.4's outbound half, as on the pricing hop; Program.cs registers the handler itself.
        devices.AddHttpMessageHandler<CorrelationIdHandler>();

        return services;
    }
}
