using Catalog.Application.Integration;
using Catalog.Application.Products.PublishProduct;
using Common.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Application;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        services.AddDispatcher();

        // Explicit rather than scanned, beside the dispatcher it serves —
        // §4.2's registration sample is the shape. §7.5's real dispatcher,
        // and no null one beside it: a dispatcher that drops every domain
        // event is deleted rather than disabled, so nothing can register it
        // back by accident.
        services.AddDomainEventDispatcher();

        // §9.3's allow-list, explicit so what this service publishes is not whichever types the assembly holds.
        services.AddScoped<IIntegrationEventMapper, CatalogIntegrationEventMapper>();

        // The clock (§5.4) and the request histogram (§13.3), which LoggingBehavior injects.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RequestMetrics>();

        // Ordered and explicit: registration order is pipeline order (§6.3). Idempotency sits inside validation,
        // so a malformed command claims no key, and outside the transaction, so the claim precedes any work.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // The key those two behaviours share, scoped because a command is. Its absence fails the first command,
        // not startup, because ValidateOnBuild never constructs an open generic.
        services.AddScoped<IdempotencyContext>();

        // §4.2's sample line. IValidator<T> is not in PluggableInterfaces.All
        // because it is FluentValidation's contract, not one of ours — its own
        // scanner knows its own conventions (Include* filters, internal
        // validators) and a second scan would drift from it.
        services.AddValidatorsFromAssemblyContaining<PublishProductValidator>();
        return services;
    }
}
