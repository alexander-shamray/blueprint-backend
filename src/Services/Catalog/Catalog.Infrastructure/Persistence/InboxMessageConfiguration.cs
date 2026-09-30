using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§9.5's table, mapped here for the reason <see cref="OutboxMessageConfiguration"/> gives.</summary>
internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages", "catalog");

        // §9.5's composite key: one service may bind a type on two endpoints, and each must process it.
        builder.HasKey(m => new { m.MessageId, m.Endpoint });

        // nvarchar, because narrowing half a key lets an encoding decide whether a message is delivered.
        builder
            .Property(m => m.Endpoint)
            .HasMaxLength(InboxMessage.EndpointMaxLength)

            // BIN2, because queue names are case-sensitive and matched exactly, and this column is half a key.
            .UseCollation("Latin1_General_BIN2");

        // The purge's predicate (§9.5), unfiltered because every row is handled by construction.
        builder
            .HasIndex(m => m.HandledAt)
            .HasDatabaseName("IX_Inbox_HandledAt");
    }
}
