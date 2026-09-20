using Common.Contracts.Payments.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payments.Application;
using Payments.Application.Provider;
using Payments.Domain.Intents;
using Payments.Domain.Orders;

namespace Payments.Infrastructure.Persistence;

internal sealed class PaymentIntentConfiguration : IEntityTypeConfiguration<PaymentIntent>
{
    public void Configure(EntityTypeBuilder<PaymentIntent> builder)
    {
        builder.ToTable("PaymentIntents", "payments");

        builder.HasKey(i => i.Id);
        builder
            .Property(i => i.Id)
            .HasColumnName("OrderId")
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();

        // §7.2: an enum a reader of the database should be able to name.
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(16);

        builder.Property(i => i.Amount).HasPrecision(PaymentAmounts.Precision, PaymentAmounts.Scale);
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);

        // The published width and this service's own, both enforced by the
        // adapter before a verdict exists, so nothing these columns refuse
        // can reach them.
        builder.Property(i => i.Reference).HasMaxLength(PaymentLimits.MaxReferenceLength);
        builder.Property(i => i.DeclineReason).HasMaxLength(ProviderLimits.MaxReasonLength);

        builder.Property(i => i.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(i => i.DomainEvents);
    }
}
