using Common.Infrastructure.Redis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>The registration, read off the collection, since the keyed multiplexers connect on first resolve.</summary>
public sealed class AddRedisConnectionsTests
{
    internal static IConfiguration Configuration(
        string? cache = "localhost:6379",
        string? coordination = "localhost:6380")
    {
        Dictionary<string, string?> settings = new();
        if (cache is not null)
            settings["ConnectionStrings:RedisCache"] = cache;
        if (coordination is not null)
            settings["ConnectionStrings:RedisCoordination"] = coordination;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceCollection Registered()
    {
        ServiceCollection services = new();
        services.AddRedisConnections(Configuration());
        return services;
    }

    [Fact]
    public void Both_connections_register_as_keyed_singletons()
    {
        ServiceCollection services = Registered();

        services.ShouldContain(d =>
            d.ServiceType == typeof(IConnectionMultiplexer) &&
            Equals(d.ServiceKey, RedisConnections.Cache) &&
            d.Lifetime == ServiceLifetime.Singleton);
        services.ShouldContain(d =>
            d.ServiceType == typeof(IConnectionMultiplexer) &&
            Equals(d.ServiceKey, RedisConnections.Coordination) &&
            d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void The_cache_stack_and_the_helpers_are_registered()
    {
        ServiceCollection services = Registered();

        services.ShouldContain(d => d.ServiceType == typeof(HybridCache));
        services.ShouldContain(d => d.ServiceType == typeof(IDistributedCache));
        services.ShouldContain(d => d.ServiceType == typeof(RedisKeys));
        services.ShouldContain(d => d.ServiceType == typeof(IDistributedLockFactory));
    }

    [Theory]
    [InlineData(null, "localhost:6380", "ConnectionStrings:RedisCache")]
    [InlineData("", "localhost:6380", "ConnectionStrings:RedisCache")]
    [InlineData("localhost:6379", null, "ConnectionStrings:RedisCoordination")]
    [InlineData("localhost:6379", "   ", "ConnectionStrings:RedisCoordination")]
    public void A_missing_connection_string_fails_at_registration_naming_the_key(
        string? cache,
        string? coordination,
        string expected)
    {
        ServiceCollection services = new();

        InvalidOperationException thrown = Should.Throw<InvalidOperationException>(() =>
            services.AddRedisConnections(Configuration(cache, coordination)));

        thrown.Message.ShouldContain(expected);
    }
}
