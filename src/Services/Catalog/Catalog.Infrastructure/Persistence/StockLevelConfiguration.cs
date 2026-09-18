using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// §3.2's one Catalog projection: Inventory's level per product, with the
/// watermark the contract's remark asks for. Mapped so the migration is
/// generated; read only by Dapper.
/// </summary>
/// <remarks>
/// Mapped by EF, read by Dapper, written by Dapper — §7.4's terms for a read
/// model, as <c>ordering.ProductPrices</c> is mapped. There is no
/// <c>DbSet</c> for it, deliberately: nothing loads a level through EF, and a
/// set would invite a write path that bypasses the projection's idempotent
/// <c>MERGE</c>. <c>ApplyConfigurationsFromAssembly</c> finds this class
/// regardless.
/// </remarks>
internal sealed class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        builder.ToTable("StockLevels", "catalog");
        builder.HasKey(s => s.ProductId);
        builder.Property(s => s.ProductId).ValueGeneratedNever();
        builder.Property(s => s.QuantityAvailable).IsRequired();
        builder.Property(s => s.AsOf).IsRequired();
    }
}

/// <summary>
/// The read model's row: a level, not a delta, and the instant Inventory
/// stamped it, which is the projection's out-of-order guard.
/// </summary>
internal sealed class StockLevel
{
    public Guid ProductId { get; set; }
    public int QuantityAvailable { get; set; }
    public DateTimeOffset AsOf { get; set; }
}
