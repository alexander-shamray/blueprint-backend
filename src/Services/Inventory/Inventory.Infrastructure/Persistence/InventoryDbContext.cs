using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>The write-side context (§7.2); the architecture gates, not the modifier, confine it.</summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();

    /// <summary>One row per order (§3.2).</summary>
    public DbSet<Reservation> Reservations => Set<Reservation>();

    /// <summary>§9.4's outbox, on this context so the row enlists in the aggregate's transaction.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>§9.5's inbox; common code reaches it through <c>Set</c>, so the property states the model.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>§8.5's markers; common code reaches them through <c>Set</c>, so this states the model.</summary>
    public DbSet<IdempotencyMarker> IdempotencyMarkers => Set<IdempotencyMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");

        // §7.2 maps in configuration classes, never attributes, which would put EF Core in Inventory.Domain.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
    }

    /// <summary>§7.2's global conventions, for every row this context maps.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}
