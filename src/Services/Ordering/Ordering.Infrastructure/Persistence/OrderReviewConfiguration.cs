using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§9.6's escalation table: a work queue, not a log, so resolving a row deletes it.</summary>
/// <remarks>Mapped by EF for the migration, written by Dapper in the command's transaction (§6.3).</remarks>
internal sealed class OrderReviewConfiguration : IEntityTypeConfiguration<OrderReview>
{
    public void Configure(EntityTypeBuilder<OrderReview> builder)
    {
        builder.ToTable("OrderReviews", "ordering");

        // One outstanding review per order per reason, so a redelivery is absorbed by the key (§9.6).
        builder.HasKey(r => new { r.OrderId, r.Reason });

        builder
            .Property(r => r.Reason)
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsRequired();

        // §13.6's alert on outstanding reviews is a range scan over this column.
        builder
            .HasIndex(r => r.RaisedAt)
            .HasDatabaseName("IX_OrderReviews_RaisedAt");
    }
}

/// <summary>A row of the review table: an operations fact, so not a domain type (§9.6).</summary>
internal sealed class OrderReview
{
    public Guid OrderId { get; set; }
    public string Reason { get; set; } = null!;
    public DateTimeOffset RaisedAt { get; set; }
}
