using Common.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§8.5's durable marker, mapped here because the schema is this service's.</summary>
internal sealed class IdempotencyMarkerConfiguration : IEntityTypeConfiguration<IdempotencyMarker>
{
    public void Configure(EntityTypeBuilder<IdempotencyMarker> builder)
    {
        builder.ToTable("IdempotencyMarkers", "ordering");

        // The key alone: §8.5 puts everything that distinguishes an attempt inside it, and there is no second axis.
        builder.HasKey(marker => marker.Key);

        builder
            .Property(marker => marker.Key)

            // SQL Server's 900-byte clustered key limit at two bytes a character.
            .HasMaxLength(450)

            // nvarchar, since narrowing a key would let an encoding decide whether two commands are one.
            .UseCollation("Latin1_General_BIN2");

        // The database's clock at both ends of the purge's comparison (ADR-038). ValueGeneratedOnAdd makes the
        // default reachable, while a marker constructed with a timestamp still writes it.
        builder
            .Property(marker => marker.CommittedAt)
            .HasDefaultValueSql("SYSDATETIMEOFFSET()")
            .ValueGeneratedOnAdd();

        // What the purge's DELETE identifies a row by (ADR-041); a shadow property, as only that raw SQL reads it.
        builder
            .Property<byte[]>(IdempotencyMarker.RowVersionColumn)
            .IsRowVersion()
            .IsRequired();

        // The purge's predicate (§8.5); unfiltered, since every row records committed work.
        builder
            .HasIndex(marker => marker.CommittedAt)
            .HasDatabaseName("IX_Idempotency_CommittedAt");
    }
}
