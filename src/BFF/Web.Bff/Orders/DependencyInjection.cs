using Common.Application;
using Common.Infrastructure.Messaging;

namespace Web.Bff.Orders;

/// <summary>ADR-051's projection: its handlers and their instruments.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddOrderProjection(this IServiceCollection services)
    {
        // §6.2's scan, which reaches only public types, so every handler here is one.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        // The inbox filter's and the consumer's instruments (§13.3).
        services.AddSingleton<MessagingMetrics>();

        return services;
    }
}
