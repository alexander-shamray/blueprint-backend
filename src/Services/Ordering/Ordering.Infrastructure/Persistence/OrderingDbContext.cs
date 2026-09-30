using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Ordering.Application;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>The write-side context (§7.2); the architecture gates, not the modifier, confine it.</summary>
public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    /// <summary>§5.4's aggregate root; <c>OrderLine</c> is reached through it, so it has no set of its own.</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>§9.4's outbox, on this context so the row enlists in the aggregate's transaction.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>§9.5's inbox; common code reaches it through <c>Set</c>, so the property states the model.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>§8.5's markers; common code reaches them through <c>Set</c>, so this states the model.</summary>
    public DbSet<IdempotencyMarker> IdempotencyMarkers => Set<IdempotencyMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ordering");

        // §7.2 maps in configuration classes, never attributes, which would put EF Core in Ordering.Domain.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderingDbContext).Assembly);

        // ADR-032's three tables, which MassTransit maps itself; a configuration of ours would be a second
        // definition of a schema the library owns (§7.2).
        modelBuilder.AddTransactionalOutboxEntities();
    }

    /// <summary>§7.2's global conventions, for every row this context maps.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(OrderAmounts.Precision, OrderAmounts.Scale);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}
