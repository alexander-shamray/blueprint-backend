using Catalog.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Common.Infrastructure.Redis;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Catalog.TestSupport;

/// <summary>The real Catalog host over caller-supplied dependencies (§12.4).</summary>
public class CatalogApiFactory(
    string connectionString,
    string rabbitConnectionString,
    string? redisCacheConnectionString = null,
    string? redisCoordinationConnectionString = null)
    : WebApplicationFactory<Program>
{
    /// <summary>The authority every host must name (§11.3); <c>.invalid</c> never resolves.</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>The Redis address a host takes when a test gives none; <c>.invalid</c> never resolves.</summary>
    /// <remarks>
    /// Startup's <c>ConfigureRedisInstrumentation</c> resolves both multiplexers, so every host dials it, and
    /// <c>AbortOnConnectFail = false</c> (§8.1) is what keeps that from failing the host.
    /// </remarks>
    public const string UnreachableRedis = "redis.invalid:6379";

    /// <summary>Supplies only §7.1's runtime connection; the host must not read <c>CatalogMigrator</c>.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Catalog", connectionString)
            .UseSetting("ConnectionStrings:RabbitMq", rabbitConnectionString)
            .UseSetting(
                $"ConnectionStrings:{RedisConnections.Cache}",
                redisCacheConnectionString ?? UnreachableRedis)
            .UseSetting(
                $"ConnectionStrings:{RedisConnections.Coordination}",
                redisCoordinationConnectionString ?? UnreachableRedis)
            .UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority)
            .ConfigureServices(services =>
            {
                ConfigureAuthentication(services);

                // Only the outbox dispatcher: MassTransit's bus is a hosted service too. Left running, the dispatcher
                // drains rows underneath assertions about them; AddHostedService<T> is what sets ImplementationType.
                ServiceDescriptor hosted = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                services.Remove(hosted);

                services.AddSingleton<OutboxDispatcher>();

                // §9.5's purge, removed by the same match, so a test that a row survives retention drives the pass.
                ServiceDescriptor purge = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(RetentionPurgeService));
                services.Remove(purge);

                services.AddSingleton<RetentionPurgeService>();

                // §9.4: added to rather than replaced, so a test cannot stage a type the real host would refuse.
                services
                    .Single(d => d.ServiceType == typeof(MessageTypeSource))
                    .ImplementationInstance
                    .ShouldBeSource()
                    .Add(typeof(AlwaysThrows).Assembly);

                // The projection handlers those events need; each layer scans itself (§6.2).
                services.AddPluggableFrom(typeof(AlwaysThrows).Assembly);
            });

    /// <summary>Swaps the JWT scheme for <see cref="TestAuthHandler"/> (§12.4); a host may override it.</summary>
    protected virtual void ConfigureAuthentication(IServiceCollection services)
    {
        services.Configure<AuthenticationOptions>(o =>
        {
            o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
            o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
        });

        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
    }
}

file static class ServiceDescriptorExtensions
{
    /// <summary>The registered instance as itself, with a message a failed cast would not give.</summary>
    public static MessageTypeSource ShouldBeSource(this object? instance) =>
        instance as MessageTypeSource ??
            throw new InvalidOperationException(
                "MessageTypeSource is no longer registered as a singleton instance, so the test " +
                "assembly's events cannot be added to it before the map is built (§9.4).");
}
