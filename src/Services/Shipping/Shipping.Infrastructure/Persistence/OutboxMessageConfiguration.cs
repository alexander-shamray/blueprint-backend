using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Shipping.Infrastructure.Persistence;

/// <summary>§9.4's table, mapped here because the schema is this service's.</summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", "shipping");
        builder.HasKey(m => m.Id);

        // Identity, which §5.2 rejects for aggregates, because this is a queue position; rows go by MessageId.
        builder
            .Property(m => m.Id)
            .ValueGeneratedOnAdd();

        // §9.1's single identity, made structural.
        builder.HasIndex(m => m.MessageId).IsUnique();

        // The map's own bound, so the startup guard and the column agree; Unicode, as C# identifiers may be.
        builder
            .Property(m => m.MessageType)
            .HasMaxLength(MessageTypeMap.MaxNameLength);

        // §7.2's one exception, since a truncated payload can be neither delivered nor read. The model's
        // MaxLength is cleared too, or the migration would carry the convention's 400 beside nvarchar(max).
        builder
            .Property(m => m.Payload)
            .HasColumnType("nvarchar(max)")
            .Metadata
            .SetMaxLength(null);

        // As its name: the dispatcher branches on the string it reads back (§9.4), and reordering is no migration.
        builder
            .Property(m => m.Lane)
            .HasConversion<string>()
            .HasMaxLength(OutboxMessage.LaneMaxLength)
            .IsUnicode(false);

        // The width the dispatcher's LEFT truncates to, or the update recording a failure would itself fail.
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

        // Filtered and covering, so the claim stays cheap while processed rows wait for §9.4's purge.
        builder
            .HasIndex(m => m.OccurredAt)
            .HasDatabaseName("IX_Outbox_Unprocessed")
            .IncludeProperties(m => new { m.Lane, m.Attempts, m.LockedUntil })
            .HasFilter("[ProcessedAt] IS NULL");

        // The purge's index: the filter above excludes every row its DELETE targets.
        builder
            .HasIndex(m => m.ProcessedAt)
            .HasDatabaseName("IX_Outbox_Processed")
            .HasFilter("[ProcessedAt] IS NOT NULL");
    }
}
