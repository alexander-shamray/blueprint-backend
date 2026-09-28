using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shipping.Application.Carrier;

namespace Shipping.Infrastructure.Persistence;

internal sealed class DeliveryAddressRowConfiguration : IEntityTypeConfiguration<DeliveryAddressRow>
{
    public void Configure(EntityTypeBuilder<DeliveryAddressRow> builder)
    {
        builder.ToTable("DeliveryAddresses", "shipping");

        // Keyed by the order and not by the shipment: the address is Ordering's
        // fact about an order, and the two are one-to-one only because §3.2
        // gives one shipment per confirmed order (ADR-052).
        builder.HasKey(r => r.OrderId);
        builder.Property(r => r.OrderId).ValueGeneratedNever();

        // Every free-text column is nvarchar, which is the default here and
        // is the whole of why a Kazakh-script address survives (spec,
        // section 7).
        builder.Property(r => r.Line1).HasMaxLength(AddressLimits.MaxLineLength).IsRequired();
        builder.Property(r => r.Line2).HasMaxLength(AddressLimits.MaxLineLength);
        builder.Property(r => r.City).HasMaxLength(AddressLimits.MaxCityLength).IsRequired();
        builder.Property(r => r.PostalCode).HasMaxLength(AddressLimits.MaxPostalCodeLength).IsRequired();

        // Two ASCII letters by contract; IsFixedLength plus IsUnicode(false)
        // is what emits char(2) rather than nvarchar(2).
        builder.Property(r => r.Country).HasMaxLength(2).IsFixedLength().IsUnicode(false).IsRequired();
    }
}
