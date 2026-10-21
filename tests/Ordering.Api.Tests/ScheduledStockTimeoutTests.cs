using Common.Contracts.Ordering.V1;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>An Ordering host whose stock timeout is short enough to watch fire (§9.6, §14.1's lever).</summary>
/// <remarks>
/// Its own containers (§12.4): a second host over the shared broker would compete for the saga's queue with one
/// that waits the full five minutes, and either could take the order.
/// </remarks>
public sealed class ShortStockTimeoutFixture : ServiceFixture
{
    /// <summary>Above <see cref="OrderFulfilmentTimeouts.MinimumDelay"/>, and short against the wait below.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    protected override OrderingApiFactory CreateFactory() =>
        new ShortStockTimeoutFactory(
            ConnectionString,
            BrokerConnectionString,
            RedisCacheConnectionString,
            RedisCoordinationConnectionString);

    private sealed class ShortStockTimeoutFactory(string sql, string broker, string? cache, string? coordination)
        : OrderingApiFactory(sql, broker, cache, coordination)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting(
                $"{OrderFulfilmentTimeouts.SectionName}:{nameof(OrderFulfilmentTimeouts.StockTimeoutDelay)}",
                Delay.ToString("c"));
        }
    }
}

[CollectionDefinition(nameof(ShortStockTimeoutCollection))]
[Trait("Category", "Integration")]
public sealed class ShortStockTimeoutCollection : ICollectionFixture<ShortStockTimeoutFixture>;

/// <summary>The stock timeout as production delivers it: nothing in the test publishes the expiry.</summary>
[Collection(nameof(ShortStockTimeoutCollection))]
public sealed class ScheduledStockTimeoutTests(ShortStockTimeoutFixture fixture) : IAsyncLifetime
{
    /// <summary>The delay, then the outbox's and the delayed exchange's own latency on a loaded runner.</summary>
    private static readonly TimeSpan Budget = ShortStockTimeoutFixture.Delay + TimeSpan.FromSeconds(60);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_stock_timeout_the_saga_schedules_reaches_it_through_the_delayed_exchange()
    {
        // The saga's Schedule, the EF outbox, ADR-021's delayed exchange and the endpoint's inbox filter carry the
        // expiry; the filter refuses a message with no MessageId, so a scheduled one has to bring its own.
        Guid orderId = Guid.CreateVersion7();
        Guid placedId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, placedId);
        await Eventually(() => SagaRowsAsync(orderId), 1, "the transition into AwaitingStock arms the timeout");

        await Eventually(
            () => SagaRowsAsync(orderId),
            0,
            "the scheduled StockReservationExpired cancels the order and finalises the instance");

        // Waited for, since the filter writes its row after the consumer returns (§9.5).
        await Eventually(
            async () => (await fixture.InboxAsync())
                .Count(r => r.Endpoint == DependencyInjection.FulfilmentSagaQueue && r.MessageId != placedId),
            1,
            "the expiry passed the endpoint's inbox filter under a MessageId of its own");
    }

    private Task<int> SagaRowsAsync(Guid orderId) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId);

    private async Task PublishPlacedAsync(Guid orderId, Guid messageId)
    {
        OrderPlaced placed = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId,
            CustomerId = Guid.CreateVersion7(),
            TotalAmount = 19.99m,
            Currency = "EUR",
            Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 19.99m)]
        };

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                placed,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = placed.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Budget;
        int actual = await read();

        while (actual != expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            actual = await read();
        }

        actual.ShouldBe(expected, because);
    }
}
