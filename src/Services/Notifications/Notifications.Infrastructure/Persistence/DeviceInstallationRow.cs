namespace Notifications.Infrastructure.Persistence;

/// <summary>Mapped only so <c>migrations add</c> emits <see cref="SqlDeviceStore"/>'s table (ADR-117).</summary>
/// <remarks>A cleared row keeps only its installation id, its sequence and its last-seen time.</remarks>
internal sealed class DeviceInstallationRow
{
    public Guid InstallationId { get; set; }
    public Guid? SubjectId { get; set; }
    public string? Platform { get; set; }
    public string? AppId { get; set; }
    public string? Token { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset? RegisteredAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
