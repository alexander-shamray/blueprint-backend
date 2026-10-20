using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Devices;

namespace Notifications.Infrastructure.Persistence;

internal sealed class DeviceInstallationRowConfiguration : IEntityTypeConfiguration<DeviceInstallationRow>
{
    public void Configure(EntityTypeBuilder<DeviceInstallationRow> builder)
    {
        builder.ToTable("DeviceInstallations", "notifications");

        // One row per installation of the app, keyed by the id the app minted (ADR-117).
        builder.HasKey(r => r.InstallationId);
        builder.Property(r => r.InstallationId).ValueGeneratedNever();

        // varchar throughout: every value is printable ASCII by the validators before a row is written.
        builder.Property(r => r.Platform).HasMaxLength(DeviceLimits.MaxPlatformLength).IsUnicode(false);
        builder.Property(r => r.AppId).HasMaxLength(DeviceLimits.MaxAppIdLength).IsUnicode(false);
        builder.Property(r => r.Token).HasMaxLength(DeviceLimits.MaxTokenLength).IsUnicode(false);

        // A token is one installation's: one that reappears under another installation leaves the first (ADR-117).
        builder.HasIndex(r => new { r.Platform, r.AppId, r.Token }).IsUnique().HasFilter("[Token] IS NOT NULL");

        // The erasure requests already applied to the subject when the registration read them, which a reissue keeps
        // the row for (ADR-138); a JSON column, as on OrderRecords, since no row is looked up or indexed by them.
        builder.Property(r => r.ErasedUnder).HasColumnType("nvarchar(max)").HasDefaultValueSql("N'[]'");

        // The cap's count and erasure's clearing, both by subject; and the stale pass, by when a row was last seen.
        builder.HasIndex(r => r.SubjectId);
        builder.HasIndex(r => r.LastSeenAt);
    }
}
