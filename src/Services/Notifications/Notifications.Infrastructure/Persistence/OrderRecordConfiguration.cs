using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

/// <summary>The <c>OrderRecords</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class OrderRecordConfiguration : IEntityTypeConfiguration<OrderRecord>
{
    public void Configure(EntityTypeBuilder<OrderRecord> builder)
    {
        builder.ToTable("OrderRecords", "notifications");

        // The order's own id, the commuting writers' meeting point: two first arrivals cannot both insert.
        builder.HasKey(r => r.OrderId);
        builder.Property(r => r.OrderId).ValueGeneratedNever();

        builder.Property(r => r.CancelReason).HasMaxLength(OrderRecordLimits.MaxCodeLength);
        builder.Property(r => r.CancelOrigin).HasMaxLength(OrderRecordLimits.MaxCodeLength);

        // OrderRetention's purge, by the instant this service first heard of the order.
        builder.HasIndex(r => r.RecordedAt);
    }
}
