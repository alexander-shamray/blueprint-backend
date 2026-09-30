using System.Diagnostics.Metrics;
using Catalog.Infrastructure.Messaging;
using Catalog.TestSupport;
using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Inbox;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §3.2's one Catalog Consumes cell on a real broker, since the harness replaces the callback the endpoint lives in.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class InventoryEventEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>Generous for a busy runner, and bounded because an endpoint that binds nothing never arrives.</summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>Every message id published, so <see cref="DisposeAsync"/> can drain each delivery.</summary>
    private readonly List<Guid> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>Waits for each inbox row, a delivery's last write (§9.5), so none races the next reset.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid messageId in _published)
        {
            await Eventually(
                async () => (await fixture.InboxAsync(messageId)).Count,
                expected: 1,
                because: "a delivery still running when the next test resets the schema is a flake in " +
                    "that test rather than a failure in this one");
        }
    }

    [Fact]
    public async Task A_published_level_reaches_the_projection_over_the_broker()
    {
        var product = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await PublishAsync(product, 7, messageId);

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM catalog.StockLevels WHERE ProductId = {0}",
                product),
            expected: 1,
            because: "the endpoint declared in AddMassTransitMessaging is what binds StockLevelChanged to " +
                "the projection — a consumer registered and never bound looks exactly like this until the " +
                "budget runs out");

        (await fixture.ScalarAsync<int>(
            "SELECT Value = QuantityAvailable FROM catalog.StockLevels WHERE ProductId = {0}",
            product))
            .ShouldBe(7);

        // The inbox row is the delivery's last write, so it is waited on, then read.
        await Eventually(
            async () => (await fixture.InboxAsync(messageId)).Count,
            expected: 1,
            because: "§9.5's filter records every delivery under the endpoint that took it");
        (await fixture.InboxAsync(messageId)).ShouldHaveSingleItem().Endpoint.ShouldBe(StockLevelConsumer.Queue);
    }

    [Fact]
    public async Task The_same_message_delivered_twice_leaves_one_inbox_row_and_one_level_row()
    {
        var product = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        // §9.5's filter counts a drop before it returns, so the instrument proves the duplicate was dropped.
        using SemaphoreSlim suppressed = new(0);
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == "Commerce.Messaging" &&
                instrument.Name == "messaging.inbox.suppressed")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (TagValue(tags, "message") == nameof(StockLevelChanged) &&
                TagValue(tags, "endpoint") == StockLevelConsumer.Queue)
            {
                suppressed.Release();
            }
        });

        listener.Start();

        await PublishAsync(product, 7, messageId);
        await Eventually(
            async () => (await fixture.InboxAsync(messageId)).Count,
            expected: 1,
            because: "the first delivery has to land before the second can be a redelivery of it");

        // A different level under the same MessageId, since an identical copy could not tell suppression from an
        // idempotent MERGE reapplying the row.
        await PublishAsync(product, 3, messageId);

        (await suppressed.WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the redelivery has to be counted as suppressed before the rows below can be read " +
                "as settled");

        (await fixture.InboxAsync(messageId)).Count.ShouldBe(
            1,
            "§9.5's inbox keys on the transport MessageId, which the publish pins to the contract's; a second " +
            "row means the filter never saw the first");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM catalog.StockLevels WHERE ProductId = {0}",
            product))
            .ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = QuantityAvailable FROM catalog.StockLevels WHERE ProductId = {0}",
            product))
            .ShouldBe(7, "the redelivery's level never reached the projection: the filter suppressed it " +
                "before the second row could apply");
    }

    /// <summary>Pins both transport ids to the contract's, as §9.5's inbox and §9.1 require.</summary>
    private async Task PublishAsync(Guid product, int level, Guid messageId)
    {
        StockLevelChanged message = new()
        {
            MessageId = messageId,
            CorrelationId = product,
            OccurredAt = DateTimeOffset.UtcNow,
            ProductId = product,
            QuantityAvailable = level
        };

        if (!_published.Contains(messageId))
            _published.Add(messageId);

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                message,
                c =>
                {
                    c.MessageId = message.MessageId;
                    c.CorrelationId = message.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        int last = await read();

        while (last != expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            last = await read();
        }

        last.ShouldBe(expected, because);
    }

    /// <summary>One tag off a measurement, read inside the callback because a span cannot be captured.</summary>
    private static string TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == name)
                return tag.Value?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }
}
