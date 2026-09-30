using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// Catalog's write-side context (§7.2), public because the architecture gates, not the access modifier,
/// keep it inside Infrastructure.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    /// <summary>§9.4's outbox, on this context so a row enlists in the aggregate's transaction.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>§9.5's inbox, declared so this context states its whole model.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>§8.5's durable idempotency markers, declared on the same terms.</summary>
    public DbSet<IdempotencyMarker> IdempotencyMarkers => Set<IdempotencyMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");

        // §7.2 puts mapping in these classes, never in attributes on domain types.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }

    /// <summary>§7.2's global conventions, which govern every row this context maps.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}
