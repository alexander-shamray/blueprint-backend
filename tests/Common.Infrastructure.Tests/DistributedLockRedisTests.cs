using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>The lock against a real server: NX, expiry, and the token check on release.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class DistributedLockRedisTests(RedisFixture fixture)
{
    [Fact]
    public async Task A_held_lock_cannot_be_acquired_again()
    {
        // Disposed, since a discarded provider keeps its multiplexers alive to process exit.
        await using ServiceProvider provider = fixture.BuildProvider("locks");
        IDistributedLockFactory factory = provider.GetRequiredService<IDistributedLockFactory>();

        await using IDistributedLock? first =
            await factory.TryAcquireAsync("contend", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        IDistributedLock? second =
            await factory.TryAcquireAsync("contend", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task Release_frees_the_lock_for_the_next_holder()
    {
        await using ServiceProvider provider = fixture.BuildProvider("locks");
        IDistributedLockFactory factory = provider.GetRequiredService<IDistributedLockFactory>();

        IDistributedLock? first =
            await factory.TryAcquireAsync("handover", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await first!.DisposeAsync();

        await using IDistributedLock? second =
            await factory.TryAcquireAsync("handover", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task Expiry_frees_the_lock_without_a_release()
    {
        await using ServiceProvider provider = fixture.BuildProvider("locks");
        IDistributedLockFactory factory = provider.GetRequiredService<IDistributedLockFactory>();

        IDistributedLock? first = await factory.TryAcquireAsync(
            "expire",
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);
        first.ShouldNotBeNull();

        IDistributedLock? second = await WaitForAcquireAsync(factory, "expire");
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_stale_handle_must_not_release_the_next_holders_lock()
    {
        await using ServiceProvider provider = fixture.BuildProvider("locks");
        IDistributedLockFactory factory = provider.GetRequiredService<IDistributedLockFactory>();

        IDistributedLock? stale = await factory.TryAcquireAsync(
            "stolen",
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);
        stale.ShouldNotBeNull();

        // The key expires; another holder takes the lock with a new token.
        IDistributedLock? current = await WaitForAcquireAsync(factory, "stolen");
        current.ShouldNotBeNull();

        // The stale token no longer matches: this must delete nothing.
        await stale.DisposeAsync();

        IDistributedLock? intruder =
            await factory.TryAcquireAsync("stolen", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        intruder.ShouldBeNull();
    }

    [Fact]
    public async Task The_release_script_runs_under_the_documented_ACL_grant()
    {
        // §8.1's per-service user, created live: EVAL is @scripting, which no data category includes.
        ConfigurationOptions admin = ConfigurationOptions.Parse(fixture.CoordinationConnectionString);
        admin.AllowAdmin = true;
        await using ConnectionMultiplexer adminConnection = await ConnectionMultiplexer.ConnectAsync(admin);
        object[] grant =
        [
            "SETUSER",
            "acl-svc",
            "reset",
            "on",
            ">s3cret",
            "~acl:*",
            "+@read",
            "+@write",
            "+@keyspace",
            "+@connection",
            "+eval",
            "-@dangerous",
            "+client|setname",
            "+client|setinfo"
        ];
        await adminConnection.GetServer(adminConnection.GetEndPoints()[0]).ExecuteAsync("ACL", grant);

        ConfigurationOptions restricted = ConfigurationOptions.Parse(fixture.CoordinationConnectionString);
        restricted.User = "acl-svc";
        restricted.Password = "s3cret";
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(restricted);

        // The real factory over the restricted connection, by keyed override.
        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment("acl"));
        services.AddRedisConnections(AddRedisConnectionsTests.Configuration());
        services.AddKeyedSingleton<IConnectionMultiplexer>(RedisConnections.Coordination, connection);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IDistributedLockFactory factory = provider.GetRequiredService<IDistributedLockFactory>();

        IDistributedLock? held = await factory.TryAcquireAsync(
            "guarded",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        held.ShouldNotBeNull();
        await held.DisposeAsync();

        // Re-acquisition proves the EVAL ran; otherwise NX refuses this for the 30 s TTL.
        IDistributedLock? reacquired = await factory.TryAcquireAsync(
            "guarded",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        reacquired.ShouldNotBeNull();
    }

    /// <summary>Polls for about 5 s, far past the 200 ms TTLs, so a slow expiry cannot fail the test.</summary>
    private static async Task<IDistributedLock?> WaitForAcquireAsync(IDistributedLockFactory factory, string name)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            IDistributedLock? held =
                await factory.TryAcquireAsync(name, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (held is not null)
                return held;

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
