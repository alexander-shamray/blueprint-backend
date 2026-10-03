using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Persistence;

internal sealed class ContactRecordRowConfiguration : IEntityTypeConfiguration<ContactRecordRow>
{
    public void Configure(EntityTypeBuilder<ContactRecordRow> builder)
    {
        builder.ToTable("ContactRecords", "notifications");

        // Keyed by the customer whose mailbox it is; the owner's "does not exist" and erasure delete by it (ADR-052).
        builder.HasKey(r => r.CustomerId);
        builder.Property(r => r.CustomerId).ValueGeneratedNever();

        // nvarchar at the owner's own width, so an internationalised mailbox survives whole.
        builder.Property(r => r.Email).HasMaxLength(ContactLimits.MaxEmailLength).IsRequired();

        // varchar: a language tag is ASCII by the shape the adapter holds it to before a row is written.
        builder.Property(r => r.Locale).HasMaxLength(LanguageTag.MaxLength).IsUnicode(false);

        // ContactRetention's purge, which deletes a row by the instant it was last fetched (ADR-052).
        builder.HasIndex(r => r.FetchedAt);
    }
}
