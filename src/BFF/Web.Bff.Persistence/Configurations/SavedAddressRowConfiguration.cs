using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>The <c>SavedAddresses</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class SavedAddressRowConfiguration : IEntityTypeConfiguration<SavedAddressRow>
{
    public void Configure(EntityTypeBuilder<SavedAddressRow> builder)
    {
        builder.ToTable("SavedAddresses", BffSchema.Name);

        // The customer's id is the key, so a second save replaces the first and two cannot coexist (ADR-107).
        builder.HasKey(a => a.CustomerId);
        builder.Property(a => a.CustomerId).ValueGeneratedNever();

        builder.Property(a => a.Line1).HasMaxLength(AddressLimits.Line1MaxLength);
        builder.Property(a => a.Line2).HasMaxLength(AddressLimits.Line2MaxLength);
        builder.Property(a => a.City).HasMaxLength(AddressLimits.CityMaxLength);
        builder.Property(a => a.PostalCode).HasMaxLength(AddressLimits.PostalCodeMaxLength);
        builder.Property(a => a.Country).HasMaxLength(AddressLimits.CountryLength).IsFixedLength();
        builder.Property(a => a.SavedAt);

        // A row saved before the column existed was saved under no request, which is what the empty list says.
        builder.Property(a => a.ErasedUnder).HasColumnType("nvarchar(max)").HasDefaultValueSql("N'[]'");
    }
}
