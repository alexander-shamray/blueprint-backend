using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

internal sealed class TrackingEventConfiguration : IEntityTypeConfiguration<TrackingEvent>
{
    public void Configure(EntityTypeBuilder<TrackingEvent> builder)
    {
        builder.ToTable("TrackingEvents", "shipping");

        // The carrier's own id, under the shipment's. The key is what makes a
        // repeated page free (spec, section 5), and the shipment leads it
        // because every read is per shipment.
        builder.HasKey(e => new { e.ShipmentId, e.CarrierEventId });

        builder
            .Property(e => e.ShipmentId)
            .HasConversion(id => id.Value, value => new ShipmentId(value));

        // Case-sensitive at both ends, as the aggregate compares it: this is
        // half a key rather than text. The binary collation is the inbox
        // endpoint's reason, SQL Server's default folding case; the ordinal
        // comparer is the change tracker's, which on this provider folds case
        // too. Either alone fails the commit of a page holding two ids that
        // differ only by case; trailing spaces, which the engine ignores, the
        // aggregate already treats as one id.
        builder
            .Property(e => e.CarrierEventId)
            .HasMaxLength(ShipmentLimits.MaxCarrierEventIdLength)
            .UseCollation("Latin1_General_BIN2")
            .Metadata.SetValueComparer(new ValueComparer<string>(
                (left, right) => string.Equals(left, right, StringComparison.Ordinal),
                value => StringComparer.Ordinal.GetHashCode(value),
                value => value));

        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
    }
}
