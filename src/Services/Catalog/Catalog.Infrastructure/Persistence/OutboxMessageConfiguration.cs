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

        // Identity, which §5.2 rejects for aggregates: this is a queue position, and rows are addressed by MessageId.
        builder
            .Property(m => m.Id)
            .ValueGeneratedOnAdd();

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

        // Filtered to unprocessed rows and covering the claim, so the claim stays cheap whatever the table's size.
        builder
            .HasIndex(m => m.OccurredAt)
            .HasDatabaseName("IX_Outbox_Unprocessed")
            .IncludeProperties(m => new { m.Lane, m.Attempts, m.LockedUntil })
            .HasFilter("[ProcessedAt] IS NULL");

        // The purge's own index, since the one above excludes every row its DELETE targets; filtered the other
        // way, so it stays the size of the undeleted backlog.
        builder
            .HasIndex(m => m.ProcessedAt)
            .HasDatabaseName("IX_Outbox_Processed")
            .HasFilter("[ProcessedAt] IS NOT NULL");
    }
}
