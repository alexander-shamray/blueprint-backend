using Common.Application;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

/// <summary>§8.1's two connections, §8.2's cache stack and the coordination helpers, in one call (§8.2).</summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddRedisConnections(IConfiguration configuration)
        {
            // Read eagerly, so a host missing its connection string does not start.
            string cacheConnection = RequiredConnectionString(configuration, RedisConnections.Cache);
            string coordinationConnection = RequiredConnectionString(configuration, RedisConnections.Coordination);

            services.AddKeyedSingleton<IConnectionMultiplexer>(
                RedisConnections.Cache,
                (_, _) => Connect(cacheConnection));
            services.AddKeyedSingleton<IConnectionMultiplexer>(
                RedisConnections.Coordination,
                (_, _) => Connect(coordinationConnection));

            services.AddSingleton<RedisKeys>();
            services.AddSingleton<IDistributedLockFactory, RedisDistributedLockFactory>();

            // §8.5's store, on the coordination connection like the lock factory beside it.
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();

            // The cache connection; the factory hands the cache its keyed multiplexer, so that one is traced.
            services.AddStackExchangeRedisCache(_ => { });
            services
                .AddOptions<RedisCacheOptions>()
                .Configure<IServiceProvider>((options, provider) =>
                {
                    // §8.1's key prefix, spelled once, in RedisKeys (§8.3).
                    options.InstanceName = provider.GetRequiredService<RedisKeys>().CacheInstanceName;
                    options.ConnectionMultiplexerFactory = () =>
                        Task.FromResult(
                            provider.GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Cache));
                });

            services.AddHybridCache(options =>
            {
                options.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromMinutes(10),            // L2, Redis
                    LocalCacheExpiration = TimeSpan.FromMinutes(1)    // L1, in-process
                };
                options.MaximumPayloadBytes = 1024 * 1024;
            });

            // Here, not in Common.Web (§13.2): the parameterless overload finds no keyed connection.
            services
                .AddOpenTelemetry()
                .WithTracing(tracing => tracing
                    .AddRedisInstrumentation()
                    .ConfigureRedisInstrumentation((provider, instrumentation) =>
                    {
                        instrumentation.AddConnection(
                            provider.GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Cache));
                        instrumentation.AddConnection(
                            provider.GetRequiredKeyedService<IConnectionMultiplexer>(RedisConnections.Coordination));
                    }));

            return services;
        }
    }

    private static string RequiredConnectionString(IConfiguration configuration, string name)
    {
        // Whitespace too: an empty environment variable configures an empty string.
        string? connectionString = configuration.GetConnectionString(name);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException(
                $"ConnectionStrings:{name} is not configured — §8.1 needs both Redis connections.")
            : connectionString;
    }

    private static ConnectionMultiplexer Connect(string connectionString)
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);

        // Degrade, don't die (§8.1): coordination callers still fail closed, since their operations throw.
        options.AbortOnConnectFail = false;

        return ConnectionMultiplexer.Connect(options);
    }
}
