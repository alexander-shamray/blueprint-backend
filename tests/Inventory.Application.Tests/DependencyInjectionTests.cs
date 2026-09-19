using Common.Application;
using FluentValidation;
using Inventory.Application.Stock.GetStock;
using Inventory.Application.Stock.SetOnHand;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

/// <summary>
/// The registration surface of <c>AddInventoryApplication</c>, asserted on the
/// collection rather than a built provider: registration order is pipeline
/// order (§6.3), and only the descriptor list still shows it.
/// </summary>
public class DependencyInjectionTests
{
    [Fact]
    public void AddInventoryApplication_registers_the_dispatcher_scoped()
    {
        ServiceCollection services = new();

        services.AddInventoryApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInventoryApplication_registers_the_system_clock()
    {
        // LoggingBehavior injects TimeProvider, and neither ValidateOnBuild
        // nor the host smoke can see the hole: an open generic is not
        // constructed until a closed IPipelineBehavior<,> resolves, which
        // nothing does before the first dispatched request (§4.2, §5.4).
        ServiceCollection services = new();

        services.AddInventoryApplication();

        ServiceDescriptor clock = services
            .Where(d => d.ServiceType == typeof(TimeProvider))
            .ShouldHaveSingleItem();
        clock.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        clock.ImplementationInstance.ShouldBeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddInventoryApplication_registers_the_request_metrics_singleton()
    {
        // The clock test's twin, for the same reason: LoggingBehavior injects
        // RequestMetrics, and neither ValidateOnBuild nor the host smoke can
        // see the omission before the first dispatched request.
        ServiceCollection services = new();

        services.AddInventoryApplication();

        ServiceDescriptor metrics = services
            .Where(d => d.ServiceType == typeof(RequestMetrics))
            .ShouldHaveSingleItem();
        metrics.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInventoryApplication_registers_the_real_domain_event_dispatcher_scoped()
    {
        // §4.2 registers IDomainEventDispatcher in Application, beside
        // AddDispatcher. Without it the first resolved TransactionBehavior
        // throws, and nothing resolves one before the first dispatched
        // command.
        ServiceCollection services = new();

        services.AddInventoryApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDomainEventDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);

        // Named, not merely counted. The null object this replaced satisfied
        // every other assertion in this test while dropping every domain event
        // the aggregate raised, which is exactly the failure a shape-only
        // check cannot see.
        dispatcher.ImplementationType!.Name.ShouldBe("DomainEventDispatcher");
    }

    [Fact]
    public void AddInventoryApplication_registers_the_projection_registry_scoped()
    {
        // Scoped, not singleton: the registry resolves scoped handlers, and
        // GetServices for a scoped service from the root provider throws
        // (§7.5). Its memo is the singleton beside it, keyed to the container.
        ServiceCollection services = new();

        services.AddInventoryApplication();

        services
            .Where(d => d.ServiceType == typeof(IProjectionRegistry))
            .ShouldHaveSingleItem()
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInventoryApplication_registers_the_allow_list_mapper()
    {
        // The one registration that decides what Inventory publishes (§9.3).
        // Explicit rather than scanned, so "Inventory publishes these facts" is
        // not a property of which types happen to be in the assembly.
        ServiceCollection services = new();

        services.AddInventoryApplication();

        services
            .Where(d => d.ServiceType == typeof(IIntegrationEventMapper))
            .ShouldHaveSingleItem()
            .ImplementationType!.Name.ShouldBe("InventoryIntegrationEventMapper");
    }

    [Fact]
    public void AddInventoryApplication_registers_the_four_behaviours_in_pipeline_order()
    {
        ServiceCollection services = new();

        services.AddInventoryApplication();

        IEnumerable<Type?> behaviours = services
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType);

        behaviours.ShouldBe(
            [
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(IdempotencyBehavior<,>),
                typeof(TransactionBehavior<,>)
            ],
            "all four, in pipeline order (§6.3) — idempotency claims its key inside validation, " +
            "so a malformed command is refused without burning one, and outside the transaction, " +
            "so the claim is held before any work starts");
    }

    [Fact]
    public void AddInventoryApplication_registers_the_key_carrier_the_two_behaviours_share()
    {
        // §8.5's behaviour builds the key and §6.3's transaction writes the
        // durable marker under it, and the carrier between them is a plain
        // scoped class rather than an interface — so nothing fails at startup
        // if it is missing. ValidateOnBuild never constructs an open generic,
        // so the omission surfaces as a TransactionBehavior that cannot be
        // resolved on the first command this service dispatches.
        ServiceCollection services = new();

        services.AddInventoryApplication();

        ServiceDescriptor carrier = services
            .Where(d => d.ServiceType == typeof(IdempotencyContext))
            .ShouldHaveSingleItem();

        carrier.Lifetime.ShouldBe(
            ServiceLifetime.Scoped,
            "a singleton would carry one command's key into every other command in the process");
    }

    [Fact]
    public void AddInventoryApplication_registers_the_slice_handlers()
    {
        // The scan is public-only (§6.2); a handler or validator it misses
        // registers as nothing rather than as something wrong, so every
        // slice adds its rows here.
        ServiceCollection services = new();

        services.AddInventoryApplication();

        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<SetOnHandCommand, Result>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IQueryHandler<GetStockQuery, StockDto?>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IValidator<SetOnHandCommand>));
    }
}
