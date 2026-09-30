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

        // The key makes a repeated page free, and the shipment leads it because every read is per shipment.
        builder.HasKey(e => new { e.ShipmentId, e.CarrierEventId });

        builder
            .Property(e => e.ShipmentId)
            .HasConversion(id => id.Value, value => new ShipmentId(value));

        // Case-sensitive in the engine and the change tracker alike, as the aggregate compares it: either alone
        // would fail the commit of a page holding two ids that differ only by case.
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
