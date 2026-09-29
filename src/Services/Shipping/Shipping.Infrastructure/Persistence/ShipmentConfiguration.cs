using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

/// <summary>
/// §7.2's pattern: configuration in a class, never in attributes on the domain
/// type — which would put EF Core in <c>Shipping.Domain</c>, past the gate.
/// Found by <c>ApplyConfigurationsFromAssembly</c>.
/// </summary>
internal sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> builder)
    {
        builder.ToTable("Shipments", "shipping");
        builder.HasKey(s => s.Id);

        builder
            .Property(s => s.Id)
            .HasConversion(id => id.Value, value => new ShipmentId(value))
            .ValueGeneratedNever();

        builder
            .Property(s => s.OrderId)
            .HasConversion(id => id.Value, value => new OrderId(value));

        // One shipment per confirmed order (§3.2), and the database is where
        // that holds: a consumer redelivered past the inbox would otherwise
        // write a second shipment nobody reconciles, and the two would then
        // both be booked with the carrier.
        builder.HasIndex(s => s.OrderId).IsUnique();

        // By name, never by number (§7.2). An enum stored as an int makes the
        // member order a storage contract: inserting a status in the middle
        // silently reinterprets every existing row.
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);

        builder.Property(s => s.CarrierReference).HasMaxLength(ShipmentLimits.MaxCarrierReferenceLength);
        builder.Property(s => s.TrackingNumber).HasMaxLength(ShipmentLimits.MaxTrackingNumberLength);
        builder.Property(s => s.UnfulfillableReason).HasMaxLength(ShipmentLimits.MaxUnfulfillableReasonLength);

        // Defaulted in the database although the aggregate always sets it:
        // while a release rolls out, the version still running inserts
        // shipments without the column, and §7.4 requires every migration to
        // be backward compatible with it. The default also stamps the rows
        // that exist when the column arrives.
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("SYSDATETIMEOFFSET()");

        // The two workers' bookkeeping, mapped here because the columns are
        // this row's (spec, section 7). The claim, the backoff and the poll
        // schedule arrive with the workers that run them.
        builder.Property(s => s.Attempts);
        builder.Property(s => s.NextAttemptAt);
        builder.Property(s => s.LockedUntil);
        builder.Property(s => s.NextPollAt);

        builder.Property(s => s.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(s => s.DomainEvents);

        // A related entity rather than an owned collection: the tracking event
        // has a key of its own that the carrier chose, and an owned collection
        // would give it a synthetic one. The aggregate boundary is kept by
        // what is absent — no DbSet<TrackingEvent> on the context, and the
        // entity's constructor is internal — so the only route to an orphan is
        // the schema permitting one, which IsRequired is what refuses.
        builder
            .HasMany(s => s.TrackingEvents)
            .WithOne()
            .HasForeignKey(e => e.ShipmentId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .Navigation(s => s.TrackingEvents)
            .HasField("_trackingEvents")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
