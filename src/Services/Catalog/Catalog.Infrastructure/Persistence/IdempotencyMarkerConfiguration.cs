using Common.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// §8.5's durable marker, mapped here rather than beside the entity, for the
/// reason <see cref="InboxMessageConfiguration"/> gives one file over: the
/// entity is a building block and knows no schema, the schema is this
/// service's, and this assembly is where the
/// <c>ApplyConfigurationsFromAssembly</c> scan looks.
/// </summary>
internal sealed class IdempotencyMarkerConfiguration : IEntityTypeConfiguration<IdempotencyMarker>
{
    public void Configure(EntityTypeBuilder<IdempotencyMarker> builder)
    {
        builder.ToTable("IdempotencyMarkers", "catalog");

        // The key alone, because §8.5 already put everything that
        // distinguishes an attempt inside it — {subject}:{operation}:{commandId}.
        // The inbox's composite key exists because one service may bind one
        // message type on two endpoints; there is no second axis here, and a
        // column that distinguishes nothing is what that entity's own comment
        // warns against.
        builder.HasKey(marker => marker.Key);

        builder
            .Property(marker => marker.Key)

            // 450, which is exactly SQL Server's 900-byte limit for a clustered
            // index key at two bytes a character — the widest this column can
            // be while the primary key above stays clustered. The key needs 74
            // characters for its two GUIDs and two separators, so what the
            // width really bounds is the declared operation name, and 376
            // characters is past any name a service would write. A per-service
            // gate asserts the ones it declares fit, because the alternative is
            // SQL Server truncating a key and refusing the insert on the first
            // dispatch of a command nobody tested with a long name.
            .HasMaxLength(450)

            // nvarchar for the inbox's reason one file over: this column is a
            // key, and narrowing a key column lets an encoding decide whether
            // two commands are the same command. The operation segment is a
            // developer-chosen string and the subject segment is a principal's
            // identity; neither is promised to be ASCII by anything.
            .UseCollation("Latin1_General_BIN2");

        // Stamped by the database and never by a pod (ADR-038), so the purge
        // compares one clock at both ends: this default writes the row, and
        // RetentionPurgeService cuts off with SYSDATETIMEOFFSET() in the same
        // statement that reads it. The marker alone gets this, because its
        // window is the only one that is a correctness setting; the outbox's
        // and the inbox's stay on the registered TimeProvider (§9.5).
        // ValueGeneratedOnAdd makes the default reachable while a marker
        // constructed with a timestamp still writes it, which is what lets a
        // fixture stage one at a controlled age. Spelt out rather than left to
        // EF's convention, because a correctness property should not move.
        builder
            .Property(marker => marker.CommittedAt)
            .HasDefaultValueSql("SYSDATETIMEOFFSET()")
            .ValueGeneratedOnAdd();

        // What RetentionPurgeService's DELETE identifies a row by (ADR-041):
        // unique and monotonic per database, immutable for a row nothing
        // updates, and reading no clock — unlike the (Key, CommittedAt) pair
        // it replaced, where datetimeoffset(7) carries no uniqueness and a
        // replacement stamped at the selected row's tick was matched by a
        // stale delete and removed with its claim still live. A shadow
        // property, because the only reader is the purge's own Dapper SQL; the
        // name comes from the entity so the mapping and that SQL agree by
        // construction. Required, because a shadow byte[] is optional by
        // convention and this column never is.
        builder
            .Property<byte[]>(IdempotencyMarker.RowVersionColumn)
            .IsRowVersion()
            .IsRequired();

        // The purge's predicate (§8.5). Non-covering and non-filtered, like the
        // inbox's and for the same reason: every row here records work that
        // committed, so there is no unfinished subset to narrow to, and the
        // delete already has the key from the clustered primary key above.
        builder
            .HasIndex(marker => marker.CommittedAt)
            .HasDatabaseName("IX_Idempotency_CommittedAt");
    }
}
