using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Application;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// Maps the order-record table §6.3's <see cref="SqlPaymentOrderStore"/>
/// reads and writes: no <c>DbSet</c>, and the columns are nullable because
/// either event can arrive first (§9.4).
/// </summary>
internal sealed class PaymentOrderRowConfiguration : IEntityTypeConfiguration<PaymentOrderRow>
{
    public void Configure(EntityTypeBuilder<PaymentOrderRow> builder)
    {
        builder.ToTable("PaymentOrders", "payments");

        builder.HasKey(r => r.OrderId);
        builder.Property(r => r.OrderId).ValueGeneratedNever();

        builder.Property(r => r.TotalAmount).HasPrecision(PaymentAmounts.Precision, PaymentAmounts.Scale);

        // Three ASCII letters by contract; IsFixedLength plus IsUnicode(false)
        // is what emits char(3) rather than nvarchar(3).
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
    }
}
