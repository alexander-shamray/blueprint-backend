using Ordering.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Common.Infrastructure.Redis;
using Common.Web;
using MassTransit.EntityFrameworkCoreIntegration;
using Ordering.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ordering.TestSupport;

/// <summary>The real Ordering host over caller-supplied dependencies (§12.4).</summary>
public class OrderingApiFactory(
    string connectionString,
    string rabbitConnectionString,
    string? redisCacheConnectionString = null,
    string? redisCoordinationConnectionString = null)
    : WebApplicationFactory<Program>
{
    /// <summary>The authority every host must name (§11.3); <c>.invalid</c> never resolves.</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>The Redis address a host names when a test gives none; <c>.invalid</c> never resolves.</summary>
    /// <remarks>
    /// Every host still resolves both connections at startup for Redis tracing, and survives the failed connect
    /// because §8.1's <c>AbortOnConnectFail = false</c> makes it non-fatal.
    /// </remarks>
    public const string UnreachableRedis = "redis.invalid:6379";

    /// <summary>Supplies only §7.1's runtime connection; the host must not read <c>OrderingMigrator</c>.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Ordering", connectionString)
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

                // One by one, not every hosted service: MassTransit's bus is one too. Left running, the dispatcher
                // drains rows underneath assertions about them; AddHostedService<T> is what sets ImplementationType.
                ServiceDescriptor hosted = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                services.Remove(hosted);

                // Still resolvable directly, so tests can drive one pass.
                services.AddSingleton<OutboxDispatcher>();

                // §9.5's purge, removed by the same match, so a test that a row survives retention drives the pass.
                ServiceDescriptor purge = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(RetentionPurgeService));
                services.Remove(purge);

                services.AddSingleton<RetentionPurgeService>();

                // ADR-032's inbox cleanup, removed on the same terms: a writer, on a timer no test drives, to a table
                // this suite asserts over.
                ServiceDescriptor inboxCleanup = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(InboxCleanupService<OrderingDbContext>));
                services.Remove(inboxCleanup);

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
    /// <remarks>Forbid falls back to the challenge scheme, so a 403 is <see cref="TestAuthHandler"/>'s own.</remarks>
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
