using System.Reflection;
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders.GetDeliveryAddress;
using Ordering.Application.Orders.PlaceOrder;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§6.2's and §6.3's wiring tests, resolved from the real host rather than a test-only container.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class RegistrationTests(ServiceFixture fixture)
{
    /// <summary>The host and every Ordering assembly it reaches.</summary>
    private static List<Assembly> ServiceAssemblies()
    {
        List<Assembly> found = [typeof(Program).Assembly];

        for (int next = 0; next < found.Count; next++)
        {
            foreach (AssemblyName reference in found[next].GetReferencedAssemblies())
            {
                if (reference.Name?.StartsWith("Ordering.", StringComparison.Ordinal) == true &&
                    found.All(a => a.GetName().Name != reference.Name))
                {
                    found.Add(Assembly.Load(reference));
                }
            }
        }

        return found;
    }

    [Fact]
    public void The_scan_reaches_both_layers_that_hold_handlers()
    {
        // The subject of the test below.
        string[] names = [.. ServiceAssemblies().Select(a => a.GetName().Name!)];

        names.ShouldContain("Ordering.Application");
        names.ShouldContain("Ordering.Infrastructure");
    }

    [Fact]
    public void Every_handler_implementation_is_registered()
    {
        // Handlers are scoped; resolving them from the root provider throws.
        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        (Type Implementation, Type Service)[] implementations =
        [
            .. ServiceAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t is { IsAbstract: false, IsInterface: false, ContainsGenericParameters: false })
                .SelectMany(t => t
                    .GetInterfaces()
                    .Where(i => i.IsGenericType &&
                        PluggableInterfaces.All.Contains(i.GetGenericTypeDefinition()))
                    .Select(i => (Implementation: t, Service: i)))
        ];

        implementations.ShouldNotBeEmpty("the scan found no handler, so the loop below would assert nothing");

        foreach ((Type implementation, Type service) in implementations)
        {
            scope.ServiceProvider.GetServices(service).ShouldContain(
                s => s!.GetType() == implementation,
                $"{implementation.Name} implements {service.Name} but is not registered.");
        }
    }

    [Fact]
    public void Command_behaviours_are_registered_in_the_documented_order()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        Type[] actual =
        [
            .. scope.ServiceProvider
                .GetServices<IPipelineBehavior<PlaceOrderCommand, Result<Guid>>>()
                .Select(b => b.GetType().GetGenericTypeDefinition())
        ];

        actual.ShouldBe(
            [
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>),
                typeof(IdempotencyBehavior<,>),
                typeof(TransactionBehavior<,>)
            ],
            "outermost first, as §6.3's pipeline diagram draws it");
    }

    [Fact]
    public void Queries_run_without_the_transaction_and_idempotency_behaviours()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();

        Type[] actual =
        [
            .. scope.ServiceProvider
                .GetServices<IPipelineBehavior<GetDeliveryAddressQuery, DeliveryAddressView?>>()
                .Select(b => b.GetType().GetGenericTypeDefinition())
        ];

        actual.ShouldBe(
            [
                typeof(LoggingBehavior<,>),
                typeof(ValidationBehavior<,>)
            ],
            "queries get logging and validation only (§6.3)");
    }
}
