using Shipping.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Shipping.Infrastructure.Messaging.DependencyInjection;

namespace Shipping.Worker.Tests;

/// <summary>A second host the fault tests start takes none of the fixture's deliveries.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SecondHostTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Queues = [MessagingRegistration.EventsQueue, MessagingRegistration.PrivacyQueue];

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_second_host_takes_no_delivery_from_the_fixture_s_queues()
    {
        // A consumer it held would take every other delivery out of the fixture's sight; its bus connects late, so
        // the count is watched for a while rather than read once.
        await Eventually(async () => await ConsumersAsync() == Queues.Length);
        using ShippingWorkerFactory second = fixture.NewWorkerHost(ShippingWorkerFactory.UnreachableCarrier);
        _ = second.Services;

        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < until)
        {
            (await ConsumersAsync()).ShouldBe(Queues.Length, "one consumer a queue, the fixture's own");
            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }
    }

    private async Task<int> ConsumersAsync() => (await Task.WhenAll(Queues.Select(fixture.ConsumersAsync))).Sum();

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the fixture's own consumers never attached");
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
    }
}
