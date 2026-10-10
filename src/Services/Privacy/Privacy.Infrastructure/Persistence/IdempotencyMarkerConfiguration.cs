using Common.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Privacy.Infrastructure.Persistence;

/// <summary>§8.5's durable marker, mapped here for the reason <see cref="InboxMessageConfiguration"/> gives.</summary>
internal sealed class IdempotencyMarkerConfiguration : IEntityTypeConfiguration<IdempotencyMarker>
{
    public void Configure(EntityTypeBuilder<IdempotencyMarker> builder)
    {
        builder.ToTable("IdempotencyMarkers", "privacy");

        // The key alone: §8.5 puts {subject}:{operation}:{commandId} inside it, so there is no second axis.
        builder.HasKey(marker => marker.Key);

        builder
            .Property(marker => marker.Key)
            .HasMaxLength(IdempotencyMarker.KeyMaxLength)

            // nvarchar, because narrowing a key column lets an encoding decide whether two commands are one.
            .UseCollation("Latin1_General_BIN2");

        // Stamped by the database, never a pod (ADR-038), so the purge compares one clock at both ends.
        // ValueGeneratedOnAdd keeps the default reachable while a marker built with a timestamp still writes it.
        builder
            .Property(marker => marker.CommittedAt)
            .HasDefaultValueSql("SYSDATETIMEOFFSET()")
            .ValueGeneratedOnAdd();

        // What the purge's DELETE identifies a row by (ADR-041). A shadow property, because only the purge's
        // Dapper SQL reads it; required, because a shadow byte[] is optional by convention.
        builder
            .Property<byte[]>(IdempotencyMarker.RowVersionColumn)
            .IsRowVersion()
            .IsRequired();

        // The purge's predicate (§8.5), unfiltered because every row here records committed work.
        builder
            .HasIndex(marker => marker.CommittedAt)
            .HasDatabaseName("IX_Idempotency_CommittedAt");
    }
}
