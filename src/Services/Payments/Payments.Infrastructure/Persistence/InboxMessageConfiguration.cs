using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Payments.Infrastructure.Persistence;

/// <summary>§9.5's table, mapped here because the schema is this service's.</summary>
internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages", "payments");

        // §9.5's composite key: one type bound on two endpoints is processed independently on each.
        builder.HasKey(m => new { m.MessageId, m.Endpoint });

        // nvarchar, since narrowing half a key would let an encoding decide whether a message is delivered.
        builder
            .Property(m => m.Endpoint)
            .HasMaxLength(InboxMessage.EndpointMaxLength)

            // Binary, since queue names are case-sensitive and an address is matched exactly.
            .UseCollation("Latin1_General_BIN2");

        // The purge's predicate (§9.5); unfiltered, since every row is handled by construction.
        builder
            .HasIndex(m => m.HandledAt)
            .HasDatabaseName("IX_Inbox_HandledAt");
    }
}
