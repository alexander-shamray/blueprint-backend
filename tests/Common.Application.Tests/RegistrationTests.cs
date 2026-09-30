using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>§6.2's trap: an implementation nothing registered resolves as empty, and nothing says so.</summary>
public class RegistrationTests
{
    [Fact]
    public void Every_handler_implementation_is_registered()
    {
        // Handlers are scoped; resolving them from the root provider throws.
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();

        // The list the scan reads, so a new interface is covered once it joins PluggableInterfaces.
        IEnumerable<(Type Implementation, Type Service)> implementations =
            typeof(Ping).Assembly
                .GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false })
                .SelectMany(t => t
                    .GetInterfaces()
                    .Where(i => i.IsGenericType &&
                        PluggableInterfaces.All.Contains(i.GetGenericTypeDefinition()))
                    .Select(i => (Implementation: t, Service: i)));

        foreach (var (implementation, service) in implementations)
        {
            scope.ServiceProvider.GetServices(service).ShouldContain(
                s => s!.GetType() == implementation,
                $"{implementation.Name} implements {service.Name} but is not registered.");
        }
    }

    [Fact]
    public void The_scan_registers_an_implementation_of_every_pluggable_interface()
    {
        // Named in source, so a deletion from PluggableInterfaces fails here rather than being followed.
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetService<ICommandHandler<Ping, string>>()
            .ShouldNotBeNull("ICommandHandler<,> — §6.2");
        scope.ServiceProvider.GetService<IQueryHandler<Ask, string>>()
            .ShouldNotBeNull("IQueryHandler<,> — §6.5");
        scope.ServiceProvider.GetService<IProjectionHandler<ScannedEvent>>()
            .ShouldNotBeNull("IProjectionHandler<> — §7.5, the local outbox lane");
        scope.ServiceProvider.GetService<IIntegrationEventHandler<ScannedEvent>>()
            .ShouldNotBeNull("IIntegrationEventHandler<> — §9.4, another service's events");
        scope.ServiceProvider.GetService<ICommandMessageMapper<ScannedMessage, ScannedCommand>>()
            .ShouldNotBeNull("ICommandMessageMapper<,> — §9.4, wire contract to command");
    }

    [Fact]
    public void The_pluggable_list_holds_exactly_the_five_interfaces_the_scan_is_for()
    {
        // Order is not asserted, because the scan does not depend on it.
        PluggableInterfaces.All.ShouldBe(
            [
                typeof(ICommandHandler<,>),
                typeof(IQueryHandler<,>),
                typeof(IProjectionHandler<>),
                typeof(IIntegrationEventHandler<>),
                typeof(ICommandMessageMapper<,>)
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void The_scan_finds_something_to_register()
    {
        // An empty scan would pass every assertion made about its members.
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<ICommandHandler<Ping, string>>().ShouldNotBeEmpty();
        scope.ServiceProvider.GetServices<IQueryHandler<Ask, string>>().ShouldNotBeEmpty();
    }

    [Fact]
    public void The_pipeline_behaviour_interface_is_not_one_of_the_scanned_ones()
    {
        // Registration order is pipeline order (§6.3), and a scan guarantees no order.
        PluggableInterfaces.All.ShouldNotContain(typeof(IPipelineBehavior<,>));
    }

    [Fact]
    public void Handlers_are_registered_with_a_scoped_lifetime()
    {
        // Scoped, so a handler gets the request's own unit of work and repositories (§4.2).
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        ICommandHandler<Ping, string> a = first.ServiceProvider.GetRequiredService<ICommandHandler<Ping, string>>();
        ICommandHandler<Ping, string> b = second.ServiceProvider.GetRequiredService<ICommandHandler<Ping, string>>();

        a.ShouldNotBeSameAs(b);
    }
}
