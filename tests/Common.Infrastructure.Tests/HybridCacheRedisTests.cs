using Common.Infrastructure.Redis;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§8.2's stack through the real registration, asserted against the server rather than the code.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class HybridCacheRedisTests(RedisFixture fixture)
{
    [Fact]
    public async Task Two_reads_execute_the_factory_once()
    {
        await using ServiceProvider provider = fixture.BuildProvider("hits");
        HybridCache cache = provider.GetRequiredService<HybridCache>();
        int executions = 0;

        for (int i = 0; i < 2; i++)
        {
            await cache.GetOrCreateAsync(
                "product:1:v1",
                (object?)null,
                (_, _) =>
                {
                    executions++;
                    return ValueTask.FromResult("priced");
                },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        executions.ShouldBe(1);
    }

    [Fact]
    public async Task The_stored_key_carries_the_cache_prefix_and_a_TTL()
    {
        await using ServiceProvider provider = fixture.BuildProvider("prefixed");
        HybridCache cache = provider.GetRequiredService<HybridCache>();

        await cache.GetOrCreateAsync(
            "product:2:v1",
            (object?)null,
            (_, _) => ValueTask.FromResult("priced"),
            cancellationToken: TestContext.Current.CancellationToken);

        IConnectionMultiplexer redis =
            provider.GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Cache);
        RedisKey[] keys =
            [.. redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: "prefixed:cache:*")];

        keys.ShouldNotBeEmpty("the §8.3 prefix comes from InstanceName, and its absence " +
            "is a key outside the keyspace whose eviction policy was §8.1's whole argument");
        foreach (RedisKey key in keys)
        {
            redis.GetDatabase().KeyTimeToLive(key).ShouldNotBeNull(
                "every cache key has a TTL — §8.1's first enforced rule");
        }
    }

    [Fact]
    public async Task Removing_a_tag_invalidates_the_entry()
    {
        // One instance only: §8.2 bounds cross-instance staleness by the L1 expiry instead.
        await using ServiceProvider provider = fixture.BuildProvider("tags");
        HybridCache cache = provider.GetRequiredService<HybridCache>();
        int executions = 0;

        async ValueTask<string> ReadAsync() =>
            await cache.GetOrCreateAsync(
                "product:3:v1",
                (object?)null,
                (_, _) =>
                {
                    executions++;
                    return ValueTask.FromResult("priced");
                },
                tags: ["product:3"],
                cancellationToken: TestContext.Current.CancellationToken);

        await ReadAsync();
        await cache.RemoveByTagAsync("product:3", TestContext.Current.CancellationToken);
        await ReadAsync();

        executions.ShouldBe(2);
    }

    [Fact]
    public async Task A_cache_operation_produces_a_Redis_client_span()
    {
        ExportedActivities exported = new();

        // Only the exporter is added; AddRedisConnections registering the instrumentation is under test (§13.2).
        await using ServiceProvider provider = fixture.BuildProvider(
            "traced",
            services => services
                .AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(exported)));

        // No host builds the TracerProvider here, so the test resolves it as startup would.
        provider.GetRequiredService<TracerProvider>();

        HybridCache cache = provider.GetRequiredService<HybridCache>();
        await cache.GetOrCreateAsync(
            "product:4:v1",
            (object?)null,
            (_, _) => ValueTask.FromResult("priced"),
            cancellationToken: TestContext.Current.CancellationToken);

        // The instrumentation drains profiling sessions on a timer, so the span arrives later.
        for (int attempt = 0; attempt < 150 && exported.Count == 0; attempt++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        exported.ShouldNotBeEmpty("AddRedisConnections registers the Redis instrumentation " +
            "and hands it both keyed connections — §13.2");
    }

    [Fact]
    public async Task A_lock_operation_produces_a_span_from_the_coordination_connection()
    {
        ExportedActivities exported = new();

        await using ServiceProvider provider = fixture.BuildProvider(
            "traced-lock",
            services => services
                .AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(exported)));

        provider.GetRequiredService<TracerProvider>();

        // No cache operation happens here, so any span is the coordination multiplexer's.
        IDistributedLockFactory factory = provider.GetRequiredService<IDistributedLockFactory>();
        IDistributedLock? held = await factory.TryAcquireAsync(
            "traced",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        await held!.DisposeAsync();

        for (int attempt = 0; attempt < 150 && exported.Count == 0; attempt++)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        exported.ShouldNotBeEmpty("the instrumentation is handed BOTH keyed connections — " +
            "the coordination one is what a cache-only test cannot see (§13.2)");
    }
}
