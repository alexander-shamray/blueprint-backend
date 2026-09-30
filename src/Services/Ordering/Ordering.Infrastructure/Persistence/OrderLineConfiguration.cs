using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>The lines of an order, a related entity on <see cref="OrderConfiguration"/>'s argument (§7.2).</summary>
/// <remarks>No <c>DbSet</c> and an internal factory: a line is reached only through <see cref="Order"/>.</remarks>
internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("OrderLines", "ordering");
        builder.HasKey(l => l.Id);

        builder
            .Property(l => l.Id)
            .HasConversion(id => id.Value, value => new OrderLineId(value))
            .ValueGeneratedNever();

        builder
            .Property(l => l.ProductId)
            .HasConversion(id => id.Value, value => new ProductId(value));

        // Value object mapped as a complex type — columns on the same table,
        // no identity, exactly matching the domain semantics (§7.2).
        builder.ComplexProperty(
            l => l.UnitPrice,
            price =>
            {
                price.Property(m => m.Amount)
                    .HasColumnName("UnitPriceAmount")
                    .HasPrecision(OrderAmounts.Precision, OrderAmounts.Scale);
                price.Property(m => m.Currency).HasColumnName("UnitPriceCurrency").HasMaxLength(3);
            });

        // Derived on read, not stored.
        builder.Ignore(l => l.LineTotal);

        // The repository's Include seeks by the order, so the foreign key is the index that matters.
        builder.HasIndex("OrderId");
    }
}
