using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;

namespace Web.Bff.Persistence;

/// <summary>ADR-051's projection schema, which the migrator applies and the host's SQL reads and writes.</summary>
public sealed class BffDbContext(DbContextOptions<BffDbContext> options) : DbContext(options)
{
    /// <summary>One row per order the projection has heard of.</summary>
    public DbSet<OrderRow> Orders => Set<OrderRow>();

    /// <summary>The lines each order was placed with.</summary>
    public DbSet<OrderLineRow> OrderLines => Set<OrderLineRow>();

    /// <summary>The product names Catalog published.</summary>
    public DbSet<ProductRow> Products => Set<ProductRow>();

    /// <summary>§9.5's inbox.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(BffSchema.Name);

        // §7.2 puts mapping in these classes, never in attributes.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BffDbContext).Assembly);
    }

    /// <summary>§7.2's global conventions, which govern every row this context maps.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}
