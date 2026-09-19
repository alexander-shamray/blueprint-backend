using Inventory.Infrastructure;
using Inventory.Infrastructure.Observability;
using Inventory.TestSupport;
using Inventory.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// The three aggregate queries behind §13.6's gauges, against the real table.
/// </summary>
/// <remarks>
/// The lane predicate is the subject, not a detail. §13.6 gives the two lanes
/// different thresholds precisely because they fail for different reasons — so
/// a query that dropped <c>Lane = @lane</c> would report one number for both,
/// every alert would read it, and the local lane's threshold would fire on a
/// broker blip it was designed to tolerate. Each test below stages rows on
/// both lanes for that reason: an assertion over one lane cannot fail on a
/// missing predicate.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OutboxStatsTests(ServiceFixture fixture) : IAsyncLifetime
{
    private readonly List<ServiceProvider> _providers = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider provider in _providers)
            await provider.DisposeAsync();
    }

    [Fact]
    public async Task Pending_counts_unprocessed_rows_on_the_named_lane_only()
    {
        await StageAsync(OutboxLane.Broker, OutboxLane.Broker, OutboxLane.Local);

        IOutboxStats stats = NewStats();

        stats.PendingCount(OutboxLane.Broker).ShouldBe(2);
        stats.PendingCount(OutboxLane.Local).ShouldBe(1);
    }

    [Fact]
    public async Task A_processed_row_is_not_pending()
    {
        OutboxMessage processed = await StageOneAsync(OutboxLane.Broker);
        await fixture.SetOutboxProcessedAtAsync(processed.MessageId, DateTimeOffset.UtcNow);

        NewStats().PendingCount(OutboxLane.Broker).ShouldBe(0);
    }

    [Fact]
    public async Task Abandoned_counts_only_rows_at_or_past_the_dispatchers_own_cap()
    {
        // One row below the cap and one at it. The cap is read from
        // OutboxDispatcher rather than written here, so this assertion follows
        // the loop it describes if anybody ever tunes it — which is the whole
        // reason that constant is public.
        OutboxMessage retrying = await StageOneAsync(OutboxLane.Broker);
        OutboxMessage abandoned = await StageOneAsync(OutboxLane.Broker);
        await fixture.SetOutboxAttemptsAsync(retrying.MessageId, OutboxDispatcher.MaxAttempts - 1);
        await fixture.SetOutboxAttemptsAsync(abandoned.MessageId, OutboxDispatcher.MaxAttempts);

        IOutboxStats stats = NewStats();

        stats.AbandonedCount(OutboxLane.Broker).ShouldBe(1);

        // Still pending, and that is not an oversight: an abandoned row is
        // unprocessed for ever, so the growth alert counts it exactly as §13.6
        // describes. Backing it out of the pending count would make a lane full
        // of poison read as empty.
        stats.PendingCount(OutboxLane.Broker).ShouldBe(2);
    }

    [Fact]
    public async Task An_abandoned_row_on_one_lane_is_not_counted_on_the_other()
    {
        OutboxMessage local = await StageOneAsync(OutboxLane.Local);
        await fixture.SetOutboxAttemptsAsync(local.MessageId, OutboxDispatcher.MaxAttempts);

        IOutboxStats stats = NewStats();

        stats.AbandonedCount(OutboxLane.Local).ShouldBe(1);
        stats.AbandonedCount(OutboxLane.Broker).ShouldBe(0);
    }

    [Fact]
    public async Task Oldest_age_reads_the_oldest_unprocessed_row_on_that_lane()
    {
        OutboxMessage old = await StageOneAsync(OutboxLane.Broker);
        OutboxMessage recent = await StageOneAsync(OutboxLane.Broker);
        await AgeAsync(old, TimeSpan.FromHours(2));
        await AgeAsync(recent, TimeSpan.FromMinutes(1));

        // MIN, not MAX: a lane that has stopped is diagnosed by its oldest
        // unshipped row, and reading the newest would report a healthy few
        // seconds while an hours-old message sat behind it.
        NewStats().OldestAgeSeconds(OutboxLane.Broker).ShouldBeInRange(7_000, 7_400);
    }

    [Fact]
    public async Task An_empty_lane_reads_zero_rather_than_failing()
    {
        // MIN over no rows is NULL, and a gauge callback that throws is
        // swallowed by the SDK — the series would simply stop being exported,
        // which on a dashboard is indistinguishable from a lane that is fine.
        // Zero is the honest reading for a lane with nothing waiting.
        await StageAsync(OutboxLane.Broker);

        IOutboxStats stats = NewStats();

        stats.OldestAgeSeconds(OutboxLane.Local).ShouldBe(0);
        stats.PendingCount(OutboxLane.Local).ShouldBe(0);
        stats.AbandonedCount(OutboxLane.Local).ShouldBe(0);
    }

    [Fact]
    public async Task A_processed_row_does_not_hold_the_age_gauge_up()
    {
        // The age query filters on ProcessedAt exactly as the pending count
        // does. Without that predicate a delivered row from last week would pin
        // outbox.oldest.age at days and page somebody every night.
        OutboxMessage delivered = await StageOneAsync(OutboxLane.Broker);
        await AgeAsync(delivered, TimeSpan.FromDays(7));
        await fixture.SetOutboxProcessedAtAsync(delivered.MessageId, DateTimeOffset.UtcNow);

        NewStats().OldestAgeSeconds(OutboxLane.Broker).ShouldBe(0);
    }

    /// <summary>
    /// A fresh instance per assertion, resolved through the real registration.
    /// </summary>
    /// <remarks>
    /// Fresh matters, and so does resolved. The type caches its reads and
    /// these tests change the table between them, so a shared instance would
    /// let a stale snapshot satisfy an assertion about a row written after
    /// it. Resolving rather than constructing is the other half:
    /// <c>OutboxStats</c> is internal, and asking the container for
    /// <see cref="IOutboxStats"/> proves <c>AddInventoryInfrastructure</c>
    /// wires it to the schema and connection the service actually uses — a
    /// hand-built instance would pass with that registration deleted.
    /// </remarks>
    private IOutboxStats NewStats()
    {
        ServiceProvider provider = new ServiceCollection()
            .AddInventoryInfrastructure(new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Inventory"] = fixture.ConnectionString,
                        // §12.4's .invalid convention: no host runs here, so
                        // the bus never starts and nothing should be able to
                        // dial one. AddMassTransitMessaging throws without it.
                        ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@inventory-rabbit.invalid:5672",
                        // Both read eagerly by AddRedisConnections, which
                        // throws naming the missing one — the same reason the
                        // bus key above is here, unreachable on the same
                        // §12.4 convention.
                        ["ConnectionStrings:RedisCache"] = "inventory-redis.invalid:6379",
                        ["ConnectionStrings:RedisCoordination"] = "inventory-redis.invalid:6380"
                    })
                .Build())
            .BuildServiceProvider();

        _providers.Add(provider);

        return provider.GetRequiredService<IOutboxStats>();
    }

    private async Task<OutboxMessage> StageOneAsync(OutboxLane lane)
    {
        OutboxMessage row = OutboxRows.Healthy(fixture);
        await fixture.StageOutboxAsync(row);
        await fixture.SetOutboxLaneAsync(row.MessageId, lane);

        return row;
    }

    private async Task StageAsync(params OutboxLane[] lanes)
    {
        foreach (OutboxLane lane in lanes)
            await StageOneAsync(lane);
    }

    private Task AgeAsync(OutboxMessage row, TimeSpan by) =>
        fixture.ExecuteAsync(
            "UPDATE inventory.OutboxMessages SET OccurredAt = {0} WHERE MessageId = {1};",
            DateTimeOffset.UtcNow.Subtract(by),
            row.MessageId);
}
