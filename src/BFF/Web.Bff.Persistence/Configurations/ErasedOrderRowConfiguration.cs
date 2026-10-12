using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>The <c>ErasedOrders</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class ErasedOrderRowConfiguration : IEntityTypeConfiguration<ErasedOrderRow>
{
    public void Configure(EntityTypeBuilder<ErasedOrderRow> builder)
    {
        builder.ToTable("ErasedOrders", BffSchema.Name);

        // No foreign key to Orders: the row it names is the one just deleted (ADR-098).
        builder.HasKey(e => e.OrderId);
        builder.Property(e => e.OrderId).ValueGeneratedNever();
        builder.Property(e => e.ErasedAt);
    }
}
