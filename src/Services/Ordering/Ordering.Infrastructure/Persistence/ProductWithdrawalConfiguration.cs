using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>When Catalog last withdrew a product, at product level, for §6.6's price projection.</summary>
/// <remarks>
/// <c>ProductDiscontinued</c> carries no currency, so a withdrawal must survive having no price row to reach;
/// a watermark, not a flag, so a later republish re-lists the product (§6.6).
/// </remarks>
internal sealed class ProductWithdrawalConfiguration : IEntityTypeConfiguration<ProductWithdrawal>
{
    public void Configure(EntityTypeBuilder<ProductWithdrawal> builder)
    {
        builder.ToTable("ProductWithdrawals", "ordering");

        // The product alone: this covers the currencies the price table has never seen.
        builder.HasKey(w => w.ProductId);
    }
}

/// <summary>A row of the table: the newest <c>OccurredAt</c> of any <c>ProductDiscontinued</c> seen.</summary>
internal sealed class ProductWithdrawal
{
    public Guid ProductId { get; set; }
    public DateTimeOffset WithdrawnAt { get; set; }
}
