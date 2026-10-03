using Common.Application;
using Notifications.Application.Contacts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>Asserted on the collection, not a built provider: registration order is pipeline order (§6.3).</summary>
public class DependencyInjectionTests
{
    [Fact]
    public void AddNotificationsApplication_registers_the_dispatcher_scoped()
    {
        ServiceCollection services = new();

        services.AddNotificationsApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddNotificationsApplication_registers_the_system_clock()
    {
        ServiceCollection services = new();

        services.AddNotificationsApplication();

        ServiceDescriptor clock = services
            .Where(d => d.ServiceType == typeof(TimeProvider))
            .ShouldHaveSingleItem();
        clock.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        clock.ImplementationInstance.ShouldBeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddNotificationsApplication_registers_the_request_metrics_singleton()
    {
        ServiceCollection services = new();

        services.AddNotificationsApplication();

        ServiceDescriptor metrics = services
            .Where(d => d.ServiceType == typeof(RequestMetrics))
            .ShouldHaveSingleItem();
        metrics.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddNotificationsApplication_registers_the_dispatcher_that_stages_nothing()
    {
        // Named, since §4.1 gives this service no Domain project and the real one would not resolve (§7.5).
        ServiceCollection services = new();

        services.AddNotificationsApplication();

        ServiceDescriptor dispatcher = services
            .Where(d => d.ServiceType == typeof(IDomainEventDispatcher))
            .ShouldHaveSingleItem();
        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);
        dispatcher.ImplementationType!.Name.ShouldBe("NoDomainEventDispatcher");

        // Nothing is published, so §9.3's mapper and §7.5's registry have nothing to serve.
        services.ShouldNotContain(d => d.ServiceType == typeof(IIntegrationEventMapper));
        services.ShouldNotContain(d => d.ServiceType == typeof(IProjectionRegistry));
    }

    [Fact]
    public void AddNotificationsApplication_registers_the_four_behaviours_in_pipeline_order()
    {
        ServiceCollection services = new();

        services.AddNotificationsApplication();

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
    public void AddNotificationsApplication_registers_the_key_carrier_the_two_behaviours_share()
    {
        ServiceCollection services = new();

        services.AddNotificationsApplication();

        ServiceDescriptor carrier = services
            .Where(d => d.ServiceType == typeof(IdempotencyContext))
            .ShouldHaveSingleItem();

        carrier.Lifetime.ShouldBe(
            ServiceLifetime.Scoped,
            "a singleton would carry one command's key into every other command in the process");
    }

    [Fact]
    public void AddNotificationsApplication_registers_the_records_contact_numbers()
    {
        ServiceCollection services = new();

        services.AddNotificationsApplication();

        ServiceDescriptor options = services
            .Where(d => d.ServiceType == typeof(ContactOptions))
            .ShouldHaveSingleItem();
        options.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        options.ImplementationInstance.ShouldBe(new ContactOptions());
    }

    // The first handler of either kind and the first validator each bring back their own registration test (§6.2).
}
