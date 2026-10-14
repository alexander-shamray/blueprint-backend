using Common.Infrastructure.Tracing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Devices;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

/// <summary>The push design's <c>PushDeliveries</c>, one row per push notice and installation (§7.2).</summary>
internal sealed class PushDeliveryConfiguration : IEntityTypeConfiguration<PushDelivery>
{
    public void Configure(EntityTypeBuilder<PushDelivery> builder)
    {
        builder.ToTable("PushDeliveries", "notifications");
        builder.HasKey(d => d.DeliveryId);
        builder.Property(d => d.DeliveryId).ValueGeneratedNever();

        // A fan-out repeated after a crash inserts nothing twice: the one transaction first, then this key.
        builder.HasIndex(d => new { d.NotificationId, d.InstallationId }).IsUnique();

        // A delivery ages with its notice, whose retention deletes it only once none of its deliveries is pending.
        builder
            .HasOne<Notification>()
            .WithMany()
            .HasForeignKey(d => d.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The push claim's population, its filter repeated by PushClaims.Claimable; the platform included, as an open
        // provider's breaker leaves its platform's rows out of the claim.
        builder
            .HasIndex(d => d.NextAttemptAt)
            .HasDatabaseName("IX_PushDeliveries_SendClaim")
            .HasFilter("[Status] = 'Pending'")
            .IncludeProperties(d => new { d.LockedUntil, d.Platform });

        // §11.7's erasure, by the customer copied onto the row.
        builder
            .HasIndex(d => d.CustomerId)
            .HasDatabaseName("IX_PushDeliveries_Customer")
            .HasFilter("[CustomerId] IS NOT NULL");

        // ConsentRetention's floor: a journal entry a kept delivery names stays (ADR-120).
        builder
            .HasIndex(d => d.ConsentEntryId)
            .HasDatabaseName("IX_PushDeliveries_ConsentEntry")
            .HasFilter("[ConsentEntryId] IS NOT NULL");

        builder.Property(d => d.Platform).HasMaxLength(DeviceLimits.MaxPlatformLength).IsUnicode(false);
        builder.Property(d => d.Languages).HasMaxLength(NotificationLimits.MaxLanguagesLength);
        builder.Property(d => d.Reason).HasMaxLength(NotificationLimits.MaxReasonLength);

        // By name, never by number (§7.2).
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);

        // A shadow property, so the record names no EF type, as the notice's does (§7.2).
        builder.Property<byte[]>("RowVersion").IsRowVersion().IsRequired();

        // The fan-out's trace, which the push send worker's pass restores (§9.4).
        StagedTraceColumns.Map(builder);
    }
}
