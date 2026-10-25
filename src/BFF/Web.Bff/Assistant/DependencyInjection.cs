using System.Threading.RateLimiting;
using Common.Application;
using Common.Assistant.Providers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Web.Bff.Assistant;

public static class DependencyInjection
{
    /// <summary>ADR-151's assistant: options, tools, runtime, and the per-customer limit on starting work.</summary>
    /// <remarks>
    /// The provider is a factory registration, so nothing of it is built until the runtime asks, which it does only
    /// when <c>Assistant:Provider</c> names one (ADR-151); a test registers its own provider after this.
    /// </remarks>
    public static IServiceCollection AddCustomerAssistant(this IServiceCollection services)
    {
        services
            .AddOptions<AssistantOptions>()
            .BindConfiguration(AssistantOptions.SectionName);

        services.AddSingleton<CustomerReadTools>();
        services.AddSingleton<IAssistantProvider>(sp => AnthropicProviderFactory.Create(
            sp.GetRequiredService<IOptions<AssistantOptions>>().Value,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<AssistantRuntime>();

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(
                AssistantEndpoints.StartPolicy,
                context => RateLimitPartition.GetFixedWindowLimiter(
                    context.RequestServices.GetRequiredService<ICurrentUser>().Id,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = context.RequestServices
                            .GetRequiredService<IOptions<AssistantOptions>>()
                            .Value
                            .TurnsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            // A 429 with no body, which UseStatusCodePages writes as problem+json (§10.5). No Retry-After: its one
            // form is the gateway's RetryAfterHeader (§10.3), which this host cannot reference.
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        return services;
    }
}
