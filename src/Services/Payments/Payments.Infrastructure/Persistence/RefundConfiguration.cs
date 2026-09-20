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

        // The intent's width, from the same constant: a refund carries the
        // intent's reference, so the two columns cannot be allowed to differ.
        builder.Property(r => r.Reference).HasMaxLength(PaymentLimits.MaxReferenceLength).IsRequired();
        builder.Property(r => r.Amount).HasPrecision(PaymentAmounts.Precision, PaymentAmounts.Scale);
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(r => r.VoidedAt).IsRequired();

        // No rowversion: one refund per order, inserted once, never updated,
        // and the order record's lock serialises the insert (spec, section 6).
        builder.Ignore(r => r.Version);
        builder.Ignore(r => r.DomainEvents);
    }
}
