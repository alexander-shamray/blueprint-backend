using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// §9.4's table, mapped here rather than beside the entity. The entity is a
/// building block and knows no schema; the schema is this service's, and this
/// assembly is where the <c>ApplyConfigurationsFromAssembly</c> scan looks.
/// A configuration in <c>Common.Infrastructure</c> would need EF Core there
/// and still not be found — a package reference and a silent no-op at once.
/// </summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", "payments");
        builder.HasKey(m => m.Id);

        // The claim orders by OccurredAt and the dispatcher never chooses an
        // Id, so identity is right here where §5.2 rejects it for aggregates:
        // this is a queue position, not an identifier anything outside the
        // table refers to. Rows are addressed by MessageId.
        builder
            .Property(m => m.Id)
            .ValueGeneratedOnAdd();

        // The unique constraint is the single-identity rule of §9.1 made
        // structural: two rows carrying one message id is the state where
        // "was this message processed?" stops having an answer.
        builder.HasIndex(m => m.MessageId).IsUnique();

        // Unicode, and bounded at MessageTypeMap.MaxNameLength, not a literal
        // 300: the map refuses a longer name at startup, and two independent
        // numbers would let the guard and the column drift into disagreeing
        // about what fits. Not varchar: C# permits Unicode identifiers, so a
        // domain event named in another language is a legal type, and a
        // storage choice does not get to narrow what the domain may call
        // something. The cost is 300 bytes per unprocessed row, not paid by
        // the claim's index, which covers OccurredAt and includes only Lane,
        // Attempts and LockedUntil.
        builder
            .Property(m => m.MessageType)
            .HasMaxLength(MessageTypeMap.MaxNameLength);

        // The one deliberate exception to §7.2's max-length convention. A
        // payload is a contract or a domain event of unknown size, and a
        // truncated one is a row that cannot be delivered and cannot be read.
        // HasColumnType alone fixes the DDL but leaves MaxLength at the
        // convention's 400 in the model, so the property is cleared as well —
        // otherwise the generated migration says both `nvarchar(max)` and
        // `maxLength: 400` in the same line. A container test stages a
        // payload past 400 characters and reads it back.
        builder
            .Property(m => m.Payload)
            .HasColumnType("nvarchar(max)")
            .Metadata
            .SetMaxLength(null);

        // Stored as its name, not its ordinal. The dispatcher branches on the
        // string it reads back (§9.4), an operator reading the table sees
        // 'Broker' rather than 0, and reordering the enum stops being a
        // silent data migration.
        builder
            .Property(m => m.Lane)
            .HasConversion<string>()
            .HasMaxLength(OutboxMessage.LaneMaxLength)
            .IsUnicode(false);

        // OutboxMessage.LastErrorMaxLength, not a literal 2000: the
        // dispatcher's fail statement truncates to this width, and a column
        // that disagrees with the LEFT writing into it fails the update that
        // was recording why a delivery failed.
        builder
            .Property(m => m.LastError)
            .HasMaxLength(OutboxMessage.LastErrorMaxLength);

        // Filtered: the dispatcher only ever scans unprocessed rows, so the
        // index stays small regardless of table size — which is what keeps
        // the claim cheap while processed rows wait for §9.4's purge. The
        // included columns are exactly the ones the claim's predicate and
        // OUTPUT need beyond the key, so it covers the query.
        builder
            .HasIndex(m => m.OccurredAt)
            .HasDatabaseName("IX_Outbox_Unprocessed")
            .IncludeProperties(m => new { m.Lane, m.Attempts, m.LockedUntil })
            .HasFilter("[ProcessedAt] IS NULL");

        // The retention purge's index, and it has to be a second one: the
        // filtered index above is `WHERE ProcessedAt IS NULL`, which excludes
        // by construction every row the purge's own DELETE targets, and
        // without this the hourly purge scans the whole table. Filtered the
        // other way for the same reason its twin is, so it stays the size of
        // the undeleted backlog rather than the table; nothing is included,
        // since the delete needs only the clustered key it already has.
        builder
            .HasIndex(m => m.ProcessedAt)
            .HasDatabaseName("IX_Outbox_Processed")
            .HasFilter("[ProcessedAt] IS NOT NULL");
    }
}
