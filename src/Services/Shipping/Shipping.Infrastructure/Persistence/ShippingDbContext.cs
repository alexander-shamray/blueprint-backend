using Common.Domain;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using Common.Infrastructure.Tracing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

/// <summary>The write-side context (§7.2); the architecture gates, not the modifier, confine it.</summary>
public sealed class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    /// <summary>§9.4's outbox, on this context so the row enlists in the aggregate's transaction.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>§9.5's inbox; common code reaches it through <c>Set</c>, so the property states the model.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>§8.5's markers; common code reaches them through <c>Set</c>, so this states the model.</summary>
    public DbSet<IdempotencyMarker> IdempotencyMarkers => Set<IdempotencyMarker>();

    /// <summary>§3.2's aggregate; no <c>DbSet</c> of tracking events, which are reached only through it.</summary>
    public DbSet<Shipment> Shipments => Set<Shipment>();

    /// <summary>§11.7's audit rows: a hash, a count and a time per request, holding no personal data.</summary>
    public DbSet<PersonalDataErasure> PersonalDataErasures => Set<PersonalDataErasure>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("shipping");

        // §7.2 maps in configuration classes, never attributes, which would put EF Core in Shipping.Domain.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ShippingDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampFulfilmentTraces();

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    // cancellationToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampFulfilmentTraces();

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>§7.2's global conventions, for every row this context maps.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }

    // The writes that hand the fulfilment worker a pass, a recorded shipment and a requested cancellation, keep the
    // trace they ran in, so the booking and what it publishes join it (§9.4). The worker's own writes do not, save
    // the keep of a booking the carrier would not take back, whose retried cancel so joins the pass that booked it.
    private void StampFulfilmentTraces()
    {
        foreach (EntityEntry<Shipment> entry in ChangeTracker.Entries<Shipment>())
        {
            if (entry.State == EntityState.Added ||
                (entry.State == EntityState.Modified && entry.Property(s => s.CancellationRequestedAt).IsModified))
            {
                StagedTraceColumns.Stamp(entry);
            }
        }
    }
}
