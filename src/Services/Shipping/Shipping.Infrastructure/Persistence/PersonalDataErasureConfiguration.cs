using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Shipping.Infrastructure.Persistence;

/// <summary>Holds a request id, a hash, a count and two times: no personal data, so no retention window
/// (§11.7).</summary>
internal sealed class PersonalDataErasureConfiguration : IEntityTypeConfiguration<PersonalDataErasure>
{
    public void Configure(EntityTypeBuilder<PersonalDataErasure> builder)
    {
        builder.ToTable("PersonalDataErasures", "shipping");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("RequestId").ValueGeneratedNever();

        // SHA-256 as lowercase hex, always 64 characters.
        builder.Property(e => e.SubjectHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(e => e.Count);
        builder.Property(e => e.ErasedAt);
        builder.Property(e => e.ClosedAt);

        // The check reads the open rows and the ones closed lately, and the hash it tries (ADR-139).
        builder.HasIndex(e => e.ClosedAt).IncludeProperties(e => e.SubjectHash);

        builder.Property(e => e.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(e => e.DomainEvents);
    }
}
