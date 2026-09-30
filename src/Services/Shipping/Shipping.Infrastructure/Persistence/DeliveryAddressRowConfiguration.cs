using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shipping.Application.Carrier;

namespace Shipping.Infrastructure.Persistence;

internal sealed class DeliveryAddressRowConfiguration : IEntityTypeConfiguration<DeliveryAddressRow>
{
    public void Configure(EntityTypeBuilder<DeliveryAddressRow> builder)
    {
        builder.ToTable("DeliveryAddresses", "shipping");

        // Keyed by the order, not the shipment: ADR-052's contact row is Ordering's fact about an order.
        builder.HasKey(r => r.OrderId);
        builder.Property(r => r.OrderId).ValueGeneratedNever();

        // nvarchar, the default here, so a Kazakh-script address survives.
        builder.Property(r => r.Line1).HasMaxLength(AddressLimits.MaxLineLength).IsRequired();
        builder.Property(r => r.Line2).HasMaxLength(AddressLimits.MaxLineLength);
        builder.Property(r => r.City).HasMaxLength(AddressLimits.MaxCityLength).IsRequired();
        builder.Property(r => r.PostalCode).HasMaxLength(AddressLimits.MaxPostalCodeLength).IsRequired();

        // char(2), not nvarchar(2): two ASCII letters by contract.
        builder.Property(r => r.Country)
            .HasMaxLength(AddressLimits.CountryLength)
            .IsFixedLength()
            .IsUnicode(false)
            .IsRequired();
    }
}
