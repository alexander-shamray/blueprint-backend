using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>The lock's guards and call shapes against a substituted Redis.</summary>
public sealed class DistributedLockTests
{
    private readonly IConnectionMultiplexer _redis = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase _database = Substitute.For<IDatabase>();

    private IDistributedLockFactory Factory()
    {
        _redis.GetDatabase().Returns(_database);

        // The real registration, then the substitute keyed in after it, since the last keyed registration wins.
        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment("catalog"));
        services.AddRedisConnections(AddRedisConnectionsTests.Configuration());
        services.AddKeyedSingleton(RedisConnections.Coordination, _redis);

        return services.BuildServiceProvider().GetRequiredService<IDistributedLockFactory>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_lock_without_a_positive_TTL_is_refused_before_any_IO(int seconds)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            Factory().TryAcquireAsync("reprice", TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken));

        _redis.DidNotReceive().GetDatabase();
    }

    [Fact]
    public async Task Contention_returns_null_rather_than_throwing()
    {
        _database.StringSetAsync(default, default, null, When.Always).ReturnsForAnyArgs(false);

        IDistributedLock? held = await Factory().TryAcquireAsync(
            "reprice",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        held.ShouldBeNull();
    }

    [Fact]
    public async Task Acquisition_writes_the_lock_key_with_NX_and_the_TTL()
    {
        _database.StringSetAsync(default, default, null, When.Always).ReturnsForAnyArgs(true);

        IDistributedLock? held = await Factory().TryAcquireAsync(
            "reprice",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        held.ShouldNotBeNull();
        held.Name.ShouldBe("reprice");
        await _database.Received(1).StringSetAsync(
            "catalog:lock:reprice",
            Arg.Any<RedisValue>(),
            TimeSpan.FromSeconds(30),
            When.NotExists);
    }

    [Fact]
    public async Task A_failed_release_puts_the_handle_back_for_retry()
    {
        _database.StringSetAsync(default, default, null, When.Always).ReturnsForAnyArgs(true);
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(
                _ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"),
                _ => Task.FromResult(RedisResult.Create((RedisValue)1)));

        IDistributedLock held = (await Factory().TryAcquireAsync(
            "reprice",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken))!;

        // A transient failure propagates without consuming the handle, so a later release still runs.
        await Should.ThrowAsync<RedisConnectionException>(async () => await held.DisposeAsync());
        await held.DisposeAsync();

        await _database.Received(2).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Concurrent_disposals_share_one_in_flight_release()
    {
        _database.StringSetAsync(default, default, null, When.Always).ReturnsForAnyArgs(true);
        TaskCompletionSource<RedisResult> inFlight = new();
        _database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(inFlight.Task);

        IDistributedLock held = (await Factory().TryAcquireAsync(
            "reprice",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken))!;

        ValueTask first = held.DisposeAsync();
        ValueTask second = held.DisposeAsync();

        // Neither caller may report success while the delete is on the wire.
        first.IsCompleted.ShouldBeFalse();
        second.IsCompleted.ShouldBeFalse();

        inFlight.SetResult(RedisResult.Create((RedisValue)1));
        await first;
        await second;

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Dispose_releases_once_and_only_once()
    {
        _database.StringSetAsync(default, default, null, When.Always).ReturnsForAnyArgs(true);

        IDistributedLock held = (await Factory().TryAcquireAsync(
            "reprice",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken))!;
        await held.DisposeAsync();
        await held.DisposeAsync();

        await _database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }
}
