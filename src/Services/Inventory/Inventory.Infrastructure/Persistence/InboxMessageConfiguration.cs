using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// §9.5's table, mapped here rather than beside the entity, for the reason
/// <see cref="OutboxMessageConfiguration"/> gives one file over: the entity is a
/// building block and knows no schema, the schema is this service's, and this
/// assembly is where the <c>ApplyConfigurationsFromAssembly</c> scan looks.
/// </summary>
internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages", "inventory");

        // The composite key of §9.5, and the second column is the whole point:
        // one service may bind the same type on two endpoints — a normal queue
        // and a bulk/replay one — and each must process the message
        // independently. MessageId alone would let whichever finished first
        // suppress the other.
        builder.HasKey(m => new { m.MessageId, m.Endpoint });

        // nvarchar, like the outbox's MessageType column one file over, and the
        // reason is the same one stated there: narrowing a column that is half
        // a key lets an encoding decide whether a message is delivered.
        //
        // This was varchar for one revision, on the claim that "a RabbitMQ queue
        // name is ASCII by the transport's own rules" — which is simply untrue.
        // AMQP 0-9-1 gives a queue name up to 255 bytes of UTF-8, so two legal
        // endpoint names differing only outside the code page both arrive here
        // as the same run of `?` characters. They then collide in the composite
        // key below, and the second endpoint's first message is suppressed as
        // already handled — a message dropped by the mechanism whose entire
        // purpose is dropping only true duplicates, and dropped silently,
        // because a suppressed message looks exactly like a suppressed
        // duplicate. The collation cannot help: it compares what was stored,
        // and the loss happens on the way in.
        //
        // InboxMessage.EndpointMaxLength, not a literal 300 and not §9.5's
        // width: the entity is what both services map, so the width is the
        // entity's to state and the chapter's to cite. The remarks there
        // carry why it is as generous as it is.
        builder
            .Property(m => m.Endpoint)
            .HasMaxLength(InboxMessage.EndpointMaxLength)

            // Binary collation, because this column is half a key rather than
            // text. SQL Server's default is case-insensitive, and a broker's
            // queue names are not: `orders` and `Orders` are two queues, and
            // under the default collation the second one's row collides with
            // the first's. The message is then dropped as a duplicate on an
            // endpoint that never saw it — the exact once-per-endpoint
            // guarantee the composite key exists to provide, defeated by the
            // column's comparison semantics rather than by its contents.
            //
            // BIN2 rather than a CS_AS collation: an endpoint address is an
            // identifier to be matched exactly, and linguistic comparison has
            // no meaning over it. Accents and width would be the same argument
            // one rule further on.
            .UseCollation("Latin1_General_BIN2");

        // The purge's predicate (§9.5). Non-covering and non-filtered, unlike
        // the outbox's: there is no unprocessed subset to narrow to here — every
        // row is handled by construction — and the delete needs the key, which
        // it already has from the clustered primary key above.
        builder
            .HasIndex(m => m.HandledAt)
            .HasDatabaseName("IX_Inbox_HandledAt");
    }
}
