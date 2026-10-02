using Common.Application;
using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§8.5's store against a real server: atomicity, TTLs and holding, which a double cannot test.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class RedisIdempotencyStoreTests(RedisFixture fixture)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

    /// <summary>Short enough for a claim to lapse inside a test, which the shipped retention never does.</summary>
    private static readonly TimeSpan Brief = TimeSpan.FromSeconds(1);

    /// <summary>A token no claim minted, ill-formed so it cannot read as one this test took.</summary>
    private const string Foreign = "not-a-claim-this-test-holds";

    [Fact]
    public async Task A_claimed_key_cannot_be_claimed_again()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string? first = await store.TryClaimAsync("contend", Retention, TestContext.Current.CancellationToken);
        string? second = await store.TryClaimAsync("contend", Retention, TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task A_fresh_claim_reads_back_as_in_progress_and_carries_no_payload()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await store.TryClaimAsync("in-flight", Retention, TestContext.Current.CancellationToken);
        IdempotencyEntry? entry = await store.GetAsync("in-flight", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeTrue();
        entry.Payload.ShouldBeNull();
    }

    [Fact]
    public async Task A_completed_key_reads_back_the_payload_and_is_no_longer_in_progress()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = await ClaimedAsync(store, "done", Retention);
        await store.CompleteAsync(
            "done", claim, "\"0195e4b2\"", TestContext.Current.CancellationToken);

        IdempotencyEntry? entry = await store.GetAsync("done", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeFalse();
        entry.Payload.ShouldBe("\"0195e4b2\"");
    }

    [Fact]
    public async Task The_void_payload_is_told_apart_from_the_in_progress_marker()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = await ClaimedAsync(store, "void", Retention);
        await store.CompleteAsync("void", claim, "null", TestContext.Current.CancellationToken);

        IdempotencyEntry? entry = await store.GetAsync("void", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeFalse("a stored \"null\" is an outcome, not a missing one");
        entry.Payload.ShouldBe("null");
    }

    [Fact]
    public async Task A_released_key_is_claimable_again()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = await ClaimedAsync(store, "released", Retention);
        await store.ReleaseAsync("released", claim, TestContext.Current.CancellationToken);

        (await store.GetAsync("released", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await store.TryClaimAsync("released", Retention, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task An_unknown_key_reads_back_as_nothing()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        (await store.GetAsync("never-claimed", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Both_writes_carry_the_service_prefixed_idem_namespace()
    {
        // Read raw, because the port hides the prefix §8.1's ACL key pattern matches.
        await using ServiceProvider provider = fixture.BuildProvider("prefixed");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        string claim = await ClaimedAsync(store, "claimed", Retention);
        (await database.KeyExistsAsync("prefixed:idem:claimed")).ShouldBeTrue();

        await store.CompleteAsync("claimed", claim, "null", TestContext.Current.CancellationToken);

        // The encoding both scripts compare on, so changing it changes what a running release can read.
        (await database.StringGetAsync("prefixed:idem:claimed")).ToString().ShouldBe($"{claim}:null");
    }

    [Fact]
    public async Task The_claim_is_written_to_the_coordination_server_and_not_the_cache_one()
    {
        // §8.1's split, which is why the fixture runs two servers.
        await using ServiceProvider provider = fixture.BuildProvider("routed");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await store.TryClaimAsync("routing", Retention, TestContext.Current.CancellationToken);

        IDatabase cache = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Cache)
            .GetDatabase();

        (await cache.KeyExistsAsync("routed:idem:routing")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_claim_carries_the_retention_as_a_time_to_live()
    {
        await using ServiceProvider provider = fixture.BuildProvider("ttl");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        string claim = await ClaimedAsync(store, "expiring", Retention);

        TimeSpan? claimTtl = await database.KeyTimeToLiveAsync("ttl:idem:expiring");
        claimTtl.ShouldNotBeNull();
        claimTtl.Value.ShouldBeLessThanOrEqualTo(Retention);

    }

    [Fact]
    public async Task An_outcome_inherits_what_the_claim_had_left_rather_than_a_fresh_retention()
    {
        // ADR-038. Expired down first, so an inherited TTL and a re-armed one differ.
        await using ServiceProvider provider = fixture.BuildProvider("keepttl");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        string claim = await ClaimedAsync(store, "inherited", Retention);

        // A margin against this test's runtime: far below Retention, far above a completion.
        await database.KeyExpireAsync("keepttl:idem:inherited", TimeSpan.FromSeconds(30));

        await store.CompleteAsync("inherited", claim, "null", TestContext.Current.CancellationToken);

        TimeSpan? completedTtl = await database.KeyTimeToLiveAsync("keepttl:idem:inherited");

        completedTtl.ShouldNotBeNull("the outcome must still expire — KEEPTTL keeps a TTL, not none");
        completedTtl.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        completedTtl.Value.ShouldBeLessThanOrEqualTo(
            TimeSpan.FromSeconds(30),
            "a re-armed entry would carry minutes; an inherited one carries what was left");

        // Readable, so the TTL above is a write's rather than an untouched claim's.
        IdempotencyEntry? entry = await store.GetAsync("inherited", TestContext.Current.CancellationToken);
        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeFalse();
        entry.Payload.ShouldBe("null");
    }

    [Fact]
    public async Task Releasing_a_key_that_is_not_held_is_not_an_error()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await Should.NotThrowAsync(
            () => store.ReleaseAsync("never-held", Foreign, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_claim_that_outlived_its_retention_cannot_complete_over_its_successors()
    {
        await using ServiceProvider provider = fixture.BuildProvider("stale");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string stale = await ClaimedAsync(store, "outlived", Brief);
        string successor = await WaitForClaimAsync(store, "outlived");

        successor.ShouldNotBe(stale);

        await store.CompleteAsync(
            "outlived", stale, "\"clobbered\"", TestContext.Current.CancellationToken);

        IdempotencyEntry? entry = await store.GetAsync("outlived", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeTrue("the successor's claim is still in flight");
        entry.Payload.ShouldBeNull("a lost claim must not record an outcome over a live one");
    }

    [Fact]
    public async Task A_claim_that_outlived_its_retention_cannot_release_its_successors()
    {
        await using ServiceProvider provider = fixture.BuildProvider("stale");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string stale = await ClaimedAsync(store, "freed", Brief);
        await WaitForClaimAsync(store, "freed");

        await store.ReleaseAsync("freed", stale, TestContext.Current.CancellationToken);

        (await store.GetAsync("freed", TestContext.Current.CancellationToken))
            .ShouldNotBeNull("the successor still holds this key");
        (await store.TryClaimAsync("freed", Retention, TestContext.Current.CancellationToken))
            .ShouldBeNull("a freed key would let a third attempt in");
    }

    [Fact]
    public async Task A_pre_token_claim_reads_back_as_in_progress()
    {
        await using ServiceProvider provider = fixture.BuildProvider("legacy");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        await database.StringSetAsync("legacy:idem:untokened", "in-progress", Retention);

        IdempotencyEntry? entry = await store.GetAsync("untokened", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeTrue();
        entry.Payload.ShouldBeNull();
    }

    [Theory]
    [InlineData("legacy-void", "null")]
    [InlineData("legacy-value", "\"0195e4b2-0000-7000-8000-0000000000ff\"")]
    public async Task A_pre_token_outcome_reads_as_completed_not_in_flight(string key, string payload)
    {
        // Untokened outcomes of the void and value shapes, which a shape test alone cannot tell from a claim.
        await using ServiceProvider provider = fixture.BuildProvider("legacy");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();
        IDatabase database = provider
            .GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination)
            .GetDatabase();

        await database.StringSetAsync($"legacy:idem:{key}", payload, Retention);

        IdempotencyEntry? entry = await store.GetAsync(key, TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.InProgress.ShouldBeFalse("a completed pre-token entry is a recorded outcome, not in flight");
        entry.Payload.ShouldBe(payload);
    }

    [Fact]
    public async Task The_stores_scripts_run_under_the_documented_ACL_grant()
    {
        // §8.1's grant: EVAL is @scripting, which none of the data categories include.
        ConfigurationOptions admin = ConfigurationOptions.Parse(fixture.CoordinationConnectionString);
        admin.AllowAdmin = true;
        await using ConnectionMultiplexer adminConnection = await ConnectionMultiplexer.ConnectAsync(admin);
        object[] grant =
        [
            "SETUSER",
            "aclidem-svc",
            "reset",
            "on",
            ">s3cret",
            "~aclidem:*",
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
        restricted.User = "aclidem-svc";
        restricted.Password = "s3cret";
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(restricted);

        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment("aclidem"));

        // The store logs a lost claim and a failed release, so this bare collection needs a logger.
        services.AddLogging();
        services.AddRedisConnections(AddRedisConnectionsTests.Configuration());
        services.AddKeyedSingleton<IConnectionMultiplexer>(RedisConnections.Coordination, connection);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        // Reading the payload back proves the script ran, not merely that nothing threw.
        string completed = await ClaimedAsync(store, "acl-done", Retention);
        await store.CompleteAsync(
            "acl-done", completed, "\"ok\"", TestContext.Current.CancellationToken);

        IdempotencyEntry? entry = await store.GetAsync("acl-done", TestContext.Current.CancellationToken);
        entry.ShouldNotBeNull();
        entry.Payload.ShouldBe("\"ok\"");

        // ReleaseAsync logs rather than throws, so only the re-claim shows the script ran.
        string released = await ClaimedAsync(store, "acl-freed", Retention);
        await store.ReleaseAsync("acl-freed", released, TestContext.Current.CancellationToken);

        (await store.TryClaimAsync("acl-freed", Retention, TestContext.Current.CancellationToken))
            .ShouldNotBeNull("a release that never ran would hold the key for its whole retention");
    }

    [Fact]
    public async Task A_key_this_store_never_saw_is_unheld()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync(["never-claimed"], TestContext.Current.CancellationToken);

        unheld.ShouldBe(["never-claimed"]);
    }

    [Fact]
    public async Task A_live_claim_is_held()
    {
        // ADR-039: while the claim is held, the purge keeps the marker behind it.
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await ClaimedAsync(store, "unheld-live", Retention);

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync(["unheld-live"], TestContext.Current.CancellationToken);

        unheld.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_recorded_outcome_is_held_for_what_the_claim_had_left()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = await ClaimedAsync(store, "unheld-complete", Retention);
        await store.CompleteAsync("unheld-complete", claim, "42", TestContext.Current.CancellationToken);

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync(["unheld-complete"], TestContext.Current.CancellationToken);

        unheld.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_released_claim_is_unheld()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        string claim = await ClaimedAsync(store, "unheld-released", Retention);
        await store.ReleaseAsync("unheld-released", claim, TestContext.Current.CancellationToken);

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync(["unheld-released"], TestContext.Current.CancellationToken);

        unheld.ShouldBe(["unheld-released"]);
    }

    [Fact]
    public async Task An_expired_claim_becomes_unheld_without_anybody_deleting_it()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await ClaimedAsync(store, "unheld-expiring", Brief);

        // Thrown at the deadline rather than asserted past, so a slow runner does not read as a broken store.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

        while (true)
        {
            IReadOnlyCollection<string> unheld =
                await store.UnheldAsync(["unheld-expiring"], TestContext.Current.CancellationToken);

            if (unheld.Count == 1)
            {
                unheld.ShouldBe(["unheld-expiring"], "the claim's own TTL is what frees the key");
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "'unheld-expiring' was still held 30 seconds after a one-second claim. The " +
                    "store answered, so this is the machine rather than the contract — a Redis " +
                    "container starved of CPU expires keys late.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_mixed_set_is_answered_key_by_key()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        await ClaimedAsync(store, "mixed-held-one", Retention);
        await ClaimedAsync(store, "mixed-held-two", Retention);

        IReadOnlyCollection<string> unheld = await store.UnheldAsync(
            ["mixed-gone-one", "mixed-held-one", "mixed-gone-two", "mixed-held-two"],
            TestContext.Current.CancellationToken);

        unheld.ShouldBe(["mixed-gone-one", "mixed-gone-two"], ignoreOrder: true);
    }

    [Fact]
    public async Task No_keys_is_no_question()
    {
        await using ServiceProvider provider = fixture.BuildProvider("idem");
        IIdempotencyStore store = provider.GetRequiredService<IIdempotencyStore>();

        IReadOnlyCollection<string> unheld =
            await store.UnheldAsync([], TestContext.Current.CancellationToken);

        unheld.ShouldBeEmpty();
    }

    /// <summary>Claims <paramref name="key"/> under <see cref="Retention"/> once the previous claim lapses.</summary>
    private static async Task<string> WaitForClaimAsync(IIdempotencyStore store, string key)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTimeOffset.UtcNow < deadline)
        {
            string? claim = await store.TryClaimAsync(key, Retention, TestContext.Current.CancellationToken);

            if (claim is not null)
                return claim;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The claim on '{key}' had not expired after 15 seconds.");
    }

    /// <summary>A claim the caller needs, failing at the claim rather than at a later null dereference.</summary>
    private static async Task<string> ClaimedAsync(IIdempotencyStore store, string key, TimeSpan retention)
    {
        string? claim = await store.TryClaimAsync(key, retention, TestContext.Current.CancellationToken);

        if (claim is null)
        {
            throw new InvalidOperationException(
                $"'{key}' could not be claimed, so this test never reached its subject. " +
                $"The key was already held — a leftover from an earlier run against a reused server.");
        }

        return claim;
    }
}
