using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>The <c>OrderLines</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class OrderLineRowConfiguration : IEntityTypeConfiguration<OrderLineRow>
{
    public void Configure(EntityTypeBuilder<OrderLineRow> builder)
    {
        builder.ToTable("OrderLines", BffSchema.Name);

        // By position, since nothing in the contracts' line types promises a product appears once (ADR-051).
        builder.HasKey(l => new { l.OrderId, l.LineNumber });
        builder.Property(l => l.LineNumber).ValueGeneratedNever();

        // Cascade, so erasing a buyer's orders takes their lines in the same statement.
        builder
            .HasOne<OrderRow>()
            .WithMany()
            .HasForeignKey(l => l.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
