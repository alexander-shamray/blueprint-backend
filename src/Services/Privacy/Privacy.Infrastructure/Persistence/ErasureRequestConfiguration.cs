using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Infrastructure.Persistence;

/// <summary>The request's table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class ErasureRequestConfiguration : IEntityTypeConfiguration<ErasureRequest>
{
    public void Configure(EntityTypeBuilder<ErasureRequest> builder)
    {
        builder.ToTable("ErasureRequests", "privacy");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("RequestId").ValueGeneratedNever();

        // Personal data until the request closes, so it is nullable and indexed only while it is present.
        builder.Property(r => r.SubjectId);

        // One request carries a subject's id at a time: a second raise finds the first, and a race between two
        // loses on this index and not by writing a second request.
        builder
            .HasIndex(r => r.SubjectId)
            .IsUnique()
            .HasDatabaseName("UX_ErasureRequests_Subject")
            .HasFilter("[SubjectId] IS NOT NULL");

        // By name, never by number (§7.2).
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);

        // The set is stored as the aggregate holds it, one delimited column, which a name can never contain.
        builder
            .Property<string>("_respondersCsv")
            .HasColumnName("RespondersCsv")
            .HasMaxLength(400)
            .IsRequired();

        builder.Property(r => r.RaisedAt);
        builder.Property(r => r.DueAt);

        builder.Property(r => r.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(r => r.Responders);
        builder.Ignore(r => r.DomainEvents);
    }
}
