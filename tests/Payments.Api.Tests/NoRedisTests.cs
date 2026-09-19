using Common.Infrastructure.Messaging;
using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// §2: Payments reaches neither Redis instance. It takes no §8.5 key, because
/// it has no HTTP write command, and caches nothing.
/// </summary>
public sealed class NoRedisTests
{
    [Fact]
    public void The_host_starts_with_no_redis_key_and_registers_no_connection()
    {
        using PaymentsApiFactory factory = new(
            "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://payments-svc:x@rabbit.invalid:5672");

        IServiceProvider services = factory.Services;

        // By name, not by a package reference: a test proving the service has no
        // Redis should not be the thing that gives its project one. The type is
        // still loadable, because Common.Infrastructure carries the package.
        Type multiplexer = Type.GetType("StackExchange.Redis.IConnectionMultiplexer, StackExchange.Redis")
            ?? throw new InvalidOperationException("StackExchange.Redis did not load; the assertions below would prove nothing.");
        IKeyedServiceProvider keyed = (IKeyedServiceProvider)services;

        services.GetService(multiplexer).ShouldBeNull();
        keyed.GetKeyedService(multiplexer, RedisConnections.Cache).ShouldBeNull();
        keyed.GetKeyedService(multiplexer, RedisConnections.Coordination).ShouldBeNull();

        // RetentionPurgeService (Common.Infrastructure) resolves
        // IIdempotencyStore unconditionally for ADR-039's marker purge, so
        // resolving it here proves NoClaimsIdempotencyStore satisfies that
        // dependency without Redis — ValidateOnBuild would otherwise have
        // thrown before `factory.Services` above ever returned.
        services.GetRequiredService<RetentionPurgeService>().ShouldNotBeNull();
    }
}
