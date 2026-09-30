using Ordering.Infrastructure;
using Ordering.Infrastructure.Observability;
using Ordering.TestSupport;
using Ordering.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>The three aggregate queries behind §13.6's gauges, against the real table.</summary>
/// <remarks>The lane predicate is the subject, since §13.6 sets the two lanes' thresholds far apart.</remarks>
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
        // The cap is read from OutboxDispatcher, so this follows the loop if anybody tunes it.
        OutboxMessage retrying = await StageOneAsync(OutboxLane.Broker);
        OutboxMessage abandoned = await StageOneAsync(OutboxLane.Broker);
        await fixture.SetOutboxAttemptsAsync(retrying.MessageId, OutboxDispatcher.MaxAttempts - 1);
        await fixture.SetOutboxAttemptsAsync(abandoned.MessageId, OutboxDispatcher.MaxAttempts);

        IOutboxStats stats = NewStats();

        stats.AbandonedCount(OutboxLane.Broker).ShouldBe(1);

        // Still pending: an abandoned row is unprocessed for ever, and §13.6's growth alert counts it.
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

        // MIN, not MAX: a stopped lane is diagnosed by its oldest unshipped row.
        NewStats().OldestAgeSeconds(OutboxLane.Broker).ShouldBeInRange(7_000, 7_400);
    }

    [Fact]
    public async Task An_empty_lane_reads_zero_rather_than_failing()
    {
        // MIN over no rows is NULL, and a throwing gauge callback silently stops the series, so zero is the reading.
        await StageAsync(OutboxLane.Broker);

        IOutboxStats stats = NewStats();

        stats.OldestAgeSeconds(OutboxLane.Local).ShouldBe(0);
        stats.PendingCount(OutboxLane.Local).ShouldBe(0);
        stats.AbandonedCount(OutboxLane.Local).ShouldBe(0);
    }

    [Fact]
    public async Task A_processed_row_does_not_hold_the_age_gauge_up()
    {
        // Filtered on ProcessedAt, or a delivered row from last week would pin outbox.oldest.age at days.
        OutboxMessage delivered = await StageOneAsync(OutboxLane.Broker);
        await AgeAsync(delivered, TimeSpan.FromDays(7));
        await fixture.SetOutboxProcessedAtAsync(delivered.MessageId, DateTimeOffset.UtcNow);

        NewStats().OldestAgeSeconds(OutboxLane.Broker).ShouldBe(0);
    }

    /// <summary>A fresh instance per read, since the type caches, resolved through the real registration.</summary>
    private IOutboxStats NewStats()
    {
        ServiceProvider provider = new ServiceCollection()
            .AddOrderingInfrastructure(new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Ordering"] = fixture.ConnectionString,
                        // AddMassTransitMessaging throws without it; unreachable (§12.4), since no bus starts here.
                        ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@ordering-rabbit.invalid:5672",
                        // Both read eagerly by AddRedisConnections; unreachable on the same convention.
                        ["ConnectionStrings:RedisCache"] = "ordering-redis.invalid:6379",
                        ["ConnectionStrings:RedisCoordination"] = "ordering-redis.invalid:6380"
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
            "UPDATE ordering.OutboxMessages SET OccurredAt = {0} WHERE MessageId = {1};",
            DateTimeOffset.UtcNow.Subtract(by),
            row.MessageId);
}
