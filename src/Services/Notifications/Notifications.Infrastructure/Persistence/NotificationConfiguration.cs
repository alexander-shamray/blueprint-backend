using Common.Infrastructure.Tracing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Preferences;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

/// <summary>§3.2's <c>NotificationLog</c>, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("NotificationLog", "notifications");
        builder.HasKey(n => n.NotificationId);
        builder.Property(n => n.NotificationId).ValueGeneratedNever();

        // One row per event per template per channel, the second line behind §9.5's inbox rather than the first.
        builder.HasIndex(n => new { n.EventId, n.TemplateKey, n.Channel }).IsUnique();

        // The send claim's population, its filter repeated by SendClaims.Claimable so the optimiser matches it; the
        // channel included, as an open relay narrows the claim to push notices (the push design's section 1). Keyed
        // on the row's id, whose order SQL Server reads as random, so the insert, the lease and the outcome do not
        // all latch the newest page; the claim sorts the few pending rows instead (ADR-175).
        builder
            .HasIndex(n => n.NotificationId)
            .HasDatabaseName("IX_NotificationLog_SendClaim")
            .HasFilter("[Status] = 'Pending'")
            .IncludeProperties(n => new { n.NextAttemptAt, n.LockedUntil, n.Channel });

        // OrderRetention's floor: an order record goes only once no pending notice names its order.
        builder
            .HasIndex(n => n.OrderId)
            .HasDatabaseName("IX_NotificationLog_PendingOrder")
            .HasFilter("[Status] = 'Pending'");

        // LogRetention's purge, over terminal rows by the instant each ended (ADR-053). Keyed on the id, as the claim's
        // is and by name since the claim's has that key, so an outcome does not append to the newest page; the purge
        // sorts the ended rows instead (ADR-176).
        builder
            .HasIndex(n => n.NotificationId, "IX_NotificationLog_CompletedAt")
            .HasFilter("[CompletedAt] IS NOT NULL")
            .IncludeProperties(n => new { n.CompletedAt, n.Status });

        // ConsentRetention's floor: a journal entry a kept row names, or the one that ended it, stays (ADR-120).
        builder
            .HasIndex(n => n.ConsentEntryId)
            .HasDatabaseName("IX_NotificationLog_ConsentEntry")
            .HasFilter("[ConsentEntryId] IS NOT NULL");

        builder.Property(n => n.TemplateKey).HasMaxLength(NotificationLimits.MaxTemplateKeyLength);

        // Defaulted to email, which backfills every row written before push and every row a version that predates
        // it writes while both run (§7.4); the record always sets it, so the default never stands in for a push.
        builder
            .Property(n => n.Channel)
            .HasMaxLength(ConsentVocabulary.MaxChannelLength)
            .IsUnicode(false)
            .HasDefaultValue(ConsentVocabulary.Email);
        builder.Property(n => n.Languages).HasMaxLength(NotificationLimits.MaxLanguagesLength);
        builder.Property(n => n.Parameters).HasMaxLength(NotificationLimits.MaxParametersLength);
        builder.Property(n => n.Reason).HasMaxLength(NotificationLimits.MaxReasonLength);

        // By name, never by number (§7.2).
        builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(16);

        // A shadow property, so the record names no EF type, as §8.5's marker does (§7.2).
        builder.Property<byte[]>("RowVersion").IsRowVersion().IsRequired();

        // The intake's trace, which the send worker's pass restores (§9.4); shadow, for the same reason.
        StagedTraceColumns.Map(builder);
    }
}
