using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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

        // One row per event per template, the second line behind §9.5's inbox rather than the first.
        builder.HasIndex(n => new { n.EventId, n.TemplateKey }).IsUnique();

        builder.Property(n => n.TemplateKey).HasMaxLength(NotificationLimits.MaxTemplateKeyLength);
        builder.Property(n => n.Languages).HasMaxLength(NotificationLimits.MaxLanguagesLength);
        builder.Property(n => n.Parameters).HasMaxLength(NotificationLimits.MaxParametersLength);
        builder.Property(n => n.Reason).HasMaxLength(NotificationLimits.MaxReasonLength);

        // By name, never by number (§7.2).
        builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(16);

        // A shadow property, so the record names no EF type, as §8.5's marker does (§7.2).
        builder.Property<byte[]>("RowVersion").IsRowVersion();
    }
}
