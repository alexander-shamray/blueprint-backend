using Microsoft.EntityFrameworkCore;
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

        builder.Property(e => e.CarrierEventId).HasMaxLength(ShipmentLimits.MaxCarrierEventIdLength);

        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
    }
}
