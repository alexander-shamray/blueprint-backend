using Ordering.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§9.6's five delays as the production helper binds them: the lever §14.1 gives a developer's stack.</summary>
public class OrderFulfilmentTimeoutsTests
{
    private static ServiceProvider Provider(params (string Key, string Value)[] settings)
    {
        Dictionary<string, string?> values = new() { ["ConnectionStrings:RabbitMq"] = "amqp://ordering.invalid:5672" };
        foreach ((string key, string value) in settings)
            values[key] = value;

        ServiceCollection services = new();
        services.AddMassTransitMessaging(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_host_that_sets_nothing_waits_the_delays_the_saga_declares()
    {
        using ServiceProvider provider = Provider();

        OrderFulfilmentTimeouts bound = provider.GetRequiredService<IOptions<OrderFulfilmentTimeouts>>().Value;

        bound.StockTimeoutDelay.ShouldBe(OrderFulfilmentSaga.StockTimeoutDelay);
        bound.PaymentTimeoutDelay.ShouldBe(OrderFulfilmentSaga.PaymentTimeoutDelay);
        bound.ConfirmationTimeoutDelay.ShouldBe(OrderFulfilmentSaga.ConfirmationTimeoutDelay);
        bound.ReleaseTimeoutDelay.ShouldBe(OrderFulfilmentSaga.ReleaseTimeoutDelay);
        bound.DespatchTimeoutDelay.ShouldBe(OrderFulfilmentSaga.DespatchTimeoutDelay);
    }

    [Fact]
    public void A_configured_delay_replaces_its_own_wait_and_no_other()
    {
        using ServiceProvider provider = Provider(("Saga:StockTimeoutDelay", "00:00:07"));

        OrderFulfilmentTimeouts bound = provider.GetRequiredService<IOptions<OrderFulfilmentTimeouts>>().Value;

        bound.StockTimeoutDelay.ShouldBe(TimeSpan.FromSeconds(7));
        bound.PaymentTimeoutDelay.ShouldBe(OrderFulfilmentSaga.PaymentTimeoutDelay);
    }

    [Fact]
    public void The_container_can_satisfy_the_saga_constructor_that_takes_the_bound_delays()
    {
        // The saga has a constructor without options, which the container takes whenever it cannot satisfy the
        // other. A TimeProvider left unregistered would drop every bound delay and fail nothing above.
        using ServiceProvider provider = Provider(("Saga:StockTimeoutDelay", "00:00:07"));

        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(TimeProvider.System);
        provider.GetRequiredService<OrderFulfilmentSaga>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Saga:StockTimeoutDelay", "00:00:00")]
    [InlineData("Saga:PaymentTimeoutDelay", "-00:00:05")]
    [InlineData("Saga:DespatchTimeoutDelay", "4000.00:00:00")]
    public void A_delay_outside_its_bounds_refuses_the_host(string key, string value)
    {
        using ServiceProvider provider = Provider((key, value));

        OptionsValidationException refused = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<OrderFulfilmentTimeouts>>().Value);

        refused.Message.ShouldContain(key[(key.IndexOf(':') + 1)..]);
    }
}
