using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Shipping.V1;
using FluentValidation;
using Inventory.Application.Reservations.Fulfil;
using Inventory.Application.Reservations.GetReservation;
using Inventory.Application.Reservations.Reinstate;
using Inventory.Application.Reservations.ReleaseStock;
using Inventory.Application.Reservations.ReserveStock;
using Inventory.Application.Stock.GetStock;
using Inventory.Application.Stock.SetOnHand;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

/// <summary>Asserted on the collection, not a built provider: registration order is pipeline order (§6.3).</summary>
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
        // §4.2 registers IDomainEventDispatcher in Application, beside AddDispatcher.
        ServiceCollection services = new();

        services.AddInventoryApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDomainEventDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);

        // Named, not merely counted, since a null object would satisfy every other assertion here.
        dispatcher.ImplementationType!.Name.ShouldBe("DomainEventDispatcher");
    }

    [Fact]
    public void AddInventoryApplication_registers_the_projection_registry_scoped()
    {
        // Scoped, not singleton: the registry resolves scoped handlers (§7.5).
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
        // Explicit rather than scanned, since this registration decides what Inventory publishes (§9.3).
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
        // The scan is public-only (§6.2), and a handler or validator it misses registers as nothing.
        ServiceCollection services = new();

        services.AddInventoryApplication();

        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<SetOnHandCommand, Result>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IQueryHandler<GetStockQuery, StockDto?>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IValidator<SetOnHandCommand>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<ReserveStockCommand, Result>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IValidator<ReserveStockCommand>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<ReleaseStockCommand, Result>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IValidator<ReleaseStockCommand>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<ReinstateReservationCommand, Result>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IQueryHandler<GetReservationQuery, ReservationDto?>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<FulfilReservationCommand, Result>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IValidator<FulfilReservationCommand>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IIntegrationEventHandler<OrderCancelled>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IIntegrationEventHandler<ShipmentDispatched>));
    }
}
