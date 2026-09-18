using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence;

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("StockItems", "inventory");

        builder.HasKey(s => s.Id);

        builder
            .Property(s => s.Id)
            .HasColumnName("ProductId")
            .HasConversion(id => id.Value, value => new ProductId(value))
            .ValueGeneratedNever();

        builder.Property(s => s.Available).IsRequired();
        builder.Property(s => s.Reserved).IsRequired();
        builder.Property(s => s.UpdatedAt).IsRequired();

        // §7.3: the admin path is optimistic; the reservation path bypasses
        // this column by statement and never loads the entity.
        builder.Property(s => s.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(s => s.DomainEvents);
    }
}
