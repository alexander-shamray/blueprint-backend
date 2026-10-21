using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

        // Nullable, as a record from before the column has no instant to bound the erasure check by (ADR-139).
        builder.Property(r => r.SourcedAt);

        builder.Property(r => r.CancelReason).HasMaxLength(OrderRecordLimits.MaxCodeLength);
        builder.Property(r => r.CancelOrigin).HasMaxLength(OrderRecordLimits.MaxCodeLength);

        // Request ids, kept as Ordering keeps them (ADR-099): a JSON column, as no row is looked up by them (ADR-101).
        builder
            .Property(r => r.ErasedUnder)
            .HasConversion(
                ids => JsonSerializer.Serialize(ids, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<Guid>>(json, (JsonSerializerOptions?)null)!,
                new ValueComparer<IReadOnlyList<Guid>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (h, id) => HashCode.Combine(h, id)),
                    v => v.ToList()))
            .HasColumnType("nvarchar(max)")
            .HasDefaultValueSql("N'[]'");

        // OrderRetention's purge, by the instant this service first heard of the order.
        builder.HasIndex(r => r.RecordedAt);

        // The erasure's held read seeks its subject here, so its range lock is the subject's keys and the gap to the
        // next, not every customer's records.
        builder.HasIndex(r => r.CustomerId);
    }
}
