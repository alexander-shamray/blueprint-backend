using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Preferences;

namespace Notifications.Infrastructure.Persistence;

internal sealed class ConsentEntryRowConfiguration : IEntityTypeConfiguration<ConsentEntryRow>
{
    public void Configure(EntityTypeBuilder<ConsentEntryRow> builder)
    {
        builder.ToTable("ConsentEntries", "notifications");

        builder.HasKey(r => r.EntryId);
        builder.Property(r => r.EntryId).ValueGeneratedNever();

        // varchar throughout: the vocabulary and the azp claim are printable ASCII by the validators.
        builder.Property(r => r.Topic).HasMaxLength(ConsentVocabulary.MaxTopicLength).IsUnicode(false);
        builder.Property(r => r.Channel).HasMaxLength(ConsentVocabulary.MaxChannelLength).IsUnicode(false);
        builder.Property(r => r.Client).HasMaxLength(ConsentVocabulary.MaxClientLength).IsUnicode(false);

        // The pair's range an append holds and the erasure's by subject (ADR-120), and every read of a subject's.
        // Named, as EF keys an unnamed index by its columns and the second key below shares these.
        builder.HasIndex(r => new { r.SubjectId, r.Topic, r.Channel }, "IX_ConsentEntries_SubjectPair");

        // ADR-120's two keys: an entry is superseded at most once, and a subject's pair has one first entry.
        builder
            .HasIndex(r => r.SupersedesEntryId, "UX_ConsentEntries_Supersedes")
            .IsUnique()
            .HasFilter("[SupersedesEntryId] IS NOT NULL");
        builder
            .HasIndex(r => new { r.SubjectId, r.Topic, r.Channel }, "UX_ConsentEntries_FirstEntry")
            .IsUnique()
            .HasFilter("[SupersedesEntryId] IS NULL AND [SubjectId] IS NOT NULL");
    }
}
