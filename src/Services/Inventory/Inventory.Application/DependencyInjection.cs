using Inventory.Application.Integration;
using Inventory.Application.Reservations;
using Inventory.Application.Reservations.ReserveStock;
using Common.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

/// <summary>
/// The one registration method this layer exposes (§4.2). Also the assembly's
/// <c>typeof</c> anchor for the architecture gates, per §4.1's shape — the
/// layer that has a <c>DependencyInjection.cs</c> needs no separate marker.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInventoryApplication(this IServiceCollection services)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        services.AddDispatcher();

        // Explicit rather than scanned, beside the dispatcher it serves —
        // §4.2's registration sample is the shape. It stages nothing until
        // this service has an aggregate raising domain events, and needs no
        // null object to say so: a collector over an empty change tracker
        // returns nothing and the dispatcher exits early (§7.5).
        services.AddDomainEventDispatcher();

        // The allow-list of §9.3, and the one registration that decides what
        // this service publishes. Explicit rather than scanned: a mapper
        // discovered by convention would make "Inventory publishes these three
        // facts" a property of which types happen to be in the assembly.
        services.AddScoped<IIntegrationEventMapper, InventoryIntegrationEventMapper>();

        // The clock (§5.4) and the request histogram (§13.3): LoggingBehavior
        // injects both, and nothing catches an omission before the first
        // dispatched request — not ValidateOnBuild, which never constructs an
        // open generic, and not the host smoke, which never enters the
        // dispatcher. The registration test is what guards these two lines.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RequestMetrics>();

        // §13.3's claim, forced beside RequestMetrics for the reason that one
        // is: nothing dispatches a request through UnreservedDespatchProjection,
        // so only MetricsInitialiser constructing this makes the counter exist
        // before the first claim (§13.6).
        services.AddSingleton<InventoryMetrics>();

        // Ordered, explicit, not scanned — registration order is pipeline
        // order (§6.3), and all four seats are filled since the PR that built
        // §8.5's behaviour.
        //
        // Idempotency sits INSIDE validation and OUTSIDE the transaction, and
        // both neighbours are load-bearing. Inside validation, because a
        // malformed command must be refused without claiming a key — a 400
        // that burned the caller's CommandId for 24 hours would make a typo
        // unretryable. Outside the transaction, because the claim has to be
        // held before any work starts, and a claim taken inside the
        // transaction would be released by a rollback it knows nothing about.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // The key one of those two behaviours builds and the other one writes,
        // and it is scoped because a command is. Registered beside them rather
        // than in Common.Application's own AddDispatcher, because these lines
        // are where a reader looks to find out what the pipeline is made of —
        // and because a service that omits it does not fail at startup:
        // ValidateOnBuild never constructs an open generic, so the miss would
        // surface as a TransactionBehavior that cannot be resolved on the first
        // dispatched command. A registration test is what guards this line.
        services.AddScoped<IdempotencyContext>();

        // §4.2's sample line. IValidator<T> is not in PluggableInterfaces.All
        // because it is FluentValidation's contract, not one of ours — its own
        // scanner knows its own conventions (Include* filters, internal
        // validators) and a second scan would drift from it. Anchored on
        // ReserveStockValidator rather than on this static class, which cannot
        // be a type argument. A registration test is what guards this line,
        // because ValidationBehavior takes IEnumerable<IValidator<T>> and asks
        // nobody when that sequence comes back empty.
        services.AddValidatorsFromAssemblyContaining<ReserveStockValidator>();
        return services;
    }
}
