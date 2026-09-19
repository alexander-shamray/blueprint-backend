using Common.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Payments.Infrastructure.Persistence;

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
        builder.ToTable("IdempotencyMarkers", "payments");

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

        // Stamped by the database and never by a pod (#167, ADR-038). Both
        // ends of the comparison the purge makes are then the one clock: this
        // default writes the row, and RetentionPurgeService computes its cutoff
        // with SYSDATETIMEOFFSET() in the same statement that reads it. The
        // marker is the only one of the three retention tables that gets this,
        // because it is the only one whose window is a correctness setting —
        // the outbox's and the inbox's stay on the registered TimeProvider a
        // test host can substitute (§9.5).
        //
        // What makes the default reachable is ValueGenerated.OnAdd over it: EF
        // omits a property still holding its sentinel from the insert and reads
        // the generated value back, so a marker constructed WITH a timestamp
        // still writes it — which is what lets a fixture stage one at a
        // controlled age.
        //
        // The call below is REDUNDANT and is made anyway. EF's relational
        // convention derives OnAdd from a store default, measured on this
        // solution rather than read off the documentation: two properties in
        // Ordering's model configure a default and nothing else, and the
        // snapshot records ValueGeneratedOnAdd beside each. Spelling it here
        // states the behaviour the sentinel argument above depends on at the
        // one site that depends on it, rather than leaving a correctness
        // property to a convention a later EF version could narrow.
        builder
            .Property(marker => marker.CommittedAt)
            .HasDefaultValueSql("SYSDATETIMEOFFSET()")
            .ValueGeneratedOnAdd();

        // What RetentionPurgeService's DELETE identifies a row by (#173,
        // ADR-041). It is unique and monotonic per database, immutable for the
        // life of a row nothing updates, and reads no clock — which is what the
        // (Key, CommittedAt) pair before it was standing in for. That pair
        // distinguished two writes under one key BY CONSTRUCTION and nothing
        // enforced it: a datetimeoffset(7) carries no uniqueness, so a
        // replacement stamped at the selected row's exact tick was matched by a
        // stale delete and removed with its claim still live.
        //
        // A SHADOW property, because nothing in C# reads this column through
        // the model — the one reader is the purge's own SQL, over Dapper, which
        // never consults it. The name comes from the entity rather than a
        // literal here, so the two statements that have to agree about it —
        // this mapping and that SQL — agree by construction, and a service that
        // omits this line fails its own purge rather than drifting quietly.
        // Required, because a shadow byte[] is optional by convention and the
        // column never is: SQL Server stamps a rowversion on every insert and
        // on every update, including the rows an ALTER TABLE adds it to. Left
        // optional it would render `nullable: true` and hand the purge a
        // MarkerCandidate whose version could be null — a state the database
        // cannot produce, modelled anyway.
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
