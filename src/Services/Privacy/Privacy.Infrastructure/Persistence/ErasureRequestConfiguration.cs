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

        // One request carries a subject's id at a time. The repository's per-subject lock serialises a race, and
        // this index is the backstop behind it (ADR-092).
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
            .HasMaxLength(ErasureRequest.MaxRespondersLength)
            .IsRequired();

        builder.Property(r => r.RaisedAt);
        builder.Property(r => r.DueAt);
        builder.Property(r => r.ClosedAt);
        builder.Property(r => r.OverdueAt);
        builder.Property(r => r.Reissues);

        // SHA-256 as lowercase hex, always 64 characters; set when the request closes and its id goes.
        builder.Property(r => r.SubjectHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);

        // The sweep's population: open requests by the time they fall due, so a pass reads nothing else.
        builder
            .HasIndex(r => r.DueAt)
            .HasDatabaseName("IX_ErasureRequests_Open")
            .HasFilter("[Status] = 'Open'");

        builder.Property(r => r.Version).HasColumnName("RowVersion").IsRowVersion();

        // Owned, since an answer means nothing outside its request and is read and written only through it.
        builder.OwnsMany(
            r => r.Completions,
            completion =>
            {
                completion.ToTable("ErasureCompletions", "privacy");
                completion.WithOwner().HasForeignKey("RequestId");
                completion.HasKey("RequestId", nameof(ErasureCompletion.Responder));
                completion.Property(c => c.Responder).HasMaxLength(ErasureRequest.MaxResponderLength);
                completion.Property(c => c.Count);
                completion.Property(c => c.Counted);
                completion.Property(c => c.ReceivedAt);
            });

        builder
            .Navigation(r => r.Completions)
            .HasField("_completions")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(r => r.Responders);
        builder.Ignore(r => r.Missing);
        builder.Ignore(r => r.DomainEvents);
    }
}
