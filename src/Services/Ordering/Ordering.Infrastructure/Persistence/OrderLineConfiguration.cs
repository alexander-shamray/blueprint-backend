using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// The lines of an order. A separate configuration because <c>OrderLine</c> is
/// mapped as a related entity rather than an owned collection —
/// <see cref="OrderConfiguration"/> carries the argument.
/// </summary>
/// <remarks>
/// Mapping it does not make it reachable. There is no <c>DbSet&lt;OrderLine&gt;</c>
/// on the context and <c>OrderLine.For</c> is internal to the domain assembly,
/// so a line is still created and loaded only through <see cref="Order"/> —
/// which is what §5.4's aggregate boundary asks for. The boundary is a rule
/// about reachability, not about which EF mapping construct expresses it.
/// </remarks>
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

        // LineTotal is UnitPrice * Quantity, derived on read: a stored copy is
        // a second source of truth that a quantity change can leave behind.
        builder.Ignore(l => l.LineTotal);

        // The one query that reads lines is the repository's Include, which
        // seeks by the order — so the foreign key is the index that matters.
        builder.HasIndex("OrderId");
    }
}
