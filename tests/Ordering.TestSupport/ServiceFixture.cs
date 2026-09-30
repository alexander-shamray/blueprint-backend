using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Infrastructure.Persistence;
using Ordering.Migrator;
using Common.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ordering.TestSupport;

/// <summary>Ordering's names, migrator, factory and seeded orders over the shared body (ADR-056).</summary>
/// <remarks>It schedules, so its broker is §14.1's image with ADR-021's delayed exchange.</remarks>
public sealed class ServiceFixture()
    : ServiceFixture<OrderingApiFactory, Program, OrderingDbContext>("Ordering", redis: true, schedules: true)
{
    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Ordering",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    // Real Redis rather than the factory's unreachable default, because §8.5 claims a key per protected command.
    protected override OrderingApiFactory CreateFactory() =>
        new(ConnectionString, BrokerConnectionString, RedisCacheConnectionString, RedisCoordinationConnectionString);

    /// <summary>Widens <c>ordering-svc</c>'s write to publish the saga's inbound events (ADR-036).</summary>
    protected override string? HarnessWrite(string granted) =>
        "^(ordering-|inventory-commands|payments-commands|Common\\.Contracts|" +
        "Ordering\\.Infrastructure\\.Messaging:|MassTransit:)";

    /// <summary>
    /// Persists a real aggregate, so the row meets §5's invariants, with its events cleared so a seeded order
    /// stages no outbox row.
    /// </summary>
    public async Task<Guid> SeedOrderAsync(Guid customerId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        Order order = Order.Place(
            new CustomerId(customerId),
            Address.Of("1 Test Street", null, "Almaty", "050000", "KZ"),
            [(ProductId.New(), 1, Money.Of(19.99m, "EUR"))],
            "EUR",
            DateTimeOffset.UtcNow);
        order.ClearDomainEvents();

        db.Orders.Add(order);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return order.Id.Value;
    }
}
