using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§3.2's one Catalog projection, mapped so the migration is generated and read only by Dapper.</summary>
/// <remarks>No <c>DbSet</c> (§7.4), which would invite a write path around the projection's <c>MERGE</c>.</remarks>
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

/// <summary>A level, not a delta, with Inventory's stamp as the projection's out-of-order guard.</summary>
internal sealed class StockLevel
{
    public Guid ProductId { get; set; }
    public int QuantityAvailable { get; set; }
    public DateTimeOffset AsOf { get; set; }
}
