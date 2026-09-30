using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

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

        // One shipment per confirmed order, held here: a redelivery past the inbox would book a second.
        builder.HasIndex(s => s.OrderId).IsUnique();

        // §7.2: an enum a reader of the database should be able to name.
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);

        builder.Property(s => s.CarrierReference).HasMaxLength(ShipmentLimits.MaxCarrierReferenceLength);
        builder.Property(s => s.TrackingNumber).HasMaxLength(ShipmentLimits.MaxTrackingNumberLength);
        builder.Property(s => s.UnfulfillableReason).HasMaxLength(ShipmentLimits.MaxUnfulfillableReasonLength);

        // Defaulted although the aggregate sets it: the version still running inserts without it (§7.4). On rows
        // already there it can only let a wait run longer, where a guess too old would end a live shipment.
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("SYSDATETIMEOFFSET()");

        // The two workers' bookkeeping, mapped here because the columns are this row's.
        builder.Property(s => s.Attempts);
        builder.Property(s => s.NextAttemptAt);
        builder.Property(s => s.LockedUntil);
        builder.Property(s => s.NextPollAt);

        // Defaulted for CreatedAt's reason (§7.4).
        builder.Property(s => s.PollAttempts).HasDefaultValue(0);

        // One filtered index per claim, since the table is never purged (ADR-054); each claim repeats its filter so
        // the optimiser matches it, and a filter cannot say OR, so booked rows with no cancellation are held too.
        builder
            .HasIndex(s => s.NextAttemptAt)
            .HasDatabaseName("IX_Shipments_FulfilmentClaim")
            .HasFilter("[Status] IN ('Pending', 'Booked') AND [CancellationRefusedAt] IS NULL")
            .IncludeProperties(s => new { s.Status, s.CancellationRequestedAt, s.LockedUntil });

        builder
            .HasIndex(s => s.NextPollAt)
            .HasDatabaseName("IX_Shipments_TrackingClaim")
            .HasFilter("[NextPollAt] IS NOT NULL")
            .IncludeProperties(s => new { s.Status, s.LockedUntil });

        builder.Property(s => s.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(s => s.DomainEvents);

        // Related rather than owned, since an owned collection would replace the carrier's key with a synthetic
        // one; the boundary is kept by the absent DbSet and the internal constructor, and IsRequired bars an orphan.
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
