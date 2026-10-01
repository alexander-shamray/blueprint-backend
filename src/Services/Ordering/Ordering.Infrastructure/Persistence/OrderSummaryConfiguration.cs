using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§6.6's order summary table, mapped here only so that <c>migrations add</c> emits it.</summary>
/// <remarks>Read and written by Dapper, never EF, so there is no <c>DbSet</c>; the columns are §6.6's DDL.</remarks>
internal sealed class OrderSummaryConfiguration : IEntityTypeConfiguration<OrderSummary>
{
    public void Configure(EntityTypeBuilder<OrderSummary> builder)
    {
        builder.ToTable("OrderSummaries", "ordering");
        builder.HasKey(s => s.OrderId);

        builder.Property(s => s.Status).HasMaxLength(32).IsUnicode(false);
        builder.Property(s => s.TotalAmount).HasPrecision(OrderAmounts.Precision, OrderAmounts.Scale);
        builder
            .Property(s => s.Currency)
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false);
        builder.Property(s => s.CancelReason).HasMaxLength(32).IsUnicode(false);

        // A JSON array of one id per line, so §7.2's 400 cap is no bound on it. The model's MaxLength is cleared
        // too, or the migration would carry the convention's 400 beside nvarchar(max).
        builder
            .Property(s => s.Products)
            .HasColumnType("nvarchar(max)")
            .Metadata
            .SetMaxLength(null);

        // §13.3's counted-once flags: a business counter is not idempotent, so its having fired is state.
        builder.Property(s => s.PlacedCounted).HasDefaultValue(false);
        builder.Property(s => s.CancelledCounted).HasDefaultValue(false);
        builder.Property(s => s.FulfilmentCounted).HasDefaultValue(false);

        builder
            .HasIndex(s => new { s.CustomerId, s.PlacedAt }, "IX_OrderSummaries_Customer_PlacedAt")
            .IsDescending(false, true)
            .IncludeProperties(s => new { s.Status, s.TotalAmount, s.Currency, s.LineCount });
    }
}

/// <summary>A row of the table; the columns the placement writes stay null until it lands (§6.6).</summary>
internal sealed class OrderSummary
{
    public Guid OrderId { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CustomerId { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? Currency { get; set; }
    public int? LineCount { get; set; }
    public string? Products { get; set; }
    public DateTimeOffset? PlacedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? CancelReason { get; set; }
    public bool PlacedCounted { get; set; }
    public bool CancelledCounted { get; set; }
    public bool FulfilmentCounted { get; set; }
}
