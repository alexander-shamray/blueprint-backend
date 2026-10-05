using Common.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Redis;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>Two real Redis containers (ADR-010), for §8.1's split, so role routing is assertable (§12.4).</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _cache = new RedisBuilder()
        .WithImage(ComposeImage.Of("redis-cache"))
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _coordination = new RedisBuilder()
        .WithImage(ComposeImage.Of("redis-coordination"))
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    public string CacheConnectionString => _cache.GetConnectionString();

    public string CoordinationConnectionString => _coordination.GetConnectionString();

    /// <summary>The real composition path; <paramref name="configure"/> adds to it without re-wiring it.</summary>
    public ServiceProvider BuildProvider(string applicationName, Action<IServiceCollection>? configure = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:RedisCache"] = CacheConnectionString,
            ["ConnectionStrings:RedisCoordination"] = CoordinationConnectionString
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(applicationName));
        services.AddRedisConnections(configuration);
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(
            _cache.StartAsync(TestContext.Current.CancellationToken),
            _coordination.StartAsync(TestContext.Current.CancellationToken));

    public async ValueTask DisposeAsync()
    {
        // Each teardown runs even when the other throws, so no container outlives the job.
        try
        {
            await _cache.DisposeAsync();
        }
        finally
        {
            await _coordination.DisposeAsync();
        }
    }
}
