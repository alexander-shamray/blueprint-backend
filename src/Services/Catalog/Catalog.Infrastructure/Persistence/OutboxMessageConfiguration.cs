using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§9.4's table, mapped here because the schema is this service's and the scan looks here.</summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", "catalog");
        builder.HasKey(m => m.Id);

        // Drawn at random rather than an identity, so neither the key nor the claim index fills at one end, where
        // every insert, claim and completion met on one page (ADR-180); rows go by MessageId.
        builder
            .Property(m => m.Id)
            .HasDefaultValueSql(OutboxMessage.RandomIdSql);

        // Computed and never stored, so the claim index can lead on it with no column to write.
        builder
            .Property<byte>(OutboxMessage.ClaimBucketColumn)
            .HasComputedColumnSql(OutboxMessage.ClaimBucketSql, stored: false);

        // §9.1's single-identity rule, made structural.
        builder.HasIndex(m => m.MessageId).IsUnique();

        // The bound MessageTypeMap enforces at startup, and Unicode, because a C# identifier may be.
        builder
            .Property(m => m.MessageType)
            .HasMaxLength(MessageTypeMap.MaxNameLength);

        // The one exception to §7.2's max-length convention, since a truncated payload cannot be delivered.
        // SetMaxLength(null) as well, or the model keeps the convention's 400 beside nvarchar(max).
        builder
            .Property(m => m.Payload)
            .HasColumnType("nvarchar(max)")
            .Metadata
            .SetMaxLength(null);

        // By name: the dispatcher branches on the string it reads back (§9.4), and an enum reorder migrates nothing.
        builder
            .Property(m => m.Lane)
            .HasConversion<string>()
            .HasMaxLength(OutboxMessage.LaneMaxLength)
            .IsUnicode(false);

        // The width the dispatcher's fail statement truncates to, so recording a failure cannot itself fail.
        builder
            .Property(m => m.LastError)
            .HasMaxLength(OutboxMessage.LastErrorMaxLength);

        // ASCII by the W3C format, and nullable, so a row staged before the columns still delivers (§7.4, §9.4).
        builder
            .Property(m => m.TraceParent)
            .HasMaxLength(OutboxMessage.TraceParentMaxLength)
            .IsUnicode(false);

        builder
            .Property(m => m.TraceState)
            .HasMaxLength(OutboxMessage.TraceStateMaxLength)
            .IsUnicode(false);

        // Filtered and covering, so the claim stays cheap while processed rows wait for §9.4's purge; led by the
        // bucket, so inserts land at ClaimBuckets ends and the claim still reads each bucket oldest first (ADR-180).
        builder
            .HasIndex(OutboxMessage.ClaimBucketColumn, nameof(OutboxMessage.OccurredAt))
            .HasDatabaseName("IX_Outbox_Unprocessed")
            .IncludeProperties(
                nameof(OutboxMessage.Lane),
                nameof(OutboxMessage.Attempts),
                nameof(OutboxMessage.LockedUntil))
            .HasFilter("[ProcessedAt] IS NULL");

        // The purge's own index, since the one above excludes every row its DELETE targets; filtered the other
        // way, so it stays the size of the undeleted backlog.
        builder
            .HasIndex(m => m.ProcessedAt)
            .HasDatabaseName("IX_Outbox_Processed")
            .HasFilter("[ProcessedAt] IS NOT NULL");
    }
}
