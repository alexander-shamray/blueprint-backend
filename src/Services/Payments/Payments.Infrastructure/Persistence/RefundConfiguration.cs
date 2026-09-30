using Common.Contracts.Payments.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Application;
using Payments.Application.Provider;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;

namespace Payments.Infrastructure.Persistence;

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("Refunds", "payments");

        builder.HasKey(r => r.Id);
        builder
            .Property(r => r.Id)
            .HasColumnName("OrderId")
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();

        // The intent's width, since a refund carries the intent's reference.
        builder.Property(r => r.Reference).HasMaxLength(PaymentLimits.MaxReferenceLength).IsRequired();
        builder.Property(r => r.Amount).HasPrecision(PaymentAmounts.Precision, PaymentAmounts.Scale);
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(r => r.VoidedAt).IsRequired();

        // No rowversion: inserted once and never updated, under the order record's lock.
        builder.Ignore(r => r.Version);
        builder.Ignore(r => r.DomainEvents);
    }
}
