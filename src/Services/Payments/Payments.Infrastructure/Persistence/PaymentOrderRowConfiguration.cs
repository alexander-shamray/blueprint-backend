using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Application;

namespace Payments.Infrastructure.Persistence;

/// <summary><see cref="SqlPaymentOrderStore"/>'s table, nullable since either event can come first (§9.4).</summary>
internal sealed class PaymentOrderRowConfiguration : IEntityTypeConfiguration<PaymentOrderRow>
{
    public void Configure(EntityTypeBuilder<PaymentOrderRow> builder)
    {
        builder.ToTable("PaymentOrders", "payments");

        builder.HasKey(r => r.OrderId);
        builder.Property(r => r.OrderId).ValueGeneratedNever();

        builder.Property(r => r.TotalAmount).HasPrecision(PaymentAmounts.Precision, PaymentAmounts.Scale);

        // char(3), not nvarchar(3): three ASCII letters by contract.
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
    }
}
