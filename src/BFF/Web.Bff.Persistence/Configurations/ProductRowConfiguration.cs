using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>The <c>Products</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class ProductRowConfiguration : IEntityTypeConfiguration<ProductRow>
{
    public void Configure(EntityTypeBuilder<ProductRow> builder)
    {
        builder.ToTable("Products", BffSchema.Name);

        builder.HasKey(p => p.ProductId);
        builder.Property(p => p.ProductId).ValueGeneratedNever();

        builder.Property(p => p.Name).HasMaxLength(ProjectionLimits.ProductNameMaxLength);
    }
}
