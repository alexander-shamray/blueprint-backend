using Shipping.Application.Integration;
using Common.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Shipping.Application;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddShippingApplication(this IServiceCollection services)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        services.AddDispatcher();

        // Explicit rather than scanned, beside the dispatcher it serves, as §4.2's sample has it (§7.5).
        services.AddDomainEventDispatcher();

        // §9.3's allow-list, explicit so that what this service publishes is a decision, not a scan's finding.
        services.AddScoped<IIntegrationEventMapper, ShippingIntegrationEventMapper>();

        // The clock (§5.4) and the request histogram (§13.3), which LoggingBehavior injects.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RequestMetrics>();

        // Registration order is pipeline order; idempotency sits inside validation and outside the transaction (§6.3).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // Scoped, as a command is; only open generics inject it, so a missing one fails the first command.
        services.AddScoped<IdempotencyContext>();

        // §4.2's sample line, over the assembly because there is no validator yet to anchor on.
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        return services;
    }
}
