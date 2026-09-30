using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§6.6's local price projection, mapped here only so that <c>migrations add</c> emits its table.</summary>
/// <remarks>Read and written by Dapper, never EF, so there is no <c>DbSet</c>; the column names are §6.6's.</remarks>
internal sealed class ProductPriceConfiguration : IEntityTypeConfiguration<ProductPrice>
{
    public void Configure(EntityTypeBuilder<ProductPrice> builder)
    {
        builder.ToTable("ProductPrices", "ordering");

        // One row per product per currency, which the reader's WHERE clause seeks on.
        builder.HasKey(p => new { p.ProductId, p.Currency });

        // The types §6.6's DDL prints: char(3), as a currency code is three ASCII letters (§7.4).
        builder
            .Property(p => p.Currency)
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(p => p.Amount).HasPrecision(OrderAmounts.Precision, OrderAmounts.Scale);

        // §6.6's default.
        builder.Property(p => p.IsAvailable).HasDefaultValue(true);
    }
}

/// <summary>A row of the price table: a cache of Catalog's prices, so not part of this domain model.</summary>
internal sealed class ProductPrice
{
    public Guid ProductId { get; set; }
    public string Currency { get; set; } = null!;
    public decimal Amount { get; set; }
    public bool IsAvailable { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
