using Common.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;

namespace Notifications.Application;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsApplication(this IServiceCollection services)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        services.AddDispatcher();

        // §7.5's dispatcher for a service §4.1 gives no Domain project, where nothing raises an event.
        services.AddScoped<IDomainEventDispatcher, NoDomainEventDispatcher>();

        // The clock (§5.4) and the request histogram (§13.3), which LoggingBehavior injects.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RequestMetrics>();

        // ADR-052's two freshness numbers, registered rather than const so the service can change them.
        services.AddSingleton(new ContactOptions());

        // Ordered and explicit: registration order is pipeline order (§6.3). Idempotency sits inside validation,
        // so a malformed command claims no key, and outside the transaction, so the claim precedes any work.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // The key those two behaviours share, scoped because a command is. Its absence fails the first command,
        // not startup, because ValidateOnBuild never constructs an open generic.
        services.AddScoped<IdempotencyContext>();

        // §4.2's sample line, over the assembly until the first validator gives it a type.
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        return services;
    }
}
