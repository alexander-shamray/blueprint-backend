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
/// The async path behind §3.2's one Catalog Consumes cell: an Inventory event
/// published on a real broker, consumed by the real receive endpoint, applied
/// by the projection, and read back from <c>catalog.StockLevels</c>.
/// </summary>
/// <remarks>
/// The real transport rather than the harness, because the harness removes the
/// thing under test: <c>AddMassTransitTestHarness</c> replaces the
/// <c>UsingRabbitMq</c> configuration wholesale, and the receive endpoint, its
/// retry policy and its inbox filter all live inside that callback. The
/// projection's statement is proved without a broker elsewhere in this
/// project; this suite proves the binding. Catalog publishes Inventory's
/// event to itself, and the topology is the same either way: MassTransit
/// routes on the message type, so the exchange this reaches is the one
/// Inventory publishes to.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class InventoryEventEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// How long a published message is given to reach the table. Generous
    /// because it covers a broker round trip on a runner holding other
    /// container sets, and bounded because the failure this suite exists to
    /// catch — an endpoint that binds nothing — never arrives late, it never
    /// arrives.
    /// </summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every message id this test published, so <see cref="DisposeAsync"/>
    /// can wait for each delivery to finish before the next test truncates.
    /// </summary>
    private readonly List<Guid> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>
    /// Drains the deliveries this test started: §9.5's filter commits the
    /// inbox row after the consumer returns, so it is the last write of a
    /// delivery, and a test that returned on the projection's row alone
    /// could leave that write racing the next test's reset.
    /// </summary>
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

        // The inbox row is the delivery's last write, so it can trail the
        // projection's row by a moment; waited on, then read.
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

        await PublishAsync(product, 7, messageId);
        await Eventually(
            async () => (await fixture.InboxAsync(messageId)).Count,
            expected: 1,
            because: "the first delivery has to land before the second can be a redelivery of it");

        // A different level and a later OccurredAt (PublishAsync stamps
        // UtcNow) under the same MessageId: an identical copy could not tell
        // suppression before the projection runs from an idempotent MERGE
        // that simply reapplies the same row.
        await PublishAsync(product, 3, messageId);

        // Held past the first sighting, because the claim is about a second
        // row: an assertion that stops at the first would pass whether or not
        // another was on its way.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

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

    /// <summary>
    /// Both transport headers pinned to the contract's, as §9.5's inbox keys
    /// on <c>ConsumeContext.MessageId</c> and §9.1 keeps one correlation
    /// across body, row and transport: left to MassTransit, the transport
    /// would mint a second id per publish and a redelivery could never be
    /// recognised as one.
    /// </summary>
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
}
